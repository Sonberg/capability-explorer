import assert from "node:assert/strict";
import { after, before, test } from "node:test";
import playwright from "../spikes/likec4/node_modules/playwright/index.js";
import type { Locator, Page, Request } from "../spikes/likec4/node_modules/playwright/index.js";
import { createServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import type { ViteDevServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import { LikeC4 } from "../spikes/likec4/node_modules/likec4/dist/index.mjs";
import { listenBrowserServer } from "./browser-server.ts";

// Issue #102 frontend acceptance scenarios. Synthetic API fixtures only; the backend wire
// contract is the frozen camelCase ArchitectureGraph.unregisteredHttpTargets DTO.
//
// 102-A Given a frozen selection containing a no-known-provider HTTP target group,
//       when Architecture opens, then the actually rendered LikeC4 node is a grey dashed
//       rectangular outline with near-transparent fill, the observed name and a visible
//       "Unregistered target" text badge, while the existing external node style is unchanged.
// 102-B Given a candidate, when it is selected, then the inspector shows selection-wide
//       consumers/Capability memberships/repositories, distinct operation totals separate
//       from graph-edge occurrences, grouping reasons, exact provenance, alternatives, gaps,
//       provider coverage, distinct outcomes and bounded lists with shown/total/truncation.
// 102-E Given a selected candidate, when the result identity changes for the same candidate ID,
//       then the selection is reset and details from the previous evidence are never shown.
// 102-F Given candidates hidden by a kind filter or omitted by budgets, when the graph is shown,
//       then selection-wide counts stay discoverable and recovery keeps the other filters,
//       budgets and view/capability selection.
// 102-G Given an inspected candidate, when its evidence is navigated, then only the existing
//       GET source and symbol boundaries are used and no Capability, enrollment, binding or
//       manual-connection write is requested.
// 102-H Given a rendered candidate, when the reviewable LikeC4 source is shown or downloaded,
//       then the exported source compiles and keeps the rendered "Unregistered target" badge.
// 102-I Given a rendered candidate in the light or dark canvas theme, when its text is
//       measured against the composited near-transparent fill, then title and badge keep
//       WCAG AA text contrast while the grey dashed outline and 0.1 fill stay unchanged.
// 102-J Given a selected Capability and revision view, when candidate occurrences are opened
//       or hidden candidates are recovered, then the requests drill and the recovery keep the
//       selected Capability and view, render the occurrences and send no writes.
// 102-K Given a repository drill with 3 candidates in the selection, 1 matched and shown and no
//       search, pins or hidden candidate kind, when Architecture opens, then the 2 unmatched
//       candidates are described as outside this diagram scope or excluded by filters, not as
//       filter mismatches, and broader-level navigation is distinguished from filter refinement.

const CANDIDATE_ID = `unregistered-http-target:${"a".repeat(64)}`;
const CANDIDATE_LABEL = "inventory.example.test/api";
const EXTERNAL_LABEL = "payments.example.test";
const CALLER = "cs|planning-api|Planning.Api|M:Planning.Api.InventoryClient.Load";
const FIRST_RESULT = "1".repeat(64);
const SECOND_RESULT = "2".repeat(64);
const FIRST_COMMIT = "c".repeat(40);
const SECOND_COMMIT = "d".repeat(40);
const ARTIFACT_HASH = "e".repeat(64);
const SNIPPET_HASH = "f".repeat(64);
const CANDIDATE_KIND = "unregistered-http-target";

interface FixtureState {
  generation: number;
  resultId: string;
  commit: string;
  requests: { method: string; url: URL }[];
}

let server: ViteDevServer;
let origin = "";

before(async () => {
  server = await createServer({
    root: new URL("../platform/src/CapabilityExplorer.Explorer/client", import.meta.url).pathname,
    // The live canvas worker lives in spikes/likec4, outside the client package root.
    server: { host: "127.0.0.1", port: 0, fs: { allow: [new URL("..", import.meta.url).pathname] } }, logLevel: "error",
  });
  origin = await listenBrowserServer(server);
});

after(async () => { await server?.close(); });

function detail(state: FixtureState) {
  const source = { repositoryId: "planning-api", commitSha: state.commit, unitPath: "src/Planning.Api/Planning.Api.csproj", artifactHash: ARTIFACT_HASH };
  return {
    id: CANDIDATE_ID, label: CANDIDATE_LABEL, status: "no-known-provider", repositoryId: "planning-api",
    unitPath: source.unitPath, caller: CALLER, client: "InventoryClient", destination: "config=InventoryApi:BaseUrl",
    consumers: [
      { capabilityId: "planning", capabilityName: "Planning", repositoryId: "planning-api" },
      { capabilityId: "reporting", capabilityName: "Reporting", repositoryId: "planning-api" },
    ],
    consumerCount: 2,
    operations: ["GET /api/stock/{}", "POST /api/reservations"],
    operationCount: 3,
    reasons: ["Scoped configuration key InventoryApi:BaseUrl in Planning.Api", "No exact or method/route provider match in the selected catalogue"],
    alternatives: [], alternativeCount: 0,
    missingEvidence: ["Deployed service identity is not established"],
    occurrences: [{
      occurrence: {
        id: `http-occurrence:${"b".repeat(64)}`, source, edgeId: "e:inventory-load", caller: CALLER, client: "InventoryClient",
        target: "http|@unbound:planning-api/Planning.Api:config=InventoryApi:BaseUrl|GET /api/stock/{}", operation: "GET /api/stock/{}",
        evidence: [{ id: "ev-inventory-load", repositoryId: "planning-api", commitSha: state.commit, path: "src/Planning.Api/InventoryClient.cs",
          snippetSha256: SNIPPET_HASH, span: { startLine: 42, startColumn: 9, endLine: 44, endColumn: 30 },
          extractor: { name: "ce-cs", version: "1.0.0" } }],
      },
      evidenceCount: 2,
    }],
    occurrenceCount: 57,
    detailsTruncated: true,
    recovery: { level: "requests", focus: CANDIDATE_ID },
  };
}

function architecture(url: URL, state: FixtureState) {
  const hideKinds = [...new Set(url.searchParams.getAll("hideKind"))].sort();
  const include = [...new Set(url.searchParams.getAll("include"))].sort();
  const hidden = hideKinds.includes(CANDIDATE_KIND);
  const nodes = [
    { id: "cap:planning", category: "capability", label: "Planning", badges: [], drill: { level: "system", focus: "planning" } },
    { id: "cap:reporting", category: "capability", label: "Reporting", badges: [], drill: { level: "system", focus: "reporting" } },
    { id: "external:payments", category: "external", label: EXTERNAL_LABEL, sublabel: "other system", badges: [] },
    ...hidden ? [] : [{ id: CANDIDATE_ID, category: CANDIDATE_KIND, label: CANDIDATE_LABEL, sublabel: "Selected catalogue: no known provider",
      badges: ["Unregistered target"], drill: { level: "requests", focus: CANDIDATE_ID } }],
  ];
  const edges = [
    { id: "http:planning:payments", from: "cap:planning", to: "external:payments", kind: "http", label: "http", count: 2, certainty: "static", facts: [] },
    ...hidden ? [] : [
      { id: "candidate:planning", from: "cap:planning", to: CANDIDATE_ID, kind: CANDIDATE_KIND, label: "HTTP · unregistered target", count: 4, certainty: "candidate", facts: [] },
      { id: "candidate:reporting", from: "cap:reporting", to: CANDIDATE_ID, kind: CANDIDATE_KIND, label: "HTTP · unregistered target", count: 3, certainty: "candidate", facts: [] },
    ],
  ];
  const statuses = [["no-known-provider", 3, 9], ["ambiguous-known-providers", 1, 2], ["broken-binding", 1, 1],
    ["dynamic/insufficient-evidence", 2, 5], ["partial-provider-coverage", 1, 4]] as const;
  return {
    level: "landscape", title: "Landscape", breadcrumbs: [{ label: "Landscape", level: "landscape" }], notes: [], nodes, edges,
    budget: { nodeBudget: Number(url.searchParams.get("nodeBudget")), edgeBudget: Number(url.searchParams.get("edgeBudget")),
      totalNodes: 5, matchedNodes: hidden ? 3 : 5, shownNodes: nodes.length, totalEdges: 3, matchedEdges: edges.length, shownEdges: edges.length,
      truncated: !hidden, nodesTruncated: !hidden, edgesTruncated: false, pinned: include, missingPins: [] },
    filters: { hideKinds, query: url.searchParams.get("q"), include },
    facets: { kinds: [{ value: "http", total: 1, matched: 1 }, { value: CANDIDATE_KIND, total: 2, matched: hidden ? 0 : 2 }] },
    unregisteredHttpTargets: {
      resultId: state.resultId, ruleVersion: "scoped-http-target-v1",
      providerCoverage: { status: "partial", sources: [], missingRepositories: ["inventory-api"],
        qualifications: ["Registered repository inventory-api has no analysed artifact in this selection"] },
      providerSourceCount: 4, missingRepositoryCount: 1,
      totalCandidates: 3, matchedCandidates: hidden ? 0 : 2, shownCandidates: hidden ? 0 : 1,
      details: hidden ? [] : [detail(state)], detailsTruncated: true,
      outcomes: statuses.map(([status, groups, occurrences]) => ({ status, groups, occurrences })),
    },
  };
}

const OCCURRENCE_LABEL = "GET /api/stock/{}";
const CALLER_LABEL = "InventoryClient.Load";

// Requests drill of one candidate: its observed operations and callers, scoped like the selection.
function requestsArchitecture(url: URL, state: FixtureState) {
  const hideKinds = [...new Set(url.searchParams.getAll("hideKind"))].sort();
  const include = [...new Set(url.searchParams.getAll("include"))].sort();
  const nodes = [
    { id: CANDIDATE_ID, category: CANDIDATE_KIND, label: CANDIDATE_LABEL, sublabel: "Selected catalogue: no known provider", badges: ["Unregistered target"] },
    { id: "op:inventory:get-stock", category: "endpoint", label: OCCURRENCE_LABEL, sublabel: "observed operation", badges: [] },
    { id: CALLER, category: "member", label: CALLER_LABEL, repositoryId: "planning-api", symbolKey: CALLER, badges: [] },
  ];
  const edges = [
    { id: "occurrence:inventory-load", from: CALLER, to: "op:inventory:get-stock", kind: CANDIDATE_KIND, label: "HTTP · unregistered target", count: 4, certainty: "candidate", facts: [] },
    { id: "occurrence:operation", from: "op:inventory:get-stock", to: CANDIDATE_ID, kind: CANDIDATE_KIND, label: "operation of", count: 4, certainty: "candidate", facts: [] },
  ];
  const graph = architecture(url, state);
  return {
    ...graph, level: "requests", title: `Requests · ${CANDIDATE_LABEL}`, nodes, edges,
    breadcrumbs: [{ label: "Landscape", level: "landscape" }, { label: CANDIDATE_LABEL, level: "requests", focus: CANDIDATE_ID }],
    budget: { ...graph.budget, totalNodes: 3, matchedNodes: 3, shownNodes: 3, totalEdges: 2, matchedEdges: 2, shownEdges: 2,
      truncated: false, nodesTruncated: false, edgesTruncated: false },
    filters: { hideKinds, query: url.searchParams.get("q"), include },
  };
}

// Repository drill of Planning.Api: one candidate is in this diagram scope; the other two are elsewhere in the selection.
function repositoryArchitecture(url: URL, state: FixtureState) {
  const graph = architecture(url, state);
  const nodes = [
    { id: "comp:planning-api", category: "component", label: "Planning.Api", repositoryId: "planning-api", badges: [] },
    graph.nodes.find(node => node.id === CANDIDATE_ID)!,
  ];
  const edges = [{ id: "candidate:planning-api", from: "comp:planning-api", to: CANDIDATE_ID, kind: CANDIDATE_KIND, label: "HTTP · unregistered target", count: 4, certainty: "candidate", facts: [] }];
  return {
    ...graph, level: "repository", title: "Planning.Api", nodes, edges,
    breadcrumbs: [{ label: "Landscape", level: "landscape" }, { label: "planning-api", level: "repository", focus: "planning-api" }],
    budget: { ...graph.budget, totalNodes: 2, matchedNodes: 2, shownNodes: 2, totalEdges: 1, matchedEdges: 1, shownEdges: 1,
      truncated: false, nodesTruncated: false, edgesTruncated: false },
    facets: { kinds: [{ value: CANDIDATE_KIND, total: 1, matched: 1 }] },
    unregisteredHttpTargets: { ...graph.unregisteredHttpTargets, totalCandidates: 3, matchedCandidates: 1, shownCandidates: 1 },
  };
}

interface ExplorerOptions { path?: string; init?: () => void }

// A selected Capability with an explicit revision view, as stored by the Explorer selection provider.
function selectPlanningCapability() {
  localStorage.setItem("ce.capability", JSON.stringify("planning"));
  localStorage.setItem("ce.overrides", JSON.stringify({ planning: { "planning-api": "c".repeat(40) } }));
}
const PLANNING_VIEW = `planning-api:${FIRST_COMMIT}`;

async function withExplorer(viewport: { width: number; height: number }, state: FixtureState, run: (page: Page) => Promise<void>, options: ExplorerOptions = {}) {
  const browser = await playwright.chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport });
    page.setDefaultTimeout(10_000);
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    if (options.init) await page.addInitScript(options.init);
    await page.route("**/api/**", async route => {
      const request = route.request();
      const url = new URL(request.url());
      state.requests.push({ method: request.method(), url });
      let payload: unknown = {};
      if (url.pathname === "/api/meta") payload = { generation: state.generation, snapshotId: "fixture",
        capabilities: [{ id: "planning", name: "Planning", repositories: ["planning-api"] }, { id: "reporting", name: "Reporting", repositories: ["planning-api"] }],
        githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
        similarity: { database: false }, revisions: [], defaultView: {}, artifacts: 1, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: [] };
      else if (url.pathname === "/api/generation") payload = { generation: state.generation, snapshotId: "fixture" };
      else if (url.pathname === "/api/view") payload = { "planning-api": state.commit };
      else if (url.pathname === "/api/architecture/connections") payload = { version: 0, connections: [] };
      else if (url.pathname === "/api/binding-preview/recommendations") payload = { groups: [], owners: [], fingerprint: "fixture", algorithmVersion: "fixture", sourceView: "", targetView: "" };
      else if (url.pathname === "/api/architecture") payload = url.searchParams.get("level") === "requests" ? requestsArchitecture(url, state)
        : url.searchParams.get("level") === "repository" ? repositoryArchitecture(url, state) : architecture(url, state);
      else if (url.pathname === "/api/source") payload = { lines: [{ n: 42, text: "var stock = await inventory.GetAsync(\"api/stock/\" + id);" }], highlightStart: 42, highlightEnd: 44 };
      else if (url.pathname === "/api/symbol") payload = { node: { key: CALLER, kind: "Method", name: "Load", generated: false, repositoryId: "planning-api",
        commitSha: state.commit, unit: "Planning.Api", evidence: [] }, outgoing: [], incoming: [], history: [] };
      else if (url.pathname === "/api/similar" || url.pathname === "/api/search") payload = [];
      await route.fulfill({ json: payload });
    });
    await page.goto(`${origin}/#/architecture?${options.path ?? "level=landscape"}`);
    await canvasReady(page);
    await run(page);
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
}

