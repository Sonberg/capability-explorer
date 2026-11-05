import assert from "node:assert/strict";
import type { ModelSummary } from "../platform/src/CapabilityExplorer.Explorer/client/src/models/contracts.ts";

type Locator = {
  getByText(text: string | RegExp, options?: { exact?: boolean }): Locator;
  click(): Promise<void>;
  count(): Promise<number>;
  getAttribute(name: string): Promise<string | null>;
  hover(): Promise<void>;
  waitFor(options?: { state?: "attached" | "detached" | "visible" | "hidden" }): Promise<void>;
};
type Frame = {
  locator(selector: string): Locator;
  getByRole(role: string, options?: { name?: string | RegExp; exact?: boolean }): Locator;
  getByText(text: string | RegExp, options?: { exact?: boolean }): Locator;
};
type Route = {
  request(): { url(): string };
  fallback(): Promise<void>;
  fulfill(options: { status: number; contentType: string; body: string }): Promise<void>;
};
type Request = { url(): string };
type Tab = {
  goto(url: string): Promise<unknown>;
  reload(): Promise<unknown>;
  url(): string;
  frameLocator(selector: string): Frame;
  getByRole(role: string, options?: { name?: string | RegExp; exact?: boolean }): Locator;
  getByText(text: string | RegExp, options?: { exact?: boolean }): Locator;
  route(pattern: string, handler: (route: Route) => Promise<void>): Promise<void>;
  unroute(pattern: string): Promise<void>;
  on(event: "request", listener: (request: Request) => void): unknown;
  off(event: "request", listener: (request: Request) => void): unknown;
};

