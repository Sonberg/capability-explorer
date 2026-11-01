import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import { extractSql } from "../src/sql/extractor.ts";

interface Record { kind: string; symbolKey?: string; expr?: unknown; irHash?: string; inputHash?: string; path?: string }

function extract(files: [string, string][]) {
  const root = mkdtempSync(join(tmpdir(), "ce-independent-sql-root-"));
  try {
    for (const [path, text] of files) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), text);
    }
    const units = extractSql({ source: root, repository: "same-explicit-input", commit: "b".repeat(40) });
    for (const unit of units) {
      assert.deepEqual(validateArtifact(unit.jsonl).problems, []);
      assert.ok(!unit.jsonl.includes(root), "temporary machine root must never enter artifacts");
      assert.doesNotMatch(unit.jsonl, /(?:\/private\/tmp\/|\/Users\/|[A-Z]:\\\\)/);
      assert.doesNotMatch(unit.jsonl, /"(?:timestamp|createdAt|generatedAt|uuid|randomId)"\s*:/);
      assert.doesNotMatch(unit.jsonl, /\b[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}/);
    }
    return { units, records: units.flatMap((u) => u.jsonl.trimEnd().split("\n").map((s) => JSON.parse(s) as Record)) };
  } finally { rmSync(root, { recursive: true, force: true }); }
}

for (const dialect of ["tsql", "postgresql"] as const) {
  const fixture = (name = "calculate", literal = "100.0", cosmetic = false): [string, string][] => {
    if (dialect === "tsql") return [
      ["one/db/tables/Ledger.sql", "CREATE TABLE dbo.Ledger (Amount decimal(18,3) NOT NULL, Id int NOT NULL);\nGO\n"],
      ["one/db/functions/Calculate.sql", `CREATE FUNCTION dbo.${name}(@x int) RETURNS TABLE AS RETURN\n${cosmetic ? "-- harmless layout/comment change\n select   " : "SELECT "}SUM(${cosmetic ? "[e].[Amount]" : "e.Amount"}) / ${literal} FROM dbo.Ledger ${cosmetic ? "AS [e]" : "e"} WHERE e.Id = @x;\nGO\n`],
      ["two/db/schema.sql", "CREATE TABLE dbo.Other (Value int NOT NULL);\nGO\nCREATE FUNCTION dbo.OtherTotal() RETURNS TABLE AS RETURN SELECT SUM(o.Value) FROM dbo.Other o;\nGO\n"],
    ];
    return [
      ["one/db/migrations/001_schema.sql", "CREATE TABLE ledger (amount bigint NOT NULL, id integer NOT NULL);\n"],
      ["one/db/migrations/002_calculate.sql", `CREATE FUNCTION ${name}(p_x integer) RETURNS numeric LANGUAGE sql AS $$\n${cosmetic ? "-- harmless layout/comment change\n select   " : "SELECT "}SUM(${cosmetic ? '"e"."amount"' : "e.amount"}) / ${literal} FROM ledger ${cosmetic ? 'AS "e"' : "e"} WHERE e.id = p_x\n$$;\n`],
      ["two/db/migrations/001_other.sql", "CREATE TABLE other (value integer NOT NULL);\nCREATE FUNCTION other_total() RETURNS bigint LANGUAGE sql AS $$ SELECT SUM(o.value) FROM other o $$;\n"],
    ];
  };

  test(`${dialect}: complete JSONL bytes match across independent roots and file creation orders`, () => {
    const source = fixture();
    const first = extract(source);
    const second = extract([...source].reverse());
    assert.equal(first.units.length, 2, "comparison includes independently discovered SQL units");
    assert.deepEqual(second.units, first.units);
    assert.deepEqual(extract([source[1]!, source[2]!, source[0]!]).units, first.units);
    assert.equal(first.records.filter((r) => r.kind === "calculation").length, 2, "determinism must exercise calculation emission");
  });

  test(`${dialect}: cosmetic edits and routine rename preserve IR/hash, literal mutations change them`, () => {
    const get = (files: [string, string][], name: string) => {
      const result = extract(files);
      const calculation = result.records.find((r) => r.kind === "calculation" && r.symbolKey?.endsWith(`|${dialect === "tsql" ? "dbo." : "public."}${name}`));
      assert.ok(calculation, `missing calculation ${name}`);
      assert.match(calculation.irHash!, /^[a-f0-9]{64}$/);
      return { calculation, header: result.records.find((r) => r.kind === "header")! };
    };
    const base = get(fixture(), "calculate");
    const cosmetic = get(fixture("calculate", "100.0", true), "calculate");
    const renamed = get(fixture("renamed"), "renamed");
    const changed = get(fixture("calculate", "101.0"), "calculate");
    for (const variant of [cosmetic, renamed]) {
      assert.deepEqual(variant.calculation.expr, base.calculation.expr);
      assert.equal(variant.calculation.irHash, base.calculation.irHash);
      assert.notEqual(variant.header.inputHash, base.header.inputHash, "source identity changes independently of semantic IR");
    }
    assert.notEqual(renamed.calculation.symbolKey, base.calculation.symbolKey);
    assert.notDeepEqual(changed.calculation.expr, base.calculation.expr);
    assert.notEqual(changed.calculation.irHash, base.calculation.irHash);
  });
}
