import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { access, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn } from "node:child_process";

const root = resolve(import.meta.dirname, "..");
const collection = resolve(root, process.env.MODEL_COLLECTIONS ?? ".sandbox-cache/model-fixtures/collection");
const scratch = join(root, ".sandbox-cache", "model-fixture-autoload-test");
const screenshotDir = process.env.MODEL_FIXTURE_SCREENSHOT_DIR
  ? resolve(process.env.MODEL_FIXTURE_SCREENSHOT_DIR)
  : undefined;
const sha256 = (bytes: Uint8Array) => createHash("sha256").update(bytes).digest("hex");
const logs: string[] = [];

await access(join(collection, "collection.json"));
const nativeMake = await readFile(join(root, "config/build/native.mk"), "utf8");
const sandboxMake = await readFile(join(root, "config/build/sandbox.mk"), "utf8");
for (const makefile of [nativeMake, sandboxMake]) {
  assert.match(makefile, /explore: fixtures build model-fixtures/);
  assert.ok(makefile.includes('--source $(EXPLORER_SOURCE) --artifacts $(ARTIFACTS) --collections "$(MODEL_COLLECTIONS)"'));
  assert.match(makefile, /dev: dev-data/);
  assert.match(makefile, /--no-print-directory explore CE_DATABASE=/);
}

await mkdir(scratch, { recursive: true });
const capabilities = join(scratch, "capabilities.json");
await writeFile(capabilities, '{"capabilities":[]}\n');

