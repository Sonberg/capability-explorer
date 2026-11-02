import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { run, validateCatalogue } from '../scripts/check-platform-contract-plan.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const plan = () => JSON.parse(readFileSync(join(root, 'config/platform/exchange-contracts.json'), 'utf8'));

test('catalogue_preserves_draft_scope', () => {
  assert.deepEqual(validateCatalogue(plan()), []);
  for (const phase of ['specification', 'pseudocode', 'architecture', 'foundation']) {
    assert.match(run([phase], root), /seven boundaries\/three draft profiles checked/);
    assert.match(run([phase], root), /no byte, authorization, runtime or full-study claim/);
  }
});

test('catalogue_rejects_missing_boundary_or_runtime_claim', () => {
  const missing = plan(); missing.boundaries.pop();
  assert.match(validateCatalogue(missing).join('\n'), /seven unique boundaries|team-feedback missing/);
  for (const key of Object.keys(plan().implemented)) {
    const claimed = plan(); claimed.implemented[key] = true;
    assert.match(validateCatalogue(claimed).join('\n'), /cannot claim implemented runtime/);
  }
});

for (const [name, change, expected] of [
  ['wrong-scope', p => { p.scope = 'production'; }, /draft scope/],
  ['branch-as-revision', p => { p.baseline = 'main'; }, /full commit/],
  ['ce-v1-namespace-overload', p => { p.wireFamily = 'ce.v1'; }, /namespaces/],
  ['payload-upgrade', p => { p.payloadFamily = 'ce.v2'; }, /namespaces/],
  ['schema-path-redirection', p => { p.schema = '../elsewhere'; }, /schema path/],
  ['compatibility-claim', p => { p.profileStatus = 'stable'; }, /draft\/format/],
  ['annotation-only-format', p => { p.formatAssertionsRequired = false; }, /format assertion/],
  ['unknown-runtime-capability', p => { p.implemented.remoteService = false; }, /runtime capabilities/],
  ['canonicalizer-implemented-claim', p => { p.canonicalization.status = 'implemented'; }, /canonicalization/],
  ['whole-envelope-hash', p => { p.canonicalization.subject = 'whole-envelope'; }, /digest boundary/],
  ['attempt-in-content-hash', p => { p.canonicalization.excluded = []; }, /digest boundary/],
  ['duplicate-boundary', p => { p.boundaries[6] = p.boundaries[0]; }, /unique boundaries/],
  ['profile-on-unimplemented-product', p => { p.boundaries[4].profileKind = 'product'; }, /profile assignment/],
  ['delivered-product-claim', p => { p.boundaries[4].status = 'runtime-delivered'; }, /runtime delivery/],
  ['business-capability-overload', p => { p.terminology.existingCapabilityRole = 'business-function'; }, /Capability meaning/],
  ['missing-terminology', p => { p.terminology.draftTerms.pop(); }, /terminology incomplete/],
  ['missing-criterion', p => { p.acceptance.pop(); }, /six unique acceptance/],
  ['no-negative-case', p => { p.acceptance[0].negative = ''; }, /positive\/negative trace/],
  ['authority-gap-hidden', p => { p.futureSemanticChecks = []; }, /semantic obligations/],
]) test(`catalogue rejects ${name}`, () => {
  const value = plan(); change(value);
  assert.match(validateCatalogue(value).join('\n'), expected);
});

test('unknown and runtime phases cannot borrow foundation success', () => {
  for (const args of [[], ['all'], ['completion'], ['refinement'], ['foundation', '--skip'], ['production']]) {
    assert.throws(() => run(args, root), /runtime completion is not implemented/);
  }
});

test('source schema drift is caught independently of catalogue declarations', t => {
  const dir = mkdtempSync(join(tmpdir(), 'ce-platform-plan-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(join(dir, 'config/platform'), { recursive: true });
  mkdirSync(join(dir, 'schema/platform/v1'), { recursive: true });
  writeFileSync(join(dir, 'config/platform/exchange-contracts.json'), JSON.stringify(plan()));
  const schema = JSON.parse(readFileSync(join(root, 'schema/platform/v1/envelope.schema.json'), 'utf8'));
  schema.$defs.evidenceBundle.additionalProperties = true;
  writeFileSync(join(dir, 'schema/platform/v1/envelope.schema.json'), JSON.stringify(schema));
  assert.throws(() => run(['architecture'], dir), /schema profile evidenceBundle mismatch/);
  schema.$defs.evidenceBundle.additionalProperties = false;
  schema.$schema = 'https://json-schema.org/draft-07/schema';
  writeFileSync(join(dir, 'schema/platform/v1/envelope.schema.json'), JSON.stringify(schema));
  assert.throws(() => run(['architecture'], dir), /identity\/dialect mismatch/);
});

test('CLI resolves its own repository and exits nonzero for unsupported completion', () => {
  const script = join(root, 'scripts/check-platform-contract-plan.mjs');
  const pass = spawnSync(process.execPath, [script, 'foundation'], { cwd: tmpdir(), encoding: 'utf8' });
  assert.equal(pass.status, 0, pass.stderr);
  const fail = spawnSync(process.execPath, [script, 'completion'], { cwd: tmpdir(), encoding: 'utf8' });
  assert.equal(fail.status, 1); assert.match(fail.stderr, /runtime completion is not implemented/);
});
