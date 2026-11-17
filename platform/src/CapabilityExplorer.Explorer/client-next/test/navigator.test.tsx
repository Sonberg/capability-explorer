import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { JSDOM } from "jsdom";
import { act } from "react";
import type { Root } from "react-dom/client";
import { documentFixture, fixture, sourceFixture } from "./fixtures";
import { endpointProcess, fetchSnapshot, filterProcesses, parseResponse, readRoute, routeUrl } from "../src/model";
import type { ApiResponse } from "../src/contracts";
import { catalogueRows, sourceEndpoint } from "../src/navigator";

const dom = new JSDOM("<!doctype html><html><body><div id='root'></div></body></html>", {
  url: "http://localhost/process-landscape/",
});
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  HTMLInputElement: { value: dom.window.HTMLInputElement, configurable: true },
  IS_REACT_ACT_ENVIRONMENT: { value: true, configurable: true },
});
const { createRoot } = await import("react-dom/client");
const { App } = await import("../src/App");
const host = document.getElementById("root")!;
let root: Root | undefined;

afterEach(async () => {
  if (root) await act(async () => root?.unmount());
  root = undefined;
  window.history.replaceState(null, "", "/process-landscape/");
});

async function render(load: (signal: AbortSignal) => Promise<ApiResponse> = async () => fixture(), path?: string) {
  if (path) window.history.replaceState(null, "", path);
  root = createRoot(host);
  await act(async () => { root!.render(<App load={load} />); });
}
async function click(element: globalThis.Element | null) {
  assert.ok(element, "click target exists");
  await act(async () => element.dispatchEvent(new dom.window.MouseEvent("click", { bubbles: true, button: 0 })));
}
function button(text: string) {
  return [...host.querySelectorAll("button")].find(b => b.textContent === text) ?? null;
}
async function search(value: string) {
  const input = host.querySelector("input[type=search]")!;
  await act(async () => {
    Object.getOwnPropertyDescriptor(dom.window.HTMLInputElement.prototype, "value")!.set!.call(input, value);
    input.dispatchEvent(new dom.window.Event("input", { bubbles: true }));
  });
}

test("validates the delivered contract and rejects malformed success instead of pretending it is empty", () => {
  assert.equal(parseResponse(fixture()).status, "ok");
  const broken = fixture();
  (broken.snapshot!.processes[0].elements[0] as unknown as { attributes: unknown }).attributes = null;
  assert.throws(() => parseResponse(broken), /does not match/);
  assert.throws(() => parseResponse({ schema: "unknown", status: "ok" }), /malformed/);
  assert.equal(parseResponse({ schema: "ce.process-landscape.v1", status: "denied", reason: "content-disabled" }).status, "denied");
});

test("deep links preserve the deployed prefix, foreign parameters, and producer identity links", () => {
  const url = new URL("https://example.test/process-landscape/?foreign=keep#process:proc%3Aintake#el%3Afirst");
  const route = readRoute(url);
  assert.equal(route.process, "proc:intake");
  assert.equal(route.element, "el:first");
  const path = routeUrl({ ...route, lens: "events" }, url);
  assert.match(path, /^\/process-landscape\/\?foreign=keep&/);
  assert.equal(readRoute(new URL(path, url)).lens, "events");
  assert.equal(readRoute(new URL(path, url)).element, "el:first");
  assert.equal(readRoute(new URL("https://example.test/process-landscape/#process:%ZZ")).process, "");
});

test("search, context and phase filters intersect; overlay-only membership is searchable", () => {
  const s = fixture().snapshot!;
  const route = readRoute(new URL("https://example.test/?q=Synthetic%20check&context=front-office&phase=discovery"));
  assert.deepEqual(filterProcesses(s, route).map(p => p.processId), ["intake"]);
  assert.equal(filterProcesses(s, { ...route, phase: "delivery" }).length, 0);
  assert.equal(filterProcesses(s, { ...route, q: "", phase: "", context: "shared-context" })[0].processId, "review");
});

test("only explicit, complete endpoint identities can become navigable", () => {
  const s = fixture().snapshot!;
  assert.equal(endpointProcess(s, s.crossProcessEvents[0].source), undefined);
  assert.equal(endpointProcess(s, s.crossProcessEvents[0].target)?.processId, "review");
  assert.equal(endpointProcess(s, { ...s.crossProcessEvents[0].target, elementIdentity: "el:missing" }), undefined);
  assert.equal(endpointProcess(s, { ...s.crossProcessEvents[0].target, resolved: false }), undefined);
});

