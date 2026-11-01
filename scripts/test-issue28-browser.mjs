// Actual served PostgreSQL-mode journey. Requires the seeded issue28 corpus (>=1000 nodes).
// Run through scripts/sandbox with the pinned BROWSER_IMAGE; no intercepted or fabricated responses.
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';

const base = process.argv[2] ?? 'http://localhost:5116';
assert.equal(new URL(base).hostname, 'localhost', 'loopback Host/Origin required');
const output = '.sandbox-cache/issue28-browser';
await mkdir(output, { recursive: true });
const images = await readFile(new URL('../config/runtime/images.env', import.meta.url), 'utf8');
const receipt = {
  base, collectedAt: new Date().toISOString(), browserImage: images.match(/^BROWSER_IMAGE=(.+)$/m)?.[1],
  assertions: [], servedAssets: [], screenshots: [], apiWrites: [], modelCalls: 0,
  browserSettings: { colorScheme: 'dark', viewports: [[1920, 1080], [5120, 2160]] },
  mockedResponses: false, abortedRequests: 0,
  notTested: ['Model inference', 'Persistent writes', 'Private repositories', 'Ten-million-node scale', 'Cross-publication pagination (covered by PostgreSQL tests)'],
};
const check = (condition, message) => { assert.ok(condition, message); receipt.assertions.push(message); };
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
let page;
try {
  page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, colorScheme: 'dark' });
  page.setDefaultTimeout(15000);
  // Observe native cleanup calls while preserving the actual AbortController implementation.
  await page.addInitScript(() => {
    window.__issue28AbortCalls = 0;
    const original = AbortController.prototype.abort;
    AbortController.prototype.abort = function (...args) {
      window.__issue28AbortCalls++;
      return original.apply(this, args);
    };
  });
  const errors = [], requests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    const url = new URL(request.url());
    if (!url.pathname.startsWith('/api/')) return;
    requests.push({ path: url.pathname, query: url.searchParams });
    if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) receipt.apiWrites.push(`${request.method()} ${url.pathname}`);
    if (/chat|explanations|semantic/.test(url.pathname)) receipt.modelCalls++;
  });
  page.on('requestfailed', request => { if (/ABORTED/i.test(request.failure()?.errorText ?? '')) receipt.abortedRequests++; });
  const responseFor = (path, predicate = () => true) => page.waitForResponse(response => {
    const url = new URL(response.url());
    return url.pathname === path && response.status() === 200 && predicate(url.searchParams);
  });
  const initialResponse = responseFor('/api/search', query => !query.has('cursor'));
  await page.goto(`${base}/#/explore`);
  await page.getByRole('heading', { name: 'Explore symbols', exact: true }).waitFor();
  const first = await (await initialResponse).json();
  check(first.items.length === 50, 'actual initial search returns exactly 50 bounded rows');
  check(first.items.every(item => item.repositoryId === 'bench-0'), 'journey uses the explicitly seeded synthetic corpus');
  check(typeof first.snapshotId === 'string' && !!first.nextCursor, 'initial page carries an immutable snapshot and continuation');
  receipt.snapshotId = first.snapshotId;
  const list = () => page.getByRole('heading', { name: 'Explore symbols', exact: true }).locator('..');
  const cards = () => list().locator('div[aria-busy] > button');
  await cards().nth(49).waitFor();
  check(await cards().count() === 50, 'the DOM holds only one search page');
  const migrationStatus = page.getByRole('heading', { name: 'Migration status', exact: true });
  const statusBounds = await migrationStatus.boundingBox();
  check(statusBounds && statusBounds.y >= 0 && statusBounds.y + statusBounds.height <= 1080, 'migration availability is visible in the initial viewport above the result list');
  const firstNames = await cards().allTextContents();

  const nextResponse = responseFor('/api/search', query => query.has('cursor'));
  await list().getByRole('button', { name: 'Next', exact: true }).click();
  const second = await (await nextResponse).json();
  await list().getByText(/Page 2 · 50 rows/).waitFor();
  await list().locator('div[aria-busy="false"]').waitFor();
  check(second.snapshotId === first.snapshotId, 'next page stays bound to the original snapshot');
  check(new Set([...first.items, ...second.items].map(item => item.key)).size === 100, 'repeated names do not duplicate or skip unique symbol keys across pages');
  check(await cards().count() === 50, 'next replaces the page rather than accumulating rows');

  const previousResponse = responseFor('/api/search', query => !query.has('cursor'));
  await list().getByRole('button', { name: 'Previous', exact: true }).click();
  await previousResponse;
  await list().locator('div[aria-busy="false"]').waitFor();
  check(JSON.stringify(await cards().allTextContents()) === JSON.stringify(firstNames), 'previous reproduces the same stable first page');

  const laterPage = responseFor('/api/search', query => query.has('cursor'));
  await list().getByRole('button', { name: 'Next', exact: true }).click(); await laterPage;
  const filteredResponse = responseFor('/api/search', query => query.get('q') === 'get.foo');
  await page.getByLabel('Search symbols', { exact: true }).fill('get.foo');
  const filtered = await (await filteredResponse).json();
  await list().locator('div[aria-busy="false"]').waitFor();
  check(filtered.items.length > 0 && filtered.items.every(item => item.name === 'get.foo'), 'literal punctuation search returns actual matching symbols');
  check((await list().getByRole('button', { name: 'Previous', exact: true }).isDisabled()), 'changing filters resets the continuation history');
  const filteredRequest = requests.findLast(request => request.path === '/api/search' && request.query.get('q') === 'get.foo');
  check(filteredRequest && !filteredRequest.query.has('cursor'), 'a changed query never reuses a cursor from the previous filter');

  const absentResponse = responseFor('/api/search', query => query.get('kind') === 'MissingKind');
  await page.getByLabel('Node kind', { exact: true }).fill('MissingKind'); await absentResponse;
  await page.getByText('No matching symbols in this selection.', { exact: true }).waitFor();
  const restoredResponse = responseFor('/api/search', query => query.get('kind') === 'Function');
  await page.getByLabel('Node kind', { exact: true }).fill('Function'); await restoredResponse;
  await list().locator('div[aria-busy="false"]').waitFor();
  check(await cards().count() > 0, 'successive filter requests settle on the current selection without stale empty results');
  receipt.abortControllerCleanupCalls = await page.evaluate(() => window.__issue28AbortCalls);
  check(receipt.abortControllerCleanupCalls >= 2, 'filter replacement invokes native AbortController cleanup without intercepting API responses');
  check(requests.filter(request => request.path === '/api/search').every(request => request.query.get('snapshot') === first.snapshotId), 'every UI search explicitly carries its immutable snapshot');

  const hubResponse = responseFor('/api/search', query => query.get('q') === 'symbol0#value');
  await page.getByLabel('Search symbols', { exact: true }).fill('symbol0#value');
  const hubs = await (await hubResponse).json();
  check(hubs.items.length === 1, 'exact synthetic hub identity can be located without fetching the graph');
  await list().locator('div[aria-busy="false"]').waitFor();
  const edgeResponse = responseFor('/api/symbol/edges', query => query.get('direction') === 'outgoing' && !query.has('cursor'));
  await cards().first().click();
  const edges = await (await edgeResponse).json();
  const inspector = page.getByRole('complementary', { name: 'Symbol detail' });
  await inspector.waitFor();
  check(edges.items.length === 50 && !!edges.nextCursor, 'high-degree hub adjacency is server paginated');
  const outgoing = inspector.getByRole('heading', { name: 'Outgoing connections', exact: true }).locator('..');
  await outgoing.getByRole('listitem').nth(49).waitFor();
  const nextEdgesResponse = responseFor('/api/symbol/edges', query => query.get('direction') === 'outgoing' && query.has('cursor'));
  await outgoing.getByRole('button', { name: 'Next', exact: true }).click();
  const nextEdges = await (await nextEdgesResponse).json();
  await outgoing.getByText(/Page 2 · 50 rows/).waitFor();
  check(new Set([...edges.items, ...nextEdges.items].map(edge => `${edge.artifactId}/${edge.localId}`)).size === 100, 'hub edge pages have stable unique occurrence identities');
  check(await outgoing.getByRole('listitem').count() === 50, 'the inspector retains only one bounded edge page');
  const evidence = inspector.getByRole('heading', { name: 'Evidence', exact: true }).locator('..');
  await evidence.getByRole('listitem').first().waitFor();
  check(await evidence.getByRole('listitem').count() === 1, 'evidence is independently fetched as a bounded projection');
  await inspector.getByRole('button', { name: 'Close', exact: true }).click();

  await page.getByRole('navigation', { name: 'Database views' }).getByRole('button', { name: 'Artifacts', exact: true }).click();
  await page.getByRole('heading', { name: 'Artifacts', exact: true }).waitFor();
  await page.getByText('bench-0 · @bench/bench-0', { exact: true }).waitFor();
  check(true, 'artifact metadata is available through the database-native tab');
  const calculationsResponse = responseFor('/api/calculations');
  await page.getByRole('navigation', { name: 'Database views' }).getByRole('button', { name: 'Calculations', exact: true }).click();
  const calculations = await (await calculationsResponse).json();
  check(calculations.items.length === 0, 'synthetic corpus correctly shows no invented calculations');
  await page.getByText('Architecture, integrations, glossary, changes, binding review and model features are not yet available', { exact: false }).waitFor();
  check(true, 'remaining migration scope is clearly disclosed');

  const latestResponse = responseFor('/api/meta');
  await page.getByRole('button', { name: 'Load latest snapshot', exact: true }).click(); await latestResponse;
  check(true, 'the user can explicitly refresh the published snapshot');
  await page.getByRole('navigation', { name: 'Database views' }).getByRole('button', { name: 'Explore', exact: true }).click();
  await page.getByRole('heading', { name: 'Explore symbols', exact: true }).waitFor();
  const resetSearch = responseFor('/api/search', query => query.get('q') === '');
  await page.getByLabel('Search symbols', { exact: true }).fill(''); await resetSearch;
  await list().locator('div[aria-busy="false"]').waitFor();
  for (const [width, height] of receipt.browserSettings.viewports) {
    await page.setViewportSize({ width, height });
    const layout = await page.evaluate(() => {
      const canvas = document.createElement('canvas').getContext('2d');
      canvas.fillStyle = getComputedStyle(document.body).backgroundColor; canvas.fillRect(0, 0, 1, 1);
      return { width: document.querySelector('main').getBoundingClientRect().width, clientWidth: document.documentElement.clientWidth,
        overflow: document.documentElement.scrollWidth - window.innerWidth,
        dark: matchMedia('(prefers-color-scheme: dark)').matches,
        background: Array.from(canvas.getImageData(0, 0, 1, 1).data).slice(0, 3) };
    });
    check(layout.width >= layout.clientWidth - 2 && layout.clientWidth >= width - 20 && layout.overflow <= 1, `workspace uses the full ${width}px viewport without horizontal overflow`);
    check(layout.dark && layout.background.every(channel => channel < 60), `site theme renders dark at ${width}px`);
    const screenshot = `${output}/database-${width}.png`;
    await page.screenshot({ path: screenshot }); receipt.screenshots.push(screenshot);
  }
  for (const script of await page.locator('script[type="module"][src]').evaluateAll(elements => elements.map(element => element.src))) {
    const bytes = await page.evaluate(async src => Array.from(new Uint8Array(await (await fetch(src)).arrayBuffer())), script);
    receipt.servedAssets.push({ path: new URL(script).pathname, sha256: createHash('sha256').update(Buffer.from(bytes)).digest('hex') });
  }
  check(receipt.servedAssets.length > 0, 'receipt fingerprints the actual served application assets');
  check(receipt.apiWrites.length === 0 && receipt.modelCalls === 0, 'journey performs no API writes or model inference');
  check(errors.length === 0, `no browser runtime errors: ${errors.join('; ')}`);
  receipt.requestCancellation = receipt.abortedRequests > 0 ? 'Observed real aborted requests' : 'No in-flight abort observed on this fast corpus; stale-result/filter reset validated';
  await writeFile(`${output}/receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  console.log(`issue28 browser passed: ${receipt.assertions.length} actual served assertions; ${receipt.abortedRequests} observed aborted requests; no API mutations or model calls`);
} catch (error) {
  if (page) await page.screenshot({ path: `${output}/failure.png` }).catch(() => undefined);
  await writeFile(`${output}/failure.json`, JSON.stringify({ ...receipt, error: String(error) }, null, 2) + '\n');
  throw error;
} finally { await browser.close(); }
