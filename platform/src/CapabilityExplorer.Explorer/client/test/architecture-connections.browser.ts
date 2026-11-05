import assert from "node:assert/strict";
import { mkdir, readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import playwright from "../../../../../spikes/likec4/node_modules/playwright/index.js";

const { chromium } = playwright;

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test(`Inferred cluster at ${viewport.width}px opens contract details instead of route review`, async () => {
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport });
    page.setDefaultTimeout(5000);
    const capabilities = [{ id: "source", name: "Planning", repositories: ["source-repo"] },
      { id: "target", name: "Reference", repositories: ["target-repo"] }];
    const note = "Contract: incompatible. HTTP method differs: PUT versus PATCH. Gateway ownership is unverified.";
    await page.route("**/api/**", async route => {
      const url = new URL(route.request().url());
      let payload: unknown = {};
      if (url.pathname === "/api/meta") payload = { generation: 1, capabilities, githubAuth: { source: "host-only", hostManaged: true },
        interpretation: { enabled: false, chat: false, provider: "none" }, similarity: { database: false }, revisions: [], defaultView: {},
        artifacts: 2, problems: [], hasSource: false, hasEvaluation: false, nodeKinds: [] };
      else if (url.pathname === "/api/generation") payload = { generation: 1 };
      else if (url.pathname === "/api/architecture/connections") payload = { version: 0, connections: [] };
      else if (url.pathname === "/api/binding-preview/recommendations") payload = { groups: [], owners: [], fingerprint: "fixture", algorithmVersion: "fixture", sourceView: "", targetView: "" };
      else if (url.pathname === "/api/architecture") payload = { level: "landscape", title: "Landscape", breadcrumbs: [], notes: [],
        nodes: capabilities.map(capability => ({ id: "cap:" + capability.id, category: "capability", label: capability.name, badges: [] })),
        edges: [{ id: "automatic:cluster:target", from: "cap:source", to: "cap:target", kind: "http", label: "http (inferred)",
          count: 4, certainty: "candidate", facts: [], note }] };
      await route.fulfill({ json: payload });
    });
    try {
      await page.goto(`${process.env.CE_GRAPH_TEST_URL ?? "http://127.0.0.1:5087"}/#/architecture?level=landscape`);
      await page.locator('.architecture-canvas [data-status="ready"]').waitFor();
      await page.locator(".architecture-canvas").getByText(/http \(inferred\)/).first().click();
      await page.locator("aside").getByText(note, { exact: true }).waitFor();
      assert.equal(await page.getByRole("region", { name: "HTTP capability recommendations" }).count(), 0);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false);
    } finally { await browser.close(); }
  });
}

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test(`LLM integration suggestions at ${viewport.width}px retain evidence without adopting connections`, async () => {
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport });
    page.setDefaultTimeout(5000);
    let manualWrites = 0;
    const capabilities = [{ id: "source", name: "Planning", repositories: ["source-repo"] },
      { id: "target", name: "Reference", repositories: ["target-repo"] }];
    await page.route("**/api/**", async route => {
      const request = route.request();
      const url = new URL(request.url());
      let payload: unknown = {};
      if (url.pathname === "/api/meta") payload = { generation: 1, capabilities, githubAuth: { source: "host-only", hostManaged: true },
        interpretation: { enabled: true, chat: true, provider: "fixture" }, similarity: { database: false }, revisions: [], defaultView: {},
        artifacts: 2, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: [] };
      else if (url.pathname === "/api/generation") payload = { generation: 1 };
      else if (url.pathname === "/api/architecture/connections") {
        if (request.method() !== "GET") manualWrites++;
        payload = { version: 0, connections: [] };
      } else if (url.pathname === "/api/architecture/integration-suggestions") {
        assert.equal(request.headers()["x-ce-integration-suggestions"], "1");
        const body: unknown = request.postDataJSON();
        assert.ok(body && typeof body === "object" && "sourceCapabilityId" in body && "targetCapabilityId" in body);
        assert.equal(body.sourceCapabilityId, "source");
        assert.equal(body.targetCapabilityId, "target");
        payload = { status: "suggested", dependency: "observed", compatibility: "unverified", transport: "http", model: "fixture-model",
          explanation: "Caller uses a gateway path; target identity is not independently established.", inputHash: "a".repeat(64), citations: [1, 2],
          evidence: [{ id: 1, side: "source", repositoryId: "source-repo", commitSha: "a".repeat(40), path: "Caller.cs", startLine: 10, endLine: 11, text: "await client.PostAsync(url, body);" },
            { id: 2, side: "target", repositoryId: "target-repo", commitSha: "b".repeat(40), path: "Endpoint.cs", startLine: 20, endLine: 21, text: "[HttpPost(\"query\")]" }] };
      } else if (url.pathname === "/api/architecture") payload = { level: "landscape", title: "Landscape", breadcrumbs: [], notes: [], edges: [],
        nodes: capabilities.map(capability => ({ id: "cap:" + capability.id, category: "capability", label: capability.name, badges: [] })) };
      await route.fulfill({ json: payload });
    });
    try {
      await page.goto(`${process.env.CE_GRAPH_TEST_URL ?? "http://127.0.0.1:5085"}/#/architecture?level=landscape`);
      await page.getByRole("button", { name: "Validate integrations with model", exact: true }).click();
      await page.getByLabel("Source capability", { exact: true }).selectOption("source");
      await page.getByLabel("Target capability", { exact: true }).selectOption("target");
      await page.getByLabel("Integration query", { exact: true }).fill("ArticleReferenceRepository");
      await page.getByRole("button", { name: "Assess integration", exact: true }).click();
      await page.getByText("Target compatibility: unverified", { exact: true }).waitFor();
      await page.getByText("Source dependency: observed", { exact: true }).waitFor();
      await page.getByText("Caller uses a gateway path; target identity is not independently established.", { exact: true }).waitFor();
      assert.equal(manualWrites, 0);
      assert.equal(await page.getByRole("button", { name: /adopt|confirm connection/i }).count(), 0);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false);
      await page.locator('.architecture-canvas [data-status="ready"]').waitFor();
      const output = resolve("../../../../.sandbox-cache/integration-matching-evidence");
      await mkdir(output, { recursive: true });
      await page.screenshot({ path: resolve(output, `llm-inspector-${viewport.width}.png`), fullPage: true });
    } finally { await browser.close(); }
  });
}