async function canvasReady(page: Page, identity?: string) {
  const selector = identity ? `.architecture-canvas .live-architecture-canvas[data-canvas-ready="true"][data-input-identity*="${identity}"]`
    : '.architecture-canvas .live-architecture-canvas[data-canvas-ready="true"]';
  await page.locator(selector).waitFor();
}

function newState(): FixtureState {
  return { generation: 1, resultId: FIRST_RESULT, commit: FIRST_COMMIT, requests: [] };
}

const architecturePanel = (page: Page) => page.locator(".architecture-panels > aside");
const renderedNode = (page: Page, label: string) => page.locator(".architecture-canvas .react-flow__node").filter({ hasText: label });

async function selectCandidate(page: Page) {
  await renderedNode(page, CANDIDATE_LABEL).click();
  await architecturePanel(page).getByText(CANDIDATE_LABEL, { exact: true }).first().waitFor();
}

async function count(locator: Locator) { return locator.count(); }

interface ShapeStyle { shape: string | null; borderStyle: string; borderWidth: number; border: number[]; background: number[]; badge: string | null }

async function shapeStyle(node: Locator): Promise<ShapeStyle> {
  return node.evaluate(element => {
    const rgba = (value: string) => (value.match(/[\d.]+/g) ?? []).map(Number);
    const shape = element.querySelector(".likec4-element-shape");
    if (!(shape instanceof HTMLElement)) throw new Error("Rendered node has no HTML element shape.");
    const style = getComputedStyle(shape);
    const background = rgba(style.backgroundColor);
    return {
      shape: element.querySelector("[data-likec4-shape]")?.getAttribute("data-likec4-shape") ?? null,
      borderStyle: style.borderTopStyle, borderWidth: Number.parseFloat(style.borderTopWidth),
      border: rgba(style.borderTopColor), background: background.length === 3 ? [...background, 1] : background,
      badge: element.querySelector("[data-likec4-node-technology]")?.textContent ?? null,
    };
  });
}

