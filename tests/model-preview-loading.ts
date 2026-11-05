import assert from "node:assert/strict";
import type { Page } from "../spikes/likec4/node_modules/playwright/index.js";
import type { ModelSummary } from "../platform/src/CapabilityExplorer.Explorer/client/src/models/contracts.ts";

export async function verifyPreviewVersionGuard(tab: Page, origin: string, items: ModelSummary[]) {
  const source = items.find(item => item.unitPath === "workflow.bpmn");
  assert(source);
  await tab.addInitScript(() => {
    window.addEventListener("message", event => {
      if (event.data?.status === "render-request") delete event.data.decisionPreviewSupported;
    });
  });
  await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(source.id)}&tab=diagram`);
  const frame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  await frame.getByRole("button", { name: "Open decision reference for Evaluate order decision", exact: true }).click();
  await frame.getByRole("region", { name: "Authored decision reference insight", exact: true })
    .getByRole("alert").filter({ hasText: /different versions.*Reload the page/ }).waitFor();
  assert.equal(await frame.getByText("Loading verified decision source…", { exact: true }).count(), 0);
}
