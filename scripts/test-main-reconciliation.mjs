// Read-only journey against the actual reconciled application, never mocked API/model responses.
// Run in the pinned BROWSER_IMAGE from config/runtime/images.env, mounting this tree at /work:
// node scripts/test-main-reconciliation.mjs http://localhost:5112
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';

const base = process.argv[2] ?? 'http://localhost:5112';
assert.equal(new URL(base).hostname, 'localhost', 'use loopback Host/Origin with the Chromium resolver override');
const images = await readFile(new URL('../config/runtime/images.env', import.meta.url), 'utf8');
const output = '.sandbox-cache/main-reconciliation-browser';
await mkdir(output, { recursive: true });
const receipt = {
  base, collectedAt: new Date().toISOString(),
  browserImage: images.match(/^BROWSER_IMAGE=(.+)$/m)?.[1], assertions: [],
  simulated: [], modelCalls: 0, databaseWrites: false, servedAssets: [],
  notTested: ['Model inference and chat answers', 'Persistent deprecation writes', 'Database-backed similarity', 'Every viewport size'],
};
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--host-resolver-rules=MAP localhost host.docker.internal'] });
const check = (condition, message) => { assert.ok(condition, message); receipt.assertions.push(message); };
try {
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
  const errors = [], mutations = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (/\/api\/chat(?:\?|$)/.test(request.url())) receipt.modelCalls++;
    if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) mutations.push(`${request.method()} ${new URL(request.url()).pathname}`);
  });
  await page.addInitScript(() => { localStorage.setItem('ce.capability', '""'); localStorage.setItem('ce.overrides', '{}'); });
  await page.goto(`${base}/#/glossary`);
  await page.getByRole('heading', { name: 'Glossary concepts', exact: true }).waitFor();
  check(await page.getByRole('link', { name: 'Concepts', exact: true }).getAttribute('aria-current') === 'page', 'ranked Concepts remain the default glossary entry');
  check(await page.getByLabel('Search concepts', { exact: true }).isVisible(), 'ranked concept search is available');
  check(await page.getByText('Language found in the analyzed repositories.', { exact: false }).isVisible(), 'analyzed repository language is distinguished from project ubiquitous language');
  check(await page.getByRole('columnheader', { name: 'Why', exact: true }).isVisible(), 'concept ranking reasons remain visible');
  await page.getByRole('link', { name: 'Candidates', exact: true }).click();
  await page.getByRole('heading', { name: 'Glossary candidates', exact: true }).waitFor();
  check(await page.getByLabel('Search glossary candidates', { exact: true }).isVisible(), 'exact-label candidate workflow remains accessible');
  const candidates = await page.evaluate(async () => { const response = await fetch('/api/glossary'); if (!response.ok) throw new Error('candidate API failed'); return response.json(); });
  check(candidates.length > 0, 'actual fixture API contains glossary candidates');
  const term = candidates.find(candidate => candidate.occurrences.length > 0)?.term;
  check(typeof term === 'string', 'a source-bearing glossary candidate exists');
  await page.goto(`${base}/#/glossary?term=${encodeURIComponent(term)}`);
  await page.getByRole('heading', { name: 'Glossary candidates', exact: true }).waitFor();
  await page.getByRole('button', { name: /^(Selected connections|Explore connections) for / }).first().waitFor();
  check(await page.getByRole('link', { name: 'Candidates', exact: true }).getAttribute('aria-current') === 'page', 'legacy term bookmarks select Candidates rather than silently opening Concepts');
  await page.locator('.live-architecture-canvas[data-status="ready"]').waitFor({ timeout: 60000 });
  check(await page.getByRole('heading', { name: /Reviewed/ }).count() > 0, 'reviewed glossary connections remain available beside the shared canvas');
  await page.screenshot({ path: `${output}/glossary.png`, fullPage: true });

  await page.goto(`${base}/#/architecture?level=system`);
  const canvas = page.locator('.live-architecture-canvas[data-status="ready"]');
  await canvas.waitFor({ timeout: 60000 });
  check((await canvas.getAttribute('data-input-identity'))?.includes('|system|'), 'LikeC4 canvas is bound to the selected system snapshot');
  const graph = await page.evaluate(async () => { const response = await fetch('/api/architecture?level=system'); if (!response.ok) throw new Error('architecture API failed'); return response.json(); });
  check(graph.nodes.length > 0, 'actual fixture API contains architecture nodes');
  const rendered = page.locator('.react-flow__node[data-source-node-ids]');
  await rendered.first().waitFor();
  const ids = await page.locator('[data-source-node-ids]').evaluateAll(elements => elements.flatMap(e => JSON.parse(e.getAttribute('data-source-node-ids') ?? '[]')));
  check(ids.length > 0 && ids.every(id => graph.nodes.some(node => node.id === id)), 'rendered LikeC4 node provenance points to actual architecture API node IDs');
  const main = page.locator('main.min-w-0');
  const before = (await main.boundingBox()).width;
  check(before >= 1900, 'closed chat leaves the workspace using the full desktop width');
  const leafIndex = await rendered.evaluateAll(elements => elements.findIndex(element => JSON.parse(element.getAttribute('data-source-node-ids') ?? '[]').length === 1));
  check(leafIndex >= 0, 'shared LikeC4 canvas exposes a selectable original node');
  const focusId = JSON.parse(await rendered.nth(leafIndex).getAttribute('data-source-node-ids'))[0];
  const focused = graph.nodes.find(node => node.id === focusId);
  await rendered.nth(leafIndex).click({ force: true });
  await page.locator('[data-source-selected="true"]').first().waitFor();
  await page.getByRole('button', { name: /^Ask / }).click();
  await page.getByRole('complementary', { name: 'Ask about capabilities' }).waitFor();
  check(await page.getByLabel('Question', { exact: true }).isVisible(), 'owner chat panel remains accessible without invoking a model');
  check(await page.getByLabel('In focus', { exact: true }).getByRole('button', { name: `${focused.category}: ${focused.label}`, exact: true }).isVisible(), 'shared LikeC4 node selection publishes the owner chat Item focus');
  check((await main.boundingBox()).width <= before - 400, 'open chat reserves its desktop panel width');
  await page.getByRole('button', { name: 'Close chat', exact: true }).click();
  await page.getByRole('complementary', { name: 'Ask about capabilities' }).waitFor({ state: 'detached' });
  check(Math.abs((await main.boundingBox()).width - before) < 2, 'closing chat restores the full workspace width');
  await page.keyboard.press('Control+j');
  await page.getByRole('complementary', { name: 'Ask about capabilities' }).waitFor();
  await page.getByRole('button', { name: 'Close chat', exact: true }).click();
  check(true, 'owner keyboard shortcut still opens chat');
  await page.screenshot({ path: `${output}/architecture.png`, fullPage: true });

  await page.getByLabel('Integrations', { exact: true }).selectOption('integrations/exposed');
  await page.getByRole('heading', { name: 'What services expose', exact: true }).waitFor();
  check(await page.getByLabel('Only obsolete / deprecated', { exact: true }).isVisible(), 'exposed integrations retain deprecation filtering controls');
  await page.getByLabel('Integrations', { exact: true }).selectOption('integrations/consumed');
  await page.getByRole('heading', { name: 'What services consume', exact: true }).waitFor();
  check(await page.getByLabel('Only obsolete / deprecated', { exact: true }).isVisible(), 'consumed integrations retain deprecation filtering controls');
  await page.getByRole('link', { name: 'Binding review', exact: true }).click();
  await page.getByRole('heading', { name: 'Review integration bindings', exact: true }).waitFor();
  check(true, 'stage binding review remains available alongside owner integration directions');

  const sourceStatus = await page.evaluate(async () => (await fetch('/api/source?repo=api-cs&commit=0000000000000000000000000000000000000000&path=.git%2Fconfig')).status);
  check(sourceStatus === 400 || sourceStatus === 404, 'source endpoint rejects an uncited arbitrary repository path');
  for (const script of await page.locator('script[type="module"][src]').evaluateAll(elements => elements.map(e => e.src))) {
    const asset = await page.evaluate(async src => { const response = await fetch(src); if (!response.ok) throw new Error('served asset failed'); return response.text(); }, script);
    receipt.servedAssets.push({ path: new URL(script).pathname, sha256: createHash('sha256').update(asset).digest('hex') });
  }
  check(receipt.servedAssets.length > 0, 'receipt fingerprints actual served JavaScript assets');
  check(receipt.modelCalls === 0, 'journey makes no model inference requests');
  check(mutations.length === 0, 'journey performs no API mutations or database writes');
  check(errors.length === 0, `no browser runtime errors: ${errors.join('; ')}`);
  await writeFile(`${output}/receipt.json`, JSON.stringify(receipt, null, 2) + '\n');
  console.log(`main reconciliation browser passed: ${receipt.assertions.length} actual served assertions; no model requests or database writes`);
} catch (error) {
  await writeFile(`${output}/failure.json`, JSON.stringify({ ...receipt, error: String(error) }, null, 2) + '\n');
  throw error;
} finally { await browser.close(); }
