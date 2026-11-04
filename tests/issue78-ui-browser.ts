import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { access, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { gzipSync } from "node:zlib";
import { build, preview } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";

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
const revisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const revisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
const selection = { "repo-a": revisionA };
const snapshot = "snapshot-ui-a";
const bpmnIdentity = `model:${"1".repeat(64)}`;
const dmnIdentity = `model:${"2".repeat(64)}`;
const tableIdentity = `model:${"3".repeat(64)}`;
const ruleIdentities = [`model:${"4".repeat(64)}`, `model:${"5".repeat(64)}`, `model:${"6".repeat(64)}`];
const sourceLocator = { artifact: "model.xml", element: "Decision_Eligibility" };
let listedGeneration: number | undefined;
const bpmnEvidence = {
  schema: "ce.models.v1",
  repositoryId: "repo-a",
  commitSha: revisionA,
  unitPath: "models/application.bpmn.json",
  sourceSha256: bpmnHash,
  contentProfile: "model-content",
  coverage: { status: "partial", supported: ["BPMN process"], limitations: ["Vendor extensions are opaque."] },
  diagnostics: [{ code: "MODEL_PARTIAL", message: "One opaque extension was retained.", disposition: "partial", source: sourceLocator }],
  elements: [
    { identity: bpmnIdentity, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Process_A", type: "bpmn:process", order: 0, attributes: { name: "Application" }, source: sourceLocator },
    { identity: `model:${"7".repeat(64)}`, kind: "bpmn", namespace: "http://www.omg.org/spec/BPMN/20100524/MODEL", id: "Task_Review", type: "bpmn:task", order: 1, parent: bpmnIdentity, attributes: { name: '<img src="https://example.invalid/x" onerror="window.pwned=1">' }, source: { artifact: "model.xml", element: "Task_Review" } }
  ],
  relationships: []
};
const dmnEvidence = {
  schema: "ce.models.v1",
  repositoryId: "repo-a",
  commitSha: revisionA,
  unitPath: "models/eligibility.dmn.json",
  sourceSha256: dmnHash,
  contentProfile: "model-content",
  coverage: { status: "full", supported: ["DMN decision tables"], limitations: [] },
  diagnostics: [],
  elements: [
    { identity: dmnIdentity, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Decision_Eligibility", type: "dmn:decision", order: 0, attributes: { name: "Eligibility decision" }, source: { artifact: "eligibility.dmn", element: "Decision_Eligibility" } },
    { identity: tableIdentity, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Table_Eligibility", type: "dmn:decisionTable", order: 1, parent: dmnIdentity, attributes: { name: "Eligibility", hitPolicy: "FIRST" }, hitPolicy: "FIRST", source: { artifact: "eligibility.dmn", element: "Table_Eligibility" } },
    { identity: `model:${"8".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "InputClause_Age", type: "dmn:input", order: 2, parent: tableIdentity, attributes: { label: "Applicant age" }, source: { artifact: "eligibility.dmn", element: "InputClause_Age" } },
    { identity: `model:${"9".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Output_Eligibility", type: "dmn:output", order: 3, parent: tableIdentity, attributes: { label: "Eligibility" }, source: { artifact: "eligibility.dmn", element: "Output_Eligibility" } },
    ...["<18", "[18..24]", "-"].map((input, index) => ({
      identity: ruleIdentities[index], kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: `Rule_${index + 1}`,
      type: "dmn:rule", order: index + 4, parent: tableIdentity, attributes: {},
      cells: [{ kind: "input", order: 0, text: input }, { kind: "output", order: 1, text: [`"deny"`, `"manual"`, `"approve"`][index] }],
      source: { artifact: "eligibility.dmn", element: `Rule_${index + 1}` }
    })),
    { identity: `model:${"a".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Decision_Literal", type: "dmn:decision", order: 10, attributes: { name: "Threshold check" }, source: { artifact: "eligibility.dmn", element: "Decision_Literal" } },
    { identity: `model:${"b".repeat(64)}`, kind: "dmn", namespace: "https://www.omg.org/spec/DMN/20191111/MODEL/", id: "Literal_Threshold", type: "dmn:literalExpression", order: 11, parent: `model:${"a".repeat(64)}`, attributes: {}, content: { text: "score >= threshold", language: "FEEL" }, source: { artifact: "eligibility.dmn", element: "Literal_Threshold" } }
  ],
  relationships: []
};
const candidateSource = {
  unitPath: "src/Eligibility.cs", commitSha: revisionA, sourceSha256: "f".repeat(64),
  start: 2, length: 10, startLine: 10, startColumn: 3, endLine: 10, endColumn: 13
};
const candidateEvidence = {
  schema: "ce.candidates.v1", recipe: "roslyn.scalar-guards.v1", repositoryId: "repo-a",
  commitSha: revisionA, unitPath: "src/Eligibility.cs", sourceSha256: "f".repeat(64),
  coverage: { status: "partial", scope: "single-file-intra-method", supportedMethods: 1, unsupportedMethods: 1 },
  diagnostics: [{ code: "UNSUPPORTED_METHOD", message: "One method was outside the bounded recipe." }],
  candidates: [{
    id: "IsEligible", kind: "decision", reviewStatus: "UNREVIEWED", executable: false, contentProfile: "candidate-content",
    source: candidateSource, parameters: [{ name: "age", symbol: "age", type: "int" }], inputsRead: ["age"], inputsWritten: [],
    body: { kind: "if", source: candidateSource, children: [
      { kind: "binary", source: candidateSource, operator: "LessThan", children: [
        { kind: "parameter", source: candidateSource, symbol: "age", type: "int", children: [] },
        { kind: "constant", source: candidateSource, type: "int", value: "18", children: [] }
      ] },
      { kind: "return", source: candidateSource, children: [{ kind: "constant", source: candidateSource, type: "bool", value: "false", children: [] }] }
    ] }
  }]
};
const oversizedEvidence = {
  ...bpmnEvidence,
  commitSha: revisionB,
  unitPath: "models/oversized.bpmn.json"
};
const items = [
  { id: "model-bpmn", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/application.bpmn.json", modelKind: "bpmn", coverage: "partial", diagnosticCount: 1, source: { status: "available", sha256: bpmnHash } },
  { id: "model-dmn", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/eligibility.dmn.json", modelKind: "dmn", coverage: "full", diagnosticCount: 0, source: { status: "available", sha256: dmnHash } },
  { id: "candidate-cs", kind: "candidate", repositoryId: "repo-a", commitSha: revisionA, unitPath: "src/Eligibility.cs", modelKind: "candidate", coverage: "partial", diagnosticCount: 1, source: { status: "missing", sha256: "f".repeat(64) } },
  { id: "model-denied", kind: "authored", repositoryId: "repo-a", commitSha: revisionA, unitPath: "models/denied.bpmn.json", modelKind: "bpmn", coverage: "full", diagnosticCount: 0, source: { status: "denied", sha256: bpmnHash } },
  { id: "oversize-bpmn", kind: "authored", repositoryId: "repo-a", commitSha: revisionB, unitPath: "models/oversized.bpmn.json", modelKind: "bpmn", coverage: "partial", diagnosticCount: 1, source: { status: "available", sha256: bpmnHash } }
];
const evidenceById = { "model-bpmn": bpmnEvidence, "model-dmn": dmnEvidence, "candidate-cs": candidateEvidence, "model-denied": bpmnEvidence, "oversize-bpmn": oversizedEvidence };
const xmlById = { "model-bpmn": bpmnXml, "model-dmn": dmnXml, "model-denied": bpmnXml };
const summariesHash = await readFile(resolve(client, "package-lock.json"), "utf8").then((raw) => createHash("sha256").update(raw).digest("hex"));
const assertions: string[] = [];
const timingsMs: Record<string, number> = {};
const requests: string[] = [];
const sourceRequestIds: string[] = [];
const externalRequests: string[] = [];
const consoleErrors: string[] = [];

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
  page.on("request", (request) => {
    requests.push(request.url());
    if (new URL(request.url()).origin !== origin) externalRequests.push(request.url());
  });
  page.on("pageerror", (error) => consoleErrors.push(error.message));
  page.on("console", (message) => { if (message.type() === "error") consoleErrors.push(message.text()); });
  await page.route("**/api/**", async (route) => {
    const url = new URL(route.request().url());
    requests.push(`${url.pathname}${url.search}`);
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
    if (url.pathname === "/api/meta") return json({
      generation: 3,
      capabilities: [{ id: "cap-a", name: "Capability A", repositories: ["repo-a"] }],
      githubAuth: { source: "none", hostManaged: false }, interpretation: { enabled: false, chat: false, provider: "none" },
      similarity: { database: false }, revisions: [
        { repositoryId: "repo-a", commitSha: revisionA, tags: [], label: "revision A" },
        { repositoryId: "repo-a", commitSha: revisionB, tags: [], label: "revision B" }
      ],
      defaultView: { "repo-a": revisionA }, artifacts: 0, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: []
    });
    if (url.pathname === "/api/view") {
      const alternate = url.searchParams.get("view")?.includes(revisionB);
      return json({ "repo-a": alternate ? revisionB : revisionA });
    }
    if (url.pathname === "/api/models") {
      if (url.searchParams.has("cursor") && !/^\d+$/.test(url.searchParams.get("generation") ?? "")) {
        return json({ detail: "continuation requires numeric generation" }, 400);
      }
      const isAlternate = url.searchParams.get("view")?.includes(revisionB);
      const currentItems = isAlternate ? [items[4]] : items.slice(0, 3);
      const continuation = url.searchParams.has("cursor") ? items.slice(3) : currentItems;
      listedGeneration = 3;
      return json({
        schema: "ce.model-views.v1", items: continuation, problems: [], mode: "files",
        snapshot: isAlternate ? "snapshot-ui-b" : snapshot, generation: 3,
        selection: isAlternate ? { "repo-a": revisionB } : selection,
        nextCursor: !isAlternate && !url.searchParams.has("cursor") ? "cursor-next" : null
      });
    }
    if (url.pathname === "/api/models/evidence") {
      const id = url.searchParams.get("id") ?? "";
      if (!/^\d+$/.test(url.searchParams.get("generation") ?? "")) {
        return json({ detail: "selected model request requires numeric generation" }, 400);
      }
      if (id === "candidate-cs") await new Promise((resolve) => setTimeout(resolve, 300));
      const evidence = evidenceById[id as keyof typeof evidenceById];
      if (!evidence) return json({ detail: "not found" }, 404);
      return json({
        schema: "ce.model-evidence-view.v1", id, evidence,
        snapshot: url.searchParams.get("snapshot") === "snapshot-ui-b" ? "snapshot-ui-b" : snapshot,
        generation: Number(url.searchParams.get("generation")), selection: url.searchParams.get("snapshot") === "snapshot-ui-b" ? { "repo-a": revisionB } : selection
      });
    }
    if (url.pathname === "/api/models/source") {
      const id = url.searchParams.get("id") ?? "";
      sourceRequestIds.push(id);
      if (!/^\d+$/.test(url.searchParams.get("generation") ?? "")) {
        return json({ detail: "selected model request requires numeric generation" }, 400);
      }
      if (id === "model-denied") return json({ detail: "denied" }, 403);
      if (id === "oversize-bpmn") {
        return route.fulfill({
          contentType: "application/json",
          body: JSON.stringify({
            schema: "ce.model-source-view.v1", id, repositoryId: "repo-a",
            commitSha: revisionB, unitPath: oversizedEvidence.unitPath, sha256: bpmnHash,
            format: "bpmn+xml", xml: "x".repeat(8_388_609),
            snapshot: "snapshot-ui-b", generation: 3, selection: { "repo-a": revisionB }
          })
        });
      }
      const xml = xmlById[id as keyof typeof xmlById];
      const evidence = evidenceById[id as keyof typeof evidenceById];
      if (!xml || !evidence) return json({ detail: "not found" }, 404);
      const sourceHash = evidence.sourceSha256;
      return json({
        schema: "ce.model-source-view.v1", id, repositoryId: "repo-a",
        commitSha: revisionA, unitPath: evidence.unitPath, sha256: sourceHash,
        format: id === "model-dmn" ? "dmn+xml" : "bpmn+xml", xml,
        snapshot: url.searchParams.get("snapshot") === "snapshot-ui-b" ? "snapshot-ui-b" : snapshot,
        generation: Number(url.searchParams.get("generation")), selection: url.searchParams.get("snapshot") === "snapshot-ui-b" ? { "repo-a": revisionB } : selection
      });
    }
    return json({ detail: `unexpected API route ${url.pathname}` }, 404);
  });

  const routeStarted = Date.now();
  await page.goto(`${origin}/#/processes-decisions`);
  await page.getByRole("link", { name: "Processes & decisions" }).click();
  await page.getByRole("heading", { name: "Processes & decisions" }).waitFor();
  const selectedEvidenceResponse = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return url.pathname === "/api/models/evidence" && url.searchParams.get("id") === "model-bpmn";
  });
  await page.getByRole("button", { name: /application\.bpmn\.json/ }).click();
  const evidenceReply = await selectedEvidenceResponse;
  assert.equal(evidenceReply.status(), 200, "selected evidence must use the API's generation parameter");
  assert.equal(new URL(evidenceReply.url()).searchParams.get("generation"), String(listedGeneration));
  timingsMs.firstSummarySelection = Date.now() - routeStarted;
  const bpmnFrame = page.frameLocator(".model-viewer-frame iframe");
  await bpmnFrame.locator('.djs-element[data-element-id="Task_Review"]').waitFor();
  timingsMs.verifiedBpmnRender = Date.now() - routeStarted;
  assert.match(await page.getByRole("region", { name: "BPMN process diagram" }).getAttribute("aria-describedby") ?? "", /diagram-help/);
  const frameElement = await page.locator(".model-viewer-frame iframe").evaluate((frame) => ({
    sandbox: frame.getAttribute("sandbox"),
    srcdoc: frame.getAttribute("srcdoc") ?? ""
  }));
  assert.equal(frameElement.sandbox, "allow-scripts");
  assert(!frameElement.sandbox.includes("allow-same-origin"));
  for (const directive of ["default-src 'none'", "connect-src 'none'", "img-src data:", "style-src-attr 'unsafe-inline'", "object-src 'none'", "form-action 'none'"]) {
    assert(frameElement.srcdoc.includes(directive), `missing CSP ${directive}`);
  }
  assert(frameElement.srcdoc.includes("script-src 'nonce-"));
  assert(frameElement.srcdoc.includes("/assets/model-viewer-frame.js"));
  assert(frameElement.srcdoc.includes("/assets/model-viewer-frame.css"));
  assert.match(frameElement.srcdoc, /font-src http:\/\/127\.0\.0\.1:\d+\/assets\/ data:/);
  const watermark = bpmnFrame.locator("a.bjs-powered-by");
  assert(await watermark.isVisible());
  assert.equal(await bpmnFrame.locator("body").evaluate((node) => getComputedStyle(node).backgroundColor), "rgb(255, 255, 255)");
  assert.equal(await watermark.evaluate((node) => getComputedStyle(node).opacity), "1");
  assert.equal(await watermark.evaluate((node) => {
    const box = node.getBoundingClientRect();
    const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
    return hit === node || node.contains(hit);
  }), true);
  const licenseLink = page.getByRole("link", { name: "bpmn.io viewer license and attribution" });
  assert.equal(new URL(await licenseLink.getAttribute("href") ?? "", origin).pathname, "/licenses/bpmn-io-viewers.txt");
  assert(sourceRequestIds.includes("model-bpmn"));
  const bpmnSourceRequest = new URL(requests.find((request) => request.startsWith("/api/models/source?id=model-bpmn"))!, origin);
  assert.equal(bpmnSourceRequest.searchParams.get("snapshot"), snapshot);
  assert.equal(bpmnSourceRequest.searchParams.get("generation"), String(listedGeneration));
  const bpmnEvidenceRequest = new URL(requests.find((request) => request.startsWith("/api/models/evidence?id=model-bpmn"))!, origin);
  assert.equal(bpmnEvidenceRequest.searchParams.get("snapshot"), snapshot);
  assert.equal(bpmnEvidenceRequest.searchParams.get("generation"), String(listedGeneration));
  assert(requests.findIndex((request) => request.startsWith("/api/models/evidence?id=model-bpmn")) <
    requests.findIndex((request) => request.startsWith("/api/models/source?id=model-bpmn")));
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-bpmn-diagram.png"), fullPage: true });

  await page.getByRole("button", { name: "Elements", exact: true }).click();
  await page.getByRole("button", { name: /Task_Review/ }).click();
  assert.match(await page.getByRole("button", { name: /Task_Review/ }).getAttribute("aria-pressed") ?? "", /true/);
  assert(await page.getByText("One opaque extension was retained.").isVisible());
  assert(await page.getByText("model.xml · element: Task_Review").isVisible());
  assert.equal(await page.locator('img[src*="example.invalid"]').count(), 0);
  assert.equal(await page.evaluate(() => (window as Window & { pwned?: boolean }).pwned), undefined);
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-bpmn.png"), fullPage: true });
  assertions.push("Production route shows ordered model list, verified original BPMN source, keyboard text alternative, selection, coverage limitation, and exact diagnostics.");

  assertions.push("Live frame CSP restricts scripts to nonce-authorized bundled viewer, CSS to bundled asset, images to data, and network connections to none; watermark remains unobscured.");

  await page.getByRole("button", { name: /eligibility\.dmn\.json/ }).click();
  await page.getByRole("button", { name: "Decision table", exact: true }).click();
  const table = page.getByRole("table", { name: "Eligibility authored decision table" });
  assert.deepEqual(await table.locator("thead th").allTextContents(), ["Applicant age", "Eligibility"]);
  assert.deepEqual(await table.locator("tbody tr").evaluateAll((rows) => rows.map((row) => [...row.querySelectorAll("td")].map((cell) => cell.textContent))), [
    ["<18", '"deny"'], ["[18..24]", '"manual"'], ["-", '"approve"']
  ]);
  assert.equal(await table.getByRole("columnheader", { name: "Annotations" }).count(), 0);
  assert(await page.getByText("score >= threshold").isVisible());
  assert(await page.getByText(/never evaluated/).isVisible());
  assert(await page.getByText(/FEEL · text only; not evaluated/).isVisible());
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-dmn.png"), fullPage: true });
  assertions.push("DMN source-derived accessible table has exact authored columns and ordered rule cells; no renderer-generated annotations or expression evaluation.");

  await page.getByRole("button", { name: /Eligibility\.cs/ }).click();
  await page.waitForFunction(() => window.location.hash.includes("id=candidate-cs"));
  assert.equal(await page.getByRole("table", { name: "Eligibility authored decision table" }).count(), 0, "previous model evidence must be cleared immediately while the candidate detail is pending");
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0, "previous source frame must be disposed before candidate evidence loads");
  await page.getByRole("alert").filter({ hasText: "UNREVIEWED" }).waitFor({ state: "visible" });
  await page.getByRole("heading", { name: /Bounded candidate recipe: roslyn\.scalar-guards\.v1/ }).waitFor();
  await page.getByText(/UTF-16 span 2\+10/).first().waitFor();
  assert(!sourceRequestIds.includes("candidate-cs"), "candidate source must never use the BPMN/DMN XML endpoint");
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-candidate.png"), fullPage: true });
  assertions.push("Candidate recipe, raw text, UTF-16 source locators, and unapproved/non-executable status stay separate; no XML source request is made.");

  await page.getByRole("button", { name: "Load next page" }).click();
  await page.getByRole("button", { name: /denied\.bpmn\.json/ }).click();
  await page.getByText("denied", { exact: true }).waitFor();
  assert(!sourceRequestIds.includes("model-denied"));
  assertions.push("Opaque cursor extends the bounded list; denied source status is explicit and triggers no source request.");

  await page.getByRole("button", { name: /application\.bpmn\.json/ }).click();
  await bpmnFrame.locator('.djs-element[data-element-id="Task_Review"]').waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ colorScheme: "dark" });
  assert(await watermark.isVisible());
  assert.equal(await bpmnFrame.locator("body").evaluate((node) => getComputedStyle(node).backgroundColor), "rgb(24, 24, 27)");
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
  await page.screenshot({ path: resolve(artifacts, "issue78-ui-production-mobile-dark.png"), fullPage: true });
  assertions.push("Narrow dark presentation preserves the visible watermark and avoids page-level horizontal overflow.");
  const revision = page.getByLabel("Revision of repo-a");
  await revision.selectOption(revisionB);
  await page.getByRole("button", { name: /oversized\.bpmn\.json/ }).waitFor();
  await page.waitForFunction(() => document.querySelectorAll(".model-viewer-frame iframe").length === 0);
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0);
  assertions.push("Revision switch immediately removes old viewer content while the new generation-bound model list is pending.");
  await page.getByRole("button", { name: /oversized\.bpmn\.json/ }).click();
  await page.getByRole("alert").filter({ hasText: "bounded response limit" }).waitFor();
  assert.equal(await page.locator(".model-viewer-frame iframe").count(), 0, "oversized API source must not reach viewer construction");
  assertions.push("Streaming response byte limit cancels an oversized source wrapper before parsing or renderer creation.");

  const expectedRequests = requests.filter((request) => request.startsWith("/api/models"));
  assert(expectedRequests.some((request) => request.includes("/evidence?id=model-bpmn")));
  assert(expectedRequests.some((request) => request.includes("/source?id=model-bpmn")));
  const cursorRequest = new URL(expectedRequests.find((request) => request.startsWith("/api/models?") && request.includes("cursor="))!, origin);
  assert.equal(cursorRequest.searchParams.get("snapshot"), snapshot);
  assert.equal(cursorRequest.searchParams.get("generation"), String(listedGeneration));
  assert.deepEqual(externalRequests, []);
  assert.deepEqual(consoleErrors, []);
  const artifact = {
    commit: "dirty worktree source; see exact file hashes",
    sourceHashes: Object.fromEntries(await Promise.all([
      "src/models/contracts.ts", "src/models/viewer-sandbox.ts", "src/models/viewer-frame-entry.ts",
      "src/views/ProcessesDecisions.tsx", "src/views/ModelEvidence.tsx", "src/App.tsx", "src/views/DatabaseExplorer.tsx",
      "src/index.css", "vite.model-frame.ts", "vite.config.ts"
    ].map(async (file) => [file, sha256(await readFile(resolve(client, file)))]))),
    fixtureSha256: { bpmn: bpmnHash, dmn: dmnHash },
    packageLockSha256: summariesHash,
    bundle,
    assertions,
    timingsMs,
    apiPaths: expectedRequests.map((request) => request.replace(/^\/api\/models/, "/api/models")),
    browserRequests: requests.filter((request) => !request.startsWith("/api/")),
    externalRequests,
    browser: { version: browser.version(), node: process.version, browserImage: "Host Playwright Chromium; pinned browser container image not exercised." },
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
