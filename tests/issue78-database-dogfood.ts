import assert from "node:assert/strict";
import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { access, copyFile, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { dirname, join, relative, resolve } from "node:path";
import { pathToFileURL } from "node:url";

// Run only after the integration owner grants the single heavyweight token.
// Prerequisites: built ce/Explorer including production wwwroot, installed Playwright
// and Chromium, and this locally cached image (same digest as PostgresFixture).
assert.equal(process.env.CE_ISSUE78_DATABASE_DOGFOOD, "1",
  "Explicit heavyweight approval required: CE_ISSUE78_DATABASE_DOGFOOD=1 node tests/issue78-database-dogfood.ts");
const root = resolve(import.meta.dirname, "..");
const run = join(root, ".sandbox-cache", "issue78-database-dogfood", randomUUID());
await mkdir(run, { recursive: true, mode: 0o700 });
const engine = join(root, "scripts/container-engine");
const image = "pgvector/pgvector:0.8.6-pg17@sha256:cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f";
const ce = join(root, "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll");
const explorer = join(root, "platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/ce-explorer.dll");
const webroot = join(root, "platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/wwwroot");
const playwright = resolve(root, process.env.CE_ISSUE78_PLAYWRIGHT_MODULE ?? "spikes/likec4/node_modules/playwright/index.mjs");
const digest = (bytes: Uint8Array | string) => createHash("sha256").update(bytes).digest("hex");
const logs: string[] = [];
const name = `ce-issue78-${randomUUID()}`;
const password = randomUUID();
const capability = "database-viewer-dogfood";
const repositoryId = "database-viewer-fixture";
let container: string | undefined;
let server: ChildProcess | undefined;
let browser: { close(): Promise<void> } | undefined;
let passed = false;
let failure: string | undefined;
const sourceHashes: Record<string, string> = {};
const modelResponses: { url: string; status: number }[] = [];
const external: string[] = [];
const sourceBodies: { id: string; xml: string; sha256: string; commitSha: string; snapshot: string; generation: number }[] = [];

function command(program: string, args: string[], cwd = root, extraEnv: NodeJS.ProcessEnv = {}): string {
  const result = spawnSync(program, args, {
    cwd, env: { ...process.env, ...extraEnv }, encoding: "utf8",
    timeout: 60_000, maxBuffer: 4_194_304,
  });
  logs.push(`${relative(root, program)} ${args.join(" ")}\n${result.stdout ?? ""}${result.stderr ?? ""}`);
  assert.ifError(result.error);
  assert.equal(result.status, 0, logs.at(-1));
  return result.stdout.trim();
}
async function get(origin: string, path: string) {
  const response = await fetch(origin + path, { signal: AbortSignal.timeout(10_000) });
  assert.equal(response.status, 200, `${path}: ${await response.clone().text()}`);
  return response.json();
}
async function port(): Promise<number> {
  const socket = createServer();
  await new Promise<void>((done, reject) => {
    socket.once("error", reject);
    socket.listen(0, "127.0.0.1", done);
  });
  const address = socket.address();
  assert(address && typeof address === "object");
  await new Promise<void>((done, reject) => socket.close(error => error ? reject(error) : done()));
  return address.port;
}
async function stop(child: ChildProcess): Promise<void> {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null) return;
  const exited = new Promise<void>(done => child.once("exit", () => done()));
  child.kill("SIGTERM");
  const timer = setTimeout(() => child.kill("SIGKILL"), 5_000);
  try { await exited; } finally { clearTimeout(timer); }
}
async function fingerprint(path: string): Promise<void> {
  const entries = await readdir(path, { withFileTypes: true });
  assert(entries.length <= 1_000, "bounded production asset inventory");
  for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    const file = join(path, entry.name);
    if (entry.isDirectory()) await fingerprint(file);
    else if (entry.isFile()) sourceHashes[relative(root, file)] = digest(await readFile(file));
    else assert.fail("production assets must be regular files, not symlinks");
  }
}

