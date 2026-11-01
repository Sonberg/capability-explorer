import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import { extract } from "../src/extract/extractor.ts";

interface RecordFact {
  kind: string; id?: string; symbolKey?: string; nodeKind?: string; name?: string;
  from?: string; relation?: string; to?: string; resolution?: string;
  code?: string; path?: string; evidence?: string[]; props?: Record<string, unknown>;
  repositoryId?: string; commitSha?: string; configHash?: string; span?: { startLine: number };
}

function run(files: Record<string, string>, reverse = false) {
  const dir = mkdtempSync(join(tmpdir(), "ce-mapping-http-"));
  try {
    const entries = Object.entries({
      "package.json": JSON.stringify({ name: "web", dependencies: { next: "16.0.0", axios: "1.0.0" } }),
      "tsconfig.json": JSON.stringify({ compilerOptions: { target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext", strict: true }, include: ["**/*.ts"] }),
      ...files,
    });
    for (const [path, text] of reverse ? entries.reverse() : entries) {
      mkdirSync(dirname(join(dir, path)), { recursive: true });
      writeFileSync(join(dir, path), text);
    }
    const result = extract({ source: dir, repository: "mapping", commit: "a".repeat(40) });
    assert.equal(result.length, 1);
    const jsonl = result[0]!.jsonl;
    assert.deepEqual(validateArtifact(jsonl).problems, []);
    return { jsonl, records: jsonl.trimEnd().split("\n").map((l) => JSON.parse(l) as RecordFact) };
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const requests = (r: RecordFact[]) => r.filter((x) => x.relation === "REQUESTS");
const endpoints = (r: RecordFact[]) => r.filter((x) => x.nodeKind === "Endpoint").map((x) => x.symbolKey).sort();

test("Next route methods expose endpoints and retain method declaration source evidence", () => {
  const { records } = run({
    "app/(public)/api/items/[id]/route.ts": "export async function GET() { return new Response('ok'); }\nexport const POST = async () => new Response('ok');\n",
  });
  assert.deepEqual(endpoints(records), ["http|mapping/web|GET /api/items/{}", "http|mapping/web|POST /api/items/{}"]);
  const handles = records.filter((r) => r.relation === "HANDLES");
  assert.deepEqual(handles.map((r) => r.from).sort(), [
    "ts|mapping|web|app/(public)/api/items/[id]/route.ts|GET#value",
    "ts|mapping|web|app/(public)/api/items/[id]/route.ts|POST#value",
  ]);
  for (const fact of records.filter((r) => r.relation === "EXPOSES" || r.relation === "HANDLES")) {
    for (const id of fact.evidence!) {
      const evidence = records.find((r) => r.id === id)!;
      assert.equal(evidence.repositoryId, "mapping");
      assert.equal(evidence.commitSha, "a".repeat(40));
      assert.equal(evidence.path, "app/(public)/api/items/[id]/route.ts");
      assert.ok([1, 2].includes(evidence.span!.startLine));
    }
  }
});

test("HTTP absolute URLs retain host and pathname boundaries without query values or credentials", () => {
  const { records } = run({ "src/client.ts": [
    'import axios from "axios";',
    'const client = axios.create({ baseURL: "https://base.example/prefix" });',
    'export async function request(id: string) {',
    'await fetch(`HTTPS://user:password@API.EXAMPLE:443/v1/Items/${id}?token=secret#frag`);',
    'await client.get("https://other.example:8443/Items?key=secret#fragment");',
    '}',
  ].join("\n") });
  assert.deepEqual(requests(records).map((r) => r.to).sort(), [
    "http|@unbound:mapping/web:host=api.example|GET /v1/items/{}",
    "http|@unbound:mapping/web:host=other.example:8443|GET /items",
  ]);
  for (const req of requests(records)) {
    assert.equal(req.resolution, "unresolved");
    assert.ok(!JSON.stringify(req.props).includes("secret"));
    assert.ok(!JSON.stringify(req.props).includes("password"));
  }
});

test("Next src/app handles all explicit verbs, local aliases and distinct catch-all forms", () => {
  const { records } = run({
    "src/app/(api)/docs/[...slug]/route.ts": "async function docs() {}\nexport { docs as GET };\nexport const HEAD = function () {};\n",
    "src/app/search/[[...terms]]/route.ts": "export function POST() {}\nexport function PUT() {}\nexport function PATCH() {}\nexport function DELETE() {}\nexport function OPTIONS() {}\n",
  });
  assert.deepEqual(endpoints(records), [
    "http|mapping/web|DELETE /search/{**}", "http|mapping/web|GET /docs/{*}",
    "http|mapping/web|HEAD /docs/{*}", "http|mapping/web|OPTIONS /search/{**}",
    "http|mapping/web|PATCH /search/{**}", "http|mapping/web|POST /search/{**}", "http|mapping/web|PUT /search/{**}",
  ]);
  const docs = records.find((r) => r.symbolKey === "http|mapping/web|GET /docs/{*}")!;
  assert.deepEqual(docs.props?.routeParameterNames, ["slug"]);
  assert.deepEqual(docs.props?.routeParameterKinds, ["catch-all"]);
  const search = records.find((r) => r.symbolKey === "http|mapping/web|POST /search/{**}")!;
  assert.equal(search.props?.route, "/search/[[...terms]]");
  assert.deepEqual(search.props?.routeParameterKinds, ["optional-catch-all"]);
  assert.ok(records.some((r) => r.relation === "HANDLES" && r.from?.endsWith("|docs#value")));
});

test("Next ignores nonroute files, nonframework projects, private folders and shadowed src/app", () => {
  const { records } = run({
    "app/route.ts": "export function GET() {}\n",
    "src/app/shadowed/route.ts": "export function POST() {}\n",
    "app/_private/hidden/route.ts": "export function POST() {}\n",
    "app/items/page.ts": "export function POST() {}\n",
    "lib/route.ts": "export function DELETE() {}\n",
  });
  assert.deepEqual(endpoints(records), ["http|mapping/web|GET /"]);
  const plain = run({ "package.json": '{"name":"web"}', "app/route.ts": "export function GET() {}\n" });
  assert.deepEqual(endpoints(plain.records), []);
});

test("Next reports unsupported handler exports and path conventions without fabricated endpoints", () => {
  const { records } = run({
    "app/default/route.ts": "export default function GET() {}\n",
    "app/factory/route.ts": "declare function factory(): unknown; export const GET = factory();\n",
    "app/reexport/route.ts": 'export { handler as GET } from "../../helper.js";\n',
    "helper.ts": "export function handler() {}\n",
    "app/@slot/x/route.ts": "export function GET() {}\n",
    "app/(.)intercept/route.ts": "export function GET() {}\n",
    "app/[...slug]/child/route.ts": "export function GET() {}\n",
    "app/trace/route.ts": "export function TRACE() {}\n",
  });
  assert.deepEqual(endpoints(records), []);
  assert.equal(records.filter((r) => r.code === "NEXT_ROUTE_UNSUPPORTED").length, 7);
  assert.equal(records.at(-1)?.kind, "complete");
  assert.equal((records.at(-1) as unknown as { status: string }).status, "partial");
});

test("HTTP malformed, dynamic and protocol-relative authorities remain unresolved without targets", () => {
  const { records } = run({ "src/client.ts": [
    'import axios from "axios";',
    'export async function request(host: string) {',
    'await fetch("https://[bad/path");',
    'await fetch(`https://${host}/api/items`);',
    'await fetch("//api.example/path");',
    'await fetch("ftp://api.example/path");',
    'await axios.get("https://");',
    '}',
  ].join("\n") });
  assert.equal(requests(records).length, 5);
  assert.ok(requests(records).every((r) => r.to === undefined && r.resolution === "unresolved"));
});

test("HTTP query and fragment splitting, relative origins, templates and schemes stay separate", () => {
  const { records } = run({ "src/client.ts": [
    'export async function request(id: string) {',
    'await fetch(`/Items/${id}?z=secret&a=value&a=again#frag?fake=query`);',
    'await fetch("https://API.EXAMPLE:8443/Path?first=1#second?fake=2");',
    'await fetch("http://api.example/Path#fragment?fake=query");',
    '}',
  ].join("\n") });
  const edges = requests(records);
  const relative = edges.find((r) => r.to?.includes("base=origin"))!;
  assert.equal(relative.to, "http|@unbound:mapping/web:base=origin|GET /items/{}");
  assert.deepEqual(relative.props?.query, ["a", "z"]);
  const secure = edges.find((r) => r.to?.includes(":8443"))!;
  assert.deepEqual(secure.props?.query, ["first"]);
  assert.equal(secure.props?.scheme, "https");
  const insecure = edges.find((r) => r.props?.scheme === "http")!;
  assert.equal(insecure.to, "http|@unbound:mapping/web:host=api.example|GET /path");
  assert.equal(insecure.props?.query, undefined);
});

test("axios relative prefixes, explicit request bases and disabled absolute overrides stay conservative", () => {
  const { records } = run({ "src/client.ts": [
    'import axios from "axios";',
    'const local = axios.create({ baseURL: "/api" });',
    'const strict = axios.create({ baseURL: "https://proxy.example", allowAbsoluteUrls: false });',
    'export async function request(config: unknown) {',
    'await local.get("/items?x=secret", { params: { y: "secret" } });',
    'await local.get("/orders", { baseURL: "https://other.example/v2" });',
    'await strict.get("https://remote.example/path");',
    'await local.get("/dynamic", config);',
    '}',
  ].join("\n") });
  const edges = requests(records);
  assert.equal(edges.length, 4);
  assert.ok(edges.some((r) => r.to === "http|@unbound:mapping/web:base=origin|GET /api/items"));
  assert.ok(edges.some((r) => r.to === "http|@unbound:mapping/web:host=other.example|GET /v2/orders"));
  assert.equal(edges.filter((r) => r.to === undefined).length, 2);
  assert.deepEqual(edges.find((r) => r.to?.endsWith("/api/items"))!.props?.query, ["x", "y"]);
});

test("HTTP URI normalization removes dot segments while preserving placeholders and encoded separators", () => {
  const { records } = run({ "src/client.ts": [
    'export async function request(id: string) {',
    'await fetch(`https://API.EXAMPLE:443/v1/../items/${id}/detail/../part%2Fname`);',
    'await fetch("http://api.example:80/a/./b/%2e%2e/c%3Fd");',
    'await fetch("/root/../local/./path%2Fsegment");',
    '}',
  ].join("\n") });
  assert.deepEqual(requests(records).map((r) => r.to).sort(), [
    "http|@unbound:mapping/web:base=origin|GET /local/path%2fsegment",
    "http|@unbound:mapping/web:host=api.example|GET /a/c%3fd",
    "http|@unbound:mapping/web:host=api.example|GET /items/{}/part%2fname",
  ]);
});

test("Next proxy request facts retain their source handler without inferred remote connections", () => {
  const { records } = run({ "app/proxy/route.ts": "export async function GET() { return fetch(process.env.TARGET); }\n" });
  assert.deepEqual(endpoints(records), ["http|mapping/web|GET /proxy"]);
  assert.equal(requests(records).length, 1);
  assert.equal(requests(records)[0]!.to, undefined);
  assert.equal(requests(records)[0]!.from, "ts|mapping|web|app/proxy/route.ts|GET#value");
});

test("Next detects ambiguous routes and discloses unevaluated application configuration", () => {
  const { records } = run({
    "next.config.js": "throw new Error('analysis must never execute configuration');\n",
    "app/(one)/items/route.ts": "export function GET() {}\n",
    "app/(two)/items/route.ts": "export function GET() {}\n",
  });
  assert.equal(records.filter((r) => r.code === "NEXT_CONFIGURATION_NOT_EVALUATED").length, 2);
  assert.equal(records.filter((r) => r.code === "NEXT_ROUTE_UNSUPPORTED").length, 1);
  assert.deepEqual(endpoints(records), ["http|mapping/web|GET /items"]);
});

test("framework metadata participates in source identity", () => {
  const file = { "app/route.ts": "export function GET() {}\n" };
  const next = run(file);
  const plain = run({ ...file, "package.json": '{"name":"web"}' });
  assert.notEqual((next.records[0] as unknown as { inputHash: string }).inputHash,
    (plain.records[0] as unknown as { inputHash: string }).inputHash);
});

test("identical inputs produce byte-identical artifacts under shuffled file creation", () => {
  const files = {
    "app/(api)/items/[id]/route.ts": "export async function GET() { return fetch('https://api.example/items?key=value'); }\nexport function POST() {}\n",
    "app/docs/[[...slug]]/route.ts": "export const GET = async () => new Response('ok');\n",
  };
  const first = run(files);
  assert.equal(run(files).jsonl, first.jsonl);
  assert.equal(run(files, true).jsonl, first.jsonl);
});