const grey = (rgb: number[]) => Math.max(rgb[0]!, rgb[1]!, rgb[2]!) - Math.min(rgb[0]!, rgb[1]!, rgb[2]!) <= 32;

test("102-A Given a no-known-provider HTTP group, when Architecture opens, then the rendered node is a grey dashed near-transparent rectangle with name and badge", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    const candidate = renderedNode(page, CANDIDATE_LABEL);
    assert.equal(await count(candidate), 1, "The candidate must be discoverable as one rendered canvas node.");
    const style = await shapeStyle(candidate);
    assert.equal(style.shape, "rectangle", "The candidate must render as a rectangle.");
    assert.equal(style.borderStyle, "dashed", "The candidate outline must be dashed.");
    assert.ok(style.borderWidth >= 1, "The candidate outline must be visible.");
    assert.ok(grey(style.border), `The candidate outline must be grey: ${style.border.join(",")}`);
    assert.ok(style.background[3]! > 0 && style.background[3]! <= 0.15, `The candidate fill must be near-transparent: ${style.background.join(",")}`);
    assert.ok(grey(style.background), "The candidate fill tint must be grey.");
    assert.equal(style.badge?.trim(), "Unregistered target", "The rendered node must carry the visible text badge.");
    assert.equal(await count(candidate.getByText(CANDIDATE_LABEL, { exact: true })), 1, "The rendered node must show the observed name.");

    const external = await shapeStyle(renderedNode(page, EXTERNAL_LABEL));
    assert.equal(external.borderStyle, "dashed", "Existing external systems remain dashed.");
    assert.equal(external.background[3], 1, "Existing external systems keep their grey fill.");
    assert.notEqual(external.badge?.trim(), "Unregistered target", "External systems are not unregistered-target candidates.");
    const capability = await shapeStyle(renderedNode(page, "Planning"));
    assert.notEqual(capability.borderStyle, "dashed", "Known Capability nodes keep their existing outline.");
  });
});

