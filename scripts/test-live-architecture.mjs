// Runs against an actual Explorer build; invoke through the pinned Playwright sandbox.
// CE_TEST_BASE_URL (or first argument) selects a preview owned by the caller. This
// script starts no server, edits no artifacts and writes no reviewed database data.
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';

const base = process.env.CE_TEST_BASE_URL ?? process.env.CE_LIVE_ARCHITECTURE_BASE_URL ?? process.argv[2] ?? 'http://host.docker.internal:5096';
const output = `/work/.sandbox-cache/live-architecture-browser/${new URL(base).port || 'default'}`;
const receipt = { base, assertions: 0, viewports: [], simulated: [], checks: [], passed: false };
const check = (condition, message) => { assert.ok(condition, message); receipt.assertions++; };
const equal = (actual, expected, message) => { assert.deepEqual(actual, expected, message); receipt.assertions++; };
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
await mkdir(output, { recursive: true });
let page;
const canvas = () => page.locator('[aria-label="Architecture canvas"]');
const sourceNode = (id) => canvas().locator(`.react-flow__node[data-source-node-ids*=${JSON.stringify(JSON.stringify(id))}]:not([data-presentation-group="true"])`).first();
const sourceEdge = (id) => canvas().locator(`.react-flow__edge[data-source-edge-ids*=${JSON.stringify(JSON.stringify(id))}]`).first();
const ready = async () => {
  await page.waitForFunction(() => document.querySelector('[aria-label="Architecture canvas"]')?.getAttribute('data-status') === 'ready');
  await canvas().locator('.react-flow__node[data-source-node-ids]').first().waitFor();
  await canvas().locator('[data-routing-state="ready"]').waitFor();
};
const graphResponse = (predicate = () => true) => page.waitForResponse((response) => {
  const url = new URL(response.url());
  return url.pathname === '/api/architecture' && response.ok() && predicate(url.searchParams);
});
async function navigate(level, focus) {
  const waiting = graphResponse((params) => params.get('level') === level && (params.get('focus') ?? '') === (focus ?? ''));
  await page.goto(`${base}/#/architecture?${new URLSearchParams({ level, ...(focus ? { focus } : {}) })}`);
  const graph = await (await waiting).json();
  await ready();
  return graph;
}
async function mapping(graph) {
  const mapped = await canvas().locator('.react-flow__node[data-source-node-ids]').evaluateAll((elements) => elements.flatMap((element) => JSON.parse(element.dataset.sourceNodeIds)));
  check(mapped.length > 0, 'rendered LikeC4 nodes must carry original source IDs');
  const allowed = new Set(graph.nodes.map((node) => node.id));
  check(mapped.every((id) => allowed.has(id)), 'every rendered source node must belong to the actual selected API graph');
  const edgeIds = await canvas().locator('.react-flow__edge[data-source-edge-ids]').evaluateAll((elements) => elements.flatMap((element) => JSON.parse(element.dataset.sourceEdgeIds)));
  const allowedEdges = new Set(graph.edges.map((edge) => edge.id));
  check(edgeIds.every((id) => allowedEdges.has(id)), 'relationships must preserve source edge IDs without fixture leakage');
  return { mapped, edgeIds };
}
async function inspectEdge(edge) {
  const element = sourceEdge(edge.id);
  await element.locator('.react-flow__edge-path').first().dispatchEvent('click');
  const original = page.getByRole('button', { name: `Inspect relationship ${edge.id}`, exact: true });
  if (await original.count()) await original.click();
  await page.getByText(`${edge.count} underlying fact${edge.count === 1 ? '' : 's'}`, { exact: false }).first().waitFor();
  const panel = await page.locator('aside').last().innerText();
  const names = [edge.from, edge.to].map((id) => currentGraph.nodes.find((node) => node.id === id)?.label ?? id);
  check(names.every((name) => panel.includes(name)), 'selected relationship must retain original source endpoint direction');
  check(panel.includes(edge.label), 'selection must show the original relationship label');
  const certainty = { human: 'human assertion', static: 'static target' }[edge.certainty] ?? edge.certainty;
  check(panel.includes(certainty), 'original certainty must remain visible without being upgraded by aggregation');
  const fact = edge.facts.find((item) => item.evidence.length);
  check(!!fact, 'selected real relationship must contain source evidence');
  const evidence = fact.evidence[0];
  const source = page.waitForResponse((response) => {
    const url = new URL(response.url()), params = url.searchParams;
    return url.pathname === '/api/source' && params.get('repo') === evidence.repositoryId && params.get('commit') === evidence.commitSha && params.get('path') === evidence.path && params.get('start') === String(evidence.startLine) && params.get('end') === String(evidence.endLine);
  });
  await page.getByRole('button', { name: `${evidence.path.split('/').at(-1)}:${evidence.startLine}`, exact: true }).first().click();
  const response = await source;
  check(response.ok(), 'exact repository, revision, path and source span must load successfully');
  check((await response.json()).lines.length > 0, 'source viewer must receive actual source lines');
  if (edge.count > edge.facts.length) check(panel.includes(`first ${edge.facts.length} shown`), 'capped evidence must disclose truncation');
  return evidence;
}
async function sourceDownload(expectedGraph) {
  await page.getByRole('button', { name: 'Diagram source', exact: true }).click();
  const source = await page.locator('[data-live-source]').textContent();
  check(source.includes('metadata'), 'live diagram-as-code must carry provenance metadata');
  check(expectedGraph.nodes.every((node) => source.includes(node.id)), 'source download projection must include every actual graph node ID');
  check(expectedGraph.edges.every((edge) => source.includes(edge.id)), 'source download projection must include every actual graph edge ID, including filtered kinds');
  const pending = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Download .c4', exact: true }).click();
  const download = await pending;
  equal(download.suggestedFilename(), 'architecture.c4', 'download must retain the diagram-as-code filename');
  const path = await download.path();
  equal(await readFile(path, 'utf8'), source, 'download bytes must equal the displayed live source');
  await page.getByRole('button', { name: 'Diagram source', exact: true }).click();
  return source;
}
async function groupJourney() {
  // The ledger is deliberately an overlay. Minimize it before direct manipulation,
  // and target the scaled title rather than assuming an unscaled header height.
  await page.getByRole('button', { name: 'Minimize ledger', exact: true }).click();
  const groups = canvas().locator('.react-flow__node[data-presentation-group="true"]');
  check(await groups.count() > 0, 'actual repository ownership must produce identified groups');
  let group, candidates = [];
  for (let index = 0; index < await groups.count(); index++) {
    const candidateGroup = groups.nth(index), eligible = [];
    for (const id of JSON.parse(await candidateGroup.getAttribute('data-source-node-ids'))) {
      const node = sourceNode(id);
      if (await node.count() && JSON.parse(await node.getAttribute('data-source-node-ids')).length === 1) eligible.push(id);
      if (eligible.length === 2) break;
    }
    if (eligible.length === 2) { group = candidateGroup; candidates = eligible; break; }
  }
  check(candidates.length === 2, 'identified group must retain two independently selectable original members');
  const selectGroup = async (locator) => {
    const minimize = page.getByRole('button', { name: 'Minimize ledger', exact: true });
    if (await minimize.count()) await minimize.click(); // a full reload restores the default open ledger
    await locator.locator('.likec4-compound-title').click();
    await page.getByRole('region', { name: 'Selected group controls' }).waitFor();
  };
  const originalCount = await groups.count();
  await selectGroup(group);
  check((await page.getByRole('region', { name: 'Selected group controls' }).innerText()).includes('identified repository ownership'), 'automatic groups must disclose identified ownership');
  await page.getByRole('button', { name: 'Ungroup', exact: true }).click();
  equal(await groups.count(), originalCount - 1, 'ungroup must remove the presentation group');
  await sourceNode(candidates[0]).click({ force: true });
  await sourceNode(candidates[1]).click({ modifiers: ['Shift'], force: true });
  await page.getByRole('button', { name: 'Group selection', exact: true }).click();
  await page.getByRole('textbox', { name: 'Group name', exact: true }).fill('Browser parity group');
  await page.getByRole('button', { name: 'Save group', exact: true }).click();
  const custom = groups.filter({ hasText: 'Browser parity group' });
  await custom.waitFor(); receipt.assertions++;
  equal([...JSON.parse(await custom.getAttribute('data-source-node-ids'))].sort(), [...candidates].sort(), 'custom group must preserve exact member source identities');
  const before = await Promise.all(candidates.map((id) => sourceNode(id).boundingBox()));
  const box = await custom.locator('.likec4-compound-title').boundingBox();
  const dragX = box.x + box.width / 2, dragY = box.y + box.height / 2;
  await page.mouse.move(dragX, dragY); await page.mouse.down();
  await page.mouse.move(dragX + 55, dragY + 35, { steps: 12 }); await page.mouse.up();
  const after = await Promise.all(candidates.map((id) => sourceNode(id).boundingBox()));
  const deltas = before.map((value, index) => ({ x: after[index].x - value.x, y: after[index].y - value.y }));
  check(Math.abs(deltas[0].x) > 15, 'whole-group drag must move a member');
  check(Math.abs(deltas[0].x - deltas[1].x) < 2 && Math.abs(deltas[0].y - deltas[1].y) < 2, 'whole-group drag must preserve relative member geometry');
  await selectGroup(custom);
  await page.getByRole('button', { name: 'Rename', exact: true }).click();
  await page.getByRole('textbox', { name: 'Group name', exact: true }).fill('Browser renamed group');
  await page.getByRole('button', { name: 'Save group', exact: true }).click();
  const renamed = groups.filter({ hasText: 'Browser renamed group' });
  await renamed.waitFor(); receipt.assertions++;
  const restored = graphResponse();
  await page.reload();
  currentGraph = await (await restored).json(); await ready();
  await renamed.waitFor(); receipt.assertions++;
  equal([...JSON.parse(await renamed.getAttribute('data-source-node-ids'))].sort(), [...candidates].sort(), 'custom grouping must persist for the exact same source snapshot');
  await selectGroup(renamed);
  await page.getByRole('button', { name: 'Edit members', exact: true }).click();
  await sourceNode(candidates[1]).click({ force: true });
  await page.getByRole('button', { name: 'Save group', exact: true }).click();
  equal(JSON.parse(await renamed.getAttribute('data-source-node-ids')), [candidates[0]], 'editing membership must change only the presentation group');
  check(await sourceNode(candidates[1]).count() > 0, 'removed group member must retain its source node');
  await selectGroup(renamed);
  await page.getByRole('button', { name: 'Ungroup', exact: true }).click();
  equal(await renamed.count(), 0, 'custom presentation group must delete');
  await mapping(currentGraph);
  await sourceDownload(currentGraph);
  await page.getByRole('button', { name: 'Expand ledger', exact: true }).click();
  receipt.checks.push('identified ownership, group create/rename/membership/delete and whole-group drag');
}
let currentGraph;
try {
  page = await browser.newPage({ viewport: { width: 1440, height: 900 }, reducedMotion: 'no-preference' });
  page.setDefaultTimeout(20000);
  const errors = [], forbidden = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname.includes('/likec4/index.html') || /all-repositories\.graph\.json|architecture\.c4$/.test(url.pathname) || (/^https?:$/.test(url.protocol) && url.origin !== new URL(base).origin)) forbidden.push(request.url());
  });
  await page.addInitScript(() => {
    if (!sessionStorage.getItem('ce.live-browser-initialized')) {
      localStorage.clear(); localStorage.setItem('ce.capability', '""'); localStorage.setItem('ce.overrides', '{}');
      sessionStorage.setItem('ce.live-browser-initialized', '1');
    }
  });
  const metaResponse = await page.request.get(`${base}/api/meta`);
  check(metaResponse.ok(), 'actual metadata endpoint must succeed');
  const meta = await metaResponse.json();
  check(meta.capabilities.length > 0 && meta.revisions.length > 0, 'preview must contain analyzed capability/revision fixtures');
  currentGraph = await navigate('system');
  check(currentGraph.nodes.length > 0 && currentGraph.edges.length > 0, 'actual all-repositories graph must be nonempty');
  equal(await page.locator('iframe').count(), 0, 'production Architecture must render directly without an experimental iframe');
  equal(await page.getByRole('tab', { name: /experiment/i }).count(), 0, 'obsolete static experiment tab must be retired');
  const allIdentity = await canvas().getAttribute('data-input-identity');
  check(!!allIdentity, 'canvas must expose immutable live input identity');
  const initial = await mapping(currentGraph);
  const evidenceEdge = currentGraph.edges.find((edge) => initial.edgeIds.includes(edge.id) && edge.kind !== 'sameName' && edge.facts.some((fact) => fact.evidence.length));
  check(!!evidenceEdge, 'actual rendered all-repository relationships must expose source evidence');
  await inspectEdge(evidenceEdge);
  await sourceDownload(currentGraph);
  receipt.checks.push('real API node/edge identity, exact source request and diagram-as-code download');

  const capability = meta.capabilities.find((item) => item.repositories.length > 0);
  const scopedResponse = graphResponse((params) => params.get('cap') === capability.id);
  await page.getByLabel('Capability', { exact: true }).selectOption(capability.id);
  currentGraph = await (await scopedResponse).json(); await ready();
  check(await canvas().getAttribute('data-input-identity') !== allIdentity, 'changing capability must replace live model identity');
  await mapping(currentGraph);
  const scopedSource = await sourceDownload(currentGraph);
  check(scopedSource.includes(capability.id), 'canonical source must disclose actual selected capability');
  const selectedRepos = capability.repositories.filter((repo) => meta.revisions.filter((revision) => revision.repositoryId === repo).length > 1);
  check(selectedRepos.length > 0, 'fixtures must provide a real revision switch');
  const repo = selectedRepos[0], select = page.getByLabel(`Revision of ${repo}`, { exact: true });
  const beforeRevision = await select.inputValue();
  const revision = meta.revisions.find((item) => item.repositoryId === repo && item.commitSha !== beforeRevision);
  const beforeIdentity = await canvas().getAttribute('data-input-identity');
  const revisionResponse = graphResponse((params) => (params.get('view') ?? '').includes(`${repo}:${revision.commitSha}`));
  await select.selectOption(revision.commitSha);
  currentGraph = await (await revisionResponse).json(); await ready();
  check(await canvas().getAttribute('data-input-identity') !== beforeIdentity, 'actual selected revision must replace canvas identity');
  await mapping(currentGraph);
  check((await sourceDownload(currentGraph)).includes(revision.commitSha), 'source export must preserve exact selected revision');
  receipt.checks.push('real capability and revision selection');

  const drill = currentGraph.nodes.find((node) => node.drill && node.drill.level === 'repository');
  check(!!drill, 'selected graph must expose a repository drill');
  await sourceNode(drill.id).dispatchEvent('click');
  const repositoryResponse = graphResponse((params) => params.get('level') === 'repository' && params.get('focus') === drill.drill.focus);
  await page.getByRole('button', { name: /Open components/ }).click();
  currentGraph = await (await repositoryResponse).json(); await ready();
  check(new URL(page.url()).hash.includes('level=repository'), 'drill must update the shareable architecture hash');
  await page.getByRole('navigation', { name: 'Abstraction level', exact: true }).waitFor(); receipt.assertions++;
  await mapping(currentGraph);
  const component = currentGraph.nodes.find((node) => node.drill?.level === 'component');
  check(!!component, 'repository graph must expose a code component drill');
  await sourceNode(component.id).dispatchEvent('click');
  if (component.symbolKey) { await page.getByText(component.symbolKey, { exact: true }).first().waitFor(); receipt.assertions++; }
  check(await sourceNode(component.id).getAttribute('data-source-selected') === 'true', 'selected node must retain its exact source selection identity');
  const neighbors = new Set([component.id]);
  for (const edge of currentGraph.edges) if (edge.from === component.id || edge.to === component.id) { neighbors.add(edge.from); neighbors.add(edge.to); }
  if (currentGraph.nodes.some((node) => !neighbors.has(node.id))) {
    check(await canvas().locator('.react-flow__node[data-neighborhood="outside"]').count() > 0, 'node selection must highlight its neighborhood while retaining other source nodes');
  }
  const codeResponse = graphResponse((params) => params.get('level') === 'component' && params.get('focus') === component.drill.focus);
  await page.getByRole('button', { name: /Open code/ }).click();
  currentGraph = await (await codeResponse).json(); await ready();
  if (component.symbolKey) check((await page.locator('body').innerText()).includes(component.label), 'code drill must retain selected source component');
  await mapping(currentGraph);
  if (currentGraph.edges.some((edge) => edge.kind === 'declares')) {
    const declares = page.getByLabel('declares', { exact: true });
    equal(await declares.isChecked(), false, 'declaration edges must initially remain hidden');
    const declaredIds = currentGraph.edges.filter((edge) => edge.kind === 'declares').map((edge) => edge.id);
    check((await mapping(currentGraph)).edgeIds.every((id) => !declaredIds.includes(id)), 'hidden declarations must not be rendered');
    await declares.check();
    await sourceEdge(declaredIds[0]).waitFor({ state: 'attached' }); receipt.assertions++;
    await declares.uncheck();
    await sourceDownload(currentGraph);
    receipt.checks.push('declaration filter changes visibility while canonical facts remain intact');
  }
  const requestDrill = currentGraph.nodes.find((node) => node.drill?.level === 'requests');
  const requestFocus = requestDrill?.drill.focus ?? 'browser-check-no-unmatched-base';
  const requests = graphResponse((params) => params.get('level') === 'requests' && params.get('focus') === requestFocus);
  await page.goto(`${base}/#/architecture?${new URLSearchParams({ level: 'requests', focus: requestFocus })}`);
  const requestGraph = await (await requests).json();
  equal(requestGraph.level, 'requests', 'actual backend must serve the unmatched-requests abstraction');
  if (requestGraph.nodes.length) { await ready(); await mapping(requestGraph); }
  else { await page.getByText('Nothing to draw at this level for the selected scope.', { exact: true }).waitFor(); receipt.assertions++; }
  receipt.checks.push(requestGraph.nodes.length ? 'real requests view' : 'real empty requests view; fixture has no unmatched-base drill at selected code level');
  currentGraph = await navigate('landscape');
  check(currentGraph.nodes.some((node) => node.category === 'capability'), 'landscape must use actual capability nodes');
  await mapping(currentGraph);
  currentGraph = await navigate('system');
  receipt.checks.push('landscape, capability, repository and component levels with real drills');
  await groupJourney();

  for (const viewport of [{ width: 1440, height: 900 }, { width: 5120, height: 2160 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport); await ready();
    const fit = page.getByRole('button', { name: 'Fit View', exact: true });
    if (await fit.count()) await fit.click();
    const menu = page.getByRole('navigation', { name: 'Viewpoints', exact: true });
    check(await menu.isVisible(), `permanent viewpoints menu must be usable at ${viewport.width}`);
    check(await canvas().isVisible(), `live canvas must be visible at ${viewport.width}`);
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `page must not overflow horizontally at ${viewport.width}`);
    check((await canvas().boundingBox()).width > Math.min(250, viewport.width * .6), 'canvas must retain usable width');
    equal(await page.getByRole('switch', { name: 'Orthogonal', exact: true }).isChecked(), true, 'orthogonal routing must default on');
    equal(await page.getByRole('switch', { name: 'Animation', exact: true }).isChecked(), false, 'animation must default off');
    const ledger = page.getByRole('region', { name: 'Diagram legend', exact: true });
    check(await ledger.isVisible(), 'authoritative diagram ledger must be present');
    check(await page.locator('[data-ledger-key]').count() > 0, 'ledger must expose actual semantic colors');
    await page.getByRole('button', { name: 'Minimize ledger', exact: true }).click();
    equal(await ledger.count(), 0, 'ledger must minimize');
    await page.getByRole('button', { name: 'Expand ledger', exact: true }).click();
    await page.screenshot({ path: `${output}/architecture-${viewport.width}.png` });
    receipt.viewports.push(viewport);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  const favorite = page.getByRole('button', { name: /^Add .* to favorites$/ }).first();
  check(await favorite.count() > 0, 'at least one live viewpoint must be available to favorite');
  const favoriteName = (await favorite.getAttribute('aria-label')).replace(/^Add | to favorites$/g, '');
  await favorite.click();
  check((await page.getByRole('region', { name: 'Favorite viewpoints' }).innerText()).includes(favoriteName), 'favoriting must update permanent navigation');
  const favoriteReload = graphResponse();
  await page.reload(); currentGraph = await (await favoriteReload).json(); await ready();
  check((await page.getByRole('region', { name: 'Favorite viewpoints' }).innerText()).includes(favoriteName), 'favorites must persist for the same selected source');
  await page.keyboard.press('Escape');
  check(await page.getByRole('navigation', { name: 'Viewpoints' }).isVisible(), 'Escape must preserve pinned navigation');
  await page.getByRole('switch', { name: 'Orthogonal', exact: true }).uncheck();
  equal(await page.getByRole('switch', { name: 'Orthogonal', exact: true }).isChecked(), false, 'routing setting must be interactive');
  await page.getByRole('switch', { name: 'Orthogonal', exact: true }).check();
  await page.emulateMedia({ reducedMotion: 'reduce' });
  check(await page.getByRole('switch', { name: 'Animation', exact: true }).isDisabled(), 'reduced motion must disable animation');
  equal(await page.getByRole('switch', { name: 'Animation', exact: true }).isChecked(), false, 'reduced motion must keep animation off');
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  receipt.checks.push('1440/5120/390 layout, pinned viewpoints, favorites, ledger and settings');

  // Browser-boundary simulation: real backend graph with an altered label and a
  // synthetic metadata generation. This verifies live rebuild/stale-result behavior,
  // not filesystem watching or a real artifact import.
  const nextGeneration = meta.generation + 1000, latestGeneration = nextGeneration + 1;
  let simulatedGeneration = nextGeneration, releaseOld, markOldStarted;
  const oldStarted = new Promise((resolve) => { markOldStarted = resolve; });
  const oldGate = new Promise((resolve) => { releaseOld = resolve; });
  const obsoleteGraph = structuredClone(currentGraph); obsoleteGraph.nodes[0].label = 'Obsolete browser generation';
  const sentinel = `Browser reload generation ${latestGeneration}`;
  const generationGraph = structuredClone(currentGraph); generationGraph.nodes[0].label = sentinel;
  await page.route('**/api/meta', (route) => route.fulfill({ json: { ...meta, generation: simulatedGeneration } }));
  await page.route('**/api/generation', (route) => route.fulfill({ json: { generation: simulatedGeneration } }));
  const obsoleteRequested = page.waitForRequest((request) => {
    const url = new URL(request.url());
    return url.pathname === '/api/architecture' && url.searchParams.get('g') === String(nextGeneration);
  });
  await page.route('**/api/architecture?*', async (route) => {
    const generation = new URL(route.request().url()).searchParams.get('g');
    if (generation === String(nextGeneration)) {
      markOldStarted(); await oldGate;
      await route.fulfill({ json: obsoleteGraph }).catch(() => undefined); // aborted requests are valid cancellation
    } else if (generation === String(latestGeneration)) await route.fulfill({ json: generationGraph });
    else await route.continue();
  });
  await obsoleteRequested; await oldStarted;
  check(await canvas().count() === 0 || await canvas().getAttribute('data-status') !== 'ready', 'in-flight new generation must hide the obsolete graph and provenance');
  simulatedGeneration = latestGeneration;
  const regenerated = graphResponse((params) => params.get('g') === String(latestGeneration));
  await regenerated; await ready();
  await canvas().getByText(sentinel, { exact: true }).first().waitFor(); receipt.assertions++;
  releaseOld();
  await page.waitForTimeout(150); // allow the deliberately delayed obsolete response to reach React
  check((await canvas().getAttribute('data-input-identity')).includes(`g=${latestGeneration}|`), 'latest analysis generation must own the rendered model after obsolete response completes');
  check(Number(await canvas().getAttribute('data-generation')) > 0, 'live worker generation must be nonzero');
  equal(await canvas().getByText('Obsolete browser generation', { exact: true }).count(), 0, 'obsolete response must never replace the newer rendered model');
  check((await sourceDownload(generationGraph)).includes(sentinel), 'runtime generation must change exported source without rebuilding the app');
  receipt.simulated.push('generation polling, changed graph and delayed obsolete-response race at browser boundary; filesystem watcher not exercised');
  await page.unroute('**/api/meta'); await page.unroute('**/api/generation'); await page.unroute('**/api/architecture?*');
  await page.addInitScript(() => {
    let firstModelWorker = true;
    window.Worker = new Proxy(window.Worker, {
      construct(Target, args, NewTarget) {
        if (firstModelWorker && String(args[0]).includes('live-model.worker')) {
          firstModelWorker = false;
          throw new Error('Simulated transient model worker construction failure');
        }
        return Reflect.construct(Target, args, NewTarget);
      },
    });
  });
  const recoverGraph = graphResponse();
  await page.reload(); currentGraph = await (await recoverGraph).json();
  await page.getByRole('alert').getByText('Simulated transient model worker construction failure', { exact: true }).waitFor(); receipt.assertions++;
  equal(await canvas().getAttribute('data-status'), 'error', 'worker construction failure must be visible');
  equal(await canvas().locator('.react-flow__node').count(), 0, 'failed compilation must never expose obsolete graph nodes');
  await page.getByRole('button', { name: 'Retry diagram', exact: true }).click();
  await ready(); await mapping(currentGraph);
  receipt.simulated.push('first model-worker constructor throws; visible error and successful Retry diagram verified; native routing workers untouched');
  equal(errors, [], 'browser must have no uncaught exceptions');
  equal(forbidden, [], 'production architecture must not fetch a static LikeC4 fixture, iframe or third-party resource');
  check(receipt.assertions > 40 && receipt.viewports.length === 3, 'browser gate must execute nonzero substantive assertions at all required sizes');
  receipt.passed = true;
} catch (error) {
  receipt.error = error.stack ?? String(error);
  if (page) {
    await page.screenshot({ path: `${output}/failure.png` }).catch(() => undefined);
    console.error(await page.locator('body').innerText().catch(() => 'No page content'));
  }
  throw error;
} finally {
  await writeFile(`${output}/results.json`, `${JSON.stringify(receipt, null, 2)}\n`);
  console.log(JSON.stringify(receipt, null, 2));
  await browser.close();
}
