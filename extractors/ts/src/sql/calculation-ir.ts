import type { SqlDataType, SqlExpression, SqlRoutineCalculation } from "./calculation-types.ts";
import { lookupKey, type Catalog, type ColumnDef, type TableDef } from "./ddl.ts";

export type NumericType = "int32" | "bigint" | "decimal" | "numeric";
export interface SqlIr { op: string; type?: NumericType; [key: string]: unknown }
export interface TypedExpression { ir: SqlIr; type: string; nullable: boolean; column?: ColumnDef }
export interface Aggregate { node: SqlExpression; value: SqlExpression; fn: string; type: string }
export class UnsupportedCalculation extends Error {}
const fail = (reason: string): never => { throw new UnsupportedCalculation(reason); };
export const numeric = (t: string): t is NumericType => ["int32", "bigint", "decimal", "numeric"].includes(t);
export const integer = (t: string) => t === "int32" || t === "bigint";
export const typeName = (t: SqlDataType) => `${t.name.toLowerCase()}${t.precision === undefined ? "" : `(${t.precision}${t.scale === undefined ? "" : `,${t.scale}`})`}`;
export function scalarType(name: string, dialect?: Catalog["dialect"]): string {
  const n = name.toLowerCase().replace(/\s+/g, "");
  if (/^(int|integer)$/.test(n)) return "int32";
  if (n === "bigint") return "bigint";
  if (/^(decimal|numeric)(\(|$)/.test(n)) return dialect === "postgresql" ? "numeric" : dialect === "tsql" ? "decimal" : n.startsWith("decimal") ? "decimal" : "numeric";
  if (n === "timestampwithtimezone") return "timestamptz";
  if (n === "timestampwithouttimezone") return "timestamp";
  if (/^(date|timestamp|timestamptz|datetime|datetime2)(\(|$)/.test(n)) return n.split("(")[0]!;
  if (/^(text|n?varchar|n?char)(\(|$)/.test(n)) return "string";
  return fail("unsupported declared type");
}
export const lit = (value: string | number | null, type?: NumericType): SqlIr => ({ op: "lit", ...(type ? { type } : {}), value });
export const variable = (ref: string): SqlIr => ({ op: "var", ref });
export const binary = (o: string, l: SqlIr, r: SqlIr, type?: NumericType): SqlIr => ({ op: "bin", o, l, r, ...(type ? { type } : {}) });
export function walkSql(e: SqlExpression, f: (e: SqlExpression) => void): void {
  f(e);
  if (e.kind === "binary") { walkSql(e.left, f); walkSql(e.right, f); }
  else if (e.kind === "between") { walkSql(e.expression, f); walkSql(e.lower, f); walkSql(e.upper, f); }
  else if (e.kind === "unary") walkSql(e.operand, f);
  else if (e.kind === "cast") walkSql(e.expression, f);
  else if (e.kind === "call") for (const a of e.args) walkSql(a, f);
}

/** Source-bound lowering state. Binder numbers follow expression traversal, after declared inputs. */
export class CalculationLowering {
  readonly values = new Map<SqlExpression, TypedExpression>();
  readonly aggregates: Aggregate[] = [];
  readonly columns = new Map<SqlExpression, ColumnDef>();
  readonly inputs: Record<string, unknown>[];
  readonly table?: TableDef;
  private nextBinder: number;
  private readonly c: SqlRoutineCalculation;
  private readonly catalog: Catalog;
  private readonly tableRef?: string;

  constructor(catalog: Catalog, c: SqlRoutineCalculation, key: (name: string) => string) {
    this.c = c; this.catalog = catalog;
    this.inputs = c.parameters.map((p, i) => ({ ref: `$${i}`, type: ["int", "integer"].includes(p.type.name) ? "int32" : typeName(p.type) }));
    if (c.query) {
      this.table = catalog.objects.get(lookupKey(c.dialect, c.query.table));
      if (!this.table || this.table.kind !== "Table") fail("unresolved calculation source table");
      this.tableRef = `$${this.inputs.length}`;
      this.inputs.push({ ref: this.tableRef, type: "table", origin: key(this.table!.name) });
    }
    for (const p of c.parameters) this.validateType(typeName(p.type));
    if (this.table) for (const col of this.table.columns.values()) this.validateType(col.signature.replace(/\s+(?:NOT\s+)?NULL$/i, ""));
    this.nextBinder = this.inputs.length;
  }

  private validateType(name: string): void {
    if (/^(smallint|tinyint)\b/i.test(name)) fail("narrow declared integer types are unsupported");
    const constrained = /^(decimal|numeric)\s*\((\d+)(?:\s*,\s*(\d+))?\)$/i.exec(name);
    if (constrained) {
      const p = Number(constrained[2]), scale = Number(constrained[3] ?? 0);
      if (p < 1 || scale < 0 || scale > p || (this.c.dialect === "tsql" && p > 38) || p > 1000) fail("invalid or unsupported decimal precision and scale");
    }
  }
  builtin(name: string): string { return `sql|@builtin:${this.c.dialect}|${name}`; }
  private result(node: SqlExpression, ir: SqlIr, type: string, nullable = false, column?: ColumnDef): TypedExpression {
    const value = { ir, type, nullable, ...(column ? { column } : {}) };
    this.values.set(node, value); return value;
  }
  private call(name: string, args: SqlIr[], type?: NumericType): SqlIr {
    return { op: "call", target: this.builtin(name), args, ...(type ? { type } : {}) };
  }
  private same(a: string, b: string): boolean { return lookupKey(this.c.dialect, a) === lookupKey(this.c.dialect, b); }
  private reference(node: Extract<SqlExpression, { kind: "reference" }>, bind?: string, predicate = false): TypedExpression {
    const last = node.parts.at(-1)!;
    const parameter = node.parts.length === 1 ? this.c.parameters.findIndex((p) => this.same(p.name, last)) : -1;
    const column = this.table && [...this.table.columns.values()].find((c) => this.same(c.name, last));
    if (parameter >= 0 && column) fail("ambiguous parameter and column reference");
    if (parameter >= 0) return this.result(node, variable(`$${parameter}`), scalarType(this.c.parameters[parameter]!.type.name, this.c.dialect), true);
    if (!column || !bind || !this.c.query) return fail("unresolved calculation reference");
    if (node.parts.length > 1) {
      const qualifier = node.parts.slice(0, -1).join(".");
      const expected = this.c.query.alias ?? this.c.query.table;
      if (!this.same(qualifier, expected) && !(this.c.query.alias === undefined && this.same(qualifier, this.c.query.table.split(".").at(-1)!))) fail("unresolved column qualifier");
    }
    const type = scalarType(column.signature.replace(/\s+(?:NOT\s+)?NULL$/i, ""), this.c.dialect);
    this.columns.set(node, column);
    return this.result(node, { op: "member", of: variable(bind), name: column.name, ...(!predicate && numeric(type) ? { type } : {}) }, type, !/ NOT NULL$/i.test(column.signature), column);
  }

  lower(node: SqlExpression, bind?: string, predicate = false): TypedExpression {
    if (node.kind === "number") {
      const v = node.value;
      if (/^\d+$/.test(v) && BigInt(v) <= 2147483647n) return this.result(node, lit(Number(v), "int32"), "int32");
      if (this.c.dialect === "postgresql" && /^\d+$/.test(v) && BigInt(v) <= 9223372036854775807n) return this.result(node, lit(v, "bigint"), "bigint");
      if (!/^\d+(?:\.\d*)?(?:[eE][+-]?\d+)?$/.test(v)) return fail("unsupported numeric literal");
      if (/[eE]/.test(v)) return fail("approximate numeric literal semantics are unsupported");
      const type = this.c.dialect === "tsql" ? "decimal" : "numeric";
      if (v.replace(".", "").length > 38 && this.c.dialect === "tsql") fail("numeric literal exceeds decimal precision");
      return this.result(node, lit(v, type), type);
    }
    if (node.kind === "string") return this.result(node, lit(node.value), "string");
    if (node.kind === "null") return this.result(node, lit(null), "null", true);
    if (node.kind === "reference") return this.reference(node, bind, predicate);
    if (node.kind === "unary") {
      const a = this.lower(node.operand, bind, predicate);
      if (!numeric(a.type)) return fail("unary arithmetic requires numeric input");
      return this.result(node, node.operator === "+" ? a.ir : binary("-", lit(0, "int32"), a.ir, a.type), a.type, a.nullable);
    }
    if (node.kind === "between") {
      const x = this.lower(node.expression, bind, true), lo = this.lower(node.lower, bind, true), hi = this.lower(node.upper, bind, true);
      this.compare(x.type, lo.type); this.compare(x.type, hi.type);
      return this.result(node, { op: "logic", o: "and", args: [{ op: "cmp", o: ">=", l: x.ir, r: lo.ir }, { op: "cmp", o: "<=", l: x.ir, r: hi.ir }] }, "boolean", x.nullable || lo.nullable || hi.nullable);
    }
    if (node.kind === "binary") return this.lowerBinary(node, bind, predicate);
    if (node.kind === "cast") {
      const a = this.lower(node.expression, bind, predicate), target = scalarType(node.type.name, this.c.dialect);
      if (["smallint", "tinyint"].includes(node.type.name)) return fail("narrow integer cast is unsupported");
      if (node.type.precision !== undefined || node.type.scale !== undefined) return fail("constrained cast precision and scale cannot be represented faithfully");
      if (!numeric(target) || !numeric(a.type) || !(target === a.type || (a.type === "int32" && target === "bigint") || (integer(a.type) && target === "numeric" && this.c.dialect === "postgresql"))) return fail("lossy or unsupported cast");
      return this.result(node, { op: "conv", to: target, e: a.ir }, target, a.nullable);
    }
    return this.lowerCall(node, bind, predicate);
  }

  private compare(a: string, b: string) {
    if (a === b || a === "null" || b === "null" || (numeric(a) && numeric(b)) || (/^(date|timestamp|timestamptz|datetime2?)$/.test(a) && /^(date|timestamp|timestamptz|datetime2?)$/.test(b))) return;
    fail("unsupported comparison coercion");
  }
  private promote(a: string, b: string): NumericType {
    if (!numeric(a) || !numeric(b)) return fail("arithmetic requires established numeric types");
    return a === "numeric" || b === "numeric" ? "numeric" : a === "decimal" || b === "decimal" ? "decimal" : a === "bigint" || b === "bigint" ? "bigint" : "int32";
  }
  private lowerBinary(node: Extract<SqlExpression, { kind: "binary" }>, bind?: string, predicate = false): TypedExpression {
    const comparison = !["+", "-", "*", "/"].includes(node.operator);
    const l = this.lower(node.left, bind, predicate || comparison), r = this.lower(node.right, bind, predicate || comparison);
    const nullable = l.nullable || r.nullable;
    if (node.operator === "AND") {
      if (l.type !== "boolean" || r.type !== "boolean") return fail("AND requires predicates");
      const flatten = (e: SqlIr): SqlIr[] => e.op === "logic" && e.o === "and" ? e.args as SqlIr[] : [e];
      return this.result(node, { op: "logic", o: "and", args: [...flatten(l.ir), ...flatten(r.ir)] }, "boolean", nullable);
    }
    if (comparison) {
      this.compare(l.type, r.type);
      return this.result(node, { op: "cmp", o: node.operator === "=" ? "==" : ["<>", "!="].includes(node.operator) ? "!=" : node.operator, l: l.ir, r: r.ir }, "boolean", nullable);
    }
    if (this.c.dialect === "postgresql" && l.type === "date" && r.type === "int32" && ["+", "-"].includes(node.operator)) return this.result(node, binary(node.operator, l.ir, r.ir), "date", nullable);
    const type = this.promote(l.type, r.type);
    return this.result(node, binary(node.operator, l.ir, r.ir, type), type, nullable);
  }

  private lowerCall(node: Extract<SqlExpression, { kind: "call" }>, bind?: string, predicate = false): TypedExpression {
    const name = node.name.toUpperCase();
    if (node.distinct && name !== "COUNT") return fail("unsupported DISTINCT aggregate");
    if (name === "SUM" || name === "COUNT") {
      if (!this.tableRef || bind || node.args.length !== 1 || (name === "COUNT" && !node.distinct)) return fail("unsupported aggregate shape");
      const binder = `$${this.nextBinder++}`;
      const value = this.lower(node.args[0]!, binder);
      if (name === "SUM" && !numeric(value.type)) return fail("SUM requires numeric input");
      const type = name === "COUNT" ? (this.c.dialect === "postgresql" ? "bigint" : "int32") : this.c.dialect === "postgresql" && value.type === "bigint" ? "numeric" : this.c.dialect === "postgresql" && value.type === "int32" ? "bigint" : value.type;
      if (!numeric(type)) return fail("unsupported aggregate result type");
      const filter = this.c.query?.where ? this.lower(this.c.query.where, binder, true) : undefined;
      if (filter && filter.type !== "boolean") return fail("WHERE requires a predicate");
      const fn = name === "SUM" ? "sum" : "countDistinct";
      this.aggregates.push({ node, value: node.args[0]!, fn, type });
      return this.result(node, { op: "agg", fn, over: variable(this.tableRef), bind: binder, value: value.ir, ...(filter ? { filter: filter.ir } : {}), type }, type, name === "SUM");
    }
    if (name === "DATEDIFF") {
      const [unit, from, to] = node.args;
      if (this.c.dialect !== "tsql" || node.args.length !== 3 || !(unit?.kind === "reference" && unit.parts.length === 1 && unit.parts[0]!.toLowerCase() === "day")) return fail("unsupported DATEDIFF signature");
      const a = this.lower(from!, bind), b = this.lower(to!, bind);
      if (a.type !== "date" || b.type !== "date") return fail("DATEDIFF requires declared date inputs");
      return this.result(node, this.call("DATEDIFF", [lit("day"), a.ir, b.ir], "int32"), "int32", a.nullable || b.nullable);
    }
    const args = node.args.map((a) => this.lower(a, bind, predicate));
    if (name === "DATE_TRUNC") {
      if (this.c.dialect !== "postgresql" || args.length !== 2 || node.args[0]?.kind !== "string" || node.args[0].value !== "week" || !["timestamp", "timestamptz"].includes(args[1]!.type)) return fail("unsupported date_trunc signature");
      return this.result(node, this.call("date_trunc", args.map((a) => a.ir)), args[1]!.type, args[1]!.nullable);
    }
    if (name === "ROUND") {
      const [value, digits] = args;
      if (args.length !== 2 || !value || !["numeric", "decimal"].includes(value.type) || digits?.type !== "int32" || node.args[1]?.kind !== "number" || !Number.isInteger(Number(node.args[1].value))) return fail("unsupported ROUND signature");
      if (Number(node.args[1].value) > 38 && this.c.dialect === "tsql") return fail("ROUND digits outside supported precision");
      return this.result(node, { op: "round", mode: "halfAwayFromZero", e: value.ir, digits: digits.ir, target: this.builtin("ROUND") }, value.type, value.nullable);
    }
    if (name === "NULLIF") {
      if (args.length !== 2) return fail("unsupported NULLIF signature");
      this.compare(args[0]!.type, args[1]!.type);
      const type = args[0]!.type;
      return this.result(node, this.call("NULLIF", args.map((a) => a.ir), numeric(type) ? type : undefined), type, true);
    }
    if (name === "ISNULL" || name === "COALESCE") {
      if ((name === "ISNULL" && (this.c.dialect !== "tsql" || args.length !== 2)) || args.length < 2) return fail("unsupported null replacement signature");
      if (name === "ISNULL" && ["decimal", "numeric"].includes(args[0]!.type) && !(node.args[1]?.kind === "number" && Number(node.args[1].value) === 0)) return fail("ISNULL replacement precision is not established");
      const real = args.filter((a) => a.type !== "null");
      if (!real.length) return fail("untyped null replacement");
      let type = real[0]!.type;
      for (const a of real.slice(1)) if (a.type !== type) type = this.promote(type, a.type);
      if (name === "ISNULL" && type !== args[0]!.type) return fail("ISNULL replacement may truncate");
      return this.result(node, { op: "coalesce", args: args.map((a) => a.ir) }, type, args.every((a) => a.nullable));
    }
    return fail("unsupported SQL calculation function");
  }
}