for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  test(`102-B Given a candidate at ${viewport.width}px, when selected, then selection-wide details, provenance, gaps and bounds are explicit`, async () => {
    const state = newState();
    await withExplorer(viewport, state, async page => {
      await selectCandidate(page);
      const inspector = page.getByRole("region", { name: "Unregistered target candidate", exact: true });
      assert.equal(await count(inspector), 1, "Selecting the candidate must open its read-only inspector.");
      const text = (await inspector.innerText()).replace(/\s+/g, " ");
      for (const expected of [
        "Unregistered target",
        "No known provider in the selected catalogue. This is not proof that no runtime service exists.",
        "Consumers · 2 of 2 Capability memberships (selection-wide)",
        "Planning · planning-api", "Reporting · planning-api",
        "Distinct operations · 2 of 3 listed (selection-wide)",
        "GET /api/stock/{}", "POST /api/reservations",
        "Graph edges · 7 occurrences on 2 shown edges (edge support, not distinct operations)",
        "Scoped configuration key InventoryApi:BaseUrl in Planning.Api",
        `Repository planning-api · unit src/Planning.Api/Planning.Api.csproj`,
        `Caller ${CALLER}`, "Client InventoryClient", "Destination hint config=InventoryApi:BaseUrl",
        "Occurrences · 1 of 57 listed (selection-wide)",
        `planning-api @ ${FIRST_COMMIT}`, `artifact ${ARTIFACT_HASH}`, "edge e:inventory-load",
        "Evidence · 1 of 2 records listed", `snippet sha256 ${SNIPPET_HASH}`, "lines 42:9–44:30",
        "Alternatives · 0 of 0 listed",
        "Missing evidence", "Deployed service identity is not established",
        "Provider coverage: partial", "4 provider sources · 1 missing repository", "inventory-api",
        "Registered repository inventory-api has no analysed artifact in this selection",
        "Detail lists are bounded.",
        "Rule scoped-http-target-v1",
      ]) assert.ok(text.includes(expected), `Inspector must state: ${expected}\n${text}`);
      assert.equal(await count(inspector.getByRole("button", { name: "Open candidate occurrences", exact: true })), 1);
      assert.equal(await count(architecturePanel(page).getByRole("button", { name: "Connect to capability" })), 0,
        "Inspecting a candidate must not offer Capability adoption.");
      const discovery = page.getByRole("status").filter({ hasText: "Unregistered target candidates" });
      const outcomes = (await discovery.innerText()).replace(/\s+/g, " ");
      for (const expected of ["No known provider (no-known-provider): 3 groups · 9 occurrences",
        "Ambiguous known providers (ambiguous-known-providers): 1 group · 2 occurrences",
        "Broken binding (broken-binding): 1 group · 1 occurrence",
        "Dynamic or insufficient evidence (dynamic/insufficient-evidence): 2 groups · 5 occurrences",
        "Partial provider coverage (partial-provider-coverage): 1 group · 4 occurrences"])
        assert.ok(outcomes.includes(expected), `Outcome must remain distinct: ${expected}\n${outcomes}`);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false, "The inspector must fit the viewport.");
    });
  });
}

