import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import type { Span } from "../src/contracts/records.ts";
import { canonicalJson, sha256, spanText } from "../src/extract/builder.ts";
import { extractSql } from "../src/sql/extractor.ts";

type Dialect = "tsql" | "postgresql";
interface Expr { op: string; [key: string]: unknown }
interface Attribute { status: string; value?: unknown; detail?: Record<string, unknown>; evidence?: string[] }
interface Rec { kind: string; id?: string; symbolKey?: string; nodeKind?: string; name?: string; code?: string; relation?: string; status?: string; evidence?: string[]; path?: string; span?: Span; snippetSha256?: string }
interface Calc extends Rec {
  expr: Expr; irHash: string; output: { type: string; unit?: Attribute };
  inputs: { ref: string; type: string; origin?: string; unit?: Attribute }[];
  attributes: Record<string, Attribute>;
}

function run(files: Record<string, string>) {
  const root = mkdtempSync(join(tmpdir(), "ce-sql-calculations-"));
  try {
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), text);
    }
    const units = extractSql({ source: root, repository: "synthetic", commit: "a".repeat(40) });
    assert.equal(units.length, 1);
    const unit = units[0]!;
    assert.deepEqual(validateArtifact(unit.jsonl).problems, []);
    const records = unit.jsonl.trimEnd().split("\n").map((line) => JSON.parse(line) as Rec);
    // Independently check source bytes, not merely evidence-reference validity.
    for (const record of records.filter((r) => r.kind === "evidence")) {
      const text = files[record.path!];
      assert.ok(text !== undefined, `evidence file ${record.path}`);
      const snippet = spanText(text.replace(/\r\n/g, "\n"), record.span!);
      assert.ok(snippet.length > 0, "evidence must cite nonempty source");
      assert.equal(record.snippetSha256, sha256(snippet));
    }
    return records;
  } finally { rmSync(root, { recursive: true, force: true }); }
}

function files(dialect: Dialect, body: string, parameters = "", returns = "numeric"): Record<string, string> {
  if (dialect === "tsql") return {
    "db/schema.sql": "CREATE TABLE dbo.[Ledger] ([Amount] decimal(18,3) NULL, [Quantity] int NOT NULL, [SoldAt] date NOT NULL, [Currency] char(3) NOT NULL, [AmountOre] bigint NOT NULL);\nGO\n",
    "db/functions/calculate.sql": `CREATE FUNCTION dbo.Calculate(${parameters}) RETURNS TABLE AS RETURN ${body};\nGO\n`,
  };
  return {
    "db/migrations/001_schema.sql": 'CREATE TABLE public."Ledger" ("Amount" numeric(18,3) NULL, "Quantity" integer NOT NULL, "SoldAt" timestamptz NOT NULL, "Currency" char(3) NOT NULL, "AmountOre" bigint NOT NULL);\n',
    "db/migrations/002_calculate.sql": `CREATE FUNCTION public.calculate(${parameters}) RETURNS ${returns} LANGUAGE sql AS $$ ${body} $$;\n`,
  };
}

function calculation(records: Rec[]): Calc {
  const cs = records.filter((r) => r.kind === "calculation") as Calc[];
  assert.equal(cs.length, 1, "supported routine emits exactly one calculation");
  const c = cs[0]!;
  assert.equal(c.irHash, sha256(canonicalJson(c.expr)));
  assert.ok(c.evidence!.length > 0);
  for (const a of Object.values(c.attributes)) {
    if (a.status === "known") assert.ok(a.evidence?.length, "known attributes require source evidence");
    if (a.status === "unknown") assert.equal(a.value, undefined, "unknown carries no invented value");
  }
  return c;
}

function expressions(value: unknown): Expr[] {
  if (!value || typeof value !== "object") return [];
  if (Array.isArray(value)) return value.flatMap(expressions);
  const object = value as Record<string, unknown>;
  return [...(typeof object.op === "string" ? [object as Expr] : []), ...Object.values(object).flatMap(expressions)];
}

function rejected(records: Rec[], graph = false) {
  assert.equal(records.filter((r) => r.kind === "calculation").length, 0, "unsupported source must not invent a formula");
  assert.ok(records.some((r) => r.kind === "node" && r.nodeKind === "Routine"), "routine graph survives");
  assert.ok(records.some((r) => r.kind === "node" && r.nodeKind === "Table"), "table graph survives");
  assert.ok(records.some((r) => r.kind === "diagnostic" && /CALCULATION/.test(r.code ?? "") && r.evidence?.length), "rejection has a source-bound calculation diagnostic");
  if (graph) assert.ok(records.some((r) => r.kind === "edge" && r.relation === "READS"), "known read graph survives");
}

