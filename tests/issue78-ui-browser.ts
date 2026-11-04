import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { access, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { gzipSync } from "node:zlib";
import { build, preview } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import { createUiModelEvidence } from "./fixtures/issue78/ui-evidence.ts";
import { installIssue78UiApiRoutes, type UiApiState } from "./fixtures/issue78/ui-api-routes.ts";
import "./issue78-model-ui-quality.ts";

const root = resolve(import.meta.dirname, "..");
const client = resolve(root, "platform/src/CapabilityExplorer.Explorer/client");
const artifacts = resolve(root, ".sandbox-cache/issue78-evidence");
await mkdir(artifacts, { recursive: true });
const output = resolve(artifacts, "issue78-ui-implementation-browser");
const fixture = resolve(root, "tests/fixtures/issue78");
let playwrightModule: string | undefined;
for (const candidate of [
  resolve(root, "spikes/likec4/node_modules/playwright/index.mjs"),
  resolve(root, "../bpmn-dmn-plan/spikes/likec4/node_modules/playwright/index.mjs")
]) {
  try { await access(candidate); playwrightModule = candidate; break; } catch { /* try next existing browser tool */ }
}
assert(playwrightModule, "An existing Playwright installation is required.");
const { chromium } = await import(pathToFileURL(playwrightModule).href);
const bpmnXml = await readFile(resolve(fixture, "bpmn-representative.xml"), "utf8");
const dmnXml = await readFile(resolve(fixture, "dmn-representative.xml"), "utf8");
const sha256 = (value: string) => createHash("sha256").update(value).digest("hex");
const bpmnHash = sha256(bpmnXml);
const dmnHash = sha256(dmnXml);
const noDiXml = bpmnXml.replace(/<bpmndi:BPMNDiagram[\s\S]*?<\/bpmndi:BPMNDiagram>/, "");
const noDiHash = sha256(noDiXml);
const revisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const revisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const selection = { "repo-a": revisionA };
const snapshot = "snapshot-ui-a";
const { items, evidenceById, xmlById } = createUiModelEvidence({ bpmnHash, dmnHash, revisionA, revisionB, noDiHash, bpmnXml, dmnXml });
const summariesHash = await readFile(resolve(client, "package-lock.json"), "utf8").then((raw) => createHash("sha256").update(raw).digest("hex"));
const assertions: string[] = [];
const timingsMs: Record<string, number> = {};
const requests: string[] = [];
const sourceRequestIds: string[] = [];
const apiState: UiApiState = { databaseMode: false, requests, sourceRequestIds };
const externalRequests: string[] = [];
const consoleErrors: string[] = [];
const failedResponses: string[] = [];

await build({
  configFile: resolve(client, "vite.config.ts"),
  root: client,
  build: {
    outDir: output,
    emptyOutDir: true,
    sourcemap: false,
    rollupOptions: {
      input: {
        app: resolve(client, "index.html"),
      },
      output: {
        entryFileNames: "assets/[name].js",
        chunkFileNames: "assets/[name].js",
        assetFileNames: "assets/[name][extname]"
      }
    }
  },
  logLevel: "warn"
});
const assetDir = resolve(output, "assets");
const assetNames = await readdir(assetDir);
assert(assetNames.includes("model-viewer-frame.js") && assetNames.includes("model-viewer-frame.css"));
const lazyViewAssets = assetNames.filter((name) => /ProcessesDecisions|ModelEvidence/.test(name) && name.endsWith(".js"));
assert(lazyViewAssets.length > 0, "Processes & decisions must remain a lazy route chunk.");
const js = await Promise.all(assetNames.filter((name) => name.endsWith(".js")).map((name) => readFile(resolve(assetDir, name))));
const css = await Promise.all(assetNames.filter((name) => name.endsWith(".css")).map((name) => readFile(resolve(assetDir, name))));
const bundle = {
  appJsBytes: (await readFile(resolve(assetDir, "app.js"))).byteLength,
  frameJsBytes: (await readFile(resolve(assetDir, "model-viewer-frame.js"))).byteLength,
  lazyViewJsBytes: (await Promise.all(lazyViewAssets.map((name) => readFile(resolve(assetDir, name))))).reduce((total, file) => total + file.byteLength, 0),
  lazyViewAssets,
  allJsGzipBytes: gzipSync(Buffer.concat(js)).byteLength,
  frameCssBytes: (await readFile(resolve(assetDir, "model-viewer-frame.css"))).byteLength,
  cssBytes: css.reduce((total, file) => total + file.byteLength, 0)
};
const server = await preview({
  configFile: false, root: client, publicDir: false, build: { outDir: output },
  preview: { host: "127.0.0.1", port: 0 }, logLevel: "warn"
});
let browser: Awaited<ReturnType<typeof chromium.launch>> | undefined;
try {
  const address = server.httpServer.address();
  assert(address && typeof address !== "string");
  const origin = `http://127.0.0.1:${address.port}`;
  browser = await chromium.launch({ headless: true, args: ["--no-sandbox"] });
  const page = await browser.newPage({ viewport: { width: 1500, height: 1100 }, colorScheme: "light" });
  const keyboardJourney: unknown[] = [];
  async function activateByKeyboard(target: ReturnType<typeof page.getByRole>, label: string, key: "Enter" | "Space") {
    const visited: string[] = [];
    try {
      await target.waitFor({ state: "visible" });
      let reached = false;
      for (let steps = 0; steps < 120; steps++) {
        await page.keyboard.press("Tab");
        visited.push(await page.evaluate(() => {
          const active = document.activeElement;
          return `${active?.tagName}: ${active?.getAttribute("aria-label") ?? active?.textContent?.trim().slice(0, 100) ?? ""}`;
        }));
        if (await target.evaluate((node) => node === document.activeElement)) {
          reached = true;
          break;
        }
      }
      assert(reached, `${label} must be reachable within 120 real Tab presses; visited ${JSON.stringify(visited)}`);
      const focus = await target.evaluate((node) => {
        const style = getComputedStyle(node);
        const box = node.getBoundingClientRect();
        return {
          focusVisible: node.matches(":focus-visible"),
          outlineStyle: style.outlineStyle, outlineWidth: style.outlineWidth, outlineColor: style.outlineColor,
          boxShadow: style.boxShadow,
          visible: box.width > 0 && box.height > 0 && box.bottom > 0 && box.top < innerHeight &&
            box.right > 0 && box.left < innerWidth && style.visibility === "visible" && Number(style.opacity) > 0
        };
      });
      keyboardJourney.push({ label, key, tabPresses: visited.length, visited, focus });
      assert(focus.focusVisible && focus.visible, `${label} must show keyboard focus in the viewport`);
      assert((focus.outlineStyle !== "none" && Number.parseFloat(focus.outlineWidth) > 0 &&
        !["transparent", "rgba(0, 0, 0, 0)"].includes(focus.outlineColor)) ||
        focus.boxShadow !== "none", `${label} must have a visible outline or focus shadow: ${JSON.stringify(focus)}`);
      await page.screenshot({ path: resolve(artifacts, `issue78-ui-keyboard-${keyboardJourney.length}.png`), fullPage: true });
      await page.keyboard.press(key);
    } catch (error) {
      await writeFile(resolve(artifacts, "issue78-ui-keyboard-failed.json"), `${JSON.stringify({ label, visited, keyboardJourney, error: String(error) }, null, 2)}\n`);
      await page.screenshot({ path: resolve(artifacts, "issue78-ui-keyboard-failed.png"), fullPage: true });
      throw error;
    }
  }
  page.on("request", (request) => {
    requests.push(request.url());
    if (new URL(request.url()).origin !== origin) externalRequests.push(request.url());
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));
  page.on("console", (message) => { if (message.type() === "error") consoleErrors.push(message.text()); });
  page.on("response", (response) => { if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`); });
  await installIssue78UiApiRoutes(page, { state: apiState, snapshot, revisionA, revisionB, selection, bpmnHash, items, evidenceById, xmlById });

  const routeStarted = Date.now();
  await page.goto(`${origin}/#/explore`);
  await activateByKeyboard(page.getByRole("link", { name: "Processes & decisions" }), "Discover processes and decisions route", "Enter");
  await page.getByRole("heading", { name: "Processes & decisions" }).waitFor();
  const selectedEvidenceResponse = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return url.pathname === "/api/models/evidence" && url.searchParams.get("id") === "model-bpmn";
  });
  await activateByKeyboard(page.getByRole("button", { name: /application\.bpmn\.json/ }), "Select BPMN model", "Enter");
  const evidenceReply = await selectedEvidenceResponse;
  assert.equal(evidenceReply.status(), 200, "selected evidence must use the API's generation parameter");
  assert.equal(new URL(evidenceReply.url()).searchParams.get("generation"), String(apiState.listedGeneration));
  timingsMs.firstSummarySelection = Date.now() - routeStarted;
  const bpmnFrame = page.frameLocator(".model-viewer-frame iframe");
  try {
    await bpmnFrame.locator('.djs-element[data-element-id="Task_Review"]').waitFor({ timeout: 12_000 });
  } catch (error) {
    const frame = page.locator(".model-viewer-frame iframe");
    const details = {
      frameCount: await frame.count(),
      frameStatus: await page.locator(".model-viewer-frame").evaluate((element) => element.parentElement?.innerText ?? ""),
      frameState: await frame.contentFrame().locator("#state").textContent().catch(() => null),
      frameUrl: await frame.getAttribute("src"),
      requests, sourceRequestIds, externalRequests, consoleErrors
    };
    await page.screenshot({ path: resolve(artifacts, "issue78-ui-implementation-browser-failed.png"), fullPage: true });
    await writeFile(resolve(artifacts, "issue78-ui-implementation-browser-failed.json"), `${JSON.stringify(details, null, 2)}\n`);
    throw error;
  }
  timingsMs.verifiedBpmnRender = Date.now() - routeStarted;
  await page.getByRole("button", { name: "Decisions", exact: true }).click();
  await page.getByRole("button", { name: /eligibility\.dmn\.json/ }).waitFor();
  assert.equal(await page.getByRole("button", { name: /application\.bpmn\.json/ }).count(), 0);
  await page.getByRole("button", { name: "Processes", exact: true }).click();
  await page.getByRole("button", { name: /application\.bpmn\.json/ }).waitFor();
  assert.equal(await page.getByRole("button", { name: /eligibility\.dmn\.json/ }).count(), 0);
  await page.getByRole("button", { name: "All", exact: true }).click();
  assert.match(await page.getByRole("region", { name: "BPMN process diagram" }).getAttribute("aria-describedby") ?? "", /diagram-help/);
  const frameElement = await page.locator(".model-viewer-frame iframe").evaluate((frame) => ({
    sandbox: frame.getAttribute("sandbox"),
    srcdoc: frame.getAttribute("srcdoc") ?? ""
  }));
  assert.equal(frameElement.sandbox, "allow-scripts allow-popups allow-popups-to-escape-sandbox");
  assert(!frameElement.sandbox.includes("allow-same-origin"));
  assert(!/allow-top-navigation|allow-forms/i.test(frameElement.sandbox));
  assert(frameElement.sandbox.includes("allow-popups"));
  for (const directive of ["default-src 'none'", "connect-src 'none'", "img-src data:", "style-src-attr 'unsafe-inline'", "object-src 'none'", "form-action 'none'"]) {
    assert(frameElement.srcdoc.includes(directive), `missing CSP ${directive}`);
  }
  assert(frameElement.srcdoc.includes("script-src 'nonce-"));
  assert.match(frameElement.srcdoc, /font-src data:/);
  assert.match(frameElement.srcdoc, /<script nonce="[^"]+">/);
  assert.doesNotMatch(frameElement.srcdoc, /<script[^>]+src=/);
  assert.doesNotMatch(frameElement.srcdoc, /\/assets\/model-viewer-frame\.(?:js|css)/);
  assert(requests.some((request) => request.endsWith("/assets/model-viewer-frame.js")));
  assert(requests.some((request) => request.endsWith("/assets/model-viewer-frame.css")));
  const watermark = bpmnFrame.locator("a.bjs-powered-by");
  assert(await watermark.isVisible());
  assert.equal(await bpmnFrame.locator("body").evaluate((node) => getComputedStyle(node).backgroundColor), "rgb(255, 255, 255)");
  const darkWatermarkGeometry = await watermark.evaluate((node) => {
    const box = node.getBoundingClientRect();
    const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
    return {
      hit: hit === node || node.contains(hit),
      hitTarget: hit ? `${hit.tagName}.${(hit as HTMLElement).className}` : null,
      box: { left: box.left, top: box.top, right: box.right, bottom: box.bottom },
      viewport: { width: innerWidth, height: innerHeight }
    };
  });
  if (!darkWatermarkGeometry.hit) {
    await page.locator(".model-viewer-frame iframe").screenshot({ path: resolve(artifacts, "issue78-ui-watermark-dark-hit-failed.png") });
    await writeFile(resolve(artifacts, "issue78-ui-watermark-dark-hit-failed.json"), `${JSON.stringify(darkWatermarkGeometry, null, 2)}\n`);
  }
  assert(darkWatermarkGeometry.hit &&
    darkWatermarkGeometry.box.left >= 0 && darkWatermarkGeometry.box.top >= 0 &&
    darkWatermarkGeometry.box.right <= darkWatermarkGeometry.viewport.width &&
    darkWatermarkGeometry.box.bottom <= darkWatermarkGeometry.viewport.height,
  JSON.stringify(darkWatermarkGeometry));
  const watermarkGeometry = await watermark.evaluate((node) => {
    const box = node.getBoundingClientRect();
    const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
    return {
      hit: hit === node || node.contains(hit),
      inViewport: box.left >= 0 && box.top >= 0 && box.right <= innerWidth && box.bottom <= innerHeight,
      opacity: getComputedStyle(node).opacity,
      color: getComputedStyle(node).color,
      width: box.width, height: box.height
    };
  });
  assert(watermarkGeometry.hit && watermarkGeometry.inViewport && watermarkGeometry.width > 0 && watermarkGeometry.height > 0);
  assert.equal(watermarkGeometry.opacity, "1");
  const licenseLink = page.getByRole("link", { name: "bpmn.io viewer license and attribution" });
  assert.equal(new URL(await licenseLink.getAttribute("href") ?? "", origin).pathname, "/licenses/bpmn-io-viewers.txt");
  assert(sourceRequestIds.includes("model-bpmn"));
  const bpmnSourceRequest = new URL(requests.find((request) => request.startsWith("/api/models/source?id=model-bpmn"))!, origin);
  assert.equal(bpmnSourceRequest.searchParams.get("snapshot"), snapshot);
  assert.equal(bpmnSourceRequest.searchParams.get("generation"), String(apiState.listedGeneration));
  const bpmnEvidenceRequest = new URL(requests.find((request) => request.startsWith("/api/models/evidence?id=model-bpmn"))!, origin);
  assert.equal(bpmnEvidenceRequest.searchParams.get("snapshot"), snapshot);
  assert.equal(bpmnEvidenceRequest.searchParams.get("generation"), String(apiState.listedGeneration));
  assert(requests.findIndex((request) => request.startsWith("/api/models/evidence?id=model-bpmn")) <
    requests.findIndex((request) => request.startsWith("/api/models/source?id=model-bpmn")));
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-bpmn-diagram.png"), fullPage: true });

  await activateByKeyboard(page.getByRole("button", { name: "Elements", exact: true }), "Open Elements tab", "Space");
  await activateByKeyboard(page.getByRole("button", { name: /Task_Review/ }), "Select process element", "Enter");
  await page.waitForFunction(() => window.location.hash.includes("element=Task_Review") &&
    [...document.querySelectorAll('button[aria-pressed="true"]')].some((button) => button.textContent?.includes("Task_Review")));
  assert.match(await page.getByRole("button", { name: /Task_Review/ }).getAttribute("aria-pressed") ?? "", /true/);
  assert.match(page.url(), /tab=elements/);
  assert.match(page.url(), /element=Task_Review/);
  assert(await page.getByText("Lane: Applicant lane").isVisible());
  assert(await page.getByText(/Incoming: sequenceFlow from Application received/).isVisible());
  assert(await page.getByText(/Outgoing: messageFlow to Assess eligibility/).isVisible());
  assert(await page.getByText("model.xml · element: Task_Review").isVisible());
  assert(await page.getByText(/Not rendered in current viewer projection/).count() > 0);
  await page.evaluate(() => {
    const target = window as Window & { __issue78SelectionResults?: unknown[] };
    target.__issue78SelectionResults = [];
    window.addEventListener("message", (event) => {
      if (event.data?.status === "selection-result") target.__issue78SelectionResults?.push(event.data);
    });
  });
  await page.getByRole("button", { name: /Task_Nested/ }).click();
  assert.match(await page.getByRole("navigation", { name: "Breadcrumb for Task_Nested" }).innerText(), /Application\s*\/\s*Eligibility check\s*\/\s*Nested decision task/);
  await page.getByRole("button", { name: "Navigate to SubProcess_Check" }).click();
  assert.match(page.url(), /element=SubProcess_Check/);
  assert.match(await bpmnFrame.locator('.djs-element[data-element-id="SubProcess_Check"]').getAttribute("class") ?? "", /issue78-selected/);
  await page.getByRole("button", { name: /Task_Review/ }).click();
  const flowRow = page.getByRole("button", { name: /sequenceFlow: Application received →/ });
  assert.match(page.url(), /element=Task_Review/);
  await flowRow.click();
  await page.waitForFunction(() => window.location.hash.includes("element=rel-flow-start-review") &&
    [...document.querySelectorAll("button")].some((button) =>
      button.textContent?.includes("Relationship identity: rel-flow-start-review") && button.getAttribute("aria-pressed") === "true"));
  await page.waitForFunction(() => (window as Window & { __issue78SelectionResults?: unknown[] }).__issue78SelectionResults?.length, undefined, { timeout: 3000 });
  const selectionResult = await page.evaluate(() => (window as Window & { __issue78SelectionResults?: Array<{ relationshipMatchCount?: number; message?: string }> }).__issue78SelectionResults?.at(-1));
  assert.equal(selectionResult?.relationshipMatchCount, 1, JSON.stringify(selectionResult));
  const flowPressed = await flowRow.getAttribute("aria-pressed");
  if (flowPressed !== "true") {
    await writeFile(resolve(artifacts, "issue78-ui-flow-selection-failed.json"), `${JSON.stringify({
      hash: await page.evaluate(() => window.location.hash),
      flowText: await flowRow.innerText(),
      flowPressed,
      selectedButtons: await page.locator('button[aria-pressed="true"]').allTextContents()
    }, null, 2)}\n`);
  }
  assert.equal(flowPressed, "true");
  assert.match(page.url(), /element=rel-flow-start-review/);
  const flowShape = bpmnFrame.locator('.djs-element[data-element-id="Flow_Start_Review"]');
  await flowShape.waitFor({ state: "attached" });
  const childFrame = page.frames().find((frame) => frame.url() === "about:srcdoc");
  assert(childFrame, "opaque model frame should remain attached for the selected projection");
  await childFrame.waitForFunction(() => document.querySelector('.djs-element[data-element-id="Flow_Start_Review"]')?.classList.contains("issue78-selected"));
  assert.match(await flowShape.getAttribute("class") ?? "", /issue78-selected/);
  await page.goBack();
  await page.waitForFunction(() => window.location.hash.includes("element=Task_Review"));
  await page.goForward();
  await page.waitForFunction(() => window.location.hash.includes("element=rel-flow-start-review"));
  const diagnostics = page.getByRole("region", { name: "Model diagnostics" });
  const diagnosticDisclosure = diagnostics.locator("summary");
  assert.equal(await diagnosticDisclosure.count(), 1);
  assert.match(await diagnostics.getByRole("heading").innerText(), /Diagnostics \(2\)/);
  assert.match(await diagnosticDisclosure.innerText(), /MODEL_PARTIAL · partial · Construct not identified · 2 occurrences/);
  assert.equal(await page.getByText("One opaque extension was retained.").isVisible(), false);
  await activateByKeyboard(diagnosticDisclosure, "Inspect grouped diagnostic occurrences", "Enter");
  assert(await page.getByText("One opaque extension was retained.").isVisible());
  assert(await page.getByText("Another opaque extension was retained at Vendor_2.").isVisible());
  assert(await diagnostics.getByText("Vendor_2", { exact: true }).isVisible());
  for (const [theme, width, height] of [["light", 1500, 1100], ["dark", 1500, 1100], ["dark", 390, 844]] as const) {
    await page.setViewportSize({ width, height });
    await page.emulateMedia({ colorScheme: theme });
    await diagnostics.scrollIntoViewIfNeeded();
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: resolve(artifacts, `issue78-ui-grouped-details-${theme}-${width}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 1500, height: 1100 });
  await page.emulateMedia({ colorScheme: "light" });
  assertions.push("Repeated diagnostics retain total and group counts, start collapsed, disclose exact messages and long locators by keyboard, and stay viewport-bounded in light/dark/mobile views.");
  assert(await page.getByText(/element source locator not supplied/).isVisible());
  const relationshipProvenance = page.getByRole("complementary", { name: "Model provenance" }).getByLabel("Selected relationship provenance");
  const relationshipOverflow = await page.evaluate(() => ({
    viewport: window.innerWidth,
    document: document.documentElement.scrollWidth,
    provenanceRight: document.querySelector('[aria-label="Selected relationship provenance"]')?.getBoundingClientRect().right,
    provenanceClientWidth: document.querySelector('[aria-label="Selected relationship provenance"]')?.clientWidth,
    provenanceScrollWidth: document.querySelector('[aria-label="Selected relationship provenance"]')?.scrollWidth
  }));
  if ((relationshipOverflow.provenanceRight ?? 0) > relationshipOverflow.viewport ||
      relationshipOverflow.document > relationshipOverflow.viewport ||
      (relationshipOverflow.provenanceScrollWidth ?? 0) > (relationshipOverflow.provenanceClientWidth ?? 0)) {
    await page.screenshot({ path: resolve(artifacts, "issue78-ui-relationship-overflow-failed.png"), fullPage: true });
    await writeFile(resolve(artifacts, "issue78-ui-relationship-overflow-failed.json"), `${JSON.stringify(relationshipOverflow, null, 2)}\n`);
  }
  assert(relationshipOverflow.provenanceRight !== undefined &&
    relationshipOverflow.provenanceRight <= relationshipOverflow.viewport &&
    relationshipOverflow.document <= relationshipOverflow.viewport &&
    relationshipOverflow.provenanceScrollWidth === relationshipOverflow.provenanceClientWidth,
  `Selected relationship provenance overflows the desktop viewport: ${JSON.stringify(relationshipOverflow)}`);
  const relationshipProvenanceText = await relationshipProvenance.innerText();
  assert.match(relationshipProvenanceText, /Application received/);
  assert.match(relationshipProvenanceText, /Start_Application/);
  assert.match(relationshipProvenanceText, /Task_Review/);
  assert.equal(await page.locator('img[src*="example.invalid"]').count(), 0);
  assert.equal(await page.evaluate(() => (window as Window & { pwned?: boolean }).pwned), undefined);
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-bpmn.png"), fullPage: true });
  assertions.push("Production route restores tab/element selection, aligns canvas/text flow selection, and lists evidence order, lane, incoming/outgoing, rendering projection and exact provenance.");

  assertions.push("Live frame CSP restricts scripts to nonce-authorized bundled viewer, CSS to bundled asset, images to data, and network connections to none; watermark remains unobscured.");

  await activateByKeyboard(page.getByRole("button", { name: /eligibility\.dmn\.json/ }), "Select DMN model", "Space");
  await activateByKeyboard(page.getByRole("button", { name: "Decision table", exact: true }), "Open Decision table tab", "Enter");
  const table = page.getByRole("table", { name: "Eligibility authored decision table" });
  const tableHeaders = await table.evaluate((element) => [...element.querySelectorAll("thead th")].map((header) => header.firstElementChild?.textContent));
  assert.deepEqual(tableHeaders, ["Applicant age", "Eligibility"]);
  const headers = table.getByRole("columnheader");
  assert.match(await headers.nth(0).innerText(), /Input allowed values \(literal, not evaluated\)[\s\S]*<18, \[18\.\.24\], -/);
  assert.doesNotMatch(await headers.nth(0).innerText(), /"deny"/);
  assert.match(await headers.nth(1).innerText(), /Output allowed values \(literal, not evaluated\)[\s\S]*"deny", "manual", "approve"/);
  assert.doesNotMatch(await headers.nth(1).innerText(), /<18/);
  assert.deepEqual(await table.locator("tbody tr").evaluateAll((rows) => rows.map((row) => [...row.querySelectorAll("td")].map((cell) => cell.textContent))), [
    ["<18", '"deny"'], ["[18..24]", '"manual"'], ["-", '"approve"']
  ]);
  assert.equal(await table.getByRole("columnheader", { name: "Annotations" }).count(), 0);
  const selectedRule = table.getByRole("button", { name: "<18", exact: true });
  await activateByKeyboard(selectedRule, "Select decision table rule", "Space");
  await page.waitForFunction(() => window.location.hash.includes("element=Rule_1") &&
    [...document.querySelectorAll('button[aria-pressed="true"]')].some((button) => button.textContent?.includes("<18")));
  assert.match(await selectedRule.getAttribute("aria-pressed") ?? "", /true/);
  assert.match(await selectedRule.evaluate((node) => node.closest("tr")?.className ?? ""), /outline-blue-500/);
  assert(await page.getByText("score >= threshold").isVisible());
  assert(await page.getByText(/never evaluated/).isVisible());
  assert(await page.getByText(/FEEL · text only; not evaluated/).isVisible());
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-dmn.png"), fullPage: true });
  for (const [theme, width, height] of [["light", 1500, 1100], ["dark", 1500, 1100], ["dark", 390, 844]] as const) {
    await page.setViewportSize({ width, height });
    await page.emulateMedia({ colorScheme: theme });
    await table.scrollIntoViewIfNeeded();
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: resolve(artifacts, `issue78-ui-allowed-values-${theme}-${width}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 1500, height: 1100 });
  await page.emulateMedia({ colorScheme: "light" });
  assertions.push("DMN source-derived accessible table has exact authored columns and ordered rule cells; no renderer-generated annotations or expression evaluation.");
  assertions.push("Bounded real Tab traversal reaches route, BPMN/DMN model selection, Elements/Decision table tabs, process element and decision rule; Enter/Space activate controls with viewport-visible focus indicators. This is a keyboard baseline, not assistive-technology certification.");

  await page.getByRole("button", { name: /Eligibility\.cs/ }).click();
  await page.waitForFunction(() => window.location.hash.includes("id=candidate-cs"));
  await page.waitForFunction(() => ![...document.querySelectorAll("table")].some((table) =>
    table.caption?.textContent === "Eligibility authored decision table") &&
    document.querySelectorAll(".model-viewer-frame iframe").length === 0);
  assert.equal(await page.getByRole("table", { name: "Eligibility authored decision table" }).count(), 0, "previous model evidence must be cleared immediately while the candidate detail is pending");
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0, "previous source frame must be disposed before candidate evidence loads");
  await page.getByRole("alert").filter({ hasText: "UNREVIEWED" }).waitFor({ state: "visible" });
  await page.getByRole("heading", { name: /Bounded candidate recipe: roslyn\.scalar-guards\.v1/ }).waitFor();
  await page.getByText(/UTF-16 span 2\+10/).first().waitFor();
  assert(await page.getByText("Review: UNREVIEWED · Executable: no · Inputs written: 0").isVisible());
  assert(!sourceRequestIds.includes("candidate-cs"), "candidate source must never use the BPMN/DMN XML endpoint");
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-candidate.png"), fullPage: true });
  assertions.push("Candidate recipe, raw text, UTF-16 source locators, and unapproved/non-executable status stay separate; no XML source request is made.");

  await page.getByRole("button", { name: "Load next page" }).click();
  await page.getByRole("button", { name: /denied\.bpmn\.json/ }).click();
  await page.getByText("Source access denied; no fallback is attempted.").waitFor();
  assert(!sourceRequestIds.includes("model-denied"));
  assert(await page.getByText(/Authored evidence · original source not verified \(denied\)/).isVisible());
  await page.getByRole("button", { name: /missing\.bpmn\.json/ }).click();
  await page.getByText("Original source is unavailable. Showing only separately validated textual evidence.").waitFor();
  assert(!sourceRequestIds.includes("model-missing"), "missing authored source must not call the source endpoint");
  await page.getByText(/Authored evidence · original source not verified \(missing\)/).waitFor();
  await page.getByRole("button", { name: /no-di\.bpmn\.json/ }).click();
  await page.getByText("Original source SHA-256 verified; viewer geometry is unavailable or unverified. No generated layout replaces source evidence.").waitFor();
  await page.getByRole("alert").filter({ hasText: "BPMN source has no BPMNDiagram DI" }).waitFor();
  assert(!await page.locator(".model-viewer-frame").innerText().then((text) => text.includes("authored DI")));
  assertions.push("Opaque cursor extends the bounded list; denied source status is explicit and triggers no source request.");
  await page.getByRole("button", { name: /malformed-table\.dmn\.json/ }).click();
  await page.getByRole("button", { name: "Decision table", exact: true }).click();
  const malformedTable = page.getByRole("table", { name: "Eligibility authored decision table" });
  await page.getByRole("alert").filter({ hasText: "Values are not aligned or padded." }).waitFor();
  assert.equal(await malformedTable.locator("tbody tr").count(), 0, "misaligned DMN cells must not be presented as complete rows");
  assertions.push("DMN tables with a rule that lacks an output are rejected without padding or misaligning cells.");
  await page.getByRole("button", { name: /malformed\.bpmn\.json/ }).click();
  await page.getByRole("alert").filter({ hasText: "Selected evidence does not match the bounded evidence schema" }).waitFor();
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0);
  await page.getByRole("button", { name: /tampered\.bpmn\.json/ }).click();
  await page.getByRole("alert").filter({ hasText: "Source bytes failed SHA-256 verification" }).waitFor();
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0);
  await page.getByRole("link", { name: "Explore", exact: true }).click();
  await page.getByRole("heading", { name: "Explore" }).waitFor();
  assertions.push("Malformed evidence and a source body with a mismatched SHA-256 are rejected before display; the shell remains navigable.");

  await page.getByRole("link", { name: "Processes & decisions", exact: true }).click();
  await page.getByRole("heading", { name: "Processes & decisions" }).waitFor();
  await page.getByRole("button", { name: /application\.bpmn\.json/ }).click();
  await bpmnFrame.locator('.djs-element[data-element-id="Task_Review"]').waitFor();
  await page.setViewportSize({ width: 1100, height: 900 });
  assert(await page.getByText("Model picker", { exact: false }).isVisible());
  const stageRect = await page.locator(".model-viewer-frame").boundingBox();
  const provenanceRect = await page.getByRole("complementary", { name: "Model provenance" }).boundingBox();
  assert(stageRect && provenanceRect && provenanceRect.x >= stageRect.x + stageRect.width && provenanceRect.x + provenanceRect.width <= 1100);
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ colorScheme: "dark" });
  await page.waitForFunction(() => [...document.querySelectorAll('button[aria-pressed="true"]')].some((button) => button.textContent?.trim() === "Elements"));
  assert.equal(await page.getByRole("button", { name: "Elements", exact: true }).getAttribute("aria-pressed"), "true");
  await page.getByRole("button", { name: "Diagram", exact: true }).click();
  await watermark.waitFor({ state: "visible" });
  assert.equal(await bpmnFrame.locator("body").evaluate((node) => getComputedStyle(node).backgroundColor), "rgb(255, 255, 255)");
  await page.locator(".model-viewer-frame").scrollIntoViewIfNeeded();
  const mobileWatermarkRect = await watermark.boundingBox();
  assert(mobileWatermarkRect && mobileWatermarkRect.x >= 0 && mobileWatermarkRect.y >= 0 && mobileWatermarkRect.x + mobileWatermarkRect.width <= 390 && mobileWatermarkRect.y + mobileWatermarkRect.height <= 844);
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
  await page.locator(".model-viewer-frame iframe").screenshot({ path: resolve(artifacts, "issue78-ui-production-mobile-dark.png") });
  assertions.push("Tablet picker and side inspector, narrow dark white notation paper, and viewport-bounded unobscured watermark are verified.");
  const revision = page.getByLabel("Revision of repo-a");
  const revisionRequest = page.waitForRequest((request) => {
    const url = new URL(request.url());
    return url.pathname === "/api/view" && url.searchParams.get("view")?.includes(revisionB) === true;
  });
  await revision.selectOption(revisionB);
  await revisionRequest;
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0, "old frame must be disposed before the delayed revision response returns");
  await page.locator("summary").filter({ hasText: "Model picker" }).click();
  await page.getByRole("button", { name: /oversized\.bpmn\.json/ }).waitFor();
  await page.waitForFunction(() => document.querySelectorAll(".model-viewer-frame iframe").length === 0);
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0);
  assertions.push("Revision switch immediately removes old viewer content while the new generation-bound model list is pending.");
  await page.getByRole("button", { name: /oversized\.bpmn\.json/ }).click();
  await page.getByRole("alert").filter({ hasText: "bounded response limit" }).waitFor();
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0, "oversized API source must not reach viewer construction");
  assertions.push("Streaming response byte limit cancels an oversized source wrapper before parsing or renderer creation.");

  apiState.databaseMode = true;
  await page.goto(`${origin}/?issue78-db=1#/processes-decisions?id=model-denied&tab=elements&element=Task_Review`);
  await page.getByRole("heading", { name: "Processes & decisions" }).waitFor();
  await page.locator("summary").filter({ hasText: "Model picker" }).click();
  const deniedDatabaseModel = page.getByRole("button", { name: /denied\.bpmn\.json/ });
  await deniedDatabaseModel.waitFor();
  const taskReview = page.getByRole("button", { name: /Task_Review/ });
  await taskReview.waitFor();
  assert.match(page.url(), /id=model-denied/);
  assert.equal(await page.getByRole("button", { name: "Elements", exact: true }).getAttribute("aria-pressed"), "true");
  assert.equal(await page.getByRole("button", { name: /Task_Review/ }).getAttribute("aria-pressed"), "true");
  const databaseEvidenceRequest = requests.filter((request) => request.startsWith("/api/models/evidence?id=model-denied")).at(-1);
  assert(databaseEvidenceRequest, "database deep link must request selected evidence");
  assert.equal(new URL(databaseEvidenceRequest, origin).searchParams.get("generation"), String(apiState.listedGeneration));
  assert.equal(new URL(databaseEvidenceRequest, origin).searchParams.get("snapshot"), snapshot);
  const databaseCursorRequest = requests.filter((request) => request.startsWith("/api/models?") && request.includes("cursor=")).at(-1);
  assert(databaseCursorRequest, "deep linked database model should be restored through snapshot pagination");
  assert.equal(new URL(databaseCursorRequest, origin).searchParams.get("generation"), String(apiState.listedGeneration));
  await page.getByRole("button", { name: /eligibility\.dmn\.json/ }).click();
  await page.waitForFunction(() => window.location.hash.includes("id=model-dmn"));
  const databaseDmnRequest = requests.filter((request) => request.startsWith("/api/models/evidence?id=model-dmn")).at(-1);
  assert(databaseDmnRequest, "database model selection must fetch the selected evidence");
  assert.equal(new URL(databaseDmnRequest, origin).searchParams.get("generation"), String(apiState.listedGeneration));
  await page.goBack();
  await page.waitForFunction(() => window.location.hash.includes("id=model-denied"));
  await page.locator("summary").filter({ hasText: "Model picker" }).click();
  await page.waitForFunction(() => [...document.querySelectorAll('button[aria-pressed="true"]')].some((button) => button.textContent?.includes("Task_Review")));
  assertions.push("PostgreSQL mode restores hash deep links, tab and element selection, sends the snapshot/generation scope, and model clicks update browser history.");

  const expectedRequests = requests.filter((request) => request.startsWith("/api/models"));
  assert(expectedRequests.some((request) => request.includes("/evidence?id=model-bpmn")));
  assert(expectedRequests.some((request) => request.includes("/source?id=model-bpmn")));
  const cursorRequest = new URL(expectedRequests.find((request) => request.startsWith("/api/models?") && request.includes("cursor="))!, origin);
  assert.equal(cursorRequest.searchParams.get("snapshot"), snapshot);
  assert.equal(cursorRequest.searchParams.get("generation"), String(apiState.listedGeneration));
  assert.deepEqual(externalRequests, []);
  assert.deepEqual(failedResponses, []);
  if (consoleErrors.length) console.log("Browser console errors", JSON.stringify({ consoleErrors, failedResponses }));
  assert.deepEqual(consoleErrors, []);
  const artifact = {
    commit: "dirty worktree source; see exact file hashes",
    sourceHashes: Object.fromEntries(await Promise.all([
      "src/models/contracts.ts", "src/models/viewer-sandbox.ts", "src/models/viewer-frame-entry.ts", "src/models/viewer-frame-entry.css",
      "src/views/ProcessesDecisions.tsx", "src/views/ModelEvidence.tsx", "src/views/model-view-data.ts", "src/views/DatabaseExplorer.tsx",
      "src/App.tsx", "src/index.css", "vite.model-frame.ts", "vite.config.ts", "public/licenses/bpmn-io-viewers.txt",
      "../../../../tests/issue78-ui-browser.ts", "../../../../tests/issue78-viewer-boundary.ts",
      "../../../../tests/issue78-model-ui-quality.ts",
      "../../../../tests/fixtures/issue78/ui-evidence.ts", "../../../../tests/fixtures/issue78/ui-api-routes.ts",
      "../../../../tests/fixtures/issue78/viewer-shell-entry.ts", "../../../../tests/fixtures/issue78/bpmn-representative.xml",
      "../../../../tests/fixtures/issue78/dmn-representative.xml"
    ].map(async (file) => [file, sha256(await readFile(resolve(client, file)))]))),
    fixtureSha256: { bpmn: bpmnHash, dmn: dmnHash },
    packageLockSha256: summariesHash,
    bundle,
    assertions,
    keyboardJourney,
    timingsMs,
    apiPaths: expectedRequests.map((request) => request.replace(/^\/api\/models/, "/api/models")),
    browserRequests: requests.filter((request) => !request.startsWith("/api/")),
    externalRequests,
    browser: { version: browser.version(), node: process.version, platform: process.platform, arch: process.arch },
  };
  await writeFile(resolve(artifacts, "issue78-ui-implementation-browser.json"), `${JSON.stringify(artifact, null, 2)}\n`);
  await writeFile(resolve(artifacts, "issue78-ui-implementation-browser.log"),
    `Issue #78 production UI browser passed ${assertions.length} grouped assertions; ${expectedRequests.length} API requests; 0 external requests.\n` +
    `BPMN source SHA-256 ${bpmnHash}; DMN source SHA-256 ${dmnHash}\n` +
    `Single-run observed timings (not percentile claims): ${JSON.stringify(timingsMs)}\n`);
  console.log(`Issue #78 production UI passed ${assertions.length} grouped assertions; ${expectedRequests.length} model API requests; 0 external.`);
} finally {
  await browser?.close();
  await server.close();
}