test("102-E Given a selected candidate, when its result identity changes for the same ID, then selection resets without stale details", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    await selectCandidate(page);
    const refreshed = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === "/api/architecture" && url.searchParams.get("g") === "2";
    });
    state.generation = 2;
    state.resultId = SECOND_RESULT;
    state.commit = SECOND_COMMIT;
    await refreshed;
    await canvasReady(page, "g=2");
    const panel = architecturePanel(page);
    assert.equal(await count(panel.getByText(CANDIDATE_LABEL, { exact: true })), 0, "A candidate selected under another result must not remain selected.");
    assert.equal(await count(page.getByText(FIRST_COMMIT, { exact: false })), 0, "Details from the previous evidence must not be shown.");
    assert.equal(await count(page.getByRole("status").filter({ hasText: "Candidate results changed" })), 1, "The reset must be explained.");
    await selectCandidate(page);
    const inspector = page.getByRole("region", { name: "Unregistered target candidate", exact: true });
    assert.ok((await inspector.innerText()).includes(`planning-api @ ${SECOND_COMMIT}`), "Reselection must show the current evidence.");
  });
});

test("102-F Given hidden or omitted candidates, when filtered, then selection-wide counts remain discoverable and recovery preserves other filters", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    const discovery = page.getByRole("status").filter({ hasText: "Unregistered target candidates" });
    assert.equal(await count(discovery), 1, "Candidate counts must be discoverable beside the graph.");
    let text = (await discovery.innerText()).replace(/\s+/g, " ");
    for (const expected of ["Unregistered target candidates: 3 in selection · 2 matched · 1 shown",
      "1 matched candidate is omitted by graph budgets; search, pin from omitted elements or drill down.",
      "1 candidate in the selection is outside this diagram scope or excluded by the current filters.", "Candidate details listed: 1 of 3 (bounded)."])
      assert.ok(text.includes(expected), `Discovery must state: ${expected}\n${text}`);
    const before = state.requests.filter(request => request.url.pathname === "/api/architecture").at(-1)!.url;
    await page.getByLabel("Find elements", { exact: true }).fill("inventory");
    await page.getByLabel("Unregistered-http-target", { exact: true }).uncheck();
    const hidden = page.waitForResponse(response => new URL(response.url()).searchParams.getAll("hideKind").includes(CANDIDATE_KIND));
    await page.getByRole("button", { name: "Apply graph filters", exact: true }).click();
    await hidden;
    await page.locator('.architecture-canvas [data-canvas-ready="true"]').first().waitFor();
    text = (await discovery.innerText()).replace(/\s+/g, " ");
    assert.ok(text.includes("Unregistered target candidates: 3 in selection · 0 matched · 0 shown"), text);
    assert.ok(text.includes("Hidden by the relationship kind filter."), text);
    const recovered = page.waitForRequest((request: Request) => {
      const url = new URL(request.url());
      return url.pathname === "/api/architecture" && !url.searchParams.getAll("hideKind").includes(CANDIDATE_KIND);
    });
    await discovery.getByRole("button", { name: "Show unregistered targets", exact: true }).click();
    const url = new URL((await recovered).url());
    assert.equal(url.searchParams.get("q"), "inventory", "Recovery keeps the applied search.");
    assert.deepEqual(url.searchParams.getAll("hideKind"), ["declares"], "Recovery keeps other hidden kinds.");
    for (const key of ["nodeBudget", "edgeBudget", "view", "level"]) assert.equal(url.searchParams.get(key), before.searchParams.get(key), `Recovery keeps ${key}.`);
    await canvasReady(page);
    assert.equal(await count(renderedNode(page, CANDIDATE_LABEL)), 1);
  });
});

