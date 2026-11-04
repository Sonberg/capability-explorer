import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import playwright from "../../../../../spikes/likec4/node_modules/playwright/index.js";

const { chromium } = playwright;

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