export async function verifyDecisionFailures(
  tab: Tab,
  origin: string,
  items: ModelSummary[],
  routeModelVariant: (item: ModelSummary, xml: string) => Promise<void>,
  capture: (name: string, fullPage?: boolean) => Promise<void>,
): Promise<void> {
  const source = items.find(item => item.unitPath === "workflow.bpmn");
  const target = items.find(item => item.unitPath === "decision-rich.dmn");
  assert(source && target, "Decision failure tests require the BPMN and rich DMN fixtures");
  const listResponse = await fetch(`${origin}/api/models`);
  assert.equal(listResponse.status, 200);
  const list = await listResponse.json();
  const query = new URLSearchParams({ id: source.id, snapshot: list.snapshot, generation: String(list.generation) });
  const sourceResponse = await fetch(`${origin}/api/models/source?${query}`);
  assert.equal(sourceResponse.status, 200);
  const { xml } = await sourceResponse.json() as { xml: string };
  const reference = "decision-rich.dmn#order-decision";
  assert(xml.includes(`ce:decisionRef="${reference}"`), "The source fixture must contain the authored reference");
  const sourceUrl = `${origin}/#/processes-decisions?id=${encodeURIComponent(source.id)}&tab=diagram`;
  const frame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  const markerName = "Open decision reference for Evaluate order decision";
  const marker = () => frame.getByRole("button", { name: markerName, exact: true });
  const selectedModelId = () => {
    const hash = new URL(tab.url()).hash;
    return new URLSearchParams(hash.slice(hash.indexOf("?") + 1)).get("id");
  };
  const load = async () => {
    await tab.goto(sourceUrl);
    await tab.reload();
    await frame.locator('[data-element-id="evaluate-order"]:visible').waitFor();
  };
  const openPreviewAction = async () => {
    await marker().click();
    await frame.getByRole("button", { name: "Open decision", exact: true }).click();
  };
  const clearRoutes = async () => {
    await tab.unroute("**/api/models/source?**");
    await tab.unroute("**/api/models**");
  };
  const unexpectedRequests: string[] = [];
  const checkRequest = (request: Request) => {
    const url = request.url();
    if (/^https?:/.test(url) && new URL(url).origin !== origin) unexpectedRequests.push(url);
  };
  tab.on("request", checkRequest);
  try {
    await routeModelVariant(source, xml.replace(
      'xmlns:ce="urn:capability-explorer:model-links:v1"', 'xmlns:ce="urn:unrelated:annotation"',
    ));
    await load();
    assert.equal(await marker().count(), 0, "A same-named attribute in another namespace must not create a decision marker");
    await clearRoutes();

    await routeModelVariant(source, xml.replace(reference, "unlisted.dmn#order-decision"));
    await load();
    assert.match(await marker().getAttribute("class") ?? "", /\bmodel-decision-link-unavailable\b/,
      "An unlisted relative target should use the subdued marker");
    await marker().hover();
    await frame.getByText("Authored decision reference", { exact: true }).waitFor();
    await frame.getByText(/not in the loaded summaries.*exact repository revision/i).waitFor();
    await openPreviewAction();
    await tab.getByRole("alert").waitFor();
    assert.equal(selectedModelId(), source.id, "An unresolved reference must not leave the BPMN source");
    await frame.locator('[data-element-id="evaluate-order"]:visible').waitFor();
    await capture("decision-reference-unlisted.png");
    await clearRoutes();

    await tab.route("**/api/models/source?**", async route => {
      if (new URL(route.request().url()).searchParams.get("id") !== target.id) {
        await route.fallback();
        return;
      }
      await route.fulfill({ status: 403, contentType: "application/json", body: '{"error":"Source access denied"}' });
    });
    await load();
    await marker().click();
    await frame.getByRole("region", { name: "Authored decision reference insight", exact: true })
      .getByText(/Access to this source is denied\. No alternate source was requested\./).waitFor();
    assert.equal(await frame.locator('[aria-label="DMN decision preview"] .djs-element').count(), 0,
      "Denied source bytes must not enter a mini viewer");
    await openPreviewAction();
    await tab.getByRole("alert").waitFor();
    await tab.getByText(/Access to this source is denied\. No alternate source was requested\./).waitFor();
    assert.equal(selectedModelId(), source.id, "A target source denial must not navigate despite an available summary");
    await frame.locator('[data-element-id="evaluate-order"]:visible').waitFor();
    await capture("decision-reference-denied.png");
    await clearRoutes();

    await routeModelVariant(source, xml.replace(reference, "decision-rich.dmn#absent-decision"));
    await load();
    await marker().click();
    await frame.getByRole("region", { name: "Authored decision reference insight", exact: true })
      .getByText(/referenced decision or decision service is absent from the target DMN evidence/i).waitFor();
    await openPreviewAction();
    await tab.getByRole("alert").waitFor();
    await tab.getByText(/referenced decision or decision service is absent from the target DMN evidence/i).waitFor();
    assert.equal(selectedModelId(), source.id, "A matching DMN file is insufficient without the exact authored decision ID");
    await frame.locator('[data-element-id="evaluate-order"]:visible').waitFor();
    await capture("decision-reference-absent-id.png");
    await clearRoutes();

    const childTask = '<bpmn:task id="child-task" name="Notify customer"/>';
    assert(xml.includes(childTask), "The called-process variant requires its existing child task");
    await routeModelVariant(source, xml.replace(childTask,
      `<bpmn:businessRuleTask id="child-task" name="Notify customer" ce:decisionRef="${reference}"/>`));
    await load();
    await frame.locator('[data-element-id="call-child"]').click();
    const callerView = await frame.locator(".viewport").getAttribute("transform");
    await frame.getByRole("button", { name: "Go to called process Shipment", exact: true }).click();
    await frame.locator('[data-element-id="child-task"]:visible').waitFor();
    await frame.locator('[data-element-id="review-task"]').waitFor({ state: "hidden" });
    await marker().waitFor({ state: "hidden" });
    await frame.locator('[data-element-id="child-task"]').click();
    const calledView = await frame.locator(".viewport").getAttribute("transform");
    assert(callerView && calledView, "Both caller and called-process viewports must be captured");
    await frame.getByRole("button", { name: "Open decision reference for Notify customer", exact: true }).click();
    await frame.getByRole("button", { name: "Open decision", exact: true }).click();
    const targetFrame = tab.frameLocator('iframe[title="DMN read-only model"]');
    await targetFrame.locator('[data-element-id="order-decision"].issue78-selected:visible').waitFor();
    assert.equal(selectedModelId(), target.id);
    await tab.getByRole("button", { name: "Back to Notify customer", exact: true }).click();
    await frame.locator('[data-element-id="child-task"].issue78-selected:visible').waitFor();
    await frame.locator('[data-element-id="review-task"]').waitFor({ state: "hidden" });
    await marker().waitFor({ state: "hidden" });
    const callBack = frame.getByRole("button", { name: "Back to Create shipment", exact: true });
    await callBack.waitFor({ state: "visible" });
    assert.equal(selectedModelId(), source.id);
    assert.equal(await frame.locator(".viewport").getAttribute("transform"), calledView,
      "Returning from a decision must restore the called-process view, not expose its hidden caller siblings");
    await capture("decision-reference-called-process-return.png");
    await callBack.click();
    await frame.locator('[data-element-id="call-child"].issue78-selected:visible').waitFor();
    await frame.locator('[data-element-id="review-task"]:visible').waitFor();
    await callBack.waitFor({ state: "hidden" });
    assert.equal(await frame.locator(".viewport").getAttribute("transform"), callerView,
      "The restored call path must still return to the original caller viewport");
    assert.deepEqual(unexpectedRequests, [], "Decision-reference failure handling must not fetch external sources");
  } finally {
    tab.off("request", checkRequest);
    await clearRoutes();
    await tab.goto(sourceUrl);
    await tab.reload();
    await frame.locator('[data-element-id="evaluate-order"]:visible').waitFor();
  }
}
