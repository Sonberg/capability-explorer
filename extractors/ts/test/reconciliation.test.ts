import { test } from "node:test";
import assert from "node:assert/strict";
import { dirname, join } from "node:path";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import { extract } from "../src/extract/extractor.ts";
import { nextRouteFor } from "../src/extract/next.ts";

function write(root: string, files: Record<string, string>) {
  for (const [path, text] of Object.entries(files)) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), text);
  }
}
const config = JSON.stringify({ compilerOptions: { target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext", strict: true }, include: ["**/*.ts", "../outside/*.ts"] });
const records = (jsonl: string) => {
  assert.deepEqual(validateArtifact(jsonl).problems, []);
  return jsonl.trimEnd().split("\n").map(line => JSON.parse(line));
};

test("reconciliation preserves test opt-in, confined source reads and checkout-independent fingerprints together", () => {
  const base = mkdtempSync(join(tmpdir(), "ce-ts-reconciliation-"));
  try {
    const files = {
      "package.json": '{"name":"reconciled"}', "tsconfig.json": config,
      "src/index.ts": "export function production() { return 1; }\n",
      "tests/index.test.ts": "export function testOnly() { return 2; }\n",
    };
    write(join(base, "outside"), { "escape.ts": 'export const outside = "NEVER_IMPORT_SIBLING";' });
    for (const name of ["first", "relocated"]) write(join(base, name), files);
    const options = { repository: "reconciled", commit: "0".repeat(40) };
    const first = extract({ ...options, source: join(base, "first") })[0]!.jsonl;
    const relocated = extract({ ...options, source: join(base, "relocated") })[0]!.jsonl;
    assert.equal(first, relocated);
    const normal = records(first);
    assert.ok(normal.some(r => r.name === "production"));
    assert.ok(!normal.some(r => r.name === "testOnly"));
    assert.ok(!first.includes("NEVER_IMPORT_SIBLING"));
    const including = records(extract({ ...options, source: join(base, "first"), includeTests: true })[0]!.jsonl);
    assert.ok(including.some(r => r.name === "testOnly"));
    assert.notEqual(normal[0].configHash, including[0].configHash);
  } finally { rmSync(base, { recursive: true, force: true }); }
});

test("reconciliation retains proven Next aliases while canonical catch-alls and unsupported handlers stay conservative", () => {
  const root = mkdtempSync(join(tmpdir(), "ce-next-reconciliation-"));
  try {
    write(root, {
      "package.json": '{"name":"reconciled","peerDependencies":{"next":"16.0.0"}}', "tsconfig.json": config,
      "app/docs/[...slug]/route.ts": "const read = async () => new Response('ok'); export const GET = read; export const POST = Response.json;\n",
      "app/search/[[...query]]/route.ts": "export function GET() {}\n",
      "app/unsupported/route.ts": "declare function factory(): unknown; export const GET = factory(); let mutable = async () => {}; export const DELETE = mutable; export { handler as PUT } from '../../helper.js';\n",
      "helper.ts": "export function handler() {}\n",
    });
    const result = records(extract({ source: root, repository: "reconciled", commit: "0".repeat(40) })[0]!.jsonl);
    const endpoints = result.filter(r => r.nodeKind === "Endpoint").map(r => r.symbolKey).sort();
    assert.deepEqual(endpoints, ["http|reconciled/reconciled|GET /docs/{*}", "http|reconciled/reconciled|GET /search/{**}", "http|reconciled/reconciled|POST /docs/{*}"]);
    assert.ok(result.some(r => r.relation === "HANDLES" && r.from.endsWith("|read#value")));
    assert.ok(result.some(r => r.relation === "HANDLES" && r.to.endsWith("POST /docs/{*}") && r.from.endsWith("|(module)#namespace")));
    assert.equal(result.filter(r => r.code === "NEXT_ROUTE_UNSUPPORTED").length, 3);
    assert.equal(result.at(-1)?.status, "partial");
    assert.equal((nextRouteFor("app/docs/[...slug]/route.ts") as { route: { normalized: string } }).route.normalized, "/docs/{...}");
  } finally { rmSync(root, { recursive: true, force: true }); }
});
