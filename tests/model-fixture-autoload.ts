import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { access, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:net";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { spawn } from "node:child_process";
import { verifyDecisionLinks, verifyDecisionTouch } from "./model-decision-links.ts";
import { verifyDecisionFailures } from "./model-decision-failures.ts";
import { verifyModelHoverPreviews } from "./model-hover-previews.ts";
import { verifyPreviewVersionGuard } from "./model-preview-loading.ts";
import { verifySnapshotRecovery } from "./model-snapshot-recovery.ts";

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
  for (let attempt = 0; attempt < 600; attempt++) {
    try {
      if ((await fetch(`${origin}/api/models`)).ok) { ready = true; break; }
    } catch (error) {
      if (!(error instanceof TypeError)) throw error;
    }
    if (child.exitCode !== null) throw new Error(`Explorer exited ${child.exitCode}\n${logs.join("")}`);
    await new Promise(done => setTimeout(done, 100));
  }
  assert(ready, `Explorer using the normal files-mode startup must become responsive\n${logs.join("")}`);

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
  const capture = async (name: string, fullPage = true) => {
    if (screenshotDir) await tab.screenshot({ path: join(screenshotDir, name), fullPage });
  };
  const external: string[] = [];
  tab.on("request", (request: { url(): string }) => {
    if (/^https?:/.test(request.url()) && new URL(request.url()).origin !== origin) external.push(request.url());
  });
  await tab.goto(`${origin}/#/processes-decisions`);
  await tab.getByRole("heading", { name: "Processes & decisions" }).waitFor();
  await tab.getByRole("status", { name: /Service health: Healthy/ }).waitFor();
  const healthBadge = tab.getByRole("status", { name: /Service health: Healthy/ });
  assert.equal(await healthBadge.getAttribute("aria-label"), "Service health: Healthy",
    "The compact badge label must not display uptime");
  assert.equal(await healthBadge.innerText(), "Healthy", "The compact badge must not display uptime");
  const healthDetails = tab.getByRole("region", { name: "Service health details" });
  await healthDetails.waitFor({ state: "hidden" });
  const healthToggle = tab.getByRole("button", { name: "Service health details", exact: true });
  await healthToggle.hover();
  await healthDetails.waitFor({ state: "visible" });
  assert.match(await healthDetails.innerText(), /Uptime/);
  assert.match(await healthDetails.innerText(), /Version/);
  assert.match(await healthDetails.innerText(), /Checked at/);
  await tab.getByRole("heading", { name: "Processes & decisions" }).hover();
  await healthDetails.waitFor({ state: "hidden" });
  await healthToggle.click();
  await tab.getByRole("heading", { name: "Processes & decisions" }).hover();
  await healthDetails.waitFor({ state: "visible" });
  await healthToggle.press("Escape");
  await healthDetails.waitFor({ state: "hidden" });
  await tab.getByRole("button", { name: /order\.bpmn/ }).click();
  await tab.getByText("BPMN model evidence", { exact: true }).waitFor();
  await tab.getByRole("status").filter({ hasText: "Rendered" }).waitFor();
  const orderFrame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  const orderLabels = orderFrame.locator(".djs-label:visible");
  await orderLabels.first().waitFor();
  assert.match((await orderLabels.allTextContents()).join(" "), /review/i);
  assert.match((await orderFrame.locator('[data-element-id="order-participant"] .djs-label tspan').allTextContents()).join(" "), /Order review/,
    "Given the order process, its pool must display the authored process name");
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
  await workflowLabels.first().waitFor().catch(async error => {
    await capture("workflow-render-failure.png");
    console.error(await tab.locator("body").innerText(), await workflowFrame.locator("body").innerText());
    throw error;
  });
  assert.match((await workflowLabels.allTextContents()).join(" "), /review/i);
  for (const [id, label] of [
    ["order-participant", "Order fulfilment"],
    ["nested-work", "Prepare order"],
    ["call-child", "Create shipment"],
  ]) {
    assert.equal((await workflowFrame.locator(`[data-element-id="${id}"] .djs-label tspan`).allTextContents()).join(" "), label);
  }
  const workflowItem = page.items.find((item: { unitPath: string }) => item.unitPath === "workflow.bpmn");
  const workflowSource = await (await fetch(`${origin}/api/models/source?id=${encodeURIComponent(workflowItem.id)}&${selection}`)).json();
  assert.match(workflowSource.xml, /id="route-call" sourceRef="route-gateway" targetRef="nested-work"/,
    "The parent flow must enter the subprocess, not bypass it");
  assert.match(workflowSource.xml, /sourceRef="nested-work" targetRef="call-child"/,
    "The parent flow must continue after the subprocess completes");
  assert.doesNotMatch(workflowSource.xml, /child-participant|review-message/,
    "The synthetic caller must not also depict Shipment as a communicating participant");
  assert.equal(await workflowFrame.locator('[data-element-id="child-task"]:visible').count(), 0,
    "Called-process detail belongs in its own authored diagram, not the overview");
  await capture("workflow-bpmn.png");
  const subprocessButton = workflowFrame.getByRole("button", { name: "Open Prepare order" });
  const subprocessBounds = await workflowFrame.locator('[data-element-id="nested-work"] .djs-visual').boundingBox();
  const drillBounds = await subprocessButton.boundingBox();
  assert(subprocessBounds && drillBounds && drillBounds.x >= subprocessBounds.x && drillBounds.y >= subprocessBounds.y &&
    drillBounds.x + drillBounds.width <= subprocessBounds.x + subprocessBounds.width &&
    drillBounds.y + drillBounds.height <= subprocessBounds.y + subprocessBounds.height,
    "The drill button must be inside the box, over its existing plus marker");
  await subprocessButton.focus();
  await subprocessButton.press("Enter");
  await workflowFrame.locator('[data-element-id="nested-task"]:visible').waitFor();
  assert.equal((await workflowFrame.locator('[data-element-id="nested-task"] .djs-label tspan').allTextContents()).join(" "), "Reserve inventory");
  for (const id of ["nested-start-task", "nested-task-end"]) {
    const connection = workflowFrame.locator(`[data-element-id="${id}"] .djs-visual > path`);
    await connection.waitFor({ state: "attached" });
    assert(await connection.evaluate((path: SVGPathElement) => {
      for (let element: Element | null = path; element; element = element.parentElement) {
        const style = getComputedStyle(element);
        if (style.display === "none" || style.visibility === "hidden") return false;
      }
      return path.getTotalLength() > 0;
    }),
      `Internal sequence flow ${id} must have a painted path on the active plane`);
  }
  await workflowFrame.locator('[data-element-id="review-task"]').waitFor({ state: "hidden" });
  await workflowFrame.locator('[data-element-id="nested-task"]').click();
  await tab.getByRole("button", { name: "Insights", exact: true }).click();
  await tab.getByRole("region", { name: "Selected element provenance" }).getByText("nested-task · task", { exact: true }).waitFor();
  await capture("workflow-subprocess-insights.png");
  await tab.getByRole("button", { name: "Insights", exact: true }).click();
  await workflowFrame.getByRole("button", { name: "Back to Order fulfilment", exact: true }).click();
  await workflowFrame.locator('[data-element-id="nested-work"].issue78-selected:visible').waitFor();
  await workflowFrame.locator('[data-element-id="nested-task"]').waitFor({ state: "hidden" });
  await capture("workflow-subprocess-return.png");
  await workflowFrame.locator('[data-element-id="nested-work"]').dblclick();
  await workflowFrame.locator('[data-element-id="nested-task"]:visible').waitFor();
  await workflowFrame.getByRole("button", { name: "Back to Order fulfilment", exact: true }).click();
  await workflowFrame.locator('[data-element-id="call-child"]').click();
  await workflowFrame.locator('[data-element-id="call-child"].issue78-selected').waitFor();
  await workflowFrame.getByRole("button", { name: "Back to Create shipment" }).waitFor({ state: "hidden" });
  const callView = await workflowFrame.locator(".viewport").getAttribute("transform");
  await tab.evaluate(() => {
    const frame = document.querySelector<HTMLIFrameElement>('iframe[title="BPMN read-only model"]');
    window.addEventListener("message", event => {
      if (frame && event.source === frame.contentWindow && event.data?.status === "navigation") {
        frame.dataset.navigation = JSON.stringify(event.data);
      }
    });
  });
  const callButton = workflowFrame.getByRole("button", { name: "Go to called process Shipment", exact: true });
  await callButton.click();
  await workflowFrame.locator('[data-element-id="child-task"]:visible').waitFor();
  await workflowFrame.getByText("Called process: Shipment", { exact: true }).waitFor();
  await workflowFrame.locator('[data-element-id="review-task"]').waitFor({ state: "hidden" });
  const backToCall = workflowFrame.getByRole("button", { name: "Back to Create shipment" });
  await backToCall.waitFor();
  await subprocessButton.waitFor({ state: "hidden" });
  const calledBounds = await workflowFrame.locator('[data-element-id="child-task"] .djs-visual').boundingBox();
  await tab.getByRole("button", { name: "Fit diagram", exact: true }).click();
  const fittedBounds = await workflowFrame.locator('[data-element-id="child-task"] .djs-visual').boundingBox();
  assert(calledBounds && fittedBounds && fittedBounds.width >= calledBounds.width * 0.8,
    "Fit must keep the called process readable rather than fitting hidden callers");
  await capture("workflow-called-shipment.png");
  await backToCall.click();
  await workflowFrame.locator('[data-element-id="call-child"].issue78-selected').waitFor();
  assert.equal(await workflowFrame.locator(".viewport").getAttribute("transform"), callView,
    "Returning from a call must restore the caller's viewport");
  await subprocessButton.waitFor();
  await workflowFrame.locator('[data-element-id="call-child"]').dblclick();
  await backToCall.waitFor();
  await tab.evaluate(() => {
    const frame = document.querySelector<HTMLIFrameElement>('iframe[title="BPMN read-only model"]');
    if (!frame?.dataset.navigation || !frame.contentWindow) throw new Error("Missing viewer navigation identity");
    frame.contentWindow.postMessage({
      ...JSON.parse(frame.dataset.navigation), status: "select-element", elementId: "review-task",
    }, "*");
  });
  await workflowFrame.locator('[data-element-id="review-task"].issue78-selected:visible').waitFor();
  await backToCall.waitFor({ state: "hidden" });
  await subprocessButton.waitFor();
  const unchangedSource = await (await fetch(`${origin}/api/models/source?id=${encodeURIComponent(workflowItem.id)}&${selection}`)).json();
  assert.equal(unchangedSource.xml, workflowSource.xml, "Read-only navigation must not mutate authored XML");
  await tab.getByRole("button", { name: /decision\.dmn/ }).click();
  await tab.getByText("DMN model evidence", { exact: true }).waitFor();
  const decisionFrame = tab.frameLocator('iframe[title="DMN read-only model"]');
  await decisionFrame.locator(".djs-element").first().waitFor();
  assert.match(await decisionFrame.locator("body").innerText(), /eligibility/i);
  const decisionBounds = await decisionFrame.locator('[data-element-id="eligibility"] .djs-visual').boundingBox();
  const decisionCanvas = await tab.locator(".model-viewer-frame").boundingBox();
  assert(decisionBounds && decisionCanvas && decisionBounds.x >= decisionCanvas.x &&
    decisionBounds.y >= decisionCanvas.y && decisionBounds.x + decisionBounds.width <= decisionCanvas.x + decisionCanvas.width &&
    decisionBounds.y + decisionBounds.height <= decisionCanvas.y + decisionCanvas.height,
    "Given a DMN model, the decision must be inside the visible canvas, not merely present in the DOM");
  await capture("decision-dmn.png");
  assert((await decisionFrame.locator(".djs-element").count()) >= 1);
  await tab.getByRole("button", { name: "Decision table", exact: true }).click();
  const table = tab.getByRole("table").first();
  await table.waitFor();
  assert((await table.locator("tbody tr").count()) >= 2);
  assert.match(await table.innerText(), /18/);
  await tab.getByRole("button", { name: /decision-rich\.dmn/ }).click();
  await tab.getByRole("button", { name: "Decision requirements", exact: true }).click();
  const richFrame = tab.frameLocator('iframe[title="DMN read-only model"]');
  await richFrame.locator(".djs-element").first().waitFor();
  assert.match(await richFrame.locator("body").innerText(), /order decision/i);
  assert((await richFrame.locator(".djs-element").count()) >= 1);
  await capture("decision-rich-dmn.png");
  const { extractModelXml } = await import("../extractors/ts/src/models/extract.ts");
  const routeModelVariant = async (item: { id: string; repositoryId: string; commitSha: string; unitPath: string }, xml: string) => {
    const evidence = await extractModelXml(xml, {
      repositoryId: item.repositoryId, commitSha: item.commitSha,
      unitPath: item.unitPath, contentProfile: "model-content",
    });
    await tab.route("**/api/models**", async route => {
      const request = new URL(route.request().url());
      const response = await route.fetch();
      const body = await response.json();
      if (request.pathname === "/api/models") {
        const summary = body.items.find((candidate: { id: string }) => candidate.id === item.id);
        summary.source.sha256 = evidence.sourceSha256;
        summary.coverage = evidence.coverage.status;
      } else if (request.searchParams.get("id") === item.id) {
        if (request.pathname === "/api/models/evidence") body.evidence = evidence;
        if (request.pathname === "/api/models/source") Object.assign(body, { xml, sha256: evidence.sourceSha256 });
      }
      await route.fulfill({ response, json: body });
    });
  };
  for (const [calledElement, message] of [
    ["missing-process", "The called process was not found in this file."],
    ["other.bpmn#remote", "This call points outside the file. External models are not fetched."],
  ]) {
    const xml = workflowSource.xml.replace('calledElement="child-process"', `calledElement="${calledElement}"`);
    await routeModelVariant(workflowItem, xml);
    await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(workflowItem.id)}&tab=diagram`);
    await tab.reload();
    await workflowFrame.getByRole("button", { name: `Go to called process ${calledElement}`, exact: true }).click();
    await tab.locator("#model-insights-panel").getByRole("status").filter({ hasText: message }).waitFor();
    await tab.unroute("**/api/models**");
  }
  await routeModelVariant(workflowItem, workflowSource.xml.replace(
    /  <bpmndi:BPMNDiagram id="shipment-diagram">[\s\S]*?<\/bpmndi:BPMNDiagram>/, ""));
  await tab.reload();
  await workflowFrame.getByRole("button", { name: "Go to called process Shipment", exact: true }).hover();
  const missingDiagram = workflowFrame.getByRole("region", { name: "Process detail insight", exact: true });
  await missingDiagram.getByRole("alert").filter({ hasText: /called process has no available diagram/ }).waitFor();
  await missingDiagram.getByRole("button", { name: "Open called process", exact: true }).click();
  await tab.locator("#model-insights-panel").getByRole("status")
    .filter({ hasText: "The called process is defined in this file but has no available diagram." }).waitFor();
  await workflowFrame.locator('[data-element-id="review-task"]:visible').waitFor();
  await tab.unroute("**/api/models**");
  await tab.reload();
  await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(workflowItem.id)}&tab=elements`);
  await tab.reload();
  const inventory = tab.getByRole("region", { name: "Captured model elements" });
  await inventory.getByRole("button", { name: "This diagram", exact: true }).waitFor();
  assert.equal(await inventory.getByText("Not rendered in current viewer projection.", { exact: true }).count(), 0);
  assert.equal(await inventory.locator('[data-model-element="nested-task"]').count(), 0);
  await inventory.getByRole("button", { name: "All captured source evidence", exact: true }).click();
  const nestedRecord = inventory.locator('[data-model-element="nested-task"]');
  await nestedRecord.getByText("Placed in another diagram in this source.", { exact: true }).waitFor();
  await inventory.locator('[data-model-element="review-record-ref"]').getByText(/No diagram placement captured/).waitFor();
  await inventory.locator('[data-model-element="review-task"]').getByText(/Lane \(as authored\): Review/).waitFor();
  assert.equal(await inventory.locator('[data-model-element]').filter({ hasText: "BPMNShape" }).count(), 0);
  const technical = inventory.locator("details").filter({ has: tab.locator("summary", { hasText: "Diagram layout (technical)" }) });
  assert.equal(await technical.getAttribute("open"), null);
  await capture("council-elements-all.png", false);
  await nestedRecord.getByRole("button", { name: "Show in Prepare order", exact: true }).click();
  await workflowFrame.locator('[data-element-id="nested-task"].issue78-selected:visible').waitFor();
  assert.equal(await tab.getByLabel("Diagram 2 of 3").inputValue(), "preparation-diagram");
  await capture("council-open-nested.png", false);
  await tab.getByRole("button", { name: "Elements", exact: true }).click();
  await inventory.locator('[data-model-element="nested-task"]').getByText("Placed in this diagram.", { exact: true }).waitFor();
  await tab.getByLabel("Diagram 2 of 3").selectOption("shipment-diagram");
  await workflowFrame.locator('[data-element-id="child-task"]:visible').waitFor();
  await tab.getByRole("button", { name: "Elements", exact: true }).click();
  await inventory.getByRole("button", { name: "This diagram", exact: true }).click();
  await inventory.locator('[data-model-element="child-task"]').getByText("Placed in this diagram.", { exact: true }).waitFor();
  assert.equal(await inventory.locator('[data-model-element="review-task"]').count(), 0);
  await tab.getByLabel("Diagram 3 of 3").selectOption("workflow-diagram");
  await workflowFrame.locator('[data-element-id="review-task"]:visible').waitFor();
  await tab.getByRole("button", { name: /decision-rich\.dmn/ }).click();
  await tab.getByRole("button", { name: "Elements", exact: true }).click();
  await inventory.getByRole("button", { name: "All captured source evidence", exact: true }).click();
  await inventory.locator('[data-model-element="adult-rule"]').getByText(/Decision logic; available/).waitFor();
  await inventory.locator('[data-model-element="adult-rule"]').getByRole("button", { name: "Open decision logic", exact: true }).click();
  await tab.locator("#model-logic-order-table").getByRole("heading", { name: "order-table", exact: true }).waitFor();
  await capture("council-decision-logic.png", false);
  await tab.getByRole("button", { name: "Decision requirements", exact: true }).click();
  await tab.route("**/api/models/source?**", route => route.fulfill({ status: 403, contentType: "application/json", json: { detail: "Source access denied" } }));
  await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(workflowItem.id)}&tab=elements`);
  await tab.reload();
  await inventory.getByRole("button", { name: "All captured source evidence", exact: true }).click();
  await inventory.locator('[data-model-element="review-record-ref"]').waitFor();
  assert.equal(await tab.locator('iframe[title="BPMN read-only model"]').count(), 0, "Denied source must never reach renderer");
  await tab.getByRole("button", { name: "Diagram", exact: true }).click();
  await tab.getByRole("alert").getByText(/Access to this source is denied/).waitFor();
  await tab.unroute("**/api/models/source?**");
  const richItem = page.items.find((item: { unitPath: string }) => item.unitPath === "decision-rich.dmn");
  const richSource = await (await fetch(`${origin}/api/models/source?id=${encodeURIComponent(richItem.id)}&${selection}`)).json();
  const twoDiagrams = richSource.xml.replace("</dmndi:DMNDI>", `<dmndi:DMNDiagram id="secondary-diagram" name="Secondary decision view">
    <dmndi:DMNShape id="secondary-order-shape" dmnElementRef="order-decision"><dc:Bounds x="100" y="100" width="160" height="80"/></dmndi:DMNShape>
    </dmndi:DMNDiagram></dmndi:DMNDI>`);
  await routeModelVariant(richItem, twoDiagrams);
  await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(richItem.id)}&tab=elements`);
  await tab.reload();
  await inventory.locator('[data-model-element="order-decision"]').getByRole("button", { name: "Show in Secondary decision view", exact: true }).click();
  await richFrame.locator('[data-element-id="order-decision"].issue78-selected:visible').waitFor();
  await richFrame.locator('[data-element-id="age-data"]').waitFor({ state: "hidden" });
  await tab.unroute("**/api/models**");
  await tab.reload();
  for (const [width, height] of [[390, 844], [768, 700], [1024, 768], [1440, 1000]]) {
    await tab.setViewportSize({ width, height });
    await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(workflowItem.id)}&element=nested-work`);
    const diagramTab = tab.getByRole("button", { name: "Diagram", exact: true });
    await diagramTab.waitFor();
    assert.equal(await diagramTab.getAttribute("aria-pressed"), "true",
      `Given a ${width}px deep link without tab, the diagram must be the default`);
    await workflowFrame.locator('[data-element-id="nested-work"].issue78-selected').waitFor();
    const activeFrame = tab.frames().find(frame => frame.parentFrame());
    assert(activeFrame);
    await activeFrame.waitForFunction(() => {
      const pool = document.querySelector('[data-element-id="order-participant"] .djs-visual')?.getBoundingClientRect();
      return !!pool && pool.width >= Math.min(800, innerWidth * 0.8);
    });
    const canvasBounds = await tab.locator(".model-viewer-frame").boundingBox();
    assert(canvasBounds && canvasBounds.y < height * 0.6 && canvasBounds.y + canvasBounds.height <= height + 1,
      `Diagram must fit within ${width}x${height}, got ${JSON.stringify(canvasBounds)}`);
    assert(await tab.evaluate(() => document.documentElement.scrollWidth <= innerWidth), "No page horizontal overflow");
    const toolbar = await tab.getByRole("navigation", { name: "Model evidence", exact: true }).boundingBox();
    assert(toolbar && toolbar.height <= 52, "Model toolbar must stay compact");
    await tab.getByRole("button", { name: "Insights", exact: true }).click();
    await tab.waitForFunction(() => {
      const workspace = document.querySelector(".model-workspace")?.getBoundingClientRect();
      return !!workspace && (innerWidth >= 1280 || workspace.top < 70);
    });
    const panel = await insights.boundingBox();
    await capture(`workflow-${width}-insights.png`, false);
    assert(panel && panel.y >= 52 && panel.y + panel.height <= height + 1,
      `Insights must stay on-screen at ${width}px, got ${JSON.stringify(panel)}`);
    await insights.getByRole("region", { name: "Selected element provenance" }).getByText("Prepare order", { exact: true }).waitFor();
    await tab.keyboard.press("Escape");
    await insights.waitFor({ state: "hidden" });
    await capture(`workflow-${width}.png`, false);
  }
  await tab.setViewportSize({ width: 1440, height: 1000 });
  await tab.getByRole("button", { name: "Zoom in", exact: true }).click();
  const manualZoom = await workflowFrame.locator(".viewport").getAttribute("transform");
  await tab.setViewportSize({ width: 1024, height: 768 });
  const resizedFrame = tab.frames().find(frame => frame.parentFrame());
  assert(resizedFrame);
  await resizedFrame.waitForFunction(() =>
    document.querySelector(".djs-container > svg")?.getBoundingClientRect().width === innerWidth);
  assert.equal(await workflowFrame.locator(".viewport").getAttribute("transform"), manualZoom,
    "Explicit user zoom must survive viewport resize");
  await tab.getByRole("button", { name: "Fit diagram", exact: true }).click();
  await tab.getByRole("button", { name: "Elements", exact: true }).click();
  await tab.reload();
  assert.equal(await tab.getByRole("button", { name: "Elements", exact: true }).getAttribute("aria-pressed"), "true",
    "Explicit Elements selection must remain available after reload");
  const picker = tab.locator("details").filter({ has: tab.locator("summary", { hasText: "Model picker" }) });
  await picker.locator("summary").click();
  await picker.getByRole("button", { name: /decision\.dmn/ }).click();
  assert.equal(await picker.getAttribute("open"), null, "Compact model picker must close after selection");
  await tab.getByRole("button", { name: "Decision requirements", exact: true }).click();
  await decisionFrame.locator('[data-element-id="eligibility"]').waitFor();
  await tab.setViewportSize({ width: 1440, height: 1000 });
  await tab.route("**/health", (route) => route.fulfill({
    status: 503,
    contentType: "application/json",
    body: JSON.stringify({ status: "unhealthy", version: "1.0.0", uptime_seconds: 42, timestamp: new Date().toISOString() }),
  }));
  await tab.reload();
  await tab.getByRole("status", { name: "Service health: Unhealthy", exact: true }).waitFor();
  await healthToggle.click();
  await healthDetails.getByText("42s", { exact: true }).waitFor();
  await capture("health-details-unhealthy.png");
  await tab.unroute("**/health");
  await tab.route("**/health", (route) => route.fulfill({ status: 429, body: "Too many requests" }));
  await tab.reload();
  await tab.getByRole("status", { name: "Service health: Unavailable", exact: true }).waitFor();
  await healthToggle.click();
  await healthDetails.getByRole("alert").getByText("Health endpoint returned HTTP 429", { exact: true }).waitFor();
  await tab.unroute("**/health");
  await verifyDecisionLinks(tab, origin, page.items, capture);
  await verifyDecisionFailures(tab, origin, page.items, routeModelVariant, capture);
  await verifyModelHoverPreviews(tab, origin, page.items, capture);
  const touchTab = await browser.newPage({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  try { await verifyDecisionTouch(touchTab, origin, page.items); }
  finally { await touchTab.close(); }
  const legacyTab = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  try { await verifyPreviewVersionGuard(legacyTab, origin, page.items); }
  finally { await legacyTab.close(); }
  const restartTab = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  try { await verifySnapshotRecovery(restartTab, origin, page.items); }
  finally { await restartTab.close(); }
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
