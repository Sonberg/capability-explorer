import assert from "node:assert/strict";
import type { ModelSummary } from "../platform/src/CapabilityExplorer.Explorer/client/src/models/contracts.ts";

type Locator = {
  click(): Promise<void>;
  count(): Promise<number>;
  focus(): Promise<void>;
  getAttribute(name: string): Promise<string | null>;
  hover(): Promise<void>;
  tap(): Promise<void>;
  boundingBox(): Promise<{ x: number; y: number; width: number; height: number } | null>;
  press(key: string): Promise<void>;
  waitFor(options?: { state?: "attached" | "detached" | "visible" | "hidden" }): Promise<void>;
};

type Frame = {
  getByRole(role: string, options?: { name?: string | RegExp; exact?: boolean }): Locator;
  getByText(text: string, options?: { exact?: boolean }): Locator;
  locator(selector: string): Locator;
};

type BrowserTab = {
  goto(url: string): Promise<unknown>;
  url(): string;
  getByRole(role: string, options?: { name?: string | RegExp; exact?: boolean }): Locator;
  getByText(text: string, options?: { exact?: boolean }): Locator;
  frameLocator(selector: string): Frame;
};

export async function verifyDecisionLinks(
  tab: BrowserTab,
  origin: string,
  items: ModelSummary[],
  capture: (name: string, fullPage?: boolean) => Promise<void>,
): Promise<void> {
  const selectedModelId = () => {
    const hash = new URL(tab.url()).hash;
    return new URLSearchParams(hash.slice(hash.indexOf("?") + 1)).get("id");
  };
  const source = items.find(item => item.unitPath === "workflow.bpmn");
  const target = items.find(item => item.unitPath === "decision-rich.dmn");
  assert(source && target, "The fixture collection must include the BPMN source and DMN target");
  assert.equal(source.modelKind, "bpmn");
  assert.equal(source.source.status, "available");
  assert.equal(target.modelKind, "dmn");
  assert.equal(target.source.status, "available");
  assert.equal(target.repositoryId, source.repositoryId);
  assert.equal(target.commitSha, source.commitSha);

  await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(source.id)}&tab=diagram`);
  await tab.getByText("BPMN model evidence", { exact: true }).waitFor();
  await tab.getByRole("button", { name: "Diagram", exact: true }).click();
  const sourceFrame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  await sourceFrame.locator('[data-element-id="route-gateway"]:visible').waitFor();
  await sourceFrame.locator('[data-element-id="evaluate-order"]:visible').waitFor();
  const markerName = "Open decision reference for Evaluate order decision";
  assert.equal(await sourceFrame.getByRole("button", { name: /Open decision reference for/ }).count(), 1,
    "Only the explicitly authored decision reference should create a BPMN link marker");

  await sourceFrame.locator('[data-element-id="evaluate-order"]').click();
  await sourceFrame.locator('[data-element-id="evaluate-order"].issue78-selected:visible').waitFor();
  const sourceTransform = await sourceFrame.locator(".viewport").getAttribute("transform");
  assert(sourceTransform, "The source diagram must expose its current viewport transform");
  const marker = sourceFrame.getByRole("button", { name: markerName, exact: true });
  const preview = sourceFrame.getByText("Authored decision reference", { exact: true });
  const openDecision = sourceFrame.getByRole("button", { name: "Open decision", exact: true });
  await marker.hover();
  await preview.waitFor({ state: "visible" });
  await sourceFrame.getByText("decision-rich.dmn#order-decision", { exact: true }).waitFor();
  assert.equal(selectedModelId(), source.id, "Hovering a decision marker must preview without navigating");
  await marker.press("Escape");
  await preview.waitFor({ state: "hidden" });
  await sourceFrame.locator('[data-element-id="route-gateway"]').click();
  await marker.focus();
  await preview.waitFor({ state: "visible" });
  await marker.press("Escape");
  await preview.waitFor({ state: "hidden" });
  await marker.click();
  assert.equal(selectedModelId(), source.id, "Clicking a decision marker must pin the preview, not navigate");
  await sourceFrame.locator('[data-element-id="start"]').hover();
  await preview.waitFor({ state: "visible" });
  await capture("workflow-decision-reference.png");
  await openDecision.click();

  const targetFrame = tab.frameLocator('iframe[title="DMN read-only model"]');
  await tab.getByText("DMN model evidence", { exact: true }).waitFor();
  assert.equal(selectedModelId(), target.id, "The explicit path must select the exact DMN model summary");
  await tab.getByRole("button", { name: "Decision requirements", exact: true }).waitFor();
  await targetFrame.locator('[data-element-id="order-decision"].issue78-selected:visible').waitFor();
  await capture("workflow-decision-target.png");
  const back = tab.getByRole("button", { name: "Back to Evaluate order decision", exact: true });
  await back.waitFor({ state: "visible" });
  const returnBounds = await back.boundingBox();
  const diagramBounds = await targetFrame.locator(".stage").boundingBox();
  assert(returnBounds && diagramBounds && returnBounds.x >= diagramBounds.x + 12 &&
    returnBounds.y >= diagramBounds.y + 12 && returnBounds.y < diagramBounds.y + 60,
    "DMN Back must be inside the diagram's top-left navigation area, like subprocess Back");
  assert.equal(returnBounds.width, 260);
  assert.equal(returnBounds.height, 34);
  await targetFrame.locator('[data-element-id="base-decision"]').click();
  await targetFrame.locator('[data-element-id="base-decision"].issue78-selected:visible').waitFor();
  assert.deepEqual(await back.boundingBox(), returnBounds, "Changing the selected decision must not move or resize Back");
  await targetFrame.locator('[data-element-id="order-decision"]').click();
  await targetFrame.locator('[data-element-id="order-decision"].issue78-selected:visible').waitFor();
  assert.deepEqual(await back.boundingBox(), returnBounds);
  await back.click();

  await tab.getByText("BPMN model evidence", { exact: true }).waitFor();
  assert.equal(selectedModelId(), source.id, "The return action must restore the exact BPMN source");
  await sourceFrame.locator('[data-element-id="route-gateway"]:visible').waitFor();
  await sourceFrame.locator('[data-element-id="evaluate-order"].issue78-selected:visible').waitFor();
  await sourceFrame.locator('[data-element-id="nested-task"]').waitFor({ state: "hidden" });
  assert.equal(await sourceFrame.locator(".viewport").getAttribute("transform"), sourceTransform,
    "Returning from the DMN reference must restore the source plane and exact viewport");
  await capture("workflow-decision-return.png");

  await tab.getByRole("button", { name: "Insights", exact: true }).click();
  await tab.getByText("Authored decision reference", { exact: true }).waitFor();
  const insightLink = tab.getByRole("button", { name: "Open decision order-decision", exact: true });
  await insightLink.waitFor({ state: "visible" });
  await insightLink.click();
  await tab.getByText("DMN model evidence", { exact: true }).waitFor();
  assert.equal(selectedModelId(), target.id);
  await targetFrame.locator('[data-element-id="order-decision"].issue78-selected:visible').waitFor();
}

export async function verifyDecisionTouch(tab: BrowserTab, origin: string, items: ModelSummary[]): Promise<void> {
  const source = items.find(item => item.unitPath === "workflow.bpmn");
  assert(source);
  await tab.goto(`${origin}/#/processes-decisions?id=${encodeURIComponent(source.id)}&tab=diagram`);
  const frame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  await frame.getByRole("button", { name: "Open decision reference for Evaluate order decision", exact: true }).tap();
  const preview = frame.getByRole("region", { name: "Authored decision reference insight", exact: true });
  await preview.waitFor({ state: "visible" });
  await frame.locator('[aria-label="DMN decision preview"] [data-element-id="order-decision"]').waitFor();
  const box = await preview.boundingBox();
  assert(box && box.x >= 0 && box.x + box.width <= 390 && box.y >= 0 && box.y + box.height <= 844,
    "The touch preview must stay inside the phone viewport");
  await frame.getByRole("button", { name: "Close", exact: true }).tap();
  await preview.waitFor({ state: "hidden" });
  await frame.getByRole("button", { name: "Open decision reference for Evaluate order decision", exact: true }).tap();
  await frame.getByRole("button", { name: "Open decision", exact: true }).tap();
  const dmn = tab.frameLocator('iframe[title="DMN read-only model"]');
  await dmn.locator('[data-element-id="order-decision"].issue78-selected:visible').waitFor();
  await tab.getByRole("button", { name: "Close model insights", exact: true }).tap();
  const back = tab.getByRole("button", { name: "Back to Evaluate order decision", exact: true });
  const bounds = await back.boundingBox();
  assert(bounds && bounds.x >= 0 && bounds.x + bounds.width <= 390, "Mobile Back must fit the viewport");
  await tab.getByRole("button", { name: "Decision table", exact: true }).tap();
  assert.deepEqual(await back.boundingBox(), bounds, "Mobile Back must remain fixed when switching to decision logic");
  await back.tap();
  await frame.locator('[data-element-id="evaluate-order"].issue78-selected:visible').waitFor();
}
