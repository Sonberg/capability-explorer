import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import playwright from "../spikes/likec4/node_modules/playwright/index.js";
import { createServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import type { ViteDevServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import { listenBrowserServer } from "./browser-server.ts";

const retired = [
  ["evaluation", "Evaluation"],
  ["calculations", "Calculations"],
  ["binding-preview", "Binding review"],
  ["artifacts", "Artifacts"],
  ["changes", "Changes"],
] as const;
const selection = { synthetic: "a".repeat(40) };
let server: ViteDevServer;
let origin: string;

before(async () => {
  server = await createServer({
    root: new URL("../platform/src/CapabilityExplorer.Explorer/client", import.meta.url).pathname,
    server: { host: "127.0.0.1", port: 0, fs: { allow: [new URL("..", import.meta.url).pathname] } },
    logLevel: "error",
  });
  origin = await listenBrowserServer(server);
});
after(async () => { await server?.close(); });

for (const mode of ["files", "database"] as const) {
  test(`121-125 ${mode}: retired pages leave navigation and direct routes use the existing unsupported-route behavior`, async () => {
    const browser = await playwright.chromium.launch({ headless: true });
    try {
      const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
      page.setDefaultTimeout(10_000);
      const errors: string[] = [];
      const writes: string[] = [];
      page.on("pageerror", error => errors.push(error.message));
      await page.route("**/api/**", async route => {
        const request = route.request();
        const url = new URL(request.url());
        if (request.method() !== "GET") writes.push(`${request.method()} ${url.pathname}`);
        const meta = mode === "database" ? {
          source: "postgres", snapshotId: "snapshot-fixture", generation: 1, capability: "*",
          selection, capabilities: [], supported: ["search", "calculations", "artifacts"], status: "ready",
        } : {
          snapshotId: "snapshot-fixture", generation: 1, capabilities: [],
          githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
          similarity: { database: false }, revisions: [], defaultView: selection,
          artifacts: 1, problems: [], hasSource: true, hasEvaluation: true, nodeKinds: [],
        };
        let payload: unknown;
        switch (url.pathname) {
          case "/api/meta": payload = meta; break;
          case "/api/generation": payload = { snapshotId: meta.snapshotId, generation: 1 }; break;
          case "/api/view": payload = selection; break;
          case "/api/search": payload = mode === "database"
            ? { items: [], nextCursor: null, snapshotId: meta.snapshotId } : []; break;
          case "/api/embeddings/status": payload = {
            snapshotId: meta.snapshotId, generation: 1, capability: "*", selection,
            state: "not-configured", configuredModel: null, coverageModel: null, coverageScope: "selected-view",
            similarityAvailable: false, snapshotArtifactCount: 1, selectedArtifactCount: 1, coveredArtifactCount: 0,
            snapshotArtifactsSha256: null, countsExact: false, counts: null,
            lastSuccessfulCompletion: null, reasonCode: null,
          }; break;
          case "/api/health": payload = { status: "healthy" }; break;
          default: await route.fulfill({ status: 404, json: { error: "Unexpected fixture API request" } }); return;
        }
        await route.fulfill({ json: payload });
      });

      await page.goto(`${origin}/#/unsupported-route`);
      const search = page.getByRole("searchbox", { name: "Search symbols" }).or(page.getByRole("textbox", { name: "Search symbols", exact: true }));
      await search.waitFor();
      await page.getByRole("region", { name: "Symbol results" }).locator('[role="list"][aria-busy="false"]').waitFor({ state: "attached" });
      const unknownMain = await page.locator("main").innerText();
      const nav = page.getByRole("navigation", { name: mode === "files" ? "Views" : "Database views", exact: true });
      for (const [path, label] of retired) {
        assert.equal(await nav.getByRole("link", { name: label, exact: true }).count(), 0, `${label} must not be a navigation destination.`);
        assert.equal(await nav.locator(`a[href="#/${path}"]`).count(), 0);
      }

      if (mode === "files") {
        await page.getByRole("button", { name: "Open command menu" }).click();
        const menu = page.getByRole("dialog", { name: "Command menu" });
        await menu.waitFor();
        for (const [, label] of retired)
          assert.equal(await menu.getByRole("option", { name: label, exact: true }).count(), 0, `${label} must not remain in the command menu.`);
        assert.equal(await menu.getByRole("option", { name: "Architecture", exact: true }).count(), 1);
        await page.keyboard.press("Escape");
      }

      for (const [path] of retired) {
        await page.goto(`${origin}/#/${path}`);
        await search.waitFor();
        await page.getByRole("region", { name: "Symbol results" }).locator('[role="list"][aria-busy="false"]').waitFor({ state: "attached" });
        assert.equal(await page.locator("main").innerText(), unknownMain, `${path} must use the existing unsupported-route rendering.`);
        assert.equal(new URL(page.url()).hash, `#/${path}`, "Do not invent a redirect for retired routes.");
      }
      assert.equal(await nav.getByRole("link", { name: "Explore", exact: true }).count(), 1);
      await nav.getByRole("link", { name: "Explore", exact: true }).click();
      await search.waitFor();
      assert.deepEqual(errors, [], "Retained exploration must render without page errors.");
      assert.deepEqual(writes, [], "Retiring navigation must not cause API writes.");
    } finally { await browser.close(); }
  });
}
