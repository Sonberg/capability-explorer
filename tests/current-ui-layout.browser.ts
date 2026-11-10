import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import playwright from "../spikes/likec4/node_modules/playwright/index.js";
import { createServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import type { ViteDevServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import { listenBrowserServer } from "./browser-server.ts";

const label = "Observed service " + "x".repeat(200);
const nodeId = "service:" + "n".repeat(300);
const omittedId = "missing:" + "p".repeat(4088);
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

for (const viewport of [{ width: 1440, height: 1000 }, { width: 1024, height: 768 }, { width: 390, height: 844 }]) {
  for (const theme of ["light", "dark"] as const) {
    test(`Layout ${viewport.width} ${theme}: bounded status, long pins, theme continuity and accessible details`, async () => {
      const browser = await playwright.chromium.launch({ headless: true });
      try {
        const page = await browser.newPage({ viewport, colorScheme: theme });
        page.setDefaultTimeout(15_000);
        const errors: string[] = [];
        const writes: string[] = [];
        page.on("pageerror", error => errors.push(error.message));
        await page.route("**/api/**", async route => {
          const request = route.request();
          const url = new URL(request.url());
          if (request.method() !== "GET") writes.push(request.method());
          let payload: unknown;
          switch (url.pathname) {
            case "/api/meta": payload = {
              generation: 1, snapshotId: "layout", capabilities: [], revisions: [], defaultView: {},
              githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
              similarity: { database: false }, artifacts: 1, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: [],
            }; break;
            case "/api/generation": payload = { generation: 1, snapshotId: "layout" }; break;
            case "/api/view": payload = {}; break;
            case "/api/architecture/connections": payload = { version: 0, connections: [] }; break;
            case "/api/architecture": {
              const include = url.searchParams.getAll("include");
              const hideKinds = url.searchParams.getAll("hideKind").sort();
              payload = {
                level: "landscape", title: "Landscape", breadcrumbs: [{ label: "Landscape", level: "landscape" }],
                notes: [], nodes: [{ id: nodeId, label, category: "service", badges: [], symbolKey: "s".repeat(300) }], edges: [],
                budget: {
                  nodeBudget: Number(url.searchParams.get("nodeBudget")), edgeBudget: Number(url.searchParams.get("edgeBudget")),
                  totalNodes: 12345, matchedNodes: 12345, shownNodes: 1, totalEdges: 98765, matchedEdges: 54321, shownEdges: 0,
                  truncated: true, nodesTruncated: true, edgesTruncated: true, pinned: include, missingPins: include,
                },
                filters: { query: url.searchParams.get("q"), hideKinds, include },
                facets: { kinds: [] },
                omitted: { nodeCount: 12344, nodes: [{ id: omittedId, label: "Omitted " + "o".repeat(200), category: "service", degree: 1 }] },
              };
              break;
            }
            default: payload = {};
          }
          await route.fulfill({ json: payload });
        });
        await page.goto(`${origin}/#/architecture?level=landscape`);
        const ready = async () => {
          try { await page.locator('.architecture-canvas [data-canvas-ready="true"]').waitFor(); }
          catch (error) {
            console.error(JSON.stringify({
              errors, alerts: await page.getByRole("alert").allTextContents(),
              canvas: await page.locator(".live-architecture-canvas").evaluateAll(elements => elements.map(element => ({
                status: element.getAttribute("data-status"), ready: element.getAttribute("data-canvas-ready"),
                text: element.textContent?.slice(0, 1000),
              }))),
            }));
            throw error;
          }
        };
        await ready();
        const dark = page.getByRole("switch", { name: "Dark mode", exact: true });
        await dark.setChecked(theme === "dark");
        const assertTheme = async () => assert.equal(
          await page.locator(".live-canvas-shell").getAttribute("data-theme"), theme, "A new input must retain the selected canvas theme.");
        await assertTheme();
        await page.getByLabel("Show shown elements", { exact: true }).click();
        await page.getByLabel("Show omitted elements", { exact: true }).click();
        await page.getByRole("button", { name: "Pin Omitted " + "o".repeat(200), exact: true }).click();
        await ready();
        await page.getByRole("button", { name: `Unpin ${omittedId}`, exact: true }).waitFor();
        await assertTheme();
        await page.getByRole("button", { name: "Apply graph filters", exact: true }).click();
        await ready();
        await assertTheme();

        const geometry = await page.evaluate(() => {
          const canvas = document.querySelector(".architecture-canvas")!;
          const status = document.querySelector('[aria-label="Graph status"]')!;
          const a = canvas.getBoundingClientRect(), b = status.getBoundingClientRect();
          const lists = ["Shown architecture elements", "Omitted architecture elements"].map(name =>
            document.querySelector<HTMLElement>(`[aria-label="${name}"]`)!);
          return {
            overflow: document.documentElement.scrollWidth > innerWidth,
            statusOutside: !canvas.contains(status) && b.bottom <= a.top,
            listsBounded: lists.every(list => list.scrollWidth <= list.clientWidth + 1),
            isolation: getComputedStyle(canvas).isolation,
          };
        });
        assert.deepEqual(geometry, { overflow: false, statusOutside: true, listsBounded: true, isolation: "isolate" });
        const missing = page.getByText(`Pins not found in this graph: ${omittedId}`, { exact: true });
        assert.ok(await missing.count() > 0);
        assert.ok(await missing.evaluate(element => element.clientHeight <= 96 && element.getAttribute("tabindex") === "0"));
        const shown = page.getByLabel("Show shown elements", { exact: true });
        if (!(await shown.evaluate(element => element.closest("details")!.open))) await shown.click();
        const pick = page.locator("[data-architecture-node-id]").filter({ hasText: label });
        await pick.focus();
        await page.keyboard.press("Enter");
        const details = page.getByRole("complementary", { name: "Architecture details", exact: true });
        assert.equal(await details.evaluate(element => element === document.activeElement), true, "Keyboard/list selection must focus details.");
        const detailsTop = await details.evaluate(element => element.getBoundingClientRect().top);
        const headerBottom = await page.locator("header.sticky").evaluate(element => element.getBoundingClientRect().bottom);
        assert.ok(detailsTop >= headerBottom, "Keyboard details must clear the sticky header.");
        if (viewport.width < 1024) {
          const hash = new URL(page.url()).hash;
          await page.getByRole("button", { name: "View details", exact: true }).click();
          const bounds = await details.boundingBox();
          assert.ok(bounds && bounds.y >= 0 && bounds.y < viewport.height);
          assert.equal(new URL(page.url()).hash, hash);
        }
        await page.getByRole("button", { name: `Unpin ${omittedId}`, exact: true }).click();
        await page.getByRole("button", { name: "Apply graph filters", exact: true }).click();
        await ready();
        await assertTheme();
        assert.deepEqual(errors, []);
        assert.deepEqual(writes, []);
      } finally { await browser.close(); }
    });
  }
}