test("102-K Given a repository drill without candidate filters, when 2 of 3 candidates are outside the diagram, then they are not labelled as filter mismatches", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    const request = lastArchitecture(state);
    assert.equal(request.searchParams.get("level"), "repository", "The fixture opens the repository drill.");
    assert.equal(request.searchParams.get("q"), null, "The fixture applies no search.");
    assert.deepEqual(request.searchParams.getAll("include"), [], "The fixture pins nothing.");
    assert.equal(request.searchParams.getAll("hideKind").includes(CANDIDATE_KIND), false, "The fixture does not hide candidates.");
    const discovery = page.getByRole("status").filter({ hasText: "Unregistered target candidates" });
    const text = (await discovery.innerText()).replace(/\s+/g, " ");
    assert.ok(text.includes("Unregistered target candidates: 3 in selection · 1 matched · 1 shown"), text);
    assert.equal(text.includes("do not match the current filters"), false, `Out-of-scope candidates must not be labelled as filter mismatches.\n${text}`);
    for (const expected of [
      "2 candidates in the selection are outside this diagram scope or excluded by the current filters.",
      "Open a broader Architecture level to include candidates outside this scope; changing filters recovers only candidates excluded by filters.",
    ]) assert.ok(text.includes(expected), `Discovery must state: ${expected}\n${text}`);
    assert.equal(text.includes("Hidden by the relationship kind filter."), false, text);
  }, { path: "level=repository&focus=planning-api" });
});

test("102-G Given an inspected candidate, when evidence is navigated, then only GET source and symbol boundaries are used", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    await selectCandidate(page);
    const inspector = page.getByRole("region", { name: "Unregistered target candidate", exact: true });
    const evidence = inspector.getByRole("button", { name: "InventoryClient.cs:42", exact: true });
    assert.equal(await count(evidence), 1, "Candidate evidence must use the existing source navigation.");
    const sourceRequest = page.waitForRequest((request: Request) => new URL(request.url()).pathname === "/api/source");
    await evidence.click();
    const source = new URL((await sourceRequest).url());
    assert.deepEqual(Object.fromEntries(["repo", "commit", "path", "start", "end"].map(key => [key, source.searchParams.get(key)])),
      { repo: "planning-api", commit: FIRST_COMMIT, path: "src/Planning.Api/InventoryClient.cs", start: "42", end: "44" });
    await inspector.getByText("var stock = await inventory.GetAsync", { exact: false }).waitFor();
    const view = state.requests.filter(request => request.url.pathname === "/api/architecture").at(-1)!.url.searchParams.get("view");
    const symbolRequest = page.waitForRequest((request: Request) => new URL(request.url()).pathname === "/api/symbol");
    await inspector.getByRole("link", { name: "Open caller in Explore", exact: true }).click();
    const symbol = new URL((await symbolRequest).url());
    assert.equal(symbol.searchParams.get("key"), CALLER);
    assert.equal(symbol.searchParams.get("view"), view, "Symbol navigation keeps the exact selection.");
    await page.getByRole("heading", { name: "Load" }).first().waitFor();
    const writes = state.requests.filter(request => request.method !== "GET").map(request => `${request.method} ${request.url.pathname}`);
    assert.deepEqual(writes, [], "Candidate inspection must not create Capabilities, enrollments, bindings or manual connections.");
  });
});

declare global {
  interface Window { ceDownloads?: { name: string; text: Promise<string> }[] }
}

// Records the Blob passed to an anchor download instead of writing it to disk.
function captureDownloads() {
  const blobs = new Map<string, Blob>();
  const create = URL.createObjectURL.bind(URL);
  URL.createObjectURL = (object: Blob | MediaSource) => {
    const url = create(object);
    if (object instanceof Blob) blobs.set(url, object);
    return url;
  };
  const click = HTMLAnchorElement.prototype.click;
  HTMLAnchorElement.prototype.click = function (this: HTMLAnchorElement) {
    const blob = this.download ? blobs.get(this.href) : undefined;
    if (!blob) { click.call(this); return; }
    (window.ceDownloads ??= []).push({ name: this.download, text: blob.text() });
  };
}

