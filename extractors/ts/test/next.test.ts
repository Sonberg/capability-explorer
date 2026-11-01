import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import { extract } from "../src/extract/extractor.ts";
import { nextRouteFor } from "../src/extract/next.ts";

interface Rec { kind: string; symbolKey?: string; nodeKind?: string; name?: string; from?: string; relation?: string; to?: string; code?: string; props?: Record<string, unknown> }

function run(files: Record<string, string>) {
  const dir = mkdtempSync(join(tmpdir(), "ce-next-"));
  try {
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(dir, path)), { recursive: true });
      writeFileSync(join(dir, path), text);
    }
    return extract({ source: dir, repository: "web", commit: "0".repeat(40) }).map((r) => {
      assert.deepEqual(validateArtifact(r.jsonl).problems, [], r.unitPath);
      return { ...r, records: r.jsonl.trimEnd().split("\n").map((l) => JSON.parse(l) as Rec) };
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const tsconfig = JSON.stringify({ compilerOptions: { target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext", strict: true }, include: ["**/*.ts"] });
const pkg = (extra: object = {}) => JSON.stringify({ name: "@shop/web", dependencies: { next: "15.0.0" }, ...extra });

test("nextRouteFor: literal, dynamic, catch-all, optional catch-all, groups", () => {
  const route = (p: string) => (nextRouteFor(p) as { route: { route: string; normalized: string; catchAll?: boolean } }).route;
  assert.equal(route("app/api/subscribe/route.ts").route, "/api/subscribe");
  assert.equal(route("app/api/subscribe/route.ts").catchAll, undefined);
  assert.deepEqual([route("src/app/api/Orders/[id]/route.ts").route, route("src/app/api/Orders/[id]/route.ts").normalized], ["/api/Orders/{id}", "/api/orders/{}"]);
  assert.equal(route("app/api/[...path]/route.ts").normalized, "/api/{...}");
  assert.equal(route("app/api/[[...path]]/route.ts").normalized, "/api/{...}");
  assert.equal(route("app/(admin)/reviews/route.ts").normalized, "/reviews");
  assert.equal(route("app/route.ts").normalized, "/");
});

test("nextRouteFor: not a route, private and unsupported folders", () => {
  assert.equal(nextRouteFor("app/api/page.tsx"), undefined);
  assert.equal(nextRouteFor("components/route.ts"), undefined);
  assert.deepEqual(nextRouteFor("app/_lib/route.ts"), { skip: "private" });
  assert.ok("unsupported" in nextRouteFor("app/@modal/route.ts")!);
  assert.ok("unsupported" in nextRouteFor("app/(.)photo/route.ts")!);
  assert.ok("unsupported" in nextRouteFor("app/[...path]/more/route.ts")!);
});

test("next routes: exported verbs become endpoints with handlers; pages and non-verbs do not", () => {
  const [unit] = run({
    "package.json": pkg(),
    "tsconfig.json": tsconfig,
    "app/api/subscribe/route.ts": "export async function POST(req: Request) { return new Response('ok'); }\nexport function helper() {}\n",
    "app/api/orders/[id]/route.ts": "const read = async () => new Response('x');\nexport const GET = read;\nexport const DELETE = async () => new Response(null);\nexport { read as PUT };\nexport const dynamic = 'force-dynamic';\n",
    "app/api/[...path]/route.ts": "export const GET = async () => new Response('proxy');\n",
    "app/api/external/route.ts": "export const PATCH = Response.json;\n",
    "app/page.tsx": "export default function Page() { return null; }\n",
  });
  const endpoints = unit!.records.filter((r) => r.nodeKind === "Endpoint").map((r) => r.symbolKey).sort();
  assert.deepEqual(endpoints, [
    "http|web/@shop/web|DELETE /api/orders/{}",
    "http|web/@shop/web|GET /api/orders/{}",
    "http|web/@shop/web|GET /api/{*}",
    "http|web/@shop/web|PATCH /api/external",
    "http|web/@shop/web|POST /api/subscribe",
    "http|web/@shop/web|PUT /api/orders/{}",
  ]);
  // Canonical artifacts distinguish required {*} from optional {**}; the legacy display helper stays compatible.
  const catchAll = unit!.records.find((r) => r.symbolKey === "http|web/@shop/web|GET /api/{*}")!;
  assert.equal(catchAll.props?.catchAll, true);
  assert.equal(catchAll.name, "GET /api/[...path]");
  assert.equal(unit!.records.filter((r) => r.relation === "EXPOSES").length, 6);
  const handles = unit!.records.filter((r) => r.relation === "HANDLES");
  assert.equal(handles.length, 6);
  const kinds = new Map(unit!.records.filter((r) => r.symbolKey).map((r) => [r.symbolKey, r.nodeKind]));
  assert.ok(handles.every((r) => ["Function", "Method", "File"].includes(kinds.get(r.from) ?? "")));
  assert.ok(handles.some((r) => r.from?.endsWith("|read#value") && r.to === "http|web/@shop/web|GET /api/orders/{}"));
  assert.ok(handles.some((r) => r.from?.endsWith("|app/api/external/route.ts|(module)#namespace") && r.to === "http|web/@shop/web|PATCH /api/external"));
  assert.ok(unit!.records.some((r) => r.nodeKind === "Service" && r.props?.framework === "next"));
});

test("next routes: nothing without the next dependency; unsupported folders get a diagnostic", () => {
  const [plain] = run({
    "package.json": JSON.stringify({ name: "@shop/lib" }),
    "tsconfig.json": tsconfig,
    "app/api/x/route.ts": "export function GET() {}\n",
  });
  assert.equal(plain!.records.filter((r) => r.nodeKind === "Endpoint").length, 0);
  const [slot] = run({
    "package.json": pkg(),
    "tsconfig.json": tsconfig,
    "app/@modal/route.ts": "export function GET() {}\n",
  });
  assert.equal(slot!.records.filter((r) => r.nodeKind === "Endpoint").length, 0);
  assert.ok(slot!.records.some((r) => r.kind === "diagnostic" && r.code === "NEXT_ROUTE_UNSUPPORTED"));
});

test("fetch of an absolute URL keeps the host out of the path", () => {
  const [unit] = run({
    "package.json": pkg(),
    "tsconfig.json": tsconfig,
    "lib/meta.ts": "export async function send(id: string) { await fetch(`https://graph.facebook.com/${id}/events?access_token=x`, { method: 'POST' }); }\n",
  });
  const req = unit!.records.find((r) => r.relation === "REQUESTS")!;
  assert.equal(req.to, "http|@unbound:web/@shop/web:host=graph.facebook.com|POST /{}/events");
});
