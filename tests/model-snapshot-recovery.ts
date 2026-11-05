import assert from "node:assert/strict";
import type { Page } from "../spikes/likec4/node_modules/playwright/index.js";
import type { ModelSummary } from "../platform/src/CapabilityExplorer.Explorer/client/src/models/contracts.ts";

export async function verifySnapshotRecovery(tab: Page, origin: string, items: ModelSummary[]) {
  const source = items.find(item => item.unitPath === "workflow.bpmn");
  assert(source);
  const previous = "00000000-0000-4000-8000-000000000001";
  const current = await (await fetch(`${origin}/api/models`)).json() as { snapshot: string };
  let restarted = false;
  let refreshed = false;
  let acknowledgeRefresh: () => void = () => {};
  const refresh = new Promise<void>(resolve => { acknowledgeRefresh = resolve; });
  const oldScopes: string[] = [];
  await tab.route("**/api/**", async route => {
    const url = new URL(route.request().url());
    const scoped = url.searchParams.get("snapshot");
    if (restarted && scoped === previous) oldScopes.push(url.pathname);
    if (!["/api/meta", "/api/generation", "/api/models", "/api/models/evidence", "/api/models/source"].includes(url.pathname)) {
      return route.fallback();
    }
    if (restarted) {
      if (url.pathname === "/api/models" && !scoped) {
        refreshed = true;
        acknowledgeRefresh();
      }
      return route.fallback();
    }
    const response = await route.fetch({
      url: (() => {
        const clean = new URL(url);
        if (scoped === previous) clean.searchParams.set("snapshot", current.snapshot);
        return clean.href;
      })(),
    });
    const body = await response.json();
    if (url.pathname === "/api/meta" || url.pathname === "/api/generation") body.snapshotId = previous;
    else body.snapshot = previous;
    await route.fulfill({ response, json: body });
  });
  try {
    await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(source.id)}&tab=diagram&element=evaluate-order`);
    const bpmn = tab.frameLocator('iframe[title="BPMN read-only model"]');
    await bpmn.locator('[data-element-id="evaluate-order"].issue78-selected:visible').waitFor();
    restarted = true;
    await refresh;
    await bpmn.locator('[data-element-id="evaluate-order"].issue78-selected:visible').waitFor();
    assert(refreshed, "A new snapshot with the same generation must reload the selected model");
    const viewport = (await bpmn.locator(".stage > .viewer .viewport").getAttribute("transform"))?.replaceAll(",", " ");
    await bpmn.getByRole("button", { name: "Open decision reference for Evaluate order decision", exact: true }).click();
    await bpmn.getByRole("button", { name: "Open decision", exact: true }).click();
    const dmn = tab.frameLocator('iframe[title="DMN read-only model"]');
    await dmn.locator('[data-element-id="order-decision"].issue78-selected:visible').waitFor();
    await tab.getByRole("button", { name: "Back to Evaluate order decision", exact: true }).click();
    await bpmn.locator('[data-element-id="evaluate-order"].issue78-selected:visible').waitFor();
    assert.equal((await bpmn.locator(".stage > .viewer .viewport").getAttribute("transform"))?.replaceAll(",", " "), viewport,
      "Back from DMN after a snapshot refresh must restore the exact BPMN caller viewport");
    assert.deepEqual(oldScopes, [], "Decision navigation must not reuse a pre-restart snapshot");
  } finally {
    await tab.unroute("**/api/**");
  }
}
