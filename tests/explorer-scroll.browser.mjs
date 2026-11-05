// Real served API/production UI acceptance; the optional fault cases are marked separately.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import { records } from '../scripts/benchmarks/issue28-generate.mjs';

const [base = 'http://localhost:5127', mode = 'files'] = process.argv.slice(2);
assert.equal(new URL(base).hostname, 'localhost');
assert.ok(['files', 'postgres'].includes(mode));
const output = `.sandbox-cache/navigation83/${mode}`;
await mkdir(output, { recursive: true });
const expected = [...records({ count: 1000, repo: 'bench-0', revision: 1, seed: 28 })]
  .filter(row => row.kind === 'node')
  .sort((a, b) => {
    const left = a.name.toUpperCase(), right = b.name.toUpperCase();
    return left < right ? -1 : left > right ? 1 : a.symbolKey < b.symbolKey ? -1 : a.symbolKey > b.symbolKey ? 1 : 0;
  }).map(row => row.symbolKey);
const receipt = {
  mode, base, at: new Date().toISOString(), mockedResponses: false, assertions: [],
  workload: { rows: 1000, seed: 28 },
  thresholds: { domNodes: 25000, heapBytes: 128 * 1024 * 1024, longestTaskMs: 200 },
  notTested: ['Ten-million-row browser retention', 'Private corpus', 'Live model calls'],
};
const check = (condition, message) => { assert.ok(condition, message); receipt.assertions.push(message); };
const browser = await chromium.launch({ args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 }, colorScheme: 'dark' });
  const errors = [], searchRequests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname === '/api/search') searchRequests.push(url);
  });
  await page.addInitScript(() => {
    window.__scrollLongTasks = [];
    new PerformanceObserver(list => {
      for (const entry of list.getEntries()) window.__scrollLongTasks.push({ start: entry.startTime, duration: entry.duration });
    }).observe({ type: 'longtask', buffered: true });
  });
  await page.goto(`${base}/#/explore`);
  const root = page.getByLabel('Symbol results', { exact: true });
  await root.waitFor();
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
  const appendStart = await page.evaluate(() => performance.now());
  for (let round = 0; round < 40; round++) {
    const count = await root.locator('[data-symbol-key]').count();
    if (count === expected.length) break;
    await root.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await page.waitForFunction(previous => document.querySelectorAll('[data-symbol-key]').length > previous, count);
  }
  const actual = await root.locator('[data-symbol-key]').evaluateAll(elements => elements.map(element => element.dataset.symbolKey));
  assert.deepEqual(actual, expected);
  check(new Set(actual).size === 1000, 'scrolling reaches all 1000 exact unique symbols in stable duplicate-name order');
  await root.getByText(/Loaded 1000/).waitFor();
  check(searchRequests.every(url => !!url.searchParams.get('snapshot')), 'first and all subsequent requests explicitly pin the snapshot');
  check(new Set(searchRequests.map(url => url.searchParams.get('cursor') ?? 'first')).size === searchRequests.length,
    'one request per cursor despite repeated observer notifications');
  const metrics = await page.evaluate(start => ({
    domNodes: document.querySelectorAll('*').length,
    heapBytes: performance.memory?.usedJSHeapSize,
    longestTaskMs: Math.max(0, ...window.__scrollLongTasks.filter(task => task.start >= start).map(task => task.duration)),
  }), appendStart);
  receipt.metrics = metrics;
  check(metrics.domNodes < receipt.thresholds.domNodes, 'full-scroll DOM stays within frozen qualification budget');
  check(Number.isFinite(metrics.heapBytes) && metrics.heapBytes < receipt.thresholds.heapBytes, 'full-scroll heap stays within frozen qualification budget');
  check(metrics.longestTaskMs <= receipt.thresholds.longestTaskMs, 'append tasks stay within frozen responsiveness budget');
  const search = page.getByLabel('Search symbols', { exact: true });
  await search.fill('definitely-no-such-symbol');
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length === 0);
  await root.getByText(/No (symbols match|matching symbols)/).waitFor();
  await search.fill('Repeated');
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
  const filtered = await root.locator('[data-symbol-key]').evaluateAll(elements => elements.map(element => element.textContent));
  check(filtered.every(text => text.includes('Repeated')), 'filter replacement resets results without stale rows');
  check(searchRequests.filter(url => url.searchParams.get('q') === 'Repeated').every((url, i) => i > 0 || !url.searchParams.has('cursor')),
    'new filter starts without the old continuation');
  await page.screenshot({ path: `${output}/dark-desktop.png` });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ colorScheme: 'light' });
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'mobile viewport has no horizontal overflow');
  await page.screenshot({ path: `${output}/light-mobile.png` });
  assert.deepEqual(errors, []);
  receipt.searchRequests = searchRequests.map(url => ({ q: url.searchParams.get('q'), snapshot: url.searchParams.get('snapshot'), continuation: url.searchParams.has('cursor') }));
  receipt.assertions.push('no uncaught browser errors');
  console.log(JSON.stringify({ mode, assertions: receipt.assertions.length, metrics }));
} finally {
  await writeFile(`${output}/receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  await browser.close();
}
