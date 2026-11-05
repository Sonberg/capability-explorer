// Explicit transport fault injection; successful pages still come from the real served API.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';

const [base = 'http://localhost:5127', mode = 'files'] = process.argv.slice(2);
assert.equal(new URL(base).hostname, 'localhost');
const output = `.sandbox-cache/navigation83/${mode}`;
await mkdir(output, { recursive: true });
const receipt = { mode, at: new Date().toISOString(), injectedFailures: [], assertions: [], successfulPages: 'real backend' };
const browser = await chromium.launch({ args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let fault = 'initial', held, release;
  const requests = [];
  await page.route('**/api/search?**', async route => {
    const url = new URL(route.request().url());
    requests.push({ query: url.searchParams.get('q'), cursor: url.searchParams.get('cursor') });
    const continuation = url.searchParams.has('cursor');
    if ((fault === 'initial' && !continuation) || (fault === 'continuation' && continuation) || (fault === 'stale' && continuation)) {
      const code = fault === 'stale' ? 409 : 503;
      receipt.injectedFailures.push({ kind: fault, code });
      fault = null;
      await route.fulfill({ status: code, contentType: 'application/json', body: JSON.stringify({ error: `Injected ${code} for acceptance`, code: code === 409 ? 'stale-snapshot' : 'database-unavailable' }) });
      return;
    }
    if (fault === 'late' && url.searchParams.get('q') === 'Repeated') {
      fault = null;
      const direct = new URL(url);
      direct.hostname = 'host.docker.internal';
      const response = await route.fetch({ url: direct.href, headers: { ...route.request().headers(), host: url.host } });
      held?.();
      await new Promise(resolve => { release = resolve; });
      await route.fulfill({ response }).catch(error => {
        // Native abort can close the route before the intentionally late response is delivered.
        if (!/closed|handled|cancel|abort|Invalid InterceptionId/i.test(error.message)) throw error;
      });
      return;
    }
    await route.continue();
  });
  await page.goto(`${base}/#/explore`);
  const root = page.getByLabel('Symbol results', { exact: true });
  await root.getByText('Injected 503 for acceptance').waitFor();
  assert.equal(await root.locator('[data-symbol-key]').count(), 0);
  assert.equal(await root.getByText(/No (symbols match|matching symbols)/).count(), 0);
  await root.getByRole('button', { name: /Retry/i }).click();
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
  receipt.assertions.push('initial failure exposes working Retry without an empty-success state');
  fault = 'continuation';
  await root.evaluate(element => { element.scrollTop = element.scrollHeight; });
  await root.getByText('Injected 503 for acceptance').waitFor();
  const failed = requests.at(-1), requestCount = requests.length;
  await page.waitForTimeout(300);
  assert.equal(requests.length, requestCount);
  const retained = await root.locator('[data-symbol-key]').count();
  await root.getByRole('button', { name: /Retry/i }).click();
  await page.waitForFunction(before => document.querySelectorAll('[data-symbol-key]').length > before, retained);
  assert.ok(requests.some((request, i) => i >= requestCount && request.cursor === failed.cursor));
  receipt.assertions.push('continuation failure retains rows, does not auto-retry, and Retry reuses exact failed cursor');
  fault = 'stale';
  await root.evaluate(element => { element.scrollTop = element.scrollHeight; });
  await root.getByText('Injected 409 for acceptance').waitFor();
  const restartedRequest = page.waitForRequest(request => {
    const url = new URL(request.url());
    return url.pathname === '/api/search' && !url.searchParams.has('cursor');
  });
  await root.getByRole('button', { name: /reload|restart|retry/i }).click();
  const restarted = await restartedRequest;
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
  assert.equal(new URL(restarted.url()).searchParams.get('cursor'), null);
  receipt.assertions.push('stale continuation restarts without incompatible cursor');
  fault = 'late';
  const responseHeld = new Promise(resolve => { held = resolve; });
  const search = page.getByLabel('Search symbols', { exact: true });
  await search.fill('Repeated');
  await responseHeld;
  await search.fill('definitely-no-such-symbol');
  await root.getByText(/No (symbols match|matching symbols)/).waitFor();
  release();
  await page.waitForTimeout(300);
  assert.equal(await root.locator('[data-symbol-key]').count(), 0);
  assert.deepEqual(errors, []);
  receipt.assertions.push('late real response from superseded filter cannot repopulate current empty results');
  console.log(JSON.stringify({ mode, assertions: receipt.assertions.length, injected: receipt.injectedFailures }));
} finally {
  await writeFile(`${output}/failure-receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  await browser.close();
}
