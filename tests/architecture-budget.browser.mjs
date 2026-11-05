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
  samples: [], assertions: [], notTested: ['Unavailable original private freezing component', 'Production hardware and release assurance'],
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
  assert.ok(budget.nodeBudget <= 400 && budget.edgeBudget <= 800);
  assert.ok(graph.nodes.length <= budget.nodeBudget && graph.edges.length <= budget.edgeBudget);
}

async function waitReady(page) {
  await page.waitForFunction(() => {
    const canvas = document.querySelector('.live-architecture-canvas');
    return canvas?.getAttribute('data-canvas-ready') === 'true' || canvas?.getAttribute('data-status') === 'error';
  }, null, { timeout: 35000 });
  const state = await page.locator('.live-architecture-canvas').evaluate(element => ({
    ready: element.getAttribute('data-canvas-ready'), identity: element.getAttribute('data-canvas-identity'),
    workerStatus: element.getAttribute('data-status'), text: element.textContent?.slice(0, 1000),
  }));
  assert.equal(state.ready, 'true', JSON.stringify(state));
  assert.ok(state.identity);
  return state.identity;
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
      assert.deepEqual(errors, []);
      receipt.samples.push({
        index: index + 1, identity, ...measurement, budget: graph.budget,
        responseBytes: Number(graphResponse.headers()['content-length']) || Buffer.byteLength(JSON.stringify(graph)),
        responseTiming: graphResponse.request().timing(),
      });
      if (index === 0) await page.screenshot({ path: `${output}/maximum-budget.png`, fullPage: true });
      await context.close();
    }
    const sorted = receipt.samples.map(sample => sample.readyMs).sort((a, b) => a - b);
    receipt.summary = {
      samples: sorted.length, percentileMethod: 'nearest rank; p95=19th of20, p99=maximum',
      p95Ms: sorted[18], p99Ms: sorted[19],
      longestTaskMs: Math.max(...receipt.samples.map(sample => sample.longestTaskMs)),
    };
    assert.ok(receipt.summary.p95Ms <= 1500, `p95 exceeded: ${receipt.summary.p95Ms}ms`);
    assert.ok(receipt.summary.p99Ms <= 3000, `p99 exceeded: ${receipt.summary.p99Ms}ms`);
    assert.ok(receipt.summary.longestTaskMs <= 200, `main-thread task exceeded: ${receipt.summary.longestTaskMs}ms`);
    receipt.assertions.push('Maximum enabled budget passes actual compound canvas readiness in20 fresh contexts', 'All node/edge counts are consistent', 'Frozen p95/p99/long-task SLO passes');
  }
  console.log(JSON.stringify({ mode, assertions: receipt.assertions, summary: receipt.summary, failure: receipt.observedFailure }));
} catch (error) {
  receipt.error = String(error.stack);
  throw error;
} finally {
  await writeFile(`${output}/receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  await browser.close();
}