test("renders a safe structural projection, proposal warning, unsupported evidence, and no unverified navigator data", async () => {
  await render();
  assert.match(host.textContent!, /Structural projection — not authored diagram geometry/);
  assert.match(host.textContent!, /Committee-proposal · not owner-approved/);
  assert.match(host.textContent!, /Unverified snapshot identity/);
  assert.match(host.textContent!, /Unsupported\/opaque/);
  assert.ok(!host.textContent!.includes("DO_NOT_TRUST_UNVERIFIED_CATEGORY"));
  const stations = [...host.querySelectorAll(".station")];
  assert.match(stations[0].textContent!, /Synthetic check/);
  assert.equal(host.querySelector("img"), null, "model labels render as text");
  await click(stations[0]);
  assert.equal(new URL(window.location.href).searchParams.get("element"), "el:first");
  assert.match(host.querySelector('[aria-label="Inspector"]')!.textContent!, /Element identityel:first/);
  assert.equal((window as unknown as Record<string, unknown>).synthetic, undefined);
});

test("search hits navigate to elements and preserve deployed URLs; keyboard catalogue focus works", async () => {
  await render();
  const links = [...host.querySelectorAll("[data-process-link]")];
  (links[0] as HTMLElement).focus();
  await act(async () => links[0].dispatchEvent(new dom.window.KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true })));
  assert.equal(document.activeElement, links[1]);
  await search("Synthetic check");
  assert.equal(host.querySelectorAll("[data-process-link]").length, 1);
  await click(host.querySelector('[aria-label="Search hits"] a'));
  assert.equal(window.location.pathname, "/process-landscape/");
  assert.equal(new URL(window.location.href).searchParams.get("element"), "el:first");
});

test("events show unresolved endpoints and resolved handoffs navigate without invented routes", async () => {
  await render();
  await click(button("Events"));
  assert.match(host.querySelector("main")!.textContent!, /handoffs, not sequence-flow routes/);
  assert.match(host.querySelector("main")!.textContent!, /Unresolved endpoint · not-admitted \/ missing/);
  await click(host.querySelector('main .text-button'));
  assert.equal(new URL(window.location.href).searchParams.get("process"), "proc:review");
  assert.equal(new URL(window.location.href).searchParams.get("element"), "el:review-task");
  assert.equal(new URL(window.location.href).searchParams.get("lens"), "diagram");
});

test("lifecycle and journey preserve declared order including unresolved and repeated references", async () => {
  await render();
  await click(button("Lifecycle"));
  const stages = host.querySelector('[aria-label="Lifecycle stages"]')!.textContent!;
  assert.ok(stages.indexOf("Created") < stages.indexOf("Reviewed"));
  await click(button("Journey"));
  const phases = host.querySelector('[aria-label="Journey phases"]')!.textContent!;
  assert.ok(phases.indexOf("discovery") < phases.indexOf("delivery"));
  const steps = host.querySelectorAll('[aria-label="Journey steps"]');
  assert.match(steps[1].textContent!, /Unresolved endpoint/);
  assert.ok(steps[1].textContent!.indexOf("Step order 1") < steps[1].textContent!.indexOf("Step order 2"));
});

test("unknown deep links remain explicit and history navigation restores a selected element", async () => {
  await render(undefined, "/process-landscape/?process=missing&element=missing&foreign=keep");
  assert.match(host.textContent!, /Linked process is absent/);
  assert.equal(new URL(window.location.href).searchParams.get("process"), "missing");
  await act(async () => {
    window.history.replaceState(null, "", "/process-landscape/?process=proc%3Aintake&element=el%3Afirst&foreign=keep");
    window.dispatchEvent(new dom.window.PopStateEvent("popstate"));
  });
  assert.match(host.querySelector('[aria-label="Inspector"]')!.textContent!, /Element identityel:first/);
  await click(button("Events"));
  assert.equal(new URL(window.location.href).searchParams.get("foreign"), "keep");
});

test("a failed refresh is clearly stale; subsequent denial removes the previously loaded view", async () => {
  let call = 0;
  await render(async () => {
    if (++call === 1) return fixture();
    if (call === 2) throw new Error("Synthetic offline failure");
    return { schema: "ce.process-landscape.v1", status: "denied", reason: "model-content-disabled" };
  });
  await click(button("Refresh snapshot"));
  assert.match(host.textContent!, /Stale view · refresh failed/);
  assert.match(host.textContent!, /Synthetic offline failure/);
  assert.ok(host.querySelector(".workspace"));
  await click(button("Refresh snapshot"));
  assert.match(host.textContent!, /Snapshot status: denied/);
  assert.match(host.textContent!, /model-content-disabled/);
  assert.equal(host.querySelector(".workspace"), null);
});