interface Connection {
  id: string;
  fromCapabilityId: string;
  toCapabilityId: string;
  kind: string;
  note: string;
  sourceRepositoryId?: string;
  sourceKey?: string;
  targetRepositoryId?: string;
  targetKey?: string;
}

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test(`Given a graph at ${viewport.width}px when capabilities are connected then the manual link can be edited persisted and removed`, async () => {
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport });
    let version = 0;
    let generation = 1;
    let connections: Connection[] = [];
    const sourceKey = "cs|api-cs|Sales.Api|M:Sales.Api.LoadSales";
    const targetKey = "cs|forecast-cs|Forecast.Api|M:Forecast.Api.RunQuantification";
    const capabilities = [{ id: "first", name: "Planning", repositories: ["api-cs"] },
      { id: "second", name: "Quantification", repositories: ["forecast-cs"] }];
    await page.route("**/api/**", async route => {
      const request = route.request();
      const url = new URL(request.url());
      let payload: unknown = {};
      if (url.pathname === "/api/meta") payload = {
        generation, capabilities, githubAuth: { source: "host-only", hostManaged: true },
        interpretation: { enabled: false, chat: false, provider: "none" }, similarity: { database: false },
        revisions: [], defaultView: {}, artifacts: 1, problems: [], hasSource: false, hasEvaluation: false, nodeKinds: [],
      };
      else if (url.pathname === "/api/generation") payload = { generation };
      else if (url.pathname === "/api/search") {
        const source = url.searchParams.get("repo") === "api-cs";
        const node = { key: source ? sourceKey : targetKey, name: source ? "LoadSales" : "RunQuantification",
          kind: "Method", repositoryId: source ? "api-cs" : "forecast-cs", unit: source ? "Sales.Api" : "Forecast.Api",
          generated: false, hasCalculation: false };
        payload = source ? [node, { ...node, key: "cs|api-cs|Sales.Api|M:Sales.Api.SecondClient.LoadSales" }] : [node];
      }
      else if (url.pathname.startsWith("/api/architecture/connections")) {
        if (request.method() === "POST" || request.method() === "PUT") {
          const body: unknown = request.postDataJSON();
          assert.ok(body && typeof body === "object" && "kind" in body && "note" in body);
          assert.ok("fromCapabilityId" in body && "toCapabilityId" in body);
          connections = [{ id: "manual-test", fromCapabilityId: String(body.fromCapabilityId),
            toCapabilityId: String(body.toCapabilityId), kind: String(body.kind), note: String(body.note),
            sourceRepositoryId: "sourceRepositoryId" in body ? String(body.sourceRepositoryId) : undefined,
            sourceKey: "sourceKey" in body ? String(body.sourceKey) : undefined,
            targetRepositoryId: "targetRepositoryId" in body ? String(body.targetRepositoryId) : undefined,
            targetKey: "targetKey" in body ? String(body.targetKey) : undefined }];
          version++;
        } else if (request.method() === "DELETE") {
          connections = [];
          version++;
        }
        payload = { version, connections };
      } else if (url.pathname === "/api/architecture") payload = {
        level: "landscape", title: "Landscape", breadcrumbs: [{ label: "Landscape", level: "landscape" }], notes: [],
        nodes: capabilities.map(capability => ({ id: "cap:" + capability.id, category: "capability", label: capability.name,
          drill: { level: "system", focus: capability.id }, badges: ["1 repository"] })),
        edges: connections.map(connection => ({ id: "manual:" + connection.id, from: "cap:" + connection.fromCapabilityId,
          to: "cap:" + connection.toCapabilityId, kind: connection.kind, label: connection.kind + " (manual)",
          count: 1, certainty: "human", facts: [], manualConnectionId: connection.id, note: connection.note })),
      };
      await route.fulfill({ json: payload });
    });
    try {
      await page.goto(`${process.env.CE_GRAPH_TEST_URL ?? "http://127.0.0.1:5082"}/#/architecture?level=landscape`);
      await page.getByRole("button", { name: "Connect capabilities", exact: true }).click();
      if (viewport.width < 650) {
        const editor = await page.locator(".architecture-panels aside").boundingBox();
        const canvas = await page.locator(".architecture-canvas").boundingBox();
        assert.ok(editor && canvas && editor.y < canvas.y, "The mobile connection editor must precede the diagram.");
      }
      await page.getByLabel("From capability", { exact: true }).selectOption("first");
      await page.getByLabel("To capability", { exact: true }).selectOption("second");
      await page.getByLabel("From repository", { exact: true }).selectOption("api-cs");
      await page.getByLabel("To repository", { exact: true }).selectOption("forecast-cs");
      await page.getByLabel("Search from analyzed nodes", { exact: true }).fill("LoadSales");
      await page.getByLabel("Search to analyzed nodes", { exact: true }).fill("RunQuantification");
      await page.getByLabel("From analyzed node", { exact: true }).selectOption(sourceKey);
      await page.getByLabel("To analyzed node", { exact: true }).selectOption(targetKey);
      const sourceLabels = await page.getByLabel("From analyzed node", { exact: true }).locator("option").allTextContents();
      assert.equal(new Set(sourceLabels).size, sourceLabels.length, "Analyzed nodes with the same name must have distinguishable labels.");
      await page.getByLabel("Connection type", { exact: true }).selectOption("navigation");
      await page.getByLabel("Connection note", { exact: true }).fill("Reviewed article navigation");
      const refreshed = page.waitForResponse(response => new URL(response.url()).pathname === "/api/meta");
      generation = 2;
      await refreshed;
      await page.getByLabel("From analyzed node", { exact: true }).waitFor();
      assert.equal(await page.getByLabel("From analyzed node", { exact: true }).inputValue(), sourceKey);
      assert.equal(await page.getByLabel("To analyzed node", { exact: true }).inputValue(), targetKey);
      const editorOutput = resolve("../../../../.sandbox-cache/integration-audit-evidence");
      await mkdir(editorOutput, { recursive: true });
      await page.locator('.architecture-canvas [data-status="ready"]').waitFor();
      await page.screenshot({ path: resolve(editorOutput, `manual-editor-${viewport.width}.png`), fullPage: true });
      await page.getByRole("button", { name: "Save connection", exact: true }).click();
      await page.getByRole("button", { name: "Edit navigation connection from Planning to Quantification", exact: true }).waitFor();
      assert.equal(connections[0]?.kind, "navigation");
      assert.equal(connections[0]?.sourceRepositoryId, "api-cs");
      assert.equal(connections[0]?.targetRepositoryId, "forecast-cs");
      assert.equal(connections[0]?.sourceKey, sourceKey);
      assert.equal(connections[0]?.targetKey, targetKey);
      await page.reload();
      await page.getByRole("button", { name: "Edit navigation connection from Planning to Quantification", exact: true }).click();
      assert.equal(await page.getByLabel("From repository", { exact: true }).inputValue(), "api-cs");
      assert.equal(await page.getByLabel("To repository", { exact: true }).inputValue(), "forecast-cs");
      assert.equal(await page.getByLabel("From analyzed node", { exact: true }).inputValue(), sourceKey);
      assert.equal(await page.getByLabel("To analyzed node", { exact: true }).inputValue(), targetKey);
      await page.getByLabel("Connection note", { exact: true }).fill("Reviewed navigation, updated");
      await page.getByRole("button", { name: "Save connection", exact: true }).click();
      await page.getByRole("button", { name: "Edit navigation connection from Planning to Quantification", exact: true }).waitFor();
      assert.equal(connections[0]?.note, "Reviewed navigation, updated");
      await page.locator('.architecture-canvas [data-status="ready"]').waitFor();
      await page.locator(".architecture-canvas svg").first().waitFor();
      assert.ok(await page.locator(".architecture-canvas svg").count() > 0);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
      assert.equal(overflow, false, "Graph connection controls must fit the viewport");
      const output = resolve("../../../../.sandbox-cache/integration-audit-evidence");
      await mkdir(output, { recursive: true });
      await page.screenshot({ path: resolve(output, `manual-connections-${viewport.width}.png`), fullPage: true });
      await page.getByRole("button", { name: "Edit navigation connection from Planning to Quantification", exact: true }).click();
      page.once("dialog", dialog => { void dialog.accept(); });
      await page.getByRole("button", { name: "Remove connection", exact: true }).click();
      await page.getByRole("button", { name: "Edit navigation connection from Planning to Quantification", exact: true }).waitFor({ state: "hidden" });
      assert.equal(connections.length, 0);
    } finally {
      await browser.close();
    }
  });
}

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test(`HTTP recommendation at ${viewport.width}px requires explicit review after architecture handoff`, async () => {
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport });
    page.setDefaultTimeout(5000);
    const hint = "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL";
    const target = "forecast-cs/Forecast.Api";
    const source = { repositoryId: "pricing-node", commitSha: "a".repeat(40), unitPath: "tsconfig.json", artifactHash: "b".repeat(64) };
    const destination = { repositoryId: "forecast-cs", commitSha: "c".repeat(40), unitPath: "Forecast.Api.csproj", artifactHash: "d".repeat(64) };
    const sourceView = `pricing-node:${source.commitSha}`;
    const targetView = `${sourceView},forecast-cs:${destination.commitSha}`;
    const requestEvidence = { source, edgeId: "e:client", target: `http|${hint}|GET /api/forecasts/{}`, evidence: [] };
    let fingerprint = "fixture-snapshot";
    let mutationCount = 0;
    let exports = 0;
    const capabilities = [{ id: "consumer", name: "Pricing", repositories: ["pricing-node"] },
      { id: "provider", name: "Forecast", repositories: ["forecast-cs"] }];
    await page.route("**/api/**", async route => {
      const request = route.request();
      const url = new URL(request.url());
      let payload: unknown = {};
      if (url.pathname === "/api/meta") payload = { generation: 1, capabilities, githubAuth: { source: "none", hostManaged: true },
        interpretation: { enabled: false, chat: false, provider: "none" }, similarity: { database: false }, revisions: [],
        defaultView: {}, artifacts: 2, problems: [], hasSource: false, hasEvaluation: false, nodeKinds: [] };
      else if (url.pathname === "/api/generation") payload = { generation: 1 };
      else if (url.pathname === "/api/view") payload = { "pricing-node": source.commitSha };
      else if (url.pathname === "/api/architecture/connections") {
        if (request.method() !== "GET") mutationCount++;
        payload = { version: 0, connections: [] };
      } else if (url.pathname === "/api/architecture") payload = { level: "landscape", title: "Landscape",
        breadcrumbs: [{ label: "Landscape", level: "landscape" }], notes: [], edges: [],
        nodes: [{ id: "cap:consumer", category: "capability", label: "Pricing", badges: [] }] };
      else if (url.pathname === "/api/binding-preview/recommendations") payload = {
        fingerprint, algorithmVersion: "exact-http-v1", sourceView, targetView,
        owners: [{ repositoryId: "forecast-cs", capabilityId: "provider", name: "Forecast" }],
        groups: [{ id: "forecast-host", hint, label: hint, state: "candidate", requests: [requestEvidence], candidates: [{
          target, score: 100, matchedRoutes: 1, totalRoutes: 1, state: "candidate", previewEligible: true,
          features: [{ name: "Distinct method/route coverage (not confidence)", points: 100 }],
          matches: [{ request: requestEvidence, endpoint: { source: destination, key: `http|${target}|GET /api/forecasts/{}`, evidence: [] } }],
          unmatched: [], missingEvidence: ["Host ownership", "Response schema compatibility"],
        }] }],
      };
      else if (url.pathname === "/api/binding-preview" && request.method() === "GET") payload = {
        fingerprint, sources: [source], targetSources: [destination], targets: [target],
        hints: [{ hint, existingTarget: null, occurrences: [{ source, edgeId: "e:client", relation: "REQUESTS", evidence: [] }] }],
      };
      else if (url.pathname.startsWith("/api/binding-preview") && request.method() === "POST") {
        const body: unknown = request.postDataJSON();
        assert.ok(body && typeof body === "object" && "entries" in body && Array.isArray(body.entries));
        assert.ok("expectedFingerprint" in body);
        assert.equal(body.expectedFingerprint, fingerprint);
        assert.equal(url.searchParams.get("targetView"), targetView);
        const entry: unknown = body.entries[0];
        assert.ok(entry && typeof entry === "object" && "reviewed" in entry);
        if (url.pathname.endsWith("/export")) { assert.equal(entry.reviewed, true); exports++; }
        payload = { fingerprint, canExport: entry.reviewed === true, rows: [{ hint, target, state: "evidenced", message: "Human assertion", reviewed: entry.reviewed, edgeCount: 1 }] };
      }
      await route.fulfill({ json: payload });
    });
    try {
      await page.goto(`${process.env.CE_GRAPH_TEST_URL ?? "http://127.0.0.1:5083"}/#/architecture?level=landscape`);
      await page.getByRole("button", { name: "Recommend capabilities", exact: true }).click();
      await page.getByRole("combobox", { name: "Unresolved HTTP host", exact: true }).selectOption("forecast-host");
      await page.getByText("Forecast", { exact: true }).last().waitFor();
      await page.getByRole("link", { name: `Review ${target}`, exact: true }).click();
      const destinationInput = page.getByLabel(`Destination for ${hint}`, { exact: true });
      await destinationInput.waitFor();
      await page.waitForFunction(({ label, value }) => document.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)?.value === value,
        { label: `Destination for ${hint}`, value: target });
      const reviewed = page.getByRole("checkbox", { name: "I reviewed this human mapping against the source and destination.", exact: true });
      assert.equal(await reviewed.isChecked(), false);
      await page.getByRole("button", { name: "Preview mappings", exact: true }).click();
      await page.getByRole("status").filter({ hasText: "evidenced" }).waitFor();
      assert.equal(await page.getByRole("button", { name: "Export reviewed proposal", exact: true }).isEnabled(), false);
      await reviewed.check();
      await page.getByRole("button", { name: "Preview mappings", exact: true }).click();
      await page.waitForFunction(() => [...document.querySelectorAll("button")].some(button => button.textContent === "Export reviewed proposal" && !button.disabled));
      const download = page.waitForEvent("download");
      await page.getByRole("button", { name: "Export reviewed proposal", exact: true }).click();
      await download;
      assert.equal(exports, 1);
      assert.equal(mutationCount, 0);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false);
      const output = resolve("../../../../.sandbox-cache/issue81-evidence");
      await mkdir(output, { recursive: true });
      await page.screenshot({ path: resolve(output, `recommendations-${viewport.width}.png`), fullPage: true });
      fingerprint = "changed-provider-snapshot";
      await page.getByRole("button", { name: "Reload source", exact: true }).click();
      await page.getByRole("alert").filter({ hasText: "changed" }).waitFor();
      assert.equal(await reviewed.isChecked(), false);
      assert.equal(await page.getByRole("button", { name: "Export reviewed proposal", exact: true }).isEnabled(), false);
    } finally { await browser.close(); }
  });
}

