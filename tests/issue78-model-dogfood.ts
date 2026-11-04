import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFile, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const root = resolve(import.meta.dirname, "..");
const cache = join(root, ".sandbox-cache", "model-viewer-dogfood");
await mkdir(cache, { recursive: true });
const run = await mkdtemp(join(cache, "run-"));
const logs: string[] = [];
const digest = (bytes: Uint8Array) => createHash("sha256").update(bytes).digest("hex");
const sourceHashes: Record<string, string> = {};
async function fingerprint(path: string): Promise<void> {
  const entries = await readdir(path, { withFileTypes: true });
  assert(entries.length <= 1_000, "bounded production asset inventory");
  for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    const child = join(path, entry.name);
    if (entry.isDirectory()) await fingerprint(child);
    else if (entry.isFile()) sourceHashes[child.slice(root.length + 1)] = digest(await readFile(child));
    else throw new Error(`Unsupported production asset entry: ${child}`);
  }
}
function command(program: string, args: string[], cwd = root) {
  const result = spawnSync(program, args, { cwd, encoding: "utf8", timeout: 60_000 });
  logs.push(`${program} ${args.join(" ")}\n${result.stdout ?? ""}${result.stderr ?? ""}`);
  assert.ifError(result.error);
  assert.equal(result.status, 0, logs.at(-1));
  return result.stdout.trim();
}
const ce = join(root, "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll");
const explorer = join(root, "platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/ce-explorer.dll");
for (const path of [ce, explorer, import.meta.filename])
  sourceHashes[path.slice(root.length + 1)] = digest(await readFile(path));
await fingerprint(join(root, "platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/wwwroot"));
const fixtures = join(root, "tests/fixtures/issue78");
const repo = join(run, "repo");
await mkdir(repo);
await copyFile(join(fixtures, "bpmn-representative.xml"), join(repo, "process.bpmn"));
await copyFile(join(fixtures, "dmn-representative.xml"), join(repo, "decision.dmn"));
command("git", ["init", "--quiet"], repo);
command("git", ["add", "."], repo);
command("git", ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
  "commit", "--quiet", "-m", "Seal public synthetic viewer models"], repo);
const commit = command("git", ["rev-parse", "HEAD"], repo);
const tree = command("git", ["rev-parse", "HEAD^{tree}"], repo);
const inputs: unknown[] = [];
for (const name of ["process.bpmn", "decision.dmn"]) {
  command(process.execPath, ["extractors/ts/src/models/cli.ts", "--source", join(repo, name),
    "--repository", "viewer-fixture", "--commit", commit, "--out", join(run, `${name}.json`),
    "--content-profile", "model-content", "--source-out", join(run, `${name}.source.json`)]);
  for (const suffix of [".json", ".source.json"]) {
    const path = name + suffix;
    const bytes = await readFile(join(run, path));
    const artifact = JSON.parse(bytes.toString("utf8"));
    inputs.push({ repositoryId: "viewer-fixture", profile: artifact.schema, path,
      unitPath: artifact.unitPath, sha256: digest(bytes), byteLength: bytes.length,
      required: true, contentProfile: "model-content" });
  }
}
await writeFile(join(run, "plan.json"), JSON.stringify({
  schema: "ce.collection.plan.v1", recipeSha256: "1".repeat(64), configSha256: "2".repeat(64),
  policySha256: "3".repeat(64), repositories: [{
    repositoryId: "viewer-fixture", providerId: "local", producerId: "viewer-dogfood",
    tenantRef: "synthetic", rightsRef: "explicit-local-fixture", commitSha: commit, treeSha: tree,
    permittedContentProfiles: ["model-content"],
  }], inputs,
}));
const collection = join(run, "collection");
command("dotnet", [ce, "collect", "--plan", join(run, "plan.json"), "--out", collection]);
command("dotnet", [ce, "validate-collection", collection]);
command(process.execPath, ["extractors/ts/src/collection/cli.ts", "validate", collection]);
await writeFile(join(run, "capabilities.json"), '{"capabilities":[]}');
const socket = createServer();
await new Promise<void>((done) => socket.listen(0, "127.0.0.1", done));
const address = socket.address();
assert(address && typeof address === "object");
const port = address.port;
await new Promise<void>((done, reject) => socket.close((error) => error ? reject(error) : done()));
const origin = `http://127.0.0.1:${port}`;
const server = spawn("dotnet", [explorer, "--urls", origin, "--source", "files",
  "--collections", collection, "--artifacts", join(run, "empty-native"),
  "--capabilities", join(run, "capabilities.json"), "--workspace", run], { cwd: root });