test("empty successful snapshots are distinguished from unavailable snapshots and bounded lists page", async () => {
  const data = fixture();
  data.snapshot!.processes = [];
  await render(async () => data);
  assert.match(host.textContent!, /Empty snapshot: no processes were supplied/);
  await act(async () => root!.unmount());
  root = undefined;
  const many = fixture();
  const p = many.snapshot!.processes[0];
  p.elements = Array.from({ length: 43 }, (_, i) => ({ ...p.elements[0], identity: `item:${i}`, id: `item-${i}`, order: i }));
  await render(async () => many);
  assert.equal(host.querySelectorAll(".station").length, 40);
  await click(host.querySelector('[aria-label="Process elements pages"] button:last-child'));
  assert.equal(host.querySelectorAll(".station").length, 3);
  assert.match(host.querySelector('[aria-label="Process elements pages"]')!.textContent!, /41–43 of 43/);
});

test("structural loading uses only the same-origin snapshot endpoint, never raw XML", async () => {
  const original = globalThis.fetch;
  const calls: string[] = [];
  globalThis.fetch = async (input, init) => {
    calls.push(String(input));
    assert.equal(init?.cache, "no-store");
    return new Response(JSON.stringify(fixture()), { status: 200, headers: { "Content-Type": "application/json" } });
  };
  try {
    await render(fetchSnapshot);
    assert.deepEqual(calls, ["/api/process-landscape/v1/snapshot"]);
    assert.match(host.textContent!, /raw BPMN\/XML is not requested/);
  } finally { globalThis.fetch = original; }
});

test("verified catalogue mappings supply searchable metadata and overlays without guessed basename joins", () => {
  const s = sourceFixture().snapshot!;
  const route = readRoute(new URL("http://localhost/process-landscape/?q=Synthetic%20purpose&context=source-context&phase=source-phase"));
  assert.equal(filterProcesses(s, route)[0].processId, "intake");
  assert.equal(sourceEndpoint(s, "intake.bpmn", "first").resolved, true);
  assert.equal(sourceEndpoint(s, "another/intake.bpmn", "first").resolved, false);
  s.navigator!.verified = false;
  assert.equal(sourceEndpoint(s, "intake.bpmn", "first").resolved, false);
  assert.equal(catalogueRows(s, s.processes[0]).length, 0);
  assert.equal(filterProcesses(s, route).length, 0);
});

test("verified source event roles remain distinct and source-only endpoints stay unresolved", async () => {
  await render(async () => sourceFixture());
  await click(button("Events"));
  const source = host.querySelector('[aria-label="Verified source events"]')!;
  assert.match(source.textContent!, /Throw, catch and reference/);
  assert.match(source.textContent!, /Synthetic source event/);
  assert.match(source.textContent!, /Unresolved endpoint · not-admitted.bpmn \/ missing/);
  await click(source.querySelector(".text-button"));
  assert.equal(new URL(window.location.href).searchParams.get("element"), "el:first");
});

test("source lifecycle transitions and ordered journey phases are usable without evaluating guards", async () => {
  await render(async () => sourceFixture());
  await click(button("Lifecycle"));
  const source = host.querySelector('[aria-label="Verified source lifecycle"]')!;
  assert.match(source.textContent!, /Array order is not a transition sequence/);
  assert.match(source.textContent!, /window.synthetic = true/);
  assert.equal((window as unknown as Record<string, unknown>).synthetic, undefined);
  await click(button("Journey"));
  const journey = host.querySelector('[aria-label="Verified source journey"]')!;
  assert.ok(journey.textContent!.indexOf("Synthetic first phase") < journey.textContent!.indexOf("Synthetic second phase"));
  assert.match(journey.textContent!, /Entry points \(not ordered steps\)|entry points \(not ordered steps\)/);
});

test("source filter membership changes catalogue and overlay text, while preserving excluded selections", async () => {
  await render(async () => sourceFixture(), "/process-landscape/?process=proc%3Areview&context=source-context&overlay=context");
  assert.equal(host.querySelectorAll("[data-process-link]").length, 1);
  assert.match(host.textContent!, /selected process is outside the current catalogue filters/);
  await click(host.querySelector("[data-process-link]"));
  assert.match(host.querySelector(".projection")!.textContent!, /source-context/);
});

