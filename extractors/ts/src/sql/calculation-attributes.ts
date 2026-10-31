import type { SqlExpression, SqlRoutineCalculation } from "./calculation-types.ts";
import { lookupKey, type Range } from "./ddl.ts";
import { CalculationLowering, walkSql } from "./calculation-ir.ts";

type Attribute = { status: "unknown" } | { status: "known"; value: string; evidence: string[]; detail?: Record<string, string | number> };
type Call = Extract<SqlExpression, { kind: "call" }>;
const isCall = (e: SqlExpression, name: string): e is Call => e.kind === "call" && e.name.toUpperCase() === name;
const isNumber = (e: SqlExpression | undefined, n: number) => e?.kind === "number" && Number(e.value) === n;
const unknown = (): Attribute => ({ status: "unknown" });
const contains = (e: SqlExpression, predicate: (e: SqlExpression) => boolean) => {
  let found = false; walkSql(e, (n) => { if (predicate(n)) found = true; }); return found;
};
const terms = (e: SqlExpression): SqlExpression[] => e.kind === "binary" && e.operator === "AND" ? [...terms(e.left), ...terms(e.right)] : [e];
const columnName = (name: string) => name[0]!.toLowerCase() + name.slice(1);

/** Attribute text uses resolved AST structure; identifiers supply spelling, never business semantics. */
export function calculationAttributes(c: SqlRoutineCalculation, lowering: CalculationLowering, path: string, ev: (r: Range) => string): Record<string, Attribute> {
  const known = (value: string, e: SqlExpression | Range, detail?: Record<string, string | number>): Attribute => ({
    status: "known", value, evidence: [ev("path" in e ? e : { path, ...e.at })], ...(detail ? { detail } : {}),
  });
  const out: Record<string, Attribute> = {};
  const all: SqlExpression[] = []; walkSql(c.expression, (e) => all.push(e));
  const firstAgg = lowering.aggregates.find((a) => a.fn === "sum") ?? lowering.aggregates[0];
  const divisions = all.filter((e): e is Extract<SqlExpression, { kind: "binary" }> => e.kind === "binary" && e.operator === "/");
  const round = all.find((e) => isCall(e, "ROUND"));
  const dateTrunc = all.find((e) => isCall(e, "DATE_TRUNC"));
  const filter = c.query?.where;
  const candidatePeriod = filter ? periodRange(filter) : undefined;
  const parameterDate = (e: SqlExpression): boolean => e.kind === "reference" && e.parts.length === 1 && c.parameters.some((p) => lookupKey(c.dialect, p.name) === lookupKey(c.dialect, e.parts[0]!) && p.type.name === "date");
  const upperDate = (e: SqlExpression): boolean => parameterDate(e) || (e.kind === "binary" && e.operator === "+" && parameterDate(e.left) && isNumber(e.right, 1));
  const period = candidatePeriod && ["date", "timestamptz", "timestamp", "datetime", "datetime2"].includes(lowering.values.get(candidatePeriod.column)?.type ?? "") && parameterDate(candidatePeriod.lower) && upperDate(candidatePeriod.upper) ? candidatePeriod : undefined;
  const periodBuckets = period ? all.find((e) => sevenDayBuckets(e) && e.kind === "binary" && e.left.kind === "binary" && isCall(e.left.left, "DATEDIFF") && same(e.left.left.args[1]!, period.lower) && same(e.left.left.args[2]!, period.upper)) : undefined;
  const correlatedTrunc = period && dateTrunc && isCall(dateTrunc, "DATE_TRUNC") && same(dateTrunc.args[1]!, period.column) ? dateTrunc : undefined;
  out.aggregation = firstAgg ? known(firstAgg.fn, firstAgg.node) : known("none", c.expression);
  out.timeBucket = periodBuckets ? known("sevenDayFromPeriodStart", periodBuckets, { description: "DATEDIFF(day)/7 + 1 with integer division; not ISO calendar weeks" })
    : correlatedTrunc ? known("isoWeek", correlatedTrunc, { function: `date_trunc('week', ${lowering.values.get(correlatedTrunc.args[1]!)?.type})`, note: lowering.values.get(correlatedTrunc.args[1]!)?.type === "timestamptz" ? "Monday-based weeks in the session TimeZone" : "Monday-based weeks without a time zone" })
    : all.some((e) => isCall(e, "DATEDIFF") || isCall(e, "DATE_TRUNC")) ? unknown() : known("none", c.expression);
  if (period) out.period = known("requested", period.node, { bounds: period.bounds });
  else if (firstAgg) out.period = unknown();
  const dataDenominator = divisions.find((d) => contains(d.right, (e) => isCall(e, "COUNT") || isCall(e, "SUM")));
  const periodDenominator = periodBuckets ? divisions.find((d) => contains(d.right, (e) => e === periodBuckets)) : undefined;
  const variableDenominator = divisions.find((d) => contains(d.right, (e) => e.kind === "reference"));
  out.denominatorSource = dataDenominator ? known("data", dataDenominator.right)
    : periodDenominator ? known("period", periodBuckets!)
    : variableDenominator ? known("parameter", variableDenominator.right)
    : divisions.length ? known("constant", divisions[0]!.right) : known("none", c.expression);
  if (filter) out.filters = known(filterText(filter, lowering), filter);
  out.rounding = round && isCall(round, "ROUND") ? known("halfAwayFromZero", round, { digits: Number((round.args[1] as Extract<SqlExpression, { kind: "number" }>).value), api: c.dialect === "tsql" ? "T-SQL ROUND(decimal, 2)".replace("2)", `${text(round.args[1]!)})`) : "PostgreSQL ROUND(numeric, int)" }) : known("none", c.expression);
  const numericType = lowering.values.get(c.expression)!.type;
  const numericColumn = [...lowering.columns.values()].find((col) => /^(numeric|decimal)\b/i.test(col.signature));
  out.numericType = known(numericType, numericColumn?.at ?? c.expression);
  const nullZero = all.find((e) => (!firstAgg || contains(firstAgg.value, (v) => v === e)) && e.kind === "call" && ["ISNULL", "COALESCE"].includes(e.name.toUpperCase()) && isNumber(e.args[1], 0) && e.args.length === 2);
  const valueColumns = firstAgg ? [...lowering.columns].filter(([e]) => contains(firstAgg.value, (v) => e === v)).map(([, col]) => col) : [];
  const valuesNonNull = lowering.aggregates.length ? lowering.aggregates.every((a) => !lowering.values.get(a.value)?.nullable) : !lowering.values.get(c.expression)!.nullable;
  out.nullHandling = nullZero && valuesNonNull ? known("nullAsZero", nullZero)
    : valuesNonNull && valueColumns.length && valueColumns.every((col) => / NOT NULL$/i.test(col.signature)) ? known("notApplicable", valueColumns[0]!.at, { reason: "all inputs are NOT NULL columns" })
    : !firstAgg && !lowering.values.get(c.expression)!.nullable ? known("notApplicable", c.expression) : unknown();
  const risky = divisions.filter((d) => d.right.kind !== "number" || isNumber(d.right, 0));
  const guarded = risky.find((d) => isCall(d.right, "NULLIF") && isNumber(d.right.args[1], 0));
  out.zeroDenominator = !risky.length ? known("notApplicable", c.expression)
    : guarded && risky.every((d) => isCall(d.right, "NULLIF") && isNumber(d.right.args[1], 0) && nullPropagates(c.expression, d)) ? known("returnsNull", guarded.right)
    : guarded ? unknown() : known("unguarded", risky[0]!.right);
  out.currency = unknown();
  const overflow = all.find((e) => e.kind === "binary" && e.operator === "*" && lowering.values.get(e)?.type === "bigint");
  if (overflow) out.overflow = c.dialect === "tsql" ? unknown() : known("throws", overflow, { expression: overflow.kind === "binary" ? `${lowering.values.get(overflow.left)?.type} * ${lowering.values.get(overflow.right)?.type === "int32" ? "integer" : lowering.values.get(overflow.right)?.type}` : "bigint multiplication", error: "bigint out of range" });
  return out;
}

