import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import { extract } from "../src/extract/extractor.ts";
import { normaliseRoute, parseSql } from "../src/extract/rules.ts";

interface Rec { kind: string; symbolKey?: string; nodeKind?: string; from?: string; relation?: string; to?: string; resolution?: string; unresolvedReason?: string; props?: Record<string, unknown> }

/** Writes a throwaway project and extracts it; nothing is installed, as in a real analysis. */
function run(files: Record<string, string>) {
  const dir = mkdtempSync(join(tmpdir(), "ce-ts-"));
  try {
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(dir, path)), { recursive: true });
      writeFileSync(join(dir, path), text);
    }
    const results = extract({ source: dir, repository: "shop", commit: "0".repeat(40) });
    return results.map((r) => {
      const check = validateArtifact(r.jsonl);
      assert.deepEqual(check.problems, [], r.unitPath);
      return { ...r, records: r.jsonl.trimEnd().split("\n").map((l) => JSON.parse(l) as Rec) };
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const tsconfig = JSON.stringify({ compilerOptions: { target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext", strict: true, resolveJsonModule: true }, include: ["src"] });

test("routes: app, nested routers and route() chains; handlers by reference and inline", () => {
  const [unit] = run({
    "package.json": JSON.stringify({ name: "@shop/api", dependencies: { express: "5.0.0" } }, null, 2),
    "tsconfig.json": tsconfig,
    "src/app.ts": [
      'import express, { Router } from "express";',
      'import { listOrders } from "./orders.js";',
      "const app = express();",
      "const api = Router();",
      "const orders = express.Router();",
      'orders.get("/", listOrders);',
      'orders.route("/:id(\\\\d+)").delete(async (req, res) => { res.end(); });',
      'api.use("/orders", orders);',
      'app.use("/api/", api);',
      'app.get("/health", (_req, res) => res.send("ok"));',
    ].join("\n"),
    "src/orders.ts": "export async function listOrders(req: unknown, res: unknown) {}\n",
  });
  const endpoints = unit!.records.filter((r) => r.nodeKind === "Endpoint").map((r) => r.symbolKey).sort();
  assert.deepEqual(endpoints, [
    "http|shop/@shop/api|DELETE /api/orders/{}",
    "http|shop/@shop/api|GET /api/orders",
    "http|shop/@shop/api|GET /health",
  ]);
  const handles = unit!.records.filter((r) => r.relation === "HANDLES").map((r) => `${r.from} -> ${r.to}`).sort();
  assert.deepEqual(handles, [
    "ts|shop|@shop/api|src/app.ts|(module)#namespace -> http|shop/@shop/api|DELETE /api/orders/{}",
    "ts|shop|@shop/api|src/app.ts|(module)#namespace -> http|shop/@shop/api|GET /health",
    "ts|shop|@shop/api|src/orders.ts|listOrders#value -> http|shop/@shop/api|GET /api/orders",
  ]);
  assert.equal(unit!.records.filter((r) => r.relation === "EXPOSES").length, 3);
});

test("clients, SQL and messaging bind to unbound or literal resources", () => {
  const [unit] = run({
    "package.json": JSON.stringify({ name: "jobs" }),
    "tsconfig.json": tsconfig,
    "config.json": JSON.stringify({ bus: { ns: "sb-jobs.servicebus.windows.net", topic: "orders" } }, null, 2),
    "src/jobs.ts": [
      'import pg from "pg";',
      'import axios from "axios";',
      'import { ServiceBusClient } from "@azure/service-bus";',
      'import config from "../config.json" with { type: "json" };',
      "const pool = new pg.Pool({ connectionString: process.env.JOBS_DB });",
      "const bus = new ServiceBusClient(config.bus.ns);",
      "export async function archive(id: string) {",
      '  await pool.query("UPDATE jobs.orders SET archived = true WHERE id = $1", [id]);',
      "  await axios.post(`https://billing.example.com/api/invoices/${id}`, {});",
      "  await bus.createSender(config.bus.topic).sendMessages({ body: id });",
      "}",
    ].join("\n"),
  });
  const edge = (rel: string) => unit!.records.find((r) => r.relation === rel)!;
  assert.equal(edge("WRITES").to, "sql|@unbound:shop/jobs:env=JOBS_DB|jobs.orders");
  assert.equal(edge("REQUESTS").to, "http|@unbound:shop/jobs:host=billing.example.com|POST /api/invoices/{}");
  assert.equal(edge("PUBLISHES").to, "chan|servicebus|sb-jobs.servicebus.windows.net|entity/orders");
  assert.equal(edge("PUBLISHES").resolution, "resolved");
  assert.ok(unit!.records.some((r) => r.kind === "diagnostic" && (r as { code?: string }).code === "DEPENDENCIES_NOT_INSTALLED"));
});

test("a plain JavaScript package without tsconfig is its own unit", () => {
  const [unit] = run({
    "package.json": JSON.stringify({ name: "legacy" }),
    "lib/index.js": "export function total(xs) { return xs.reduce((a, b) => a + b, 0); }\nexport const answer = total([40, 2]);\n",
  });
  assert.equal(unit!.language, "javascript");
  assert.ok(unit!.records.some((r) => r.symbolKey === "ts|shop|legacy|lib/index.js|total#value" && r.nodeKind === "Function"));
  assert.ok(unit!.records.some((r) => r.relation === "CALLS" && r.from === "ts|shop|legacy|lib/index.js|(module)#namespace" && r.to === "ts|shop|legacy|lib/index.js|total#value"));
});

test("routes and SQL helpers", () => {
  assert.equal(normaliseRoute("/api/Items/:id/"), "/api/items/{}");
  assert.equal(normaliseRoute("/a/{articleId}?x=1"), "/a/{}");
  assert.deepEqual(parseSql("SELECT a, b FROM s.t JOIN u ON u.id = t.id").map((s) => [s.table, s.write]), [["s.t", false], ["u", false]]);
  assert.deepEqual(parseSql("DELETE FROM s.t WHERE id = 1").map((s) => [s.table, s.write]), [["s.t", true]]);
});

interface CalcRec extends Rec { irHash?: string; expr?: unknown; attributes?: Record<string, { status: string; value?: string }> }

test("calculations: rounding and aggregation become IR; unknowns stay unknown; unsupported code is a diagnostic", () => {
  const calc = (body: string) => run({
    "package.json": JSON.stringify({ name: "calc" }),
    "tsconfig.json": tsconfig,
    "src/c.ts": body,
  })[0]!.records;
  const [a] = calc("export function half(x: number): number { return Math.round(x * 100) / 100; }\n").filter((r) => r.kind === "calculation") as CalcRec[];
  assert.equal(a!.attributes!.rounding!.value, "halfTowardPositiveInfinity");
  assert.equal(a!.attributes!.currency!.status, "unknown");
  assert.equal(a!.attributes!.zeroDenominator!.value, "notApplicable");
  // Same logic under another name hashes the same; a changed constant changes the hash.
  const renamed = calc("export function other(y: number): number { return Math.round(y * 100) / 100; }\n").find((r) => r.kind === "calculation") as CalcRec;
  const changed = calc("export function half(x: number): number { return Math.round(x * 10) / 100; }\n").find((r) => r.kind === "calculation") as CalcRec;
  assert.equal(renamed.irHash, a!.irHash);
  assert.notEqual(changed.irHash, a!.irHash);
  // An unguarded division is reported, not assumed safe.
  const div = calc("export function avg(t: number, n: number): number { return Math.trunc(t / n); }\n").find((r) => r.kind === "calculation") as CalcRec;
  assert.equal(div.attributes!.zeroDenominator!.value, "unguarded");
  assert.equal(div.attributes!.denominatorSource!.value, "parameter");
  // Loops the IR cannot express are never approximated.
  const records = calc("export function bad(xs: number[]): number { let s = 0; while (s < 10) { s += xs.length; } return Math.round(s); }\n");
  assert.equal(records.filter((r) => r.kind === "calculation").length, 0);
  assert.ok(records.some((r) => r.kind === "diagnostic" && (r as { code?: string }).code === "CALCULATION_UNSUPPORTED"));
});
