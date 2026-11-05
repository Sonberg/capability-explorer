import assert from 'node:assert/strict';
import { access, mkdir, writeFile } from 'node:fs/promises';
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';

const [base = 'http://localhost:5132', mode = 'ui-fixtures', head, run = String(Date.now())] = process.argv.slice(2);
assert.equal(new URL(base).hostname, 'localhost');
assert.ok(['ui-fixtures', 'real-switch'].includes(mode));
assert.match(head ?? '', /^[a-f0-9]{40}$/);
assert.match(run, /^[a-z0-9-]+$/);
const output = `.sandbox-cache/enrichment59/${mode}/${run}`;
try {
  await access(output);
  throw new Error('Use a new run identifier; existing evidence must not be overwritten');
} catch (error) {
  if (error.code !== 'ENOENT') throw error;
}
await mkdir(output, { recursive: true });
const receipt = {
  head, mode, at: new Date().toISOString(), assertions: [],
  mockedResponses: mode === 'ui-fixtures' ? 'Explicit five-state status and503 fixtures; core API remains real' : false,
};
const browser = await chromium.launch({ args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const panel = page.getByRole('region', { name: /embedding/i });
const currentModel = { id: 'synthetic-current-model', fingerprint: 'a'.repeat(64), recipeVersion: 'synthetic-v1' };
const oldModel = { id: 'synthetic-old-model', fingerprint: 'b'.repeat(64), recipeVersion: 'synthetic-v1' };

async function originalResponse(route) {
  const url = new URL(route.request().url());
  url.hostname = 'host.docker.internal';
  return route.fetch({ url: url.href, headers: { ...route.request().headers(), host: new URL(base).host } });
}

async function coreReady() {
  await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
  await panel.waitFor();
}

async function metadata(capability) {
  const url = new URL('/api/meta', base);
  url.hostname = 'host.docker.internal';
  url.searchParams.set('cap', capability);
  const response = await page.request.get(url.href, { headers: { host: new URL(base).host } });
  assert.equal(response.status(), 200);
  const { snapshotId, generation } = await response.json();
  return { snapshotId, generation };
}

function fixture(original, state) {
  const complete = state === 'ready' || state === 'stale';
  const exact = complete || state === 'partial-failure';
  return {
    ...original, state, similarityAvailable: false,
    configuredModel: state === 'not-configured' ? null : currentModel,
    coverageModel: state === 'stale' ? oldModel : exact ? currentModel : null,
    countsExact: exact,
    counts: complete ? { total: 4, ready: 4, pending: 0, failed: 0 }
      : state === 'partial-failure' ? { total: 4, ready: 2, pending: 1, failed: 1 } : null,
    coveredArtifactCount: complete ? original.selectedArtifactCount : 0,
    snapshotArtifactsSha256: exact ? 'c'.repeat(64) : null,
    lastSuccessfulCompletion: complete ? '2026-11-05T00:00:00Z' : null,
    reasonCode: state === 'partial-failure' ? 'synthetic-provider-failure' : null,
  };
}

try {
  if (mode === 'ui-fixtures') {
    let state = 'not-configured', fail = false, original;
    await page.route('**/api/embeddings/status?**', async route => {
      assert.equal(route.request().method(), 'GET');
      if (fail) {
        await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Injected embedding status failure', code: 'database-unavailable' }) });
        return;
      }
      const response = await originalResponse(route);
      assert.equal(response.status(), 200);
      original = await response.json();
      for (const field of ['configuredModel', 'coverageModel', 'counts', 'snapshotArtifactsSha256', 'lastSuccessfulCompletion', 'reasonCode']) {
        assert.ok(Object.hasOwn(original, field), `Nullable ${field} must serialize explicitly`);
      }
      const url = new URL(route.request().url());
      assert.equal(url.searchParams.get('snapshot'), original.snapshotId);
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(fixture(original, state)) });
    });
    for (const [nextState, label] of [
      ['not-configured', /not configured/i], ['pending', /pending/i],
      ['partial-failure', /partial.failure/i], ['stale', /stale/i], ['ready', /Embedding projection ready/i],
    ]) {
      state = nextState;
      await page.goto(`${base}/#/explore`);
      await coreReady();
      await panel.getByText(label).first().waitFor();
      await panel.getByText('PostgreSQL similarity search unavailable', { exact: true }).waitFor();
      if (state !== 'not-configured') assert.match(await panel.innerText(), /synthetic-current-model/);
      if (state === 'stale') assert.match(await panel.innerText(), /synthetic-old-model/);
      if (!['ready', 'stale', 'partial-failure'].includes(state)) {
        assert.doesNotMatch(await panel.innerText(), /\b0\s+(?:total|ready|pending|failed)/i);
      }
      await page.screenshot({ path: `${output}/${state}.png`, fullPage: true });
      receipt.assertions.push(`${state} status renders honestly while core browsing works`);
    }
    const before = await metadata(original.capability);
    assert.equal(before.snapshotId, original.snapshotId);
    fail = true;
    await panel.getByRole('button', { name: /refresh|retry/i }).click();
    await panel.getByText(/Injected embedding status failure/i).waitFor();
    assert.equal(await panel.getByText('Embedding projection ready', { exact: true }).count(), 0);
    assert.ok(await page.locator('[data-symbol-key]').count() >= 50);
    fail = false;
    await panel.getByRole('button', { name: /refresh|retry/i }).click();
    await panel.getByText('Embedding projection ready', { exact: true }).waitFor();
    assert.deepEqual(await metadata(original.capability), before);
    receipt.assertions.push('Status503 is an error, not pending/ready; Retry restores status without changing core generation');
  } else {
    let held, release, delay = false;
    const responseHeld = new Promise(resolve => { held = resolve; });
    const resume = new Promise(resolve => { release = resolve; });
    let oldStatus;
    const statusRequests = [];
    await page.route('**/api/embeddings/status?**', async route => {
      const response = await originalResponse(route);
      assert.equal(response.status(), 200);
      const status = await response.json();
      statusRequests.push({ snapshotId: status.snapshotId, generation: status.generation, state: status.state });
      if (delay) {
        delay = false;
        oldStatus = status;
        assert.equal(status.state, 'ready', 'Real switch starts with a completed old projection');
        held();
        await resume;
      }
      try { await route.fulfill({ response }); }
      catch (error) {
        if (!/closed|handled|cancel|abort|Invalid InterceptionId/i.test(error.message)) throw error;
      }
    });
    await page.goto(`${base}/#/explore`);
    await coreReady();
    await panel.getByText('Embedding projection ready', { exact: true }).waitFor();
    delay = true;
    await panel.getByRole('button', { name: /refresh|retry/i }).click();
    await responseHeld;
    await writeFile(`${output}/ready.json`, JSON.stringify({
      snapshotId: oldStatus.snapshotId, generation: oldStatus.generation,
      capability: oldStatus.capability, selection: oldStatus.selection,
    }) + '\n');
    const deadline = Date.now() + 120000;
    for (;;) {
      try { await access(`${output}/continue`); break; }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
      assert.ok(Date.now() < deadline, 'Host did not replace/remove the exact owned synthetic old snapshot');
      await new Promise(resolve => setTimeout(resolve, 200));
    }
    const root = page.getByLabel('Symbol results', { exact: true });
    await root.getByRole('button', { name: /Load more/i }).click();
    await root.getByRole('alert').filter({ hasText: 'snapshot is unavailable; restart pagination' }).waitFor();
    await root.getByRole('button', { name: /reload|restart|retry/i }).click();
    await page.waitForFunction(() => document.querySelectorAll('[data-symbol-key]').length >= 50);
    await panel.getByText(/pending/i).first().waitFor();
    const current = statusRequests.find(status => status.snapshotId !== oldStatus.snapshotId);
    assert.ok(current && current.state === 'pending');
    release();
    await page.waitForTimeout(300);
    assert.equal(await panel.getByText('Embedding projection ready', { exact: true }).count(), 0);
    await panel.getByText(/pending/i).first().waitFor();
    receipt.assertions.push('Actual snapshot409 recovery pins replacement metadata and pending projection',
      'Delayed real old ready status cannot overwrite the new snapshot pending status');
    receipt.snapshots = statusRequests;
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ mode, assertions: receipt.assertions }));
} catch (error) {
  receipt.error = String(error.stack);
  throw error;
} finally {
  await writeFile(`${output}/receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  await browser.close();
}