server.stdout.on("data", (data) => logs.push(data.toString()));
server.stderr.on("data", (data) => logs.push(data.toString()));
let browser: { close(): Promise<void> } | undefined;
try {
  let ready = false;
  for (let attempt = 0; attempt < 100; attempt++) {
    try { if ((await fetch(`${origin}/api/models`)).ok) { ready = true; break; } }
    catch (error) {
      if (!(error instanceof TypeError)) throw error;
    }
    if (server.exitCode !== null) throw new Error(`Explorer exited ${server.exitCode}\n${logs.join("")}`);
    await new Promise((done) => setTimeout(done, 100));
  }
  assert(ready, "actual Explorer must become responsive");
  const page = await (await fetch(`${origin}/api/models`)).json();
  assert.equal(page.items.length, 2);
  assert(page.items.every((item: { id: string }) => /^model:[a-f0-9]{64}$/.test(item.id)));
  const scope = new URLSearchParams({ snapshot: page.snapshot, generation: String(page.generation) });
  for (const item of page.items) {
    const response = await fetch(`${origin}/api/models/source?id=${encodeURIComponent(item.id)}&${scope}`);
    assert.equal(response.status, 200);
    const source = await response.json();
    assert.equal(source.commitSha, commit);
    assert.equal(digest(new TextEncoder().encode(source.xml)), source.sha256);
    assert.equal(source.xml, await readFile(join(repo, item.unitPath), "utf8"));
  }
  const { chromium } = await import(pathToFileURL(join(root, "spikes/likec4/node_modules/playwright/index.mjs")).href);
  const chromiumBrowser = await chromium.launch({ headless: true });
  browser = chromiumBrowser;
  const tab = await chromiumBrowser.newPage({ viewport: { width: 1440, height: 1000 } });
  const external: string[] = [];
  const modelResponses: { url: string; status: number }[] = [];
  tab.on("request", (request: { url(): string }) => {
    if (/^https?:/.test(request.url()) && new URL(request.url()).origin !== origin) external.push(request.url());
  });
  tab.on("response", (response: { url(): string; status(): number }) => {
    if (response.url().includes("/api/models")) modelResponses.push({ url: response.url(), status: response.status() });
  });
  try {
  await tab.goto(`${origin}/#/processes-decisions`);
  await tab.getByRole("heading", { name: "Processes & decisions" }).waitFor();
  await tab.getByRole("button", { name: /process\.bpmn/ }).click();
  await tab.frameLocator(".model-viewer-frame iframe").locator('.djs-element[data-element-id="Task_Review"]').waitFor();
  await tab.getByRole("button", { name: "Elements", exact: true }).click();
  await tab.locator("button[aria-pressed]").filter({ has: tab.getByText("Task_Review", { exact: true }) }).click();
  assert(await tab.getByText(commit, { exact: true }).first().isVisible());
  await tab.getByRole("button", { name: /decision\.dmn/ }).click();
  await tab.getByRole("button", { name: "Decision table", exact: true }).click();
  const table = tab.getByRole("table").first();
  await table.waitFor();
  assert((await table.locator("tbody tr").count()) >= 3);
  assert.equal(await table.getByRole("columnheader", { name: "Annotations" }).count(), 0);
  assert.deepEqual(external, []);
  assert(modelResponses.every((response) => response.status === 200), JSON.stringify(modelResponses));
  await tab.screenshot({ path: join(run, "actual-model-dogfood.png"), fullPage: true });
  await writeFile(join(run, "receipt.json"), JSON.stringify({
    source: command("git", ["rev-parse", "HEAD"]),
    trackedDiffSha256: digest(Buffer.from(command("git", ["diff", "HEAD", "--binary"]))),
    sourceHashes, commit, tree, origin,
    qualification: "Actual model/source CLI -> collection CLI -> both validators -> real Explorer API -> production browser; no API mocks",
    modelResponses, external, runtime: process.version,
  }, null, 2));
  console.log(`MODEL VIEWER DOGFOOD PASS:2 models,actual CLIs,both validators,real API/browser,0external; receipt ${join(run, "receipt.json")}`);
  } catch (error) {
    logs.push(await tab.locator("body").innerText(), JSON.stringify(modelResponses));
    await tab.screenshot({ path: join(run, "failed-model-dogfood.png"), fullPage: true });
    console.error(logs.slice(-2).join("\n"));
    throw error;
  }
} finally {
  await browser?.close();
  server.kill("SIGTERM");
  await new Promise<void>((done) => {
    if (server.exitCode !== null) done();
    else server.once("exit", () => done());
  });
  await writeFile(join(run, "commands.log"), logs.join("\n"));
  await rm(join(run, "repo", ".git"), { recursive: true, force: true });
}