for (const status of ["denied", "unsupported", "empty", "stale", "unavailable"]) {
  test(`API ${status} is explicit, never a successful empty catalogue`, async () => {
    await render(async () => ({ schema: "ce.process-landscape.v1", status, reason: `synthetic-${status}` }));
    assert.match(host.textContent!, new RegExp(`Snapshot status: ${status}`));
    assert.match(host.textContent!, new RegExp(`synthetic-${status}`));
    assert.equal(host.querySelector(".workspace"), null);
  });
}

test("unknown element links and unsupported source shapes retain honest status", async () => {
  const data = sourceFixture();
  data.snapshot!.navigator!.journey = ["unsupported shape"];
  await render(async () => data, "/process-landscape/?process=proc%3Aintake&element=not-present&lens=journey");
  assert.match(host.textContent!, /Linked element is absent/);
  assert.match(host.textContent!, /Unsupported source journey shape/);
  assert.equal(new URL(window.location.href).searchParams.get("element"), "not-present");
});

function callReferenceFixture() {
  const data = fixture(), [caller, called] = data.snapshot!.processes;
  caller.elements[1].type = "callActivity";
  caller.elements[1].attributes.calledElement = "#review";
  called.elements.push({
    ...called.elements[0], type: "process", identity: "root:review", id: "review", order: 0,
    attributes: { name: "Synthetic called process" },
  });
  caller.relationships = [{ identity: "call:reference", source: "el:first", target: "root:review", kind: "reference", order: 1 }];
  return data;
}

test("admitted cross-process call references navigate by exact identity, separately from event handoffs", async () => {
  await render(async () => callReferenceFixture());
  const relationships = host.querySelector('[aria-label="Relationships"]')!;
  assert.match(relationships.textContent!, /Cross-process call reference · not an event handoff/);
  assert.match(relationships.textContent!, /Synthetic called process/);
  assert.ok(!relationships.textContent!.includes("Unresolved: root:review"));
  await click([...relationships.querySelectorAll("button")].find(b => b.textContent === "Inspect target") ?? null);
  assert.equal(new URL(window.location.href).searchParams.get("process"), "proc:review");
  assert.equal(new URL(window.location.href).searchParams.get("element"), "root:review");
});

for (const variant of ["wrong-kind", "wrong-called-id", "ambiguous-root"]) {
  test(`cross-process ${variant} references remain unresolved rather than becoming inferred routes`, async () => {
    const data = callReferenceFixture(), [caller, called] = data.snapshot!.processes;
    if (variant === "wrong-kind") caller.relationships[0].kind = "messageFlow";
    if (variant === "wrong-called-id") caller.elements[1].attributes.calledElement = "missing";
    if (variant === "ambiguous-root") data.snapshot!.processes.push({
      ...structuredClone(called), processIdentity: "proc:ambiguous",
    });
    await render(async () => data);
    const relationships = host.querySelector('[aria-label="Relationships"]')!;
    assert.match(relationships.textContent!, /Unresolved: root:review/);
    assert.ok(![...relationships.querySelectorAll("button")].some(b => b.textContent === "Inspect target"));
  });
}

test("collaboration draws supplied pools and admitted message waypoints, with source-file inspection and unresolved evidence", async () => {
  await render(async () => documentFixture(), "/process-landscape/?process=proc%3Aintake&element=el%3Afirst");
  await click(button("Collaboration"));
  const canvas = host.querySelector('[aria-label="Collaboration document"]')!;
  assert.match(canvas.textContent!, /Authored coordinates · simplified symbols and UI-positioned labels/);
  const rect = canvas.querySelector('rect[data-geometry="doc:pool-a-shape"]')!;
  assert.equal(rect.getAttribute("x"), "10");
  assert.equal(rect.getAttribute("width"), "500");
  assert.equal(canvas.querySelector("polyline")!.getAttribute("points"), "120,150 120,240");
  assert.equal(canvas.querySelectorAll("polyline").length, 1);
  assert.match(canvas.textContent!, /unrepresented-message-flow-endpoint/);
  assert.match(canvas.textContent!, /no admitted relationship. No message path is drawn/);
  assert.match(canvas.textContent!, /Black-box pool · no processRef/);
  const pool = canvas.querySelector('[aria-label="Inspect pool: Synthetic black-box pool"]')!;
  await act(async () => pool.dispatchEvent(new dom.window.KeyboardEvent("keydown", { key: "Enter", bubbles: true })));
  assert.equal(new URL(window.location.href).searchParams.get("documentElement"), "doc:pool-b");
  assert.equal(new URL(window.location.href).searchParams.get("element"), "el:first");
  assert.match(host.querySelector('[aria-label="Collaboration inspector"]')!.textContent!, /synthetic\/collaboration.bpmn/);
  await click(button("Diagram"));
  assert.match(host.querySelector('[aria-label="Inspector"]')!.textContent!, /Element identityel:first/);
});

