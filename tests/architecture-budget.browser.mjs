import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import os from 'node:os';
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';

const [base = 'http://localhost:5129', mode = 'candidate', head] = process.argv.slice(2);
assert.equal(new URL(base).hostname, 'localhost');
assert.ok(['baseline', 'candidate'].includes(mode));
assert.match(head ?? '', /^[a-f0-9]{40}$/);
const output = `.sandbox-cache/graph82/${mode}`;
await mkdir(output, { recursive: true });
const receipt = {
  head, mode, at: new Date().toISOString(), apiResponsesMocked: false,
  fixture: JSON.parse(await readFile('.sandbox-cache/issue28-bench/issue82-dense/manifest.json', 'utf8')),
  environment: {
    platform: os.platform(), release: os.release(), architecture: os.arch(),
    cpu: os.cpus()[0]?.model, logicalCpus: os.cpus().length, memoryBytes: os.totalmem(),
    protocol: '20 fresh contexts and workers; one shared browser process; no intentional CPU throttling',
    viewport: { width: 1500, height: 900 }, cpuThrottleRate: 1,
  },
  qualification: 'inconclusive: non-reference host and20-context engineering protocol, not the issue82/63 three-session qualification',
  samples: [], assertions: [], notTested: ['Unavailable original private freezing component', 'Native Linuxamd64 reference envelope', 'Production hardware and release assurance'],
};
const browser = await chromium.launch({ args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
receipt.environment.browser = browser.version();
const route = `${base}/#/architecture?level=repository&focus=dense82`;

function assertBudget(graph) {
  const budget = graph.budget;
  assert.ok(budget, 'API must disclose budgeting');
  assert.equal(graph.nodes.length, budget.shownNodes);
  assert.equal(graph.edges.length, budget.shownEdges);
  assert.ok(budget.shownNodes <= budget.matchedNodes && budget.matchedNodes <= budget.totalNodes);
  assert.ok(budget.shownEdges <= budget.matchedEdges && budget.matchedEdges <= budget.totalEdges);
  assert.ok(budget.nodeBudget <= 100 && budget.edgeBudget <= 100);
  assert.ok(graph.nodes.length <= budget.nodeBudget && graph.edges.length <= budget.edgeBudget);
}

async function waitReady(page, previousIdentity = null) {
  await page.waitForFunction(previous => {
    const canvas = document.querySelector('.live-architecture-canvas');
    return (canvas?.getAttribute('data-canvas-ready') === 'true' &&
      canvas.getAttribute('data-canvas-identity') !== previous) || canvas?.getAttribute('data-status') === 'error';
  }, previousIdentity, { timeout: 35000 });
  const state = await page.locator('.live-architecture-canvas').evaluate(element => ({
    ready: element.getAttribute('data-canvas-ready'), identity: element.getAttribute('data-canvas-identity'),
    workerStatus: element.getAttribute('data-status'), text: element.textContent?.slice(0, 1000),
  }));
  assert.equal(state.ready, 'true', JSON.stringify(state));
  assert.ok(state.identity);
  return state.identity;
}

async function graphRequest(page, query) {
  return page.evaluate(async query => {
    const response = await fetch('/api/architecture?' + new URLSearchParams(query));
    if (!response.ok) throw new Error(`Census request failed: ${response.status}`);
    return response.json();
  }, query);
}

async function census(page) {
  const system = await graphRequest(page, { level: 'system' });
  assert.equal(system.budget.nodesTruncated, false, 'Repository census must not silently omit roots');
  const repositories = system.nodes.filter(node => node.drill?.level === 'repository');
  const scopes = [], components = new Map();
  for (const repository of repositories) {
    const query = { level: 'repository', focus: repository.drill.focus };
    const graph = await graphRequest(page, query);
    scopes.push({ ...query, nodes: graph.budget.totalNodes, edges: graph.budget.totalEdges });
    if (graph.budget.nodesTruncated) {
      assert.equal(query.focus, 'dense82', 'Only the documented single-function synthetic repository may elide census members');
    }
    for (const node of graph.nodes.filter(node => node.drill?.level === 'component')) {
      components.set(node.drill.focus, node.drill);
    }
  }
  for (const component of components.values()) {
    const graph = await graphRequest(page, component);
    scopes.push({ ...component, nodes: graph.budget.totalNodes, edges: graph.budget.totalEdges });
  }
  return {
    scopes,
    newlyNodeTruncated: scopes.filter(scope => scope.nodes > 100 && scope.nodes <= 400),
    omittedDenseComponents: 'One function per module; at most24outgoing+24coreincoming+8peripheryincoming+itself=57nodes, below100; excluded from exhaustive API enumeration',
    largest: [...scopes].sort((a, b) => b.nodes - a.nodes || b.edges - a.edges)[0],
    largestComponent: scopes.filter(scope => scope.level === 'component').sort((a, b) => b.nodes - a.nodes || b.edges - a.edges)[0],
  };
}

async function apply(page, previousIdentity, action) {
  const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/architecture');
  await action();
  const graph = await (await response).json();
  assertBudget(graph);
  const identity = await waitReady(page, previousIdentity);
  assert.equal(Number(await page.locator('.live-architecture-canvas').getAttribute('data-canvas-leaf-count')), graph.budget.shownNodes);
  return { graph, identity };
}

async function recovery(page) {
  const component = receipt.census.largestComponent;
  assert.ok(component);
  const largestResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/architecture');
  await page.goto(`${base}/#/architecture?${new URLSearchParams({ level: component.level, focus: component.focus })}`);
  const largestGraph = await (await largestResponse).json();
  await waitReady(page);
  assertBudget(largestGraph);
  assert.equal(Number(await page.locator('.live-architecture-canvas').getAttribute('data-canvas-leaf-count')), largestGraph.budget.shownNodes);
  receipt.largestComponentCheck = { scope: component, budget: largestGraph.budget, ready: true };
  const firstResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/architecture');
  await page.goto(route);
  const first = await (await firstResponse).json();
  let identity = await waitReady(page);
  const omitted = page.getByLabel('Omitted architecture elements', { exact: true });
  const firstOmitted = first.omitted.nodes[0];
  assert.ok(firstOmitted);
  const pin = omitted.getByRole('button', { name: `Pin ${firstOmitted.label}`, exact: true });
  await page.getByLabel('Node budget', { exact: true }).fill('0');
  await pin.focus();
  await pin.press('Enter');
  assert.equal(await pin.innerText(), 'Pin element', 'Rejected draft must not label a node pinned');
  assert.deepEqual(JSON.parse(await page.getByRole('region', { name: 'Architecture graph', exact: true }).getAttribute('data-included-node-ids')), []);
  await page.getByLabel('Node budget', { exact: true }).fill('100');
  const pinned = await apply(page, identity, () => pin.press('Enter'));
  assert.ok(pinned.graph.budget.pinned.includes(firstOmitted.id));
  assert.ok(pinned.graph.nodes.some(node => node.id === firstOmitted.id));
  identity = pinned.identity;
  const shown = page.getByLabel('Shown architecture elements', { exact: true });
  const nodeButton = shown.locator('[data-architecture-node-id]').first();
  const selected = await nodeButton.getAttribute('data-architecture-node-id');
  await nodeButton.focus();
  await nodeButton.press('Enter');
  await page.getByLabel('Find elements', { exact: true }).fill('module2300');
  let release, held;
  const intercepted = new Promise(resolve => { held = resolve; });
  const resume = new Promise(resolve => { release = resolve; });
  await page.route('**/api/architecture?**', async request => {
    if (new URL(request.request().url()).searchParams.get('q') === 'module2300') {
      held();
      await resume;
    }
    await request.continue();
  });
  const queryResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/architecture');
  await page.getByRole('button', { name: 'Apply graph filters', exact: true }).click();
  await intercepted;
  assert.equal(await page.locator('.live-architecture-canvas[data-canvas-ready=true]').count(), 0, 'Apply must clear the old readiness synchronously');
  release();
  const queried = await (await queryResponse).json();
  identity = await waitReady(page, identity);
  assertBudget(queried);
  assert.ok(queried.nodes.some(node => node.id === selected));
  assert.ok(queried.budget.pinned.includes(selected));
  assert.ok(queried.nodes.some(node => node.id.includes('module2300.ts')));
  await page.unroute('**/api/architecture?**');
  ({ identity } = await apply(page, identity, () => page.getByRole('button', { name: 'Reset filters', exact: true }).click()));
  await page.getByRole('checkbox', { name: 'Call', exact: true }).uncheck();
  const filtered = await apply(page, identity, () => page.getByRole('button', { name: 'Apply graph filters', exact: true }).click());
  assert.ok(filtered.graph.filters.hideKinds.includes('call'));
  assert.equal(filtered.graph.budget.matchedEdges, 0);
  assert.equal(filtered.graph.edges.length, 0);
  ({ identity } = await apply(page, filtered.identity, () => page.getByRole('button', { name: 'Reset filters', exact: true }).click()));
  const drill = page.getByLabel('Shown architecture elements', { exact: true }).getByRole('button', { name: /^Open component$/ }).first();
  await drill.focus();
  const drilled = await apply(page, identity, () => drill.press('Enter'));
  assert.ok(page.url().includes('level=component'));
  assert.equal(drilled.graph.level, 'component');
  receipt.assertions.push('Invalid draft does not mark unapplied pins', 'Keyboard omitted-pin and shown selection/drill recover bounded items',
    'Query/kind filters run before layout, selected pins survive', 'Apply clears stale readiness while an explicitly delayed real request is pending', 'Reset restores bounded defaults');
}

try {
  if (mode === 'baseline') {
    const context = await browser.newContext({ viewport: receipt.environment.viewport });
    const page = await context.newPage();
    await page.route('**/api/architecture?**', request => {
      const url = new URL(request.request().url());
      url.searchParams.set('nodeBudget', '2000');
      return request.continue({ url: url.href });
    });
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/architecture');
    await page.goto(route);
    const graph = await (await response).json();
    assert.equal(graph.budget.totalNodes, 2400);
    assert.equal(graph.nodes.length, 2000);
    assert.ok(graph.edges.length > 8000);
    await page.waitForFunction(() => document.querySelector('.live-architecture-canvas')?.getAttribute('data-status') === 'error', null, { timeout: 35000 });
    const error = await page.locator('.live-architecture-canvas').innerText();
    assert.match(error, /Graph exceeds (node\/edge limits|evidence byte limit)/i);
    receipt.observedFailure = { nodes: graph.nodes.length, edges: graph.edges.length, error };
    receipt.assertions.push('Real pre-fix API maximum produces actual worker limit error, not merely a new edge-cap violation');
    await page.screenshot({ path: `${output}/worker-error.png`, fullPage: true });
    await context.close();
  } else {
    const censusContext = await browser.newContext({ viewport: receipt.environment.viewport });
    const censusPage = await censusContext.newPage();
    await censusPage.goto(`${base}/#/explore`);
    await censusPage.getByLabel('Search symbols', { exact: true }).waitFor();
    receipt.census = await census(censusPage);
    assert.equal(receipt.census.largest.nodes, 2400);
    await censusContext.close();
    for (let index = 0; index < 20; index++) {
      const context = await browser.newContext({ viewport: receipt.environment.viewport });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      const cdp = await context.newCDPSession(page);
      await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });
      await page.addInitScript(() => {
        window.__graphTasks = [];
        new PerformanceObserver(list => {
          window.__graphTasks.push(...list.getEntries().map(entry => ({ startTime: entry.startTime, duration: entry.duration })));
        }).observe({ type: 'longtask', buffered: true });
      });
      const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/architecture');
      await page.goto(route);
      const graphResponse = await response;
      const graph = await graphResponse.json();
      assertBudget(graph);
      assert.equal(graph.budget.totalNodes, 2400);
      const identity = await waitReady(page);
      assert.equal(await page.getByRole('region', { name: 'Architecture graph', exact: true }).getAttribute('data-input-identity'), identity);
      await page.waitForTimeout(500);
      const measurement = await page.evaluate(identity => {
        const mark = performance.getEntriesByName('ce:canvas-ready').find(entry => entry.detail?.identity === identity);
        const tasks = window.__graphTasks.filter(task => task.startTime <= (mark?.startTime ?? Infinity) + 500);
        return {
          readyMs: mark?.startTime, longestTaskMs: Math.max(0, ...tasks.map(task => task.duration)), tasks,
          domNodes: document.querySelectorAll('*').length,
          renderedLeaves: Number(document.querySelector('.live-architecture-canvas')?.getAttribute('data-canvas-leaf-count')),
        };
      }, identity);
      assert.ok(Number.isFinite(measurement.readyMs), 'Readiness must emit a mark for the exact current input');
      assert.equal(measurement.renderedLeaves, graph.budget.shownNodes);
      measurement.renderedCompoundNodes = await page.locator('.react-flow__node').count() - measurement.renderedLeaves;
      assert.ok(measurement.renderedCompoundNodes >= 1);
      await page.getByLabel('Edge budget', { exact: true }).fill('99');
      const applyButton = page.getByRole('button', { name: 'Apply graph filters', exact: true });
      await applyButton.evaluate(button => button.addEventListener('click', () => performance.mark('ce:apply-start'), { once: true }));
      const applied = await apply(page, identity, () => applyButton.click());
      assert.equal(applied.graph.budget.shownEdges, 99);
      assert.notEqual(applied.identity, identity);
      await page.waitForTimeout(500);
      measurement.apply = await page.evaluate(identity => {
        const start = performance.getEntriesByName('ce:apply-start').at(-1).startTime;
        const end = performance.getEntriesByName('ce:canvas-ready').find(entry => entry.detail?.identity === identity).startTime;
        return {
          identity, readyMs: end - start,
          longestTaskMs: Math.max(0, ...window.__graphTasks.filter(task => task.startTime >= start && task.startTime <= end + 500).map(task => task.duration)),
        };
      }, applied.identity);
      assert.deepEqual(errors, []);
      receipt.samples.push({
        index: index + 1, identity, ...measurement, budget: graph.budget,
        responseBytes: Number(graphResponse.headers()['content-length']) || Buffer.byteLength(JSON.stringify(graph)),
        responseTiming: graphResponse.request().timing(),
      });
      if (index === 0) await page.screenshot({ path: `${output}/maximum-budget.png`, fullPage: true });
      await context.close();
    }
    const recoveryContext = await browser.newContext({ viewport: receipt.environment.viewport });
    await recovery(await recoveryContext.newPage());
    await recoveryContext.close();
    const sorted = receipt.samples.map(sample => sample.readyMs).sort((a, b) => a - b);
    const sortedApply = receipt.samples.map(sample => sample.apply.readyMs).sort((a, b) => a - b);
    receipt.summary = {
      samples: sorted.length, percentileMethod: 'nearest rank; p95=19th of20, p99=maximum',
      p95Ms: sorted[18], p99Ms: sorted[19],
      longestTaskMs: Math.max(...receipt.samples.map(sample => sample.longestTaskMs)),
      applyP95Ms: sortedApply[18], applyP99Ms: sortedApply[19],
      applyLongestTaskMs: Math.max(...receipt.samples.map(sample => sample.apply.longestTaskMs)),
    };
    assert.ok(receipt.summary.p95Ms <= 1500, `p95 exceeded: ${receipt.summary.p95Ms}ms`);
    assert.ok(receipt.summary.p99Ms <= 3000, `p99 exceeded: ${receipt.summary.p99Ms}ms`);
    assert.ok(receipt.summary.longestTaskMs <= 200, `main-thread task exceeded: ${receipt.summary.longestTaskMs}ms`);
    assert.ok(receipt.summary.applyP95Ms <= 1500 && receipt.summary.applyP99Ms <= 3000 && receipt.summary.applyLongestTaskMs <= 200, 'Apply engineering timing exceeded frozen thresholds');
    receipt.assertions.push('Maximum enabled budget passes actual compound canvas readiness in20 fresh contexts', 'All node/edge counts are consistent',
      'Engineering p95/p99/long-task thresholds pass; reference SLO remains inconclusive');
  }
  console.log(JSON.stringify({ mode, assertions: receipt.assertions, summary: receipt.summary, failure: receipt.observedFailure }));
} catch (error) {
  receipt.error = String(error.stack);
  throw error;
} finally {
  await writeFile(`${output}/receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  await browser.close();
}
