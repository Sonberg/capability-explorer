// Explicit negative-path test: inject exactly one transient 503, never fabricate successful data.
// Successful pages and the retried response come from the actual running PostgreSQL API.
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';

const base = process.argv[2] ?? 'http://localhost:5116';
assert.equal(new URL(base).hostname, 'localhost');
const output = '.sandbox-cache/issue28-pager-failure';
await mkdir(output, { recursive: true });
const receipt = { base, collectedAt: new Date().toISOString(), assertions: [],
  injectedFailures: [], mockedSuccessfulResponses: false, modelCalls: 0, writes: [], servedAssets: [] };
const check = (condition, message) => { assert.ok(condition, message); receipt.assertions.push(message); };
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
let page;
try {
  page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, colorScheme: 'dark' });
  const errors = [], continuationRequests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    const url = new URL(request.url());
    if (!url.pathname.startsWith('/api/')) return;
    if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) receipt.writes.push(request.method() + ' ' + url.pathname);
    if (/chat|explanations|semantic/.test(url.pathname)) receipt.modelCalls++;
    if (url.pathname === '/api/search' && url.searchParams.has('cursor')) continuationRequests.push(request.url());
  });
  let injected = false, failedCursor;
  await page.route('**/api/search?**', async route => {
    const url = new URL(route.request().url());
    if (url.searchParams.has('cursor') && !injected) {
      injected = true; failedCursor = url.searchParams.get('cursor');
      receipt.injectedFailures.push({ path: url.pathname, status: 503, reason: 'deliberate transient negative-path check' });
      await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Deliberate transient pager test failure' }) });
    } else await route.continue();
  });
  const initialResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/search' && response.status() === 200);
  await page.goto(base);
  await page.getByRole('heading', { name: 'Explore symbols', exact: true }).waitFor();
  const first = await (await initialResponse).json();
  check(first.items.length === 50 && !!first.nextCursor, 'test starts with a real bounded page and real continuation');
  const listing = page.getByRole('heading', { name: 'Explore symbols', exact: true }).locator('..');
  const cards = listing.locator('div[aria-busy] > button');
  await cards.nth(49).waitFor();
  const firstContent = await cards.allTextContents();
  await listing.getByRole('button', { name: 'Next', exact: true }).click();
  await listing.getByText('Deliberate transient pager test failure', { exact: true }).waitFor();
  check(receipt.injectedFailures.length === 1, 'exactly one negative response was deliberately injected');
  check(JSON.stringify(await cards.allTextContents()) === JSON.stringify(firstContent), 'failed navigation preserves the successful page rows');
  check(await listing.getByText('Page 1 · 50 rows', { exact: true }).isVisible(), 'failed navigation preserves the successful page number');
  check(await listing.getByRole('button', { name: 'Next', exact: true }).isDisabled(), 'stale rows cannot advance after a failed request');
  check(await listing.getByRole('button', { name: 'Previous', exact: true }).isDisabled(), 'failed page two does not create fictitious previous-page history');
  check(await listing.getByRole('button', { name: 'Retry page request', exact: true }).isEnabled(), 'explicit retry remains available');
  await page.screenshot({ path: `${output}/after-failure.png` });

  const retryResponse = page.waitForResponse(response => {
    const url = new URL(response.url());
    return url.pathname === '/api/search' && url.searchParams.get('cursor') === failedCursor && response.status() === 200;
  });
  await listing.getByRole('button', { name: 'Retry page request', exact: true }).click();
  const second = await (await retryResponse).json();
  await listing.getByText('Page 2 · 50 rows', { exact: true }).waitFor();
  await listing.locator('div[aria-busy="false"]').waitFor();
  check(continuationRequests.length === 2 && continuationRequests[0] === continuationRequests[1], 'retry sends the exact failed cursor request again');
  check(second.snapshotId === first.snapshotId, 'retry stays on the original immutable snapshot');
  check(new Set([...first.items, ...second.items].map(hit => hit.key)).size === 100, 'retried real page contains the next unique symbols');
  check(await cards.count() === 50, 'retry retains one bounded result page');
  check(await listing.getByRole('button', { name: 'Retry page request', exact: true }).count() === 0, 'successful retry clears the failure and retry control');
  await page.screenshot({ path: `${output}/after-retry.png` });
  const previousResponse = page.waitForResponse(response => {
    const url = new URL(response.url()); return url.pathname === '/api/search' && !url.searchParams.has('cursor') && response.status() === 200;
  });
  await listing.getByRole('button', { name: 'Previous', exact: true }).click(); await previousResponse;
  await listing.locator('div[aria-busy="false"]').waitFor();
  check(JSON.stringify(await cards.allTextContents()) === JSON.stringify(firstContent), 'successful retry preserves correct navigation back to page one');
  for (const script of await page.locator('script[type="module"][src]').evaluateAll(elements => elements.map(element => element.src))) {
    const bytes = await page.evaluate(async src => Array.from(new Uint8Array(await (await fetch(src)).arrayBuffer())), script);
    receipt.servedAssets.push({ path: new URL(script).pathname, sha256: createHash('sha256').update(Buffer.from(bytes)).digest('hex') });
  }
  check(errors.length === 0, 'no browser runtime errors during failure and recovery');
  check(receipt.writes.length === 0 && receipt.modelCalls === 0, 'negative-path test performs no writes or inference');
  await writeFile(`${output}/receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  console.log(`issue28 pager failure passed: ${receipt.assertions.length} assertions; exactly one labeled injected 503, real successful responses`);
} catch (error) {
  if (page) await page.screenshot({ path: `${output}/failure.png` }).catch(() => undefined);
  await writeFile(`${output}/failure.json`, JSON.stringify({ ...receipt, error: String(error) }, null, 2) + '\n');
  throw error;
} finally { await browser.close(); }