for (const d of ["tsql", "postgresql"] as const) {
  const x = d === "tsql" ? "@x" : "p_x";
  const table = d === "tsql" ? "dbo.[Ledger]" : 'public."Ledger"';
  const q = (name: string) => d === "tsql" ? `[${name}]` : `"${name}"`;

  test(`${d}: parameter order, precedence and integer division are preserved`, () => {
    const c = calculation(run(files(d, `SELECT (${x} + 2) * 3 - 7 / 2 AS answer`, `${x} int`, "integer")));
    assert.deepEqual(c.inputs.map(({ ref, type }) => ({ ref, type })), [{ ref: "$0", type: "int32" }]);
    assert.equal(c.output.type, "int32");
    assert.equal(c.expr.op, "bin");
    assert.equal(c.expr.o, "-");
    const left = c.expr.l as Expr;
    assert.equal(left.o, "*");
    assert.equal((left.l as Expr).o, "+");
    assert.equal(((left.l as Expr).l as Expr).ref, "$0");
    const division = c.expr.r as Expr;
    assert.equal(division.o, "/");
    assert.equal(division.type, "int32", "SQL integer division cannot become floating division");
    assert.equal((division.l as Expr).value, 7);
    assert.equal((division.r as Expr).value, 2);
  });

  test(`${d}: parameter declarations determine inputs even when references occur in reverse order`, () => {
    const left = d === "tsql" ? "@left" : "p_left";
    const right = d === "tsql" ? "@right" : "p_right";
    const c = calculation(run(files(d, `SELECT ${right} - ${left}`, `${left} int, ${right} int`, "integer")));
    assert.deepEqual(c.inputs.map(({ ref, type }) => ({ ref, type })), [{ ref: "$0", type: "int32" }, { ref: "$1", type: "int32" }]);
    assert.equal(c.expr.o, "-");
    assert.equal((c.expr.l as Expr).ref, "$1");
    assert.equal((c.expr.r as Expr).ref, "$0");
  });

  test(`${d}: quoted aliases, SUM, null-as-zero, filters and ROUND retain semantics`, () => {
    const row = q("entry");
    const amount = `${row}.${q("Amount")}`;
    const coalesce = d === "tsql" ? "ISNULL" : "COALESCE";
    const c = calculation(run(files(d,
      `SELECT ROUND(SUM(${coalesce}(${amount}, 0)) / NULLIF(${x}, 0), 2) AS ${q("AmountOre")} FROM ${table} AS ${row} WHERE ${row}.${q("Quantity")} BETWEEN 1 AND 5`,
      `${x} int`)));
    const nodes = expressions(c.expr);
    assert.ok(nodes.some((e) => e.op === "agg" && e.fn === "sum" && e.filter));
    assert.ok(nodes.some((e) => e.op === "coalesce"));
    assert.ok(nodes.some((e) => e.op === "member" && e.name === "Amount"));
    assert.ok(nodes.some((e) => e.op === "cmp" && e.o === ">="));
    assert.ok(nodes.some((e) => e.op === "cmp" && e.o === "<="));
    assert.equal(c.expr.op, "round");
    assert.equal(c.expr.mode, "halfAwayFromZero");
    assert.equal((c.expr.digits as Expr).value, 2);
    assert.equal(c.output.type, d === "tsql" ? "decimal" : "numeric");
    assert.equal(c.inputs[0]!.ref, "$0");
    assert.equal(c.inputs[1]!.ref, "$1");
    assert.equal(c.inputs[1]!.type, "table");
    assert.ok(c.inputs[1]!.origin?.endsWith("|" + (d === "tsql" ? "dbo.Ledger" : "public.Ledger")));
    assert.equal(c.attributes.nullHandling!.value, "nullAsZero");
    assert.equal(c.attributes.zeroDenominator!.value, "returnsNull");
    assert.equal(c.attributes.rounding!.detail!.digits, 2);
    assert.match(String(c.attributes.rounding!.detail!.api), /ROUND/);
    assert.deepEqual(c.output.unit, { status: "unknown" });
    assert.deepEqual(c.attributes.currency, { status: "unknown" });
    for (const input of c.inputs) if (input.unit) assert.deepEqual(input.unit, { status: "unknown" });
  });

  test(`${d}: COUNT DISTINCT and conjunction filters preserve aggregate binders`, () => {
    const c = calculation(run(files(d,
      `SELECT COUNT(DISTINCT e.${q("Quantity")}) FROM ${table} e WHERE e.${q("Quantity")} >= 1 AND e.${q("Quantity")} < 4`, "", d === "tsql" ? "integer" : "bigint")));
    assert.equal(c.expr.op, "agg");
    assert.equal(c.expr.fn, "countDistinct");
    assert.equal(c.expr.type, d === "tsql" ? "int32" : "bigint");
    assert.equal((c.expr.over as Expr).ref, "$0");
    assert.equal(((c.expr.value as Expr).of as Expr).ref, c.expr.bind);
    const filters = expressions(c.expr.filter);
    assert.ok(filters.some((e) => e.op === "cmp" && e.o === ">="));
    assert.ok(filters.some((e) => e.op === "cmp" && e.o === "<"));
  });

  test(`${d}: exact fractional lexemes and unary signs survive lowering`, () => {
    const c = calculation(run(files(d, "SELECT -12345678901234567890.1250 + 0.0001")));
    const literals = expressions(c.expr).filter((e) => e.op === "lit");
    assert.ok(literals.some((e) => String(e.value).includes("12345678901234567890.1250")));
    assert.ok(literals.some((e) => e.value === "0.0001"));
    assert.ok(expressions(c.expr).some((e) => e.op === "bin" && e.o === "-" && (e.l as Expr).value === 0)
      || literals.some((e) => String(e.value).startsWith("-12345678901234567890.1250")), "unary minus must remain observable");
  });

  test(`${d}: lossless widening casts retain conversion, lossy precision/scale casts reject`, () => {
    const c = calculation(run(files(d, `SELECT CAST(${x} AS bigint)`, `${x} int`, "bigint")));
    assert.equal(c.expr.op, "conv");
    assert.equal(c.expr.to, "bigint");
    assert.equal(c.output.type, "bigint");
    for (const type of ["decimal(5,2)", "decimal(2,0)"]) {
      rejected(run(files(d, `SELECT CAST(123.456 AS ${type})`)));
    }
  });

  test(`${d}: bigint multiplication reports throwing overflow, never silent wrapping`, () => {
    const c = calculation(run(files(d, `SELECT SUM(e.${q("AmountOre")} * e.${q("Quantity")}) FROM ${table} e`)));
    const multiplication = expressions(c.expr).find((e) => e.op === "bin" && e.o === "*")!;
    assert.equal(multiplication.type, "bigint");
    assert.equal(c.attributes.overflow!.value, "throws");
    assert.ok(c.attributes.overflow!.evidence!.length);
    assert.equal(c.attributes.nullHandling!.value, "notApplicable", "all referenced columns are declared NOT NULL");
  });

  test(`${d}: nullable SUM cannot be reported as null-as-zero or nonnullable`, () => {
    const c = calculation(run(files(d, `SELECT SUM(e.${q("Amount")}) FROM ${table} e`)));
    assert.notEqual(c.attributes.nullHandling!.value, "nullAsZero");
    assert.notEqual(c.attributes.nullHandling!.value, "notApplicable");
  });

  test(`${d}: integer literal promotion boundaries retain dialect types`, () => {
    const maxInt = calculation(run(files(d, "SELECT 2147483647", "", "integer")));
    assert.equal(maxInt.expr.type, "int32");
    assert.equal(maxInt.expr.value, 2147483647);
    const promoted = calculation(run(files(d, "SELECT 2147483648", "", d === "tsql" ? "decimal" : "bigint")));
    assert.equal(promoted.expr.type, d === "tsql" ? "decimal" : "bigint");
    assert.equal(String(promoted.expr.value), "2147483648");
    const beyondBigint = calculation(run(files(d, "SELECT 9223372036854775808")));
    assert.equal(beyondBigint.expr.type, d === "tsql" ? "decimal" : "numeric");
    assert.equal(String(beyondBigint.expr.value), "9223372036854775808", "large integer cannot round through a JS number");
  });

  test(`${d}: names and scaling never establish a monetary unit or currency`, () => {
    const c = calculation(run(files(d, `SELECT SUM(e.${q("AmountOre")}) / 100.0 FROM ${table} e WHERE e.${q("Currency")} = 'SEK'`)));
    assert.deepEqual(c.output.unit, { status: "unknown" });
    assert.deepEqual(c.attributes.currency, { status: "unknown" });
  });

  const negatives = [
    ["join", `SELECT SUM(a.${q("Quantity")}) FROM ${table} a JOIN ${table} b ON a.${q("Quantity")} = b.${q("Quantity")}`],
    ["ambiguous column", `SELECT SUM(${q("Quantity")}) FROM ${table} a, ${table} b`],
    ["missing column", `SELECT SUM(e.${q("Missing")}) FROM ${table} e`],
    ["unknown function", `SELECT mystery(e.${q("Quantity")}) FROM ${table} e`],
    ["unsupported boolean OR", `SELECT SUM(e.${q("Quantity")}) FROM ${table} e WHERE e.${q("Quantity")} = 1 OR e.${q("Quantity")} = 2`],
    ["SUM DISTINCT", `SELECT SUM(DISTINCT e.${q("Quantity")}) FROM ${table} e`],
    ["round mode", "SELECT ROUND(1.25, 1, 1)"],
    ["date signature", d === "tsql" ? "SELECT DATEDIFF(month, '2020-01-01', '2020-02-01')" : "SELECT date_trunc('fortnight', '2020-01-01')"],
    ["multiple statements", "SELECT 1; SELECT 2"],
    ["malformed number", "SELECT 1.2.3"],
    ["unterminated literal", "SELECT 'unfinished"],
    ["depth limit", `SELECT ${"(".repeat(100)}1${")".repeat(100)}`],
    ["token limit", `SELECT ${Array.from({ length: 2500 }, () => "1").join(" + ")}`],
  ];
  for (const [name, body] of negatives) test(`${d}: rejects ${name} with evidence and preserves graph`, () => {
    const input = files(d, body!);
    if (d === "tsql" && name === "multiple statements") {
      // Both RETURN statements belong to this routine; a statement following an
      // inline table-valued declaration would instead be an unrelated batch item.
      input["db/functions/calculate.sql"] = "CREATE FUNCTION dbo.Calculate() RETURNS int AS BEGIN RETURN 1; RETURN 2; END;\nGO\n";
    }
    const records = run(input);
    rejected(records, ["join", "ambiguous column", "missing column", "unknown function", "unsupported boolean OR", "SUM DISTINCT"].includes(name!));
    assert.equal(records.at(-1)!.status, "full", "unsupported calculation alone does not mean incomplete graph coverage");
  });
}