test("documents without canonical geometry remain explicit projections, never fabricated SVGs", async () => {
  const data = documentFixture();
  data.snapshot!.documents![0].elements = data.snapshot!.documents![0].elements
    .filter(e => !["BPMNShape", "BPMNEdge", "Bounds", "waypoint"].includes(e.type));
  await render(async () => data, "/process-landscape/?lens=collaboration");
  const canvas = host.querySelector('[aria-label="Collaboration document"]')!;
  assert.match(canvas.textContent!, /Structural projection — no usable authored coordinates/);
  assert.equal(canvas.querySelector("svg"), null);
  assert.ok(!canvas.textContent!.includes("Authored coordinates ·"));
  assert.match(canvas.textContent!, /Synthetic sender pool/);
});

test("old snapshots retain existing process lenses and explain the opt-in collaboration boundary", async () => {
  await render();
  await click(button("Collaboration"));
  assert.match(host.textContent!, /Optional documents\[\] is opt-in/);
  assert.equal(host.querySelector(".authored-diagram"), null);
  await click(button("Events"));
  assert.match(host.querySelector("main")!.textContent!, /Review requested/);
});

test("missing document/plane/element deep links remain explicit and are never silently substituted", async () => {
  await render(async () => documentFixture(), "/process-landscape/?lens=collaboration&document=missing&documentElement=missing");
  assert.match(host.textContent!, /Linked collaboration document is absent/);
  assert.equal(new URL(window.location.href).searchParams.get("document"), "missing");
  await act(async () => {
    window.history.replaceState(null, "", "/process-landscape/?lens=collaboration&document=synthetic%2Fcollaboration.bpmn&diagram=missing&documentElement=missing");
    window.dispatchEvent(new dom.window.PopStateEvent("popstate"));
  });
  assert.match(host.textContent!, /Linked collaboration plane is absent/);
  assert.match(host.textContent!, /Linked document element is absent/);
  assert.equal(host.querySelector(".authored-diagram"), null);
});

test("collaboration plane selection updates its deep link and uses only that plane's authored bounds", async () => {
  await render(async () => documentFixture(true), "/process-landscape/?lens=collaboration");
  const select = host.querySelector('[aria-label="Collaboration document"] select')!;
  await act(async () => {
    (select as HTMLSelectElement).value = "doc:second-plane";
    select.dispatchEvent(new dom.window.Event("change", { bubbles: true }));
  });
  assert.equal(new URL(window.location.href).searchParams.get("diagram"), "doc:second-plane");
  assert.equal(host.querySelectorAll(".authored-pool").length, 1);
  assert.equal(host.querySelector(".authored-pool rect")!.getAttribute("x"), "-20");
  assert.equal(host.querySelectorAll(".authored-message").length, 0);
  assert.match(host.textContent!, /no path inferred/);
});

test("collaboration source names remain safe React text in SVG and never trigger raw-model fetches", async () => {
  const original = globalThis.fetch, calls: string[] = [], data = documentFixture();
  data.snapshot!.documents![0].elements.find(e => e.id === "pool-a")!.attributes.name = "<script>window.synthetic=true</script>";
  globalThis.fetch = async input => {
    calls.push(String(input));
    return new Response(JSON.stringify(data), { headers: { "Content-Type": "application/json" } });
  };
  try {
    await render(fetchSnapshot, "/process-landscape/?lens=collaboration");
    const diagram = host.querySelector(".authored-diagram")!;
    assert.match(diagram.textContent!, /<script>/);
    assert.equal(diagram.querySelector("script"), null);
    assert.equal((window as unknown as Record<string, unknown>).synthetic, undefined);
    assert.deepEqual(calls, ["/api/process-landscape/v1/snapshot"]);
  } finally { globalThis.fetch = original; }
});