try {
  for (const path of [ce, explorer, join(webroot, "index.html"), playwright]) await access(path);
  for (const path of [ce, explorer, "tests/issue78-database-dogfood.ts",
    "tests/fixtures/issue78/bpmn-representative.xml", "tests/fixtures/issue78/dmn-representative.xml"]) {
    const absolute = resolve(root, path);
    sourceHashes[relative(root, absolute)] = digest(await readFile(absolute));
  }
  await fingerprint(dirname(ce));
  await fingerprint(dirname(explorer));
  command(engine, ["image", "inspect", image]);
  // No pull, no reused database, no caller-supplied connection or production state.
  container = command(engine, ["run", "--detach", "--rm", "--pull", "never", "--name", name,
    "--publish", "127.0.0.1::5432", "--env", "POSTGRES_PASSWORD",
    "--env", "POSTGRES_USER=ce_test", "--env", "POSTGRES_DB=ce_issue78", image],
    root, { POSTGRES_PASSWORD: password });
  assert.match(container, /^[a-f0-9]{12,64}$/);
  let databaseReady = false;
  for (let attempt = 0; attempt < 120; attempt++) {
    const ready = spawnSync(engine, ["exec", container, "pg_isready", "-U", "ce_test", "-d", "ce_issue78"],
      { cwd: root, encoding: "utf8", timeout: 5_000 });
    assert.ifError(ready.error);
    if (ready.status === 0) { databaseReady = true; break; }
    await new Promise(done => setTimeout(done, 250));
  }
  assert(databaseReady, "owned PostgreSQL must become responsive");
  const mapping = command(engine, ["port", container, "5432/tcp"]);
  assert.match(mapping, /^127\.0\.0\.1:\d+$/);
  const database = `Host=127.0.0.1;Port=${mapping.split(":")[1]};Database=ce_issue78;Username=ce_test;Password=${password};Timeout=5;Command Timeout=5`;
  const env = { CE_DATABASE: database, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: "1" };
  command("dotnet", [ce, "migrate"], root, env);
  const repo = join(run, "repo");
  await mkdir(repo);
  await copyFile(join(root, "tests/fixtures/issue78/bpmn-representative.xml"), join(repo, "process.bpmn"));
  await copyFile(join(root, "tests/fixtures/issue78/dmn-representative.xml"), join(repo, "decision.dmn"));
  command("git", ["init", "--quiet"], repo);
  command("git", ["add", "."], repo);
  command("git", ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
    "commit", "--quiet", "-m", "Seal synthetic database viewer inputs"], repo);
  const commit = command("git", ["rev-parse", "HEAD"], repo);
  const tree = command("git", ["rev-parse", "HEAD^{tree}"], repo);
  const originals = new Map<string, Buffer>();
  const inputs = [];
  for (const unit of ["process.bpmn", "decision.dmn"]) {
    const original = await readFile(join(repo, unit));
    originals.set(unit, original);
    command(process.execPath, ["extractors/ts/src/models/cli.ts", "--source", join(repo, unit),
      "--repository", repositoryId, "--commit", commit, "--out", join(run, `${unit}.json`),
      "--content-profile", "model-content", "--source-out", join(run, `${unit}.source.json`)]);
    const evidence = await readFile(join(run, `${unit}.json`));
    for (const suffix of [".json", ".source.json"]) {
      const path = unit + suffix;
      const bytes = await readFile(join(run, path));
      const artifact = JSON.parse(bytes.toString("utf8"));
      if (suffix === ".source.json") {
        assert.equal(artifact.sourceBytesBase64, original.toString("base64"));
        assert.equal(artifact.sourceSha256, digest(original));
        assert.equal(artifact.modelArtifactSha256, digest(evidence));
      }
      inputs.push({ repositoryId, profile: artifact.schema, path, unitPath: unit,
        sha256: digest(bytes), byteLength: bytes.length, required: true, contentProfile: "model-content" });
    }
  }
  await writeFile(join(run, "plan.json"), JSON.stringify({
    schema: "ce.collection.plan.v1", recipeSha256: digest("issue78-source-database-browser"),
    configSha256: digest("synthetic-production-renderer"), policySha256: digest("explicit-local-model-content"),
    repositories: [{ repositoryId, providerId: "local", producerId: "database-dogfood",
      tenantRef: "synthetic", rightsRef: "explicit-local-fixture-not-authentication",
      commitSha: commit, treeSha: tree, permittedContentProfiles: ["model-content"] }], inputs,
  }));
  const collection = join(run, "collection");
  command("dotnet", [ce, "collect", "--plan", join(run, "plan.json"), "--out", collection], root, env);
  command("dotnet", [ce, "validate-collection", collection], root, env);
  command(process.execPath, ["extractors/ts/src/collection/cli.ts", "validate", collection]);
  for (const scope of ["*", capability])
    command("dotnet", [ce, "publish", "--scope", scope, "--collections", collection], root, env);
  const sql = (query: string) => command(engine, ["exec", container!, "psql", "-U", "ce_test",
    "-d", "ce_issue78", "-At", "-v", "ON_ERROR_STOP=1", "-c", query]);
  assert.equal(sql("select count(*) from model_product"), "2");
  assert.equal(sql("select count(*) from model_source_product"), "2");
  assert.equal(sql("select count(*) from analysis_snapshot_model"), "4");
  assert.equal(sql("select count(*) from artifact"), "0", "model-only snapshot must not invent native evidence");
  for (const bytes of originals.values())
    assert.equal(sql(`select count(*) from model_source_product where encode(sha256(source_bytes),'hex')='${digest(bytes)}'`), "1");
  await writeFile(join(run, "capabilities.json"), JSON.stringify({ capabilities: [{
    id: capability, name: "Database viewer dogfood", repositories: [{
      id: repositoryId, kind: "local", location: join(run, "no-repository-source"), ref: commit,
    }],
  }] }));
  const origin = `http://127.0.0.1:${await port()}`;
  server = spawn("dotnet", [explorer, "--urls", origin, "--source", "postgres",
    "--capabilities", join(run, "capabilities.json"), "--artifacts", join(run, "no-native"),
    "--workspace", run, "--modelSettings", join(run, "no-model-settings.json")],
    { cwd: root, env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] });
  server.stdout!.on("data", data => logs.push(data.toString()));
  server.stderr!.on("data", data => logs.push(data.toString()));
  let serverError: Error | undefined;
  server.on("error", error => { serverError = error; });
  let ready = false;
  for (let attempt = 0; attempt < 100; attempt++) {
    if (serverError) throw serverError;
    assert.equal(server.exitCode, null, "Explorer exited before readiness");
    try {
      const response = await fetch(`${origin}/api/meta`, { signal: AbortSignal.timeout(1_000) });
      if (response.ok) { ready = true; break; }
    } catch (error) {
      if (!(error instanceof TypeError || error instanceof DOMException)) throw error;
    }
    await new Promise(done => setTimeout(done, 100));
  }
  assert(ready, "actual PostgreSQL Explorer must become responsive");
  const meta = await get(origin, `/api/meta?cap=${capability}`);
  assert.equal(meta.source, "postgres");
  const models = await get(origin, `/api/models?cap=${capability}`);
  assert.equal(models.mode, "database");
  assert.equal(models.items.length, 2);
  assert.equal(models.snapshot, meta.snapshotId);
  assert.equal(models.generation, meta.generation);
  assert.equal(models.selection[repositoryId], commit);
  const scope = new URLSearchParams({ cap: capability, snapshot: models.snapshot, generation: String(models.generation) });
  for (const item of models.items) {
    const source = await get(origin, `/api/models/source?id=${encodeURIComponent(item.id)}&${scope}`);
    assert.equal(source.commitSha, commit);
    assert.equal(source.xml, originals.get(item.unitPath)!.toString("utf8"));
    assert.equal(source.sha256, digest(originals.get(item.unitPath)!));
    assert.equal(digest(new TextEncoder().encode(source.xml)), source.sha256);
  }
  const { chromium } = await import(pathToFileURL(playwright).href);
  const chromiumBrowser = await chromium.launch({ headless: true });
  browser = chromiumBrowser;
  const tab = await chromiumBrowser.newPage({ viewport: { width: 1440, height: 1000 } });
  tab.setDefaultTimeout(20_000);
  const pageErrors: string[] = [];
  const pending: Promise<void>[] = [];
  tab.on("pageerror", (error: Error) => pageErrors.push(error.message));
  tab.on("request", (request: { url(): string }) => {
    if (/^https?:/.test(request.url()) && new URL(request.url()).origin !== origin) external.push(request.url());
  });
  tab.on("response", (response: { url(): string; status(): number; json(): Promise<unknown> }) => {
    const url = new URL(response.url());
    if (!url.pathname.startsWith("/api/models")) return;
    modelResponses.push({ url: response.url(), status: response.status() });
    if (url.pathname === "/api/models/source" && response.status() === 200) {
      pending.push(response.json().then(body => {
        if (url.searchParams.get("cap") !== capability)
          pageErrors.push("browser source did not use selected database capability");
        sourceBodies.push(body as typeof sourceBodies[number]);
      }).catch(error => { pageErrors.push(String(error)); }));
    }
  });
  try {
    await tab.goto(origin);
    const selector = tab.getByRole("combobox", { name: "Capability", exact: true });
    await selector.selectOption(capability);
    await tab.getByRole("link", { name: "Processes & decisions", exact: true }).click();
    await tab.getByRole("heading", { name: "Processes & decisions", exact: true }).waitFor();
    await tab.getByRole("button", { name: /process\.bpmn/ }).click();
    const frame = tab.frameLocator(".model-viewer-frame iframe");
    await frame.locator('.djs-element[data-element-id="Task_Review"]').waitFor();
    assert(await frame.locator('a[href*="bpmn.io"]').first().isVisible(), "required renderer watermark must remain visible");
    await tab.getByRole("button", { name: "Elements", exact: true }).click();
    await tab.locator("button[aria-pressed]").filter({ has: tab.getByText("Task_Review", { exact: true }) }).click();
    assert(await tab.getByText(commit, { exact: true }).first().isVisible());
    await tab.screenshot({ path: join(run, "database-bpmn.png"), fullPage: true });
    await tab.getByRole("button", { name: /decision\.dmn/ }).click();
    await tab.getByRole("button", { name: "Decision table", exact: true }).click();
    const table = tab.getByRole("table").first();
    await table.waitFor();
    const rows = table.locator("tbody tr");
    assert.equal(await rows.count(), 3);
    for (const [index, value] of ["deny", "manual", "approve"].entries())
      assert((await rows.nth(index).innerText()).includes(value), "original decision rule order and cells must remain visible");
    await Promise.all(pending);
    for (const item of models.items) {
      const body = sourceBodies.find(source => source.id === item.id);
      assert(body, "production browser must fetch each original source without API mocks");
      assert.equal(body.snapshot, models.snapshot);
      assert.equal(body.generation, models.generation);
      assert.equal(body.commitSha, commit);
      assert.equal(body.xml, originals.get(item.unitPath)!.toString("utf8"));
      assert.equal(body.sha256, digest(originals.get(item.unitPath)!));
    }
    assert(modelResponses.some(response => new URL(response.url).pathname === "/api/models/evidence"));
    assert(modelResponses.every(response => response.status === 200), JSON.stringify(modelResponses));
    assert.deepEqual(external, [], "untrusted labels/renderer must not request external content");
    assert.deepEqual(pageErrors, []);
    assert.equal(await tab.evaluate(() => (window as Window & { __issue78Pwned?: boolean }).__issue78Pwned), undefined);
    await tab.screenshot({ path: join(run, "database-dmn.png"), fullPage: true });
  } catch (error) {
    logs.push(await tab.locator("body").innerText(), JSON.stringify(modelResponses));
    await tab.screenshot({ path: join(run, "database-failure.png"), fullPage: true }).catch(() => undefined);
    throw error;
  }
  passed = true;
} catch (error) {
  failure = error instanceof Error ? error.message : String(error);
  throw error;
} finally {
  const cleanupErrors: string[] = [];
  try { await browser?.close(); } catch (error) { cleanupErrors.push(String(error)); }
  try { if (server) await stop(server); } catch (error) { cleanupErrors.push(String(error)); }
  try { if (container) command(engine, ["rm", "--force", "--volumes", container]); }
  catch (error) { cleanupErrors.push(String(error)); }
  await writeFile(join(run, "commands.log"), logs.join("\n"), { mode: 0o600 });
  await writeFile(join(run, "receipt.json"), JSON.stringify({
    passed: passed && cleanupErrors.length === 0, failure, cleanupErrors,
    sourceCommit: command("git", ["rev-parse", "HEAD"]), sourceStatus: command("git", ["status", "--porcelain"]),
    sourceHashes, image, modelResponses, external, runtime: process.version,
    qualification: "Actual model/source extraction -> collection -> both validators -> PostgreSQL migration/import/publication -> production database shell scope selection -> BPMN renderer and ordered DMN table; no API mocks",
    limitation: "Local synthetic permission declarations, not authenticated production authorization; no native-artifact UI or full-suite qualification.",
  }, null, 2), { mode: 0o600 });
  console.log(`DATABASE MODEL DOGFOOD ${passed && cleanupErrors.length === 0 ? "PASS" : "FAIL"}: ${join(run, "receipt.json")}`);
  assert.deepEqual(cleanupErrors, [], "owned browser/server/container must be cleaned up");
}