const socket = createServer();
await new Promise<void>((done) => socket.listen(0, "127.0.0.1", done));
const address = socket.address();
assert(address && typeof address === "object");
const port = address.port;
await new Promise<void>((done, reject) => socket.close((error) => error ? reject(error) : done()));
const origin = `http://127.0.0.1:${port}`;
const explorer = join(root, "platform/src/CapabilityExplorer.Explorer/bin/Debug/net10.0/ce-explorer.dll");
const explorerEnvironment = { ...process.env };
delete explorerEnvironment.CE_DATABASE;
explorerEnvironment.EXPLORER_SOURCE = "files";
const child = spawn(process.execPath, [
  join(root, "scripts/explorer-host.mjs"), explorer,
  "--urls", origin,
  "--source", "files",
  "--collections", collection,
  "--artifacts", join(root, "fixtures/expected"),
  "--capabilities", capabilities,
  "--workspace", join(scratch, "workspace"),
], {
  cwd: root,
  env: explorerEnvironment,
});
child.stdout.on("data", data => logs.push(data.toString()));
child.stderr.on("data", data => logs.push(data.toString()));
let browser: { close(): Promise<void> } | undefined;
try {
  let ready = false;
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      if ((await fetch(`${origin}/api/models`)).ok) { ready = true; break; }
    } catch (error) {
      if (!(error instanceof TypeError)) throw error;
    }
    if (child.exitCode !== null) throw new Error(`Explorer exited ${child.exitCode}\n${logs.join("")}`);
    await new Promise(done => setTimeout(done, 100));
  }
  assert(ready, "Explorer using the normal files-mode startup must become responsive");

  const page = await (await fetch(`${origin}/api/models`)).json();
  assert.equal(page.items.length, 4);
  assert.deepEqual([...new Set(page.items.map((item: { modelKind: string }) => item.modelKind))].sort(), ["bpmn", "dmn"]);
  assert.equal(new Set(page.items.map((item: { commitSha: string }) => item.commitSha)).size, 1);
  assert(page.items.every((item: { source: { status: string } }) => item.source.status === "available"));
  const selection = new URLSearchParams({ snapshot: page.snapshot, generation: String(page.generation) });
  for (const item of page.items) {
    const response = await fetch(`${origin}/api/models/source?id=${encodeURIComponent(item.id)}&${selection}`);
    assert.equal(response.status, 200);
    const source = await response.json();
    assert.equal(source.commitSha, item.commitSha);
    assert.equal(source.sha256, sha256(Buffer.from(source.xml)));
    assert.equal(source.xml, await readFile(join(root, "fixtures/models", item.unitPath), "utf8"));
  }

  let playwrightModule: string | undefined;
  try {
    await access(join(root, "spikes/likec4/node_modules/playwright/index.mjs"));
    playwrightModule = join(root, "spikes/likec4/node_modules/playwright/index.mjs");
  } catch { /* the browser image may provide its own compatible installation */ }
  assert(playwrightModule, "The existing Playwright installation is required.");
  const { chromium } = await import(pathToFileURL(playwrightModule).href);
  browser = await chromium.launch({ headless: true });
  const tab = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  if (screenshotDir) await mkdir(screenshotDir, { recursive: true });
  const capture = async (name: string) => {
    if (screenshotDir) await tab.screenshot({ path: join(screenshotDir, name), fullPage: true });
  };
  const external: string[] = [];
  tab.on("request", (request: { url(): string }) => {
    if (/^https?:/.test(request.url()) && new URL(request.url()).origin !== origin) external.push(request.url());
  });
  await tab.goto(`${origin}/#/processes-decisions`);
  await tab.getByRole("heading", { name: "Processes & decisions" }).waitFor();
  await tab.getByRole("status", { name: /Service health: Healthy/ }).waitFor();
  await tab.getByRole("button", { name: /order\.bpmn/ }).click();
  await tab.getByText("BPMN model evidence", { exact: true }).waitFor();
  await tab.getByRole("status").filter({ hasText: "Rendered" }).waitFor();
  const orderFrame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  const orderLabels = orderFrame.locator(".djs-label:visible");
  await orderLabels.first().waitFor();
  assert.match((await orderLabels.allTextContents()).join(" "), /review/i);
  const insights = tab.getByRole("complementary", { name: "Model insights" });
  await insights.waitFor({ state: "hidden" });
  const orderCanvas = await tab.locator(".model-viewer-frame").boundingBox();
  assert(orderCanvas && orderCanvas.width >= 560 && orderCanvas.height >= 600,
    `BPMN canvas should use the available window space, got ${JSON.stringify(orderCanvas)}`);
  await capture("order-bpmn.png");
  await tab.getByRole("button", { name: "Insights", exact: true }).click();
  await insights.waitFor({ state: "visible" });
  const insightsBounds = await insights.boundingBox();
  assert(insightsBounds && orderCanvas && insightsBounds.x > orderCanvas.x + orderCanvas.width / 2,
    "Model insights should overlay the right side of the diagram canvas");
  await capture("order-bpmn-insights.png");
  await tab.getByRole("button", { name: "Insights", exact: true }).click();
  await insights.waitFor({ state: "hidden" });
  await tab.getByRole("button", { name: "Elements", exact: true }).click();
  await tab.getByText("review", { exact: true }).first().waitFor();
  await tab.getByRole("button", { name: /workflow\.bpmn/ }).click();
  await tab.getByRole("button", { name: "Diagram", exact: true }).click();
  const workflowFrame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  const workflowLabels = workflowFrame.locator(".djs-label:visible");
  await workflowLabels.first().waitFor();
  assert.match((await workflowLabels.allTextContents()).join(" "), /review/i);
  await capture("workflow-bpmn.png");
  await tab.getByRole("button", { name: /decision\.dmn/ }).click();
  await tab.getByText("DMN model evidence", { exact: true }).waitFor();
  const decisionFrame = tab.frameLocator('iframe[title="DMN read-only model"]');
  await decisionFrame.locator(".djs-element").first().waitFor();
  assert.match(await decisionFrame.locator("body").innerText(), /eligibility/i);
  await capture("decision-dmn.png");
  assert((await decisionFrame.locator(".djs-element").count()) >= 1);
  await tab.getByRole("button", { name: "Decision table", exact: true }).click();
  const table = tab.getByRole("table").first();
  await table.waitFor();
  assert((await table.locator("tbody tr").count()) >= 2);
  assert.match(await table.innerText(), /18/);
  await tab.getByRole("button", { name: /decision-rich\.dmn/ }).click();
  await tab.getByRole("button", { name: "Requirements", exact: true }).click();
  const richFrame = tab.frameLocator('iframe[title="DMN read-only model"]');
  await richFrame.locator(".djs-element").first().waitFor();
  assert.match(await richFrame.locator("body").innerText(), /order decision/i);
  assert((await richFrame.locator(".djs-element").count()) >= 1);
  await capture("decision-rich-dmn.png");
  await tab.route("**/health", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ status: "unhealthy", version: "1.0.0", uptime_seconds: 42, timestamp: new Date().toISOString() }),
  }));
  await tab.reload();
  await tab.getByRole("status", { name: "Service health: Unhealthy · 42s uptime" }).waitFor();
  await tab.unroute("**/health");
  assert.deepEqual(external, []);
  console.log("MODEL FIXTURE AUTOLOAD PASS: four validated fixtures, both BPMN/DMN kinds listed and viewable, source consent/revision verified, no external requests");
} finally {
  await browser?.close();
  child.kill("SIGTERM");
  await new Promise<void>((done) => {
    if (child.exitCode !== null) done();
    else child.once("exit", () => done());
  });
  await rm(scratch, { recursive: true, force: true });
}