test("102-H Given a rendered candidate, when the LikeC4 source is shown and downloaded, then it compiles and keeps the rendered badge", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    const rendered = await shapeStyle(renderedNode(page, CANDIDATE_LABEL));
    assert.equal(rendered.badge?.trim(), "Unregistered target", "The badge must be rendered before it is exported.");
    await page.getByRole("button", { name: "Diagram source", exact: true }).click();
    const shown = await page.locator(".architecture-canvas pre[data-live-source]").textContent();
    await page.getByRole("button", { name: "Download .c4", exact: true }).click();
    const downloads = await page.evaluate(() => Promise.all((window.ceDownloads ?? []).map(async item => ({ name: item.name, text: await item.text }))));
    assert.equal(downloads.length, 1, "Download .c4 must export one source file.");
    const [download] = downloads;
    assert.equal(download!.name, "architecture.c4");
    assert.equal(download!.text, shown, "The downloaded source must be the reviewable source.");
    const compiler = await LikeC4.fromSource(download!.text, { logger: false, printErrors: false, throwIfInvalid: true });
    try {
      assert.deepEqual(compiler.getErrors(), [], "The exported source must compile.");
      const model = await compiler.parsedModel();
      const exported = [...model.elements()].filter(element => {
        const source = element.getMetadata("source");
        return typeof source === "string" && (JSON.parse(source) as { id?: unknown }).id === CANDIDATE_ID;
      });
      assert.equal(exported.length, 1, "The exported source must contain the candidate element.");
      assert.equal(exported[0]!.technology, rendered.badge?.trim(), "The exported candidate must keep the rendered badge.");
      const external = [...model.elements()].find(element => element.title === EXTERNAL_LABEL);
      assert.equal(external?.technology ?? null, null, "Other elements must not gain the candidate badge.");
    } finally { await compiler.dispose(); }
  }, { init: captureDownloads });
});

interface TextContrast { ratio: number; text: number[]; background: number[]; base: string; fontSize: number; fontWeight: number }

// WCAG contrast of rendered text against its composited, possibly translucent, backgrounds.
async function textContrast(node: Locator, selector: string): Promise<TextContrast> {
  return node.evaluate((element, selector) => {
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = 1;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) throw new Error("A 2D canvas is required to resolve CSS colours.");
    const rgba = (color: string) => {
      context.clearRect(0, 0, 1, 1);
      context.fillStyle = "rgba(0, 0, 0, 0)";
      context.fillStyle = color;
      context.fillRect(0, 0, 1, 1);
      const [r = 0, g = 0, b = 0, a = 0] = context.getImageData(0, 0, 1, 1).data;
      return [r, g, b, a / 255];
    };
    const over = (top: number[], bottom: number[]) => [0, 1, 2].map(i => top[i]! * top[3]! + bottom[i]! * (1 - top[3]!)).concat(1);
    const luminance = (rgb: number[]) => {
      const [r, g, b] = rgb.slice(0, 3).map(value => { const c = value / 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; });
      return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
    };
    const target = element.querySelector(selector);
    if (!(target instanceof HTMLElement)) throw new Error(`Rendered node has no ${selector}.`);
    const layers: number[][] = [];
    let base = "";
    for (let current: Element | null = target; current;) {
      // The node fill is a sibling shape beneath the text, not one of its ancestors.
      const shape = current === element ? element.querySelector(".likec4-element-shape") : null;
      if (shape) layers.push(rgba(getComputedStyle(shape).backgroundColor));
      const layer = rgba(getComputedStyle(current).backgroundColor);
      if (layer[3]! > 0) layers.push(layer);
      if (layer[3]! >= 1) { base = `${current.tagName.toLowerCase()}.${[...current.classList].join(".")}`; break; }
      const parent: Node | null = current.parentNode;
      current = parent instanceof ShadowRoot ? parent.host : parent instanceof Element ? parent : null;
    }
    if (!base) throw new Error("No opaque background behind the rendered text.");
    const background = layers.reverse().reduce((below, layer) => over(layer, below));
    const style = getComputedStyle(target);
    const color = rgba(style.color);
    const text = over([color[0]!, color[1]!, color[2]!, color[3]! * Number(style.opacity)], background);
    const [light, dark] = [luminance(text), luminance(background)].sort((a, b) => b - a);
    return { ratio: (light! + 0.05) / (dark! + 0.05), text, background, base,
      fontSize: Number.parseFloat(style.fontSize), fontWeight: Number(style.fontWeight) };
  }, selector);
}

async function measureTheme(page: Page, theme: "dark" | "light") {
  const toggle = page.getByRole("switch", { name: "Dark mode", exact: true });
  if (theme === "dark") await toggle.check(); else await toggle.uncheck();
  await page.locator(`.architecture-canvas .live-canvas-shell[data-theme="${theme}"]`).waitFor();
  await page.locator(`.architecture-canvas [data-mantine-color-scheme="${theme}"]`).first().waitFor();
  const candidate = renderedNode(page, CANDIDATE_LABEL);
  const style = await shapeStyle(candidate);
  assert.equal(style.borderStyle, "dashed", `${theme}: the candidate outline stays dashed.`);
  assert.ok(grey(style.border), `${theme}: the candidate outline stays grey: ${style.border.join(",")}`);
  assert.ok(style.background[3]! > 0 && style.background[3]! <= 0.15 && grey(style.background), `${theme}: the fill stays near-transparent grey: ${style.background.join(",")}`);
  const title = await textContrast(candidate, "[data-likec4-node-title]");
  const badge = await textContrast(candidate, "[data-likec4-node-technology]");
  const report = JSON.stringify({ theme, title, badge });
  assert.ok(title.ratio >= 4.5, `${theme}: the candidate name needs at least 4.5:1 contrast: ${report}`);
  assert.ok(badge.ratio >= 4.5, `${theme}: the Unregistered target badge needs at least 4.5:1 contrast: ${report}`);
}

