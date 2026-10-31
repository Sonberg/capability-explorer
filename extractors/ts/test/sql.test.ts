import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import { extractSql } from "../src/sql/extractor.ts";

interface Rec { kind: string; symbolKey?: string; nodeKind?: string; name?: string; signature?: string; from?: string; relation?: string; to?: string; resolution?: string; unresolvedReason?: string; props?: Record<string, unknown>; code?: string; status?: string }

function run(files: Record<string, string>) {
  const dir = mkdtempSync(join(tmpdir(), "ce-sql-"));
  try {
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(dir, path)), { recursive: true });
      writeFileSync(join(dir, path), text);
    }
    return extractSql({ source: dir, repository: "shop", commit: "0".repeat(40) }).map((r) => {
      assert.deepEqual(validateArtifact(r.jsonl).problems, [], r.unitPath);
      return { ...r, records: r.jsonl.trimEnd().split("\n").map((l) => JSON.parse(l) as Rec) };
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const short = (k: string) => k.replace(/^sql\|@unbound:[^|]*\|/, "");
const edges = (u: { records: Rec[] }, rel: string) => u.records.filter((r) => r.kind === "edge" && r.relation === rel).map((r) => `${short(r.from!)} -> ${r.to ? short(r.to) : r.props!.target}${r.resolution === "unresolved" ? " (unresolved)" : ""}`).sort();

test("a .sqlproj names the unit and decides its scripts; the project file is the evidence", () => {
  const [u] = run({
    "db/Shop.sqlproj": '<Project Sdk="Microsoft.Build.Sql/0.1.0"><ItemGroup><Build Remove="scratch/**" /><PostDeploy Include="post.sql" /></ItemGroup></Project>',
    "db/dbo/Orders.sql": "CREATE TABLE [dbo].[Orders] ([Id] int NOT NULL, [Total] decimal(10, 2) NULL);\nGO\n",
    "db/scratch/Tmp.sql": "CREATE TABLE dbo.Tmp (A int);",
    "db/post.sql": "INSERT INTO dbo.Orders (Id) VALUES (1);",
  });
  assert.equal(u!.unitName, "Shop");
  assert.equal(u!.unitPath, "db");
  const nodes = u!.records.filter((r) => r.kind === "node").map((r) => `${r.nodeKind} ${r.name}`).sort();
  assert.deepEqual(nodes, ["Column Id", "Column Total", "Project Shop", "Table dbo.Orders"]);
  assert.equal(u!.records.find((r) => r.nodeKind === "Column" && r.name === "Total")!.signature, "decimal(10, 2) NULL");
  assert.ok(!u!.records.some((r) => r.code === "SQL_UNIT_NAME_INFERRED"));
});

test("T-SQL: views, procedures, aliased UPDATE ... FROM, INSERT column lists, CTEs, subqueries and wildcards", () => {
  const [u] = run({
    "sql/schema.sql": [
      "CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL, Total decimal(10,2) NULL, Status nvarchar(20) NOT NULL);",
      "GO",
      "CREATE TABLE dbo.Customers (Id int NOT NULL, Name nvarchar(100) NOT NULL);",
      "GO",
      "CREATE TABLE dbo.Audit (OrderId int NOT NULL, At datetime2 NOT NULL);",
      "GO",
      "CREATE VIEW dbo.OpenOrders AS SELECT o.Id, o.Total FROM dbo.Orders AS o WHERE o.Status = 'open';",
      "GO",
      "CREATE PROCEDURE dbo.CloseOrders @CustomerId int",
      "AS",
      "BEGIN",
      "  SET NOCOUNT ON;",
      "  UPDATE o SET Status = 'closed'",
      "  FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId",
      "  WHERE c.Name = 'x' AND o.CustomerId = @CustomerId;",
      "  INSERT INTO dbo.Audit (OrderId, At) SELECT Id, SYSUTCDATETIME() FROM dbo.Orders WHERE Status = 'closed';",
      "  WITH big AS (SELECT Id FROM dbo.Orders WHERE Total > 100) SELECT b.Id FROM big b;",
      "  SELECT * FROM dbo.Customers c2;",
      "  DELETE FROM dbo.Missing WHERE Id = 1;",
      "END",
    ].join("\n"),
  });
  assert.equal(u!.records.find((r) => r.nodeKind === "Project")!.props!.dialect, "tsql");
  assert.equal(u!.records.find((r) => r.nodeKind === "View")!.name, "dbo.OpenOrders");
  assert.equal(u!.records.find((r) => r.name === "dbo.CloseOrders")!.props!.routineKind, "stored-procedure");
  assert.deepEqual(edges(u!, "READS").filter((e) => e.startsWith("dbo.OpenOrders")), [
    "dbo.OpenOrders -> dbo.Orders", "dbo.OpenOrders -> dbo.Orders#Id", "dbo.OpenOrders -> dbo.Orders#Status", "dbo.OpenOrders -> dbo.Orders#Total",
  ]);
  const proc = (rel: string) => edges(u!, rel).filter((e) => e.startsWith("dbo.CloseOrders"));
  assert.deepEqual(proc("WRITES"), [
    "dbo.CloseOrders -> dbo.Audit", "dbo.CloseOrders -> dbo.Audit#At", "dbo.CloseOrders -> dbo.Audit#OrderId",
    "dbo.CloseOrders -> dbo.Missing (unresolved)", "dbo.CloseOrders -> dbo.Orders", "dbo.CloseOrders -> dbo.Orders#Status",
  ]);
  const reads = proc("READS");
  for (const expected of [
    "dbo.Orders", "dbo.Orders#CustomerId", "dbo.Orders#Status", "dbo.Orders#Id", "dbo.Orders#Total",
    "dbo.Customers", "dbo.Customers#Id", "dbo.Customers#Name",
  ]) assert.ok(reads.includes(`dbo.CloseOrders -> ${expected}`), expected);
  // `@CustomerId` is a parameter, `big` is a CTE: neither is a table.
  assert.ok(!reads.some((e) => /big|@/.test(e)));
  const missing = u!.records.find((r) => r.kind === "edge" && r.resolution === "unresolved")!;
  assert.equal(missing.unresolvedReason, "missing-reference");
});

test("loose T-SQL scripts: the unit takes the folder above tables/ and functions/, and the name from the connection string", () => {
  const units = run({
    "database/tables/T.sql": "CREATE TABLE dbo.T (A int NOT NULL)",
    "database/functions/F.sql": "CREATE FUNCTION dbo.F(@x int) RETURNS TABLE AS RETURN SELECT t.A FROM dbo.T AS t WHERE t.A = @x",
    "app/appsettings.json": '{"ConnectionStrings":{"Main":"Server=s;Database=ShopDb;Integrated Security=true"}}',
  });
  assert.equal(units.length, 1);
  assert.equal(units[0]!.unitPath, "database");
  assert.equal(units[0]!.unitName, "ShopDb");
  assert.ok(units[0]!.records.some((r) => r.code === "SQL_UNIT_NAME_INFERRED"));
  assert.equal(units[0]!.records.find((r) => r.name === "dbo.F")!.props!.routineKind, "inline-table-valued-function");
});

test("PostgreSQL migrations are applied in order: ALTER TABLE, DROP COLUMN, DROP TABLE, CREATE OR REPLACE", () => {
  const [u] = run({
    "package.json": JSON.stringify({ name: "@shop/orders" }),
    "db/migrations/001_init.sql": [
      "CREATE SCHEMA shop;",
      "CREATE TABLE shop.orders (id bigserial PRIMARY KEY, customer text NOT NULL, legacy text, total numeric(10, 2));",
      "CREATE TABLE shop.old (a int);",
    ].join("\n"),
    "db/migrations/002_change.sql": [
      "ALTER TABLE shop.orders ADD COLUMN placed_at timestamptz NOT NULL DEFAULT now(), DROP COLUMN legacy;",
      "DROP TABLE IF EXISTS shop.old;",
      "CREATE TABLE shop.audit (order_id bigint NOT NULL, note text);",
    ].join("\n"),
    "db/migrations/003_fn.sql": [
      "CREATE FUNCTION shop.total_for(p_customer text) RETURNS numeric LANGUAGE sql IMMUTABLE AS $$ SELECT 0::numeric $$;",
      "CREATE OR REPLACE FUNCTION shop.total_for(p_customer text) RETURNS numeric LANGUAGE sql STABLE AS $$",
      "  SELECT COALESCE(SUM(o.total), 0) FROM shop.orders o WHERE o.customer = p_customer",
      "$$;",
      "CREATE FUNCTION shop.record(p_order bigint) RETURNS void LANGUAGE plpgsql AS $body$",
      "BEGIN",
      "  INSERT INTO shop.audit (order_id, note) VALUES (p_order, 'x') ON CONFLICT (order_id) DO UPDATE SET note = EXCLUDED.note;",
      "  PERFORM shop.total_for('a');",
      "END;",
      "$body$;",
    ].join("\n"),
  });
  assert.equal(u!.unitName, "orders-db");
  assert.equal(u!.unitPath, "db");
  const project = u!.records.find((r) => r.nodeKind === "Project")!;
  assert.deepEqual(project.props, { dialect: "postgresql", migrations: true });
  const keys = u!.records.filter((r) => r.kind === "node").map((r) => short(r.symbolKey!));
  assert.ok(keys.includes("shop.orders#placed_at") && !keys.includes("shop.orders#legacy") && !keys.includes("shop.old"));
  assert.equal(u!.records.find((r) => r.name === "placed_at")!.signature, "timestamptz NOT NULL");
  assert.equal(u!.records.find((r) => r.name === "id")!.signature, "bigserial NOT NULL");
  const fn = u!.records.find((r) => r.name === "shop.total_for")!;
  assert.deepEqual(fn.props, { routineKind: "sql-function", volatility: "STABLE" });
  assert.deepEqual(edges(u!, "READS").filter((e) => e.startsWith("shop.total_for")), ["shop.total_for -> shop.orders", "shop.total_for -> shop.orders#customer", "shop.total_for -> shop.orders#total"]);
  assert.deepEqual(edges(u!, "WRITES"), ["shop.record -> shop.audit", "shop.record -> shop.audit#note", "shop.record -> shop.audit#order_id"]);
});

test("dynamic SQL makes the unit partial instead of guessing", () => {
  const [u] = run({
    "db/001.sql": [
      "CREATE TABLE dbo.T (A int);",
      "GO",
      "CREATE PROCEDURE dbo.P @n sysname AS BEGIN EXEC ('SELECT A FROM dbo.' + @n) END",
    ].join("\n"),
  });
  const complete = u!.records.at(-1)!;
  assert.equal(complete.status, "partial");
  assert.ok(u!.records.some((r) => r.code === "SQL_DYNAMIC"));
});

test("output is deterministic and independent of line endings", () => {
  const files = { "db/migrations/001.sql": "CREATE TABLE t (a int);\nCREATE VIEW v AS SELECT a FROM t;\n" };
  const a = run(files)[0]!.jsonl;
  const b = run({ "db/migrations/001.sql": files["db/migrations/001.sql"].replace(/\n/g, "\r\n") })[0]!.jsonl;
  assert.equal(run(files)[0]!.jsonl, a);
  // CRLF input yields the same nodes and edges (the input hash covers the raw bytes, so only that line differs).
  const strip = (s: string) => s.split("\n").filter((l) => !l.includes('"kind":"header"')).join("\n");
  assert.equal(strip(b), strip(a));
});
