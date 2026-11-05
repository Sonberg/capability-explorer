import assert from "node:assert/strict";
import type { Page } from "../spikes/likec4/node_modules/playwright/index.js";
import type { ModelSummary } from "../platform/src/CapabilityExplorer.Explorer/client/src/models/contracts.ts";

export async function verifyModelHoverPreviews(
  tab: Page, origin: string, items: ModelSummary[], capture: (name: string, fullPage?: boolean) => Promise<void>,
) {
  const source = items.find(item => item.unitPath === "workflow.bpmn");
  assert(source);
  const url = `${origin}/#/processes-decisions?id=${encodeURIComponent(source.id)}&tab=diagram&element=evaluate-order`;
  await tab.goto(url);
  await tab.reload();
  const frame = tab.frameLocator('iframe[title="BPMN read-only model"]');
  const marker = frame.getByRole("button", { name: "Open decision reference for Evaluate order decision", exact: true });
  const popup = frame.getByRole("region", { name: "Authored decision reference insight", exact: true });
  await marker.waitFor();
  await frame.locator('.viewer [data-element-id="evaluate-order"].issue78-selected').waitFor();
  await popup.waitFor({ state: "hidden" });
  const modelFrame = tab.frames().find(candidate => candidate.parentFrame());
  assert(modelFrame);
  const mainViewport = async () => (await frame.locator(".stage > .viewer .viewport")
    .getAttribute("transform"))?.replaceAll(",", " ");
  const viewport = await mainViewport();
  let previewRequests = 0;
  const countRequests = (request: { url(): string }) => {
    if (/\/api\/models\/(source|evidence)\?/.test(request.url())) previewRequests++;
  };
  tab.on("request", countRequests);
  try {
    await marker.hover();
    await frame.locator('[data-element-id="start"]').hover();
    const start = await modelFrame.evaluate(() => performance.now());
    await modelFrame.waitForFunction(since => performance.now() - since >= 600, start);
    await popup.waitFor({ state: "hidden" });
    assert.equal(previewRequests, 0, "A passing hover must not open or fetch a decision preview");

    await marker.evaluate(button => {
      button.addEventListener("pointerenter", () => {
        const start = performance.now();
        const observer = new MutationObserver(() => {
          const region = document.querySelector<HTMLElement>('[aria-label="Authored decision reference insight"]');
          if (region && !region.hidden) {
            button.setAttribute("data-measured-hover-delay", String(performance.now() - start));
            observer.disconnect();
          }
        });
        observer.observe(document.body, { childList: true, subtree: true, attributes: true });
      }, { once: true });
    });
    await marker.hover();
    await popup.waitFor({ state: "visible" });
    const delay = Number(await marker.getAttribute("data-measured-hover-delay"));
    assert(delay >= 400, `The preview should wait for a deliberate hover, measured ${delay}ms`);
    const diagram = popup.getByRole("figure", { name: "DMN decision preview", exact: true });
    await diagram.locator('[data-element-id="order-decision"]').waitFor();
    const table = popup.getByRole("table");
    await table.waitFor();
    assert.match(await table.innerText(), /Age/);
    assert.match(await table.innerText(), /amount/);
    assert.match(await table.innerText(), />= 18/);
    assert.match(await table.innerText(), /< 18/);
    assert.equal(await table.getByRole("row").count(), 3, "Only the selected decision's two authored rules are previewed");
    assert.match(await popup.innerText(), /COLLECT/);
    assert.match(await popup.innerText(), /SUM/);
    assert.equal(tab.url(), url, "Reading decision rules in a preview must not navigate away from BPMN");
    assert.equal(await mainViewport(), viewport,
      "A preview must not move or refit the main diagram");
    await popup.hover();
    await popup.waitFor({ state: "visible" });
    await capture("decision-hover-diagram-and-rules.png", false);
    await popup.getByRole("button", { name: "Close", exact: true }).click();
    await popup.waitFor({ state: "hidden" });

    const plus = frame.getByRole("button", { name: "Open Prepare order", exact: true });
    const subprocess = frame.getByRole("region", { name: "Process detail insight", exact: true });
    await plus.hover();
    await subprocess.waitFor({ state: "visible" });
    const nestedDiagram = subprocess.getByRole("figure", { name: "Subprocess preview", exact: true });
    await nestedDiagram.locator('[data-element-id="nested-task"]:visible').waitFor();
    assert.match(await nestedDiagram.innerText(), /Reserve inventory/);
    assert.equal(await frame.locator('.viewer [data-element-id="nested-task"]:visible').count(), 0,
      "Previewing the subprocess must not drill down the main diagram");
    assert.equal(await mainViewport(), viewport);
    await capture("subprocess-hover-diagram.png", false);
    await subprocess.getByRole("button", { name: "Open subprocess", exact: true }).click();
    await frame.locator('.viewer [data-element-id="nested-task"]:visible').waitFor();
    await subprocess.waitFor({ state: "hidden" });
    const embeddedBack = frame.getByRole("button", { name: "Back to Order fulfilment", exact: true });
    const embeddedBackBounds = await embeddedBack.boundingBox();
    await embeddedBack.click();
    await frame.locator('.viewer [data-element-id="review-task"]:visible').waitFor();
    assert.equal(await mainViewport(), viewport,
      "Embedded subprocess Back must restore the caller viewport");
    const callPlus = frame.getByRole("button", { name: "Go to called process Shipment", exact: true });
    await callPlus.hover();
    await subprocess.waitFor({ state: "visible" });
    await subprocess.getByText("Called process", { exact: true }).waitFor();
    const calledDiagram = subprocess.getByRole("figure", { name: "Called process preview", exact: true });
    await calledDiagram.locator('[data-element-id="child-task"]:visible').waitFor();
    assert.match(await calledDiagram.innerText(), /Notify customer/);
    assert.equal(await frame.locator('.viewer [data-element-id="child-task"]:visible').count(), 0);
    assert.equal(await mainViewport(), viewport,
      "Called-process preview must not move or open the main diagram");
    await capture("called-process-hover-diagram.png", false);
    await subprocess.getByRole("button", { name: "Open called process", exact: true }).click();
    await frame.locator('.viewer [data-element-id="child-task"]:visible').waitFor();
    await subprocess.waitFor({ state: "hidden" });
    const calledBack = frame.getByRole("button", { name: "Back to Create shipment", exact: true });
    assert.deepEqual(await calledBack.boundingBox(), embeddedBackBounds,
      "Embedded and called-process Back must use the same fixed position and size despite different labels");
    const childVisual = frame.locator('.stage > .viewer [data-element-id="child-task"] .djs-visual > :first-child');
    assert.notEqual(await childVisual.evaluate(element => getComputedStyle(element).stroke), "rgb(37, 99, 235)",
      "Opening a process must not highlight every child through a selected root");
    await frame.locator('.stage > .viewer [data-element-id="child-task"]').click();
    assert.equal(await childVisual.evaluate(element => getComputedStyle(element).stroke), "rgb(37, 99, 235)");
    await frame.locator('.stage > .viewer svg[data-element-id="child-process"]').click({ position: { x: 20, y: 120 } });
    await frame.locator('.stage > .viewer [data-element-id="child-task"].issue78-selected').waitFor({ state: "hidden" });
    assert.notEqual(await childVisual.evaluate(element => getComputedStyle(element).stroke), "rgb(37, 99, 235)",
      "Blank canvas must clear selection rather than select the whole process");
    await calledBack.click();
    await frame.locator('.viewer [data-element-id="review-task"]:visible').waitFor();
    assert.equal(await mainViewport(), viewport);

    await tab.reload();
    const target = items.find(item => item.unitPath === "decision-rich.dmn");
    assert(target);
    let release: () => void = () => {};
    let sourceArrived: () => void = () => {};
    let responseFinished: () => void = () => {};
    const held = new Promise<void>(resolve => { release = resolve; });
    const arrived = new Promise<void>(resolve => { sourceArrived = resolve; });
    const finished = new Promise<void>(resolve => { responseFinished = resolve; });
    const pattern = "**/api/models/source?**";
    await tab.route(pattern, async route => {
      if (new URL(route.request().url()).searchParams.get("id") !== target.id) return route.fallback();
      const response = await route.fetch();
      sourceArrived();
      await held;
      try { await route.fulfill({ response }); }
      catch (error) {
        if (!/cancelled|aborted|closed/i.test((error as Error).message)) throw error;
      } finally { responseFinished(); }
    });
    try {
      await marker.click();
      await arrived;
      await popup.getByRole("button", { name: "Close", exact: true }).click();
      await popup.waitFor({ state: "hidden" });
      release();
      await finished;
      assert.equal(await popup.locator(".model-mini-diagram").count(), 0,
        "Closing an in-flight preview must discard its mini viewer");
      await popup.waitFor({ state: "hidden" });
    } finally {
      release();
      await tab.unroute(pattern);
    }
  } finally {
    tab.off("request", countRequests);
  }
}