test("102-I Given the light canvas theme, when candidate text is measured on its near-transparent fill, then title and badge keep AA contrast", async () => {
  await withExplorer({ width: 1440, height: 1000 }, newState(), page => measureTheme(page, "light"));
});

// The canvas opens dark (cold), then the toggle must restyle the same model both ways (warm).
test("102-I Given the dark canvas theme, when candidate text is measured on its near-transparent fill, then title and badge keep AA contrast", async () => {
  await withExplorer({ width: 1440, height: 1000 }, newState(), async page => {
    await measureTheme(page, "dark");
    await measureTheme(page, "light");
    await measureTheme(page, "dark");
  });
});

const lastArchitecture = (state: FixtureState) => state.requests.filter(request => request.url.pathname === "/api/architecture").at(-1)!.url;

test("102-J Given a selected Capability and view, when candidate occurrences are opened, then the requests drill keeps the selection and renders occurrences", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    const before = lastArchitecture(state);
    assert.equal(before.searchParams.get("cap"), "planning", "The fixture selects the Planning Capability.");
    assert.equal(before.searchParams.get("view"), PLANNING_VIEW, "The fixture selects an explicit revision view.");
    await selectCandidate(page);
    const inspector = page.getByRole("region", { name: "Unregistered target candidate", exact: true });
    const drilled = page.waitForRequest((request: Request) => {
      const url = new URL(request.url());
      return url.pathname === "/api/architecture" && url.searchParams.get("level") === "requests";
    });
    await inspector.getByRole("button", { name: "Open candidate occurrences", exact: true }).click();
    const url = new URL((await drilled).url());
    assert.equal(url.searchParams.get("focus"), CANDIDATE_ID, "The drill focuses the candidate.");
    for (const key of ["cap", "view", "g", "instance"]) assert.equal(url.searchParams.get(key), before.searchParams.get(key), `The drill keeps ${key}.`);
    await canvasReady(page, "requests");
    for (const label of [CANDIDATE_LABEL, OCCURRENCE_LABEL, CALLER_LABEL])
      assert.equal(await count(renderedNode(page, label)), 1, `The requests drill renders ${label}.`);
    assert.equal(await count(page.getByText("3 · requests", { exact: true })), 1, "The requests level is shown.");
    const hash = new URLSearchParams(new URL(page.url()).hash.split("?")[1]);
    assert.deepEqual({ level: hash.get("level"), focus: hash.get("focus") }, { level: "requests", focus: CANDIDATE_ID });
    assert.equal(await page.evaluate(() => localStorage.getItem("ce.capability")), JSON.stringify("planning"), "The selected Capability is kept.");
    const writes = state.requests.filter(request => request.method !== "GET").map(request => `${request.method} ${request.url.pathname}`);
    assert.deepEqual(writes, [], "Opening candidate occurrences must not write.");
  }, { init: selectPlanningCapability });
});

test("102-J Given a selected Capability and view, when hidden candidates are recovered, then the recovery keeps the Capability, view and filters", async () => {
  const state = newState();
  await withExplorer({ width: 1440, height: 1000 }, state, async page => {
    const before = lastArchitecture(state);
    assert.equal(before.searchParams.get("cap"), "planning", "The fixture selects the Planning Capability.");
    await page.getByLabel("Unregistered-http-target", { exact: true }).uncheck();
    const hidden = page.waitForResponse(response => new URL(response.url()).searchParams.getAll("hideKind").includes(CANDIDATE_KIND));
    await page.getByRole("button", { name: "Apply graph filters", exact: true }).click();
    await hidden;
    const discovery = page.getByRole("status").filter({ hasText: "Unregistered target candidates" });
    await discovery.getByText("Hidden by the relationship kind filter.", { exact: false }).waitFor();
    const recovered = page.waitForRequest((request: Request) => {
      const url = new URL(request.url());
      return url.pathname === "/api/architecture" && !url.searchParams.getAll("hideKind").includes(CANDIDATE_KIND);
    });
    await discovery.getByRole("button", { name: "Show unregistered targets", exact: true }).click();
    const url = new URL((await recovered).url());
    for (const key of ["cap", "view", "g", "instance", "level", "nodeBudget", "edgeBudget"])
      assert.equal(url.searchParams.get(key), before.searchParams.get(key), `Recovery keeps ${key}.`);
    assert.deepEqual(url.searchParams.getAll("hideKind"), before.searchParams.getAll("hideKind").filter(kind => kind !== CANDIDATE_KIND), "Recovery keeps the other hidden kinds.");
    await canvasReady(page);
    assert.equal(await count(renderedNode(page, CANDIDATE_LABEL)), 1);
    const writes = state.requests.filter(request => request.method !== "GET").map(request => `${request.method} ${request.url.pathname}`);
    assert.deepEqual(writes, [], "Recovery must not write.");
  }, { init: selectPlanningCapability });
});