test("HTTP recommendation real fixture API and browser export retain both source identities", { skip: !process.env.CE_GRAPH_TEST_REAL_URL }, async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const parsed: unknown = JSON.parse(await readFile(resolve("../../../../fixtures/revisions.lock.json"), "utf8"));
  assert.ok(parsed && typeof parsed === "object" && "pricing-node" in parsed && "forecast-cs" in parsed);
  const pricing: unknown = parsed["pricing-node"];
  const forecast: unknown = parsed["forecast-cs"];
  assert.ok(pricing && typeof pricing === "object" && "v1-base" in pricing && typeof pricing["v1-base"] === "string");
  assert.ok(forecast && typeof forecast === "object" && "v1-base" in forecast && typeof forecast["v1-base"] === "string");
  const hint = "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL";
  try {
    await page.addInitScript(({ pricing, forecast }) => {
      localStorage.setItem("ce.capability", JSON.stringify(""));
      localStorage.setItem("ce.overrides", JSON.stringify({ "*": { "pricing-node": pricing, "forecast-cs": forecast } }));
    }, { pricing: pricing["v1-base"], forecast: forecast["v1-base"] });
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(`${process.env.CE_GRAPH_TEST_REAL_URL}/#/architecture?level=landscape`);
    await page.getByRole("button", { name: "Recommend capabilities", exact: true }).click();
    const hosts = page.getByRole("combobox", { name: "Unresolved HTTP host", exact: true });
    const value = await hosts.locator("option").filter({ hasText: "FORECAST_API_URL" }).getAttribute("value");
    assert.ok(value);
    await hosts.selectOption(value);
    await page.getByRole("link", { name: "Review forecast-cs/Forecast.Api", exact: true }).click();
    const destination = page.getByLabel(`Destination for ${hint}`, { exact: true });
    await page.waitForFunction(label => document.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)?.value === "forecast-cs/Forecast.Api", `Destination for ${hint}`);
    const article = page.locator("article").filter({ has: destination });
    assert.equal(await article.getByRole("checkbox").isChecked(), false);
    await article.getByRole("checkbox").check();
    await page.getByRole("button", { name: "Preview mappings", exact: true }).click();
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some(button => button.textContent === "Export reviewed proposal" && !button.disabled));
    const downloading = page.waitForEvent("download");
    await page.getByRole("button", { name: "Export reviewed proposal", exact: true }).click();
    const downloadPath = await (await downloading).path();
    assert.ok(downloadPath);
    const exported: unknown = JSON.parse(await readFile(downloadPath, "utf8"));
    assert.ok(exported && typeof exported === "object" && "reviewState" in exported && "sources" in exported && Array.isArray(exported.sources));
    assert.equal(exported.reviewState, "reviewed-export-not-adopted");
    for (const repository of ["pricing-node", "forecast-cs"]) assert.ok(exported.sources.some(source =>
      source && typeof source === "object" && "repositoryId" in source && source.repositoryId === repository));
    assert.deepEqual(errors, []);
    await page.screenshot({ path: resolve("../../../../.sandbox-cache/issue81-evidence/real-fixture-review.png"), fullPage: true });
  } finally { await browser.close(); }
});