test("T-SQL DATEDIFF day and integer period division preserve inclusive date bounds", () => {
  const c = calculation(run(files("tsql", "SELECT SUM(e.[Quantity]) / NULLIF(DATEDIFF(day, @from, @to) / 7 + 1, 0) FROM dbo.[Ledger] e WHERE e.[SoldAt] BETWEEN @from AND @to", "@from date, @to date")));
  const nodes = expressions(c.expr);
  assert.ok(nodes.some((e) => e.op === "call" && String(e.target).endsWith("|DATEDIFF")));
  assert.ok(nodes.some((e) => e.op === "bin" && e.o === "/" && e.type === "int32" && (e.r as Expr).value === 7));
  assert.equal(c.attributes.timeBucket!.value, "sevenDayFromPeriodStart");
  assert.match(String(c.attributes.period!.detail!.bounds), /inclusive/);
});

test("PostgreSQL date_trunc DISTINCT uses session-sensitive weeks and half-open date bounds", () => {
  const c = calculation(run(files("postgresql", 'SELECT COUNT(DISTINCT date_trunc(\'week\', e."SoldAt")) FROM public."Ledger" e WHERE e."SoldAt" >= p_from AND e."SoldAt" < p_to + 1', "p_from date, p_to date", "bigint")));
  assert.equal(c.expr.fn, "countDistinct");
  assert.equal(c.attributes.timeBucket!.value, "isoWeek");
  assert.match(String(c.attributes.timeBucket!.detail!.note), /TimeZone/);
  assert.match(String(c.attributes.period!.detail!.bounds), /half-open/);
});

test("procedural and dynamic bodies preserve ordinary graph and dynamic coverage", () => {
  for (const [d, source] of [
    ["tsql", "CREATE FUNCTION dbo.Calculate(@x int) RETURNS int AS BEGIN DECLARE @y int = @x; RETURN @y + 1; END;"],
    ["postgresql", "CREATE FUNCTION public.calculate(p_x int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN p_x + 1; END; $$;"],
    ["tsql", "CREATE PROCEDURE dbo.Calculate AS BEGIN EXEC ('SELECT Quantity FROM dbo.Ledger'); END;"],
    ["postgresql", "CREATE FUNCTION public.calculate() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'SELECT Quantity FROM public.Ledger'; END; $$;"],
  ] as const) {
    const input = files(d, "SELECT 1");
    const routinePath = Object.keys(input).find((p) => p.includes("calculate"))!;
    input[routinePath] = source;
    const records = run(input);
    assert.equal(records.filter((r) => r.kind === "calculation").length, 0);
    assert.ok(records.some((r) => r.nodeKind === "Routine"));
    assert.ok(records.some((r) => r.kind === "diagnostic" && /CALCULATION|DYNAMIC/.test(r.code ?? "")));
    if (/EXEC(?:UTE)?\s/.test(source)) {
      assert.equal(records.at(-1)!.status, "partial");
      assert.ok(records.some((r) => r.code === "SQL_DYNAMIC"));
    }
  }
});
