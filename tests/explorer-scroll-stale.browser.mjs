// Real PostgreSQL snapshot removal handshake. Host mutates only the isolated synthetic test DB.
import assert from 'node:assert/strict';
import { mkdir, writeFile, access } from 'node:fs/promises';
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';

const base = process.argv[2] ?? 'http://localhost:5128';
assert.equal(new URL(base).hostname, 'localhost');
const run = process.argv[3] ?? 'real-stale';
assert.match(run, /^[a-z0-9-]+$/);
const output = `.sandbox-cache/navigation83/${run}`;
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  if (process.env.CE_STALE_BASELINE === '1') {
    await page.route('**/assets/**', route => {
      const name = new URL(route.request().url()).pathname.split('/').at(-1);
      assert.match(name, /^[a-zA-Z0-9._-]+$/);
      return route.fulfill({ path: `.sandbox-cache/navigation83/pre-fix-assets/${name}` });
    });
  }
  let firstSnapshot;
  const requests = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname === '/api/search') {
      firstSnapshot ??= url.searchParams.get('snapshot');
      requests.push({ cursor: url.searchParams.get('cursor'), snapshot: url.searchParams.get('snapshot') });
    }
  });
  await page.goto(`${base}/#/explore`);
  const root = page.getByLabel('Symbol results', { exact: true });
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
  const loadMore = root.getByRole('button', { name: /Load more/i });
  await loadMore.waitFor();
  await page.waitForLoadState('networkidle');
  await writeFile(`${output}/ready.json`, JSON.stringify({ firstSnapshot }) + '\n');
  console.log('stale-handshake: initial snapshot loaded; waiting for isolated DB transition');
  const deadline = Date.now() + 120000;
  for (;;) {
    try { await access(`${output}/continue`); break; }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    assert.ok(Date.now() < deadline, 'host did not transition isolated test snapshot');
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  const staleResponse = page.waitForResponse(response => {
    const url = new URL(response.url());
    return url.pathname === '/api/search' && url.searchParams.get('snapshot') === firstSnapshot && response.status() === 409;
  });
  await loadMore.click();
  await staleResponse;
  await root.getByRole('alert').filter({ hasText: 'snapshot is unavailable; restart pagination' }).waitFor();
  await root.getByRole('button', { name: /reload|restart|retry/i }).click();
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
  await page.waitForFunction(() => !document.body.textContent.includes('snapshot is unavailable; restart pagination'));
  const current = requests.find(request => request.snapshot !== firstSnapshot);
  assert.ok(current, 'Retry must request the current snapshot');
  assert.notEqual(current.snapshot, firstSnapshot);
  assert.equal(current.cursor, null);
  await writeFile(`${output}/receipt.json`, JSON.stringify({
    at: new Date().toISOString(), mockedResponses: false, firstSnapshot, currentSnapshot: current.snapshot,
    assertions: ['real unavailable snapshot409', 'Retry reloads current metadata', 'new initial request pins new snapshot without old cursor'],
    requests,
  }, null, 2) + '\n');
  console.log('real stale snapshot recovery: PASS');
} catch (error) {
  await writeFile(`${output}/error.txt`, String(error.stack) + '\n');
  throw error;
} finally {
  await browser.close();
}