function sevenDayBuckets(e: SqlExpression): boolean {
  if (e.kind !== "binary" || e.operator !== "+" || !isNumber(e.right, 1)) return false;
  const d = e.left;
  return d.kind === "binary" && d.operator === "/" && isNumber(d.right, 7) && isCall(d.left, "DATEDIFF")
    && d.left.args[0]?.kind === "reference" && d.left.args[0].parts[0]?.toLowerCase() === "day";
}
function text(e: SqlExpression, compact = false, lowering?: CalculationLowering): string {
  const gap = compact ? "" : " ";
  switch (e.kind) {
    case "number": return e.value;
    case "string": return `'${e.value.replace(/'/g, "''")}'`;
    case "null": return "null";
    case "reference": if (lowering?.columns.has(e)) return columnName(lowering.columns.get(e)!.name); return e.parts.length === 1 ? e.parts[0]! : columnName(e.parts.at(-1)!);
    case "unary": return `${e.operator}${text(e.operand, compact, lowering)}`;
    case "cast": return text(e.expression, compact, lowering);
    case "binary": {
      const precedence: Record<string, number> = { AND: 1, "=": 2, "!=": 2, "<>": 2, "<": 2, "<=": 2, ">": 2, ">=": 2, "+": 3, "-": 3, "*": 4, "/": 4 };
      const side = (child: SqlExpression, right: boolean) => {
        const rendered = text(child, compact, lowering);
        return child.kind === "binary" && (precedence[child.operator]! < precedence[e.operator]! || (right && precedence[child.operator] === precedence[e.operator] && e.operator !== "AND")) ? `(${rendered})` : rendered;
      };
      return `${side(e.left, false)}${gap}${e.operator === "AND" ? "and" : e.operator}${gap}${side(e.right, true)}`;
    }
    case "between": return `${text(e.expression)} in [${text(e.lower, true)},${text(e.upper, true)}]`;
    case "call": return `${e.name}(${e.args.map((a) => text(a, compact, lowering)).join(",")})`;
  }
}
function same(a: SqlExpression, b: SqlExpression): boolean { return text(a, true) === text(b, true); }
function periodRange(filter: SqlExpression): { node: SqlExpression; column: SqlExpression; lower: SqlExpression; upper: SqlExpression; upperExclusive: boolean; bounds: string; boundNodes: SqlExpression[] } | undefined {
  const parts = terms(filter);
  const between = parts.find((e) => e.kind === "between");
  if (between?.kind === "between") return { node: between, boundNodes: [between], column: between.expression, lower: between.lower, upper: between.upper, upperExclusive: false, bounds: `[${text(between.lower)}, ${text(between.upper)}] inclusive (BETWEEN)` };
  const lower = parts.find((e) => e.kind === "binary" && e.operator === ">=" && e.left.kind === "reference");
  if (lower?.kind !== "binary") return undefined;
  const upper = parts.find((e) => e.kind === "binary" && ["<", "<="].includes(e.operator) && same(e.left, lower.left));
  if (upper?.kind !== "binary") return undefined;
  const exclusive = upper.operator === "<";
  return { node: upper, boundNodes: [lower, upper], column: lower.left, lower: lower.right, upper: upper.right, upperExclusive: exclusive, bounds: `[${text(lower.right)}, ${text(upper.right)}${exclusive ? ") half-open" : "] inclusive"}` };
}
function filterText(filter: SqlExpression, lowering: CalculationLowering): string {
  const period = periodRange(filter);
  const parts = terms(filter);
  if (!period) return parts.map((e) => text(e, false, lowering)).join(" and ");
  const remaining = parts.filter((e) => !period.boundNodes.includes(e));
  const range = `${text(period.column, false, lowering)} in [${text(period.lower, true)},${period.node.kind === "between" ? "" : " "}${text(period.upper, true)}${period.upperExclusive ? ")" : "]"}`;
  return [...remaining.map((e) => text(e, false, lowering)), range].join(" and ");
}

/** True only when a NULL at target necessarily makes the final expression NULL. */
function nullPropagates(root: SqlExpression, target: SqlExpression): boolean {
  if (root === target) return true;
  if (root.kind === "unary") return nullPropagates(root.operand, target);
  if (root.kind === "cast") return nullPropagates(root.expression, target);
  if (root.kind === "binary") return nullPropagates(root.left, target) || nullPropagates(root.right, target);
  if (root.kind === "call") {
    if (["ROUND", "DATEDIFF", "DATE_TRUNC"].includes(root.name.toUpperCase())) return root.args.some((e) => nullPropagates(e, target));
    if (isCall(root, "NULLIF")) return nullPropagates(root.args[0]!, target);
  }
  return false;
}
