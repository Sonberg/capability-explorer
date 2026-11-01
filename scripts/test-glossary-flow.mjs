// Focused checks against an actual served Explorer build. The optional DB journey writes
// uniquely named review data and deletes it afterwards. Failure/conflict checks explicitly
// simulate responses at the browser boundary; they do not claim a real database outage.
// SANDBOX_PASS_ENV=CE_GLOSSARY_BASE_URL SANDBOX_NODE_IMAGE=mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea scripts/sandbox node node scripts/test-glossary-flow.mjs
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
const base = process.env.CE_GLOSSARY_BASE_URL ?? 'http://host.docker.internal:5080';
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
const receipt = { base, assertions: 0, simulated: [], viewports: [], databaseJourney: 'not run' };
const artifactDirectory = `/work/.sandbox-cache/glossary-browser/${new URL(base).port || "default"}`;
let page;
const check = (condition, message) => { assert.ok(condition, message); receipt.assertions++; };
try {
  page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.addInitScript(() => { localStorage.setItem('ce.capability', '""'); localStorage.setItem('ce.overrides', '{}'); });
  await mkdir(artifactDirectory, { recursive: true });
  console.log("Checking actual source flow and occurrence selection");
  const glossaryResponse = await page.request.get(`${base}/api/glossary`);
  check(glossaryResponse.ok(), 'real glossary endpoint must succeed');
  const candidates = await glossaryResponse.json();
  const candidate = candidates.find((item) => item.occurrences.length > 1) ?? candidates[0];
  check(!!candidate?.occurrences.length, 'fixture must contain glossary occurrences');
  const initialResponse = page.waitForResponse((response) => response.url().includes('/api/glossary/flow?') && response.ok());
  await page.goto(`${base}/#/glossary?term=${encodeURIComponent(candidate.term)}`);
  await page.getByRole('heading', { name: `Connections for ${candidate.occurrences[0].name}`, exact: true }).waitFor();
  const flowResponse = await initialResponse;
  const flow = await flowResponse.json();
  check(flow.seeds[0].occurrenceId === candidate.occurrences[0].id, 'initial seed must be exact selected occurrence');
  check(flow.coverage.valueFlowComplete === false, 'context fixture must not claim complete value flow');
  await page.getByText('field/value flow incomplete', { exact: true }).waitFor(); receipt.assertions++;
  await page.getByRole('heading', { name: 'Selection and revision coverage', exact: true }).waitFor(); receipt.assertions++;
  check(flow.selection.length > 0, 'flow must disclose selected revisions');
  for (const selected of flow.selection) check((await page.locator('body').innerText()).includes(selected.commitSha), 'exact selection revision must be rendered');
  if (candidate.occurrences.length > 1) {
    const next = candidate.occurrences[1];
    const changed = page.waitForResponse((response) => response.url().includes('/api/glossary/flow?') && new URL(response.url()).searchParams.get('occurrence') === next.id && response.ok());
    await page.getByRole('button', { name: `Explore connections for ${next.name} · ${next.role} · ${next.repositoryId} · ${next.commitSha}`, exact: true }).first().click();
    const nextFlow = await (await changed).json();
    check(nextFlow.seeds[0].occurrenceId === next.id, 'occurrence button must preserve repository/revision/role identity');
  }
  await page.getByLabel('Maximum nodes', { exact: true }).fill('1');
  await page.getByRole('button', { name: 'Apply flow limits', exact: true }).click();
  await page.getByText(/applied bounds 4 hops \/ 1 nodes \/ 500 edges/).waitFor(); receipt.assertions++;
  const reviewResponse = await page.request.get(`${base}/api/glossary/review`);
  check(reviewResponse.ok(), 'review status endpoint must succeed for DB-off or healthy DB');
  const review = await reviewResponse.json();
  console.log(`Checking reviewed state: configured=${review.configured}, readOnly=${review.readOnly}`);
  if (!review.configured || review.readOnly) {
    await page.getByText(/Read-only: no glossary database|Reviewed data is read-only/).waitFor();
    check(await page.getByRole('button', { name: 'Create reviewed term', exact: true }).isDisabled(), 'missing DB must disable writes');
    receipt.databaseJourney = 'database disabled/read-only verified';
  } else {
    const name = `browser-review-${Date.now()}`;
    const reviewer = 'Browser verification (self-reported)';
    const ids = [];
    const saved = async (collection, click) => {
      const response = page.waitForResponse((item) => new URL(item.url()).pathname.startsWith(`/api/glossary/${collection}/`) && item.request().method() === 'PUT');
      await click(); const result = await response; check(result.ok(), `real ${collection} mutation must succeed`);
      return (await result.json()).value;
    };
    try {
      console.log("Creating real reviewed term");
      await page.getByRole('button', { name: 'Create reviewed term', exact: true }).click();
      await page.getByLabel('Term name', { exact: true }).fill(name);
      await page.getByLabel('Definition', { exact: true }).fill('<script>window.glossaryInjected=true</script> retained as text');
      await page.getByLabel('Reviewer label (self-reported)', { exact: true }).fill(reviewer);
      const term = await saved('terms', () => page.getByRole('button', { name: 'Save review', exact: true }).click()); ids.push(['terms', term.id]);
      check(await page.evaluate(() => window.glossaryInjected) === undefined, 'script-like definition must remain text');
      await page.getByRole('button', { name: `Edit ${name}`, exact: true }).click();
      await page.getByLabel('Definition', { exact: true }).fill('Preserved conflict draft');
      await page.route(`**/api/glossary/terms/${term.id}?*`, (route) => route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ error: 'simulated stale version' }) }));
      await page.getByRole('button', { name: 'Save review', exact: true }).click();
      await page.getByRole('alert').getByText(/record changed since you loaded/).waitFor();
      check(await page.getByLabel('Definition', { exact: true }).inputValue() === 'Preserved conflict draft', '409 must preserve draft');
      receipt.simulated.push('409 retains draft');
      await page.unroute(`**/api/glossary/terms/${term.id}?*`);
      await saved('terms', () => page.getByRole('button', { name: 'Save review', exact: true }).click());
      console.log("Creating real reviewed link with exact observation");
      await page.getByRole('button', { name: 'Create reviewed link', exact: true }).click();
      await page.getByLabel('Reviewed term', { exact: true }).selectOption(term.id);
      const link = await saved('links', () => page.getByRole('button', { name: 'Save review', exact: true }).click()); ids.push(['links', link.id]);
      check(!!link.observed.artifactContentSha256 && !!link.observed.commitSha, 'persisted link must carry artifact/revision provenance');
      console.log("Creating and resolving real mismatch issue");
      await page.getByRole('button', { name: 'Create mismatch issue', exact: true }).click();
      await page.getByLabel('Reviewed term', { exact: true }).selectOption(term.id);
      await page.getByLabel('Issue summary', { exact: true }).fill(`${name} mismatch`);
      const issue = await saved('issues', () => page.getByRole('button', { name: 'Save review', exact: true }).click()); ids.push(['issues', issue.id]);
      await page.getByRole('button', { name: `Resolve issue ${issue.id}`, exact: true }).click();
      const resolved = await saved('issues', () => page.getByRole('button', { name: 'Save review', exact: true }).click());
      check(resolved.status === 'resolved', 'issue resolution must persist');
      for (const [collection, id] of [...ids].reverse()) {
        const label = collection === 'terms' ? name : `${collection === 'links' ? 'link' : 'issue'} ${id}`;
        await page.getByRole('button', { name: `Delete ${label}`, exact: true }).click();
        const response = page.waitForResponse((item) => item.url().includes(`/api/glossary/${collection}/${id}`) && item.request().method() === 'DELETE');
        await page.getByRole('button', { name: 'Confirm deletion', exact: true }).click(); check((await response).ok(), `real ${collection} delete must succeed`);
        ids.splice(ids.findIndex((item) => item[1] === id), 1);
      }
      receipt.databaseJourney = 'real term/link/issue create, term update, issue resolve, all delete verified';
    } finally {
      // Failure cleanup uses the same versioned API and is listed separately from UI assertions.
      for (const [collection, id] of [...ids].reverse()) {
        const snapshot = await (await page.request.get(`${base}/api/glossary/review`)).json();
        const record = snapshot.snapshot[collection].find((item) => item.id === id && !item.deleted);
        if (record) await page.request.delete(`${base}/api/glossary/${collection}/${id}`, { headers: { 'X-CE-Glossary-Write': '1', Origin: new URL(base).origin }, data: { expectedVersion: record.version, reviewer } });
      }
    }
  }
  await page.route('**/api/glossary/review?*', (route) => route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'simulated configured database unavailable' }) }));
  await page.getByRole('button', { name: 'Reload reviewed data', exact: true }).click();
  await page.getByText('Reviewed glossary unavailable: simulated configured database unavailable', { exact: true }).waitFor(); receipt.assertions++;
  check(await page.getByRole('button', { name: 'Create reviewed term', exact: true }).isDisabled(), '503 must prevent writes');
  receipt.simulated.push('configured DB 503 error and disabled writes');
  await page.unroute('**/api/glossary/review?*');
  await page.getByRole('button', { name: 'Reload reviewed data', exact: true }).click();
  for (const viewport of [{ width: 1440, height: 900 }, { width: 5120, height: 2160 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    check(await page.getByRole('heading', { name: 'Reviewed glossary', exact: true }).isVisible(), 'review UI must remain available at viewport');
    check(await page.getByRole('button', { name: 'Apply flow limits', exact: true }).isVisible(), 'flow controls must remain available at viewport');
    await page.getByRole('heading', { name: /^Connections for/ }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${artifactDirectory}/flow-${viewport.width}.png` });
    await page.getByRole('heading', { name: 'Reviewed glossary', exact: true }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${artifactDirectory}/review-${viewport.width}.png` });
    receipt.viewports.push(viewport);
  }
  const meta = await (await page.request.get(`${base}/api/meta`)).json();
  if (meta.capabilities.length) {
    const capability = meta.capabilities[0].id;
    const changedScope = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/glossary' && new URL(response.url()).searchParams.get('cap') === capability && response.ok());
    await page.getByLabel('Capability', { exact: true }).selectOption(capability);
    const scopedCandidates = await (await changedScope).json();
    check(scopedCandidates.every((item) => item.occurrences.every((occurrence) => occurrence.capabilities.includes(capability))), 'capability switch must fetch scoped occurrence data');
    receipt.assertions++;
  }
  check(errors.length === 0, `browser errors: ${errors.join('; ')}`);
  await writeFile(`${artifactDirectory}/results.json`, JSON.stringify(receipt, null, 2));
  console.log(JSON.stringify(receipt, null, 2));
} catch (error) {
  if (page) {
    await page.screenshot({ path: `${artifactDirectory}/failure.png` }).catch(() => undefined);
    console.error(await page.locator("body").innerText().catch(() => "No page content"));
  }
  throw error;
} finally { await browser.close(); }
