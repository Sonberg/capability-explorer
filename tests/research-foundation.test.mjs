import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { canonical, readBounded, readEvidence, sha256, verifySealedCorpus } from '../scripts/research/evidence.mjs';
import { run, validateCorpus, validateRegister } from '../scripts/research/check-code-insights.mjs';
import { councilPacketHash, validateTool } from '../scripts/research/tool-gates.mjs';
import { compareIssueObservations, freezeIssueObservation } from '../scripts/research/issue-observations.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const load = path => JSON.parse(readFileSync(join(root, path), 'utf8'));
const register = () => load('docs/research/code-insights-research-register.json');
const corpus = () => load('config/research/foundation.json');
const h = '1'.repeat(40), source = '2'.repeat(40), head = '3'.repeat(40);

// Synthetic acceptance fixtures only. These are not scc results or real agent attestations.
function fixture(t) {
  const dir = mkdtempSync(join(tmpdir(), 'ce-research-test-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  function put(path, value) {
    const bytes = typeof value === 'string' ? value : JSON.stringify(value);
    mkdirSync(dirname(join(dir, path)), { recursive: true }); writeFileSync(join(dir, path), bytes);
    return { path, sha256: sha256(bytes) };
  }
  const oracle = put('input/oracle.json', { expected: ['test'] });
  const manifest = { schemaVersion: 1, baseline: h, files: [put('input/source.txt', 'test-only source')], oracle };
  const p = { id: 'test-tool', mode: 'inventory', role: 'inventory', gap: 'test-only gap',
    executionMethod: 'parser', epistemicStatus: 'observed', baseline: h, sourceCommit: source,
    criteria: ['file membership', 'source anchors', 'no unsupported fact promotion'],
    requiredCases: ['inventory', 'isolation', 'unknowns'],
    budget: { paidCalls: 0, minutes: 20, downloadGiB: 2, outputGiB: 1, reserveGiB: 10 },
    corpus: put('input/corpus.json', manifest), oracle };
  const subjectHash = sha256(canonical(p));
  const d = { schemaVersion: 1, proposal: p, references: [{ id: 'R1', url: 'https://example.com/source',
    kind: 'primary-source', status: 'inspected', claim: 'test only' }], authors: ['writer'],
    reviews: ['sme', 'adversary', 'advisor', 'judge'].map(role => ({ role, agent: `test-${role}`,
      subjectHash, references: ['R1'], brief: put(`briefs/${role}.md`, `${role} test-only review`) })),
    blockers: [], deskDecision: 'approve-trial' };
  const receipt = { schemaVersion: 1, subjectHash, exitCode: 0, baseline: h, sourceCommit: source,
    mode: p.mode, corpusHash: p.corpus.sha256, oracleHash: p.oracle.sha256,
    platform: 'test-only', command: 'never execute this label', sourceToArtifact: 'synthetic test fixture only',
    artifact: put('artifact.bin', 'test-only'), configuration: put('config.json', {}),
    dependencyClosure: put('closure.json', []), environment: put('env.json', { locale: 'C' }), budget: p.budget,
    safety: { sourceUnchanged: true, egressDenied: true, targetExecutionDenied: true },
    cases: p.requiredCases.map(id => ({ id, status: 'pass', skipped: false, evidence: put(`cases/${id}.txt`, 'test-only') })),
    runs: ['1', '2'].map(processId => ({ processId, conditions: 'test-only fresh process',
      facts: put(`runs/${processId}.json`, { file: 'test', count: 1 }), raw: put(`runs/${processId}.txt`, 'test-only raw') })) };
  function sealDesk() { d.reviews.find(r => r.role === 'judge').packetHash = councilPacketHash(d); }
  sealDesk();
  function sealReceipt() {
    d.trial = { subjectHash, receipt: put('trial/receipt.json', receipt) };
    d.introductionDecision = { verdict: 'approve-introduction-proposal', agent: 'test-judge',
      subjectHash, packetHash: councilPacketHash(d), receiptHash: d.trial.receipt.sha256, brief: put('briefs/decision.md', 'test-only introduction review'),
      hardGates: { license: true, safety: true, provenance: true, value: true } };
  }
  sealReceipt();
  d.pr = { base: 'main', head, tools: [p.id], modes: [p.mode], foundationMerged: true, optIn: true,
    rollback: 'disable test adapter', localCi: put('ci.txt', `head=${head}\ntarget=main\nbase=${h}\n`),
    prCheck: put('pr.txt', `pr-check: verified HEAD ${head} against main ${h}`) };
  return { dir, d, p, receipt, put, sealReceipt, sealDesk, manifest };
}

test('foundation verifies 17 historical modes and three hand-labelled source anchors', () => {
  assert.equal(validateRegister(register()), 17); assert.equal(validateCorpus(corpus(), root), 3);
  assert.match(run(['foundation'], root), /no tool\/runtime\/research completion claim/);
});

for (const [label, change, error] of [
  ['bad weights', r => { r.weights.D++; }, /total 100/],
  ['wrong total', r => { r.historicalModes[0].historicalDeskScore++; }, /incorrect historical/],
  ['missing dimension', r => { delete r.historicalModes[0].historicalRatings.D; }, /missing historical/],
  ['out-of-range rating', r => { r.historicalModes[0].historicalRatings.D = 6; }, /outside/],
  ['duplicate mode', r => { r.historicalModes.push(r.historicalModes[0]); }, /duplicate/],
  ['missing evidence class', r => { delete r.historicalModes[0].confidence; }, /class missing/],
  ['current rating copied from history', r => { r.historicalModes[0].currentAssessment = { assessmentId: 'new', role: r.historicalModes[0].role, inheritsHistoricalEvidence: true }; }, /inherited/],
  ['uncited current rating', r => { r.historicalModes[0].currentAssessment = { assessmentId: 'new', role: r.historicalModes[0].role, inheritsHistoricalEvidence: false, ratings: { D: { value: 5 } } }; }, /uncited/],
]) test(`register rejects ${label}`, () => { const r = register(); change(r); assert.throws(() => validateRegister(r), error); });

test('unknown current ratings remain unknown, with no manufactured rank', () => {
  const r = register(), m = r.historicalModes[0];
  m.currentAssessment = { assessmentId: 'new', role: m.role, inheritsHistoricalEvidence: false,
    ratings: Object.fromEntries(Object.keys(r.weights).map(k => [k, { value: null }])) };
  assert.equal(validateRegister(r), 17);
});

test('current desk ratings require registered nonhistorical references and cannot assert measurements', () => {
  const r = register(), m = r.historicalModes[0];
  m.currentAssessment = { assessmentId: 'new', role: m.role, inheritsHistoricalEvidence: false,
    ratings: Object.fromEntries(Object.keys(r.weights).map(k => [k, { value: null }])) };
  const rating = m.currentAssessment.ratings.D = { value: 5, reference: 'L01', evidenceClass: 'primary-doc' };
  assert.equal(validateRegister(r), 17);
  for (const reference of ['made-up', 'R01']) {
    rating.reference = reference; assert.throws(() => validateRegister(r), /unknown\/historical/);
  }
  rating.reference = 'L01'; rating.evidenceClass = 'measured';
  assert.throws(() => validateRegister(r), /trial-bound/);
});

for (const [label, change, error] of [
  ['wrong corpus hash', c => { c.files[0].sha256 = '0'.repeat(64); }, /changed evidence/],
  ['duplicate path', c => { c.files[1] = c.files[0]; }, /duplicate corpus/],
  ['out-of-root path', c => { c.files[0].path = '../outside'; }, /unsafe/],
  ['issue completion claim', c => { c.issueComplete = true; }, /scope invalid/],
]) test(`corpus rejects ${label}`, () => { const c = corpus(); change(c); assert.throws(() => validateCorpus(c, root), error); });

test('evidence rejects symlink components, directories, large files and tampering', t => {
  const f = fixture(t);
  symlinkSync(join(f.dir, 'input'), join(f.dir, 'link'));
  assert.throws(() => readBounded(f.dir, 'link/corpus.json'), /symlink/);
  assert.throws(() => readBounded(f.dir, 'input'), /regular file/);
  const ref = f.put('large', 'x'.repeat(8 * 1024 * 1024 + 1));
  assert.throws(() => readEvidence(f.dir, ref), /8MiB/);
  writeFileSync(join(f.dir, 'input/corpus.json'), 'changed');
  assert.throws(() => readEvidence(f.dir, f.p.corpus), /changed evidence/);
  for (const path of ['/etc/passwd', 'input/../oracle.json', 'input\\oracle.json', './input', 'input//corpus.json'])
    assert.throws(() => readBounded(f.dir, path), /unsafe/);
});

test('synthetic tool fixtures separately pass desk, trial, decision and PR record gates', t => {
  const f = fixture(t);
  for (const stage of ['desk', 'trial', 'decision', 'pr']) assert.equal(validateTool(f.d, stage, f.dir).stage, stage);
});

for (const [label, change, stage, error] of [
  ['conflicting agents', f => { f.d.reviews[0].agent = f.d.reviews[3].agent; }, 'desk', /agent conflict/],
  ['missing role', f => { f.d.reviews.pop(); }, 'desk', /reviews/],
  ['author judges own tool', f => { f.d.authors.push('test-judge'); }, 'desk', /author cannot judge/],
  ['stale proposal review', f => { f.p.gap = 'changed after review'; }, 'desk', /stale council/],
  ['unknown reference', f => { f.d.reviews[0].references = ['missing']; }, 'desk', /unknown review reference/],
  ['missing brief', f => { f.d.reviews[1].brief.path = 'missing'; }, 'desk', /ENOENT/],
  ['mixed epistemic status', f => { f.p.epistemicStatus = 'observed+inferred'; }, 'desk', /mixed evidence/],
  ['open blocker', f => { f.d.blockers = [{ id: 'B1', reason: 'unsafe', status: 'open' }]; f.sealDesk(); }, 'desk', /unresolved/],
  ['unproven resolution', f => { f.d.blockers = [{ id: 'B1', reason: 'unsafe', status: 'resolved' }]; }, 'desk', /missing evidence/],
  ['invalid URL', f => { f.d.references[0].url = 'file:///private'; }, 'desk', /unsafe reference/],
  ['unavailable source asserted measured', f => { Object.assign(f.d.references[0], { kind: 'measured', status: 'unavailable' }); }, 'desk', /inaccessible/],
  ['paid call budget', f => { f.p.budget.paidCalls = 1; }, 'desk', /budget/],
  ['string-valued budget', f => { f.p.budget.minutes = '20'; }, 'desk', /budget/],
  ['changed reference with stable id', f => { f.d.references[0].claim = 'different claim'; }, 'desk', /stale judge council packet/],
  ['changed reference URL', f => { f.d.references[0].url = 'https://example.com/other'; }, 'desk', /stale judge council packet/],
  ['silent defer promotion', f => { f.d.deskDecision = 'defer'; f.sealDesk(); f.d.deskDecision = 'approve-trial'; }, 'desk', /stale judge council packet/],
  ['silent blocker deletion', f => { f.d.deskDecision = 'defer'; f.d.blockers = [{ id: 'B1', reason: 'unsafe', status: 'open' }]; f.sealDesk(); f.d.blockers = []; }, 'desk', /stale judge council packet/],
  ['fabricated pass declaration', f => { f.d.trial = { passed: true }; }, 'trial', /missing\/stale trial/],
  ['stale binary', f => { writeFileSync(join(f.dir, 'artifact.bin'), 'changed'); }, 'trial', /changed evidence/],
  ['stale source behind unchanged manifest', f => { writeFileSync(join(f.dir, 'input/source.txt'), 'changed source'); }, 'trial', /changed evidence/],
  ['wrong corpus', f => { f.receipt.corpusHash = '0'.repeat(64); f.sealReceipt(); }, 'trial', /wrong trial identity/],
  ['wrong source', f => { f.receipt.sourceCommit = h; f.sealReceipt(); }, 'trial', /wrong trial identity/],
  ['missing cases', f => { f.receipt.cases.pop(); f.sealReceipt(); }, 'trial', /invalid executed cases|missing\/duplicate/],
  ['skipped case', f => { f.receipt.cases[0].skipped = true; f.sealReceipt(); }, 'trial', /skipped/],
  ['failed process', f => { f.receipt.exitCode = 1; f.sealReceipt(); }, 'trial', /invalid trial receipt/],
  ['unobserved isolation', f => { f.receipt.safety.egressDenied = false; f.sealReceipt(); }, 'trial', /safety/],
  ['reused process', f => { f.receipt.runs[1].processId = '1'; f.sealReceipt(); }, 'trial', /distinct processes/],
  ['nonrepeatable facts', f => { f.receipt.runs[1].facts = f.put('runs/other.json', { file: 'other' }); f.sealReceipt(); }, 'trial', /nonrepeatable/],
  ['judge not bound to trial', f => { f.d.introductionDecision.receiptHash = '0'.repeat(64); }, 'decision', /stale independent/],
  ['failed license gate', f => { f.d.introductionDecision.hardGates.license = false; }, 'decision', /hard gate/],
  ['multiple tools', f => { f.d.pr.tools.push('other'); }, 'pr', /bundled/],
  ['multiple modes', f => { f.d.pr.modes.push('model'); }, 'pr', /bundled/],
  ['wrong PR base', f => { f.d.pr.base = 'stage-1-contract-and-fixtures'; }, 'pr', /wrong PR base/],
  ['unmerged foundation', f => { f.d.pr.foundationMerged = false; }, 'pr', /unmerged foundation/],
  ['missing rollback', f => { f.d.pr.rollback = ''; }, 'pr', /rollback/],
  ['stale local CI', f => { f.d.pr.head = source; }, 'pr', /local CI identity/],
  ['stale PR check', f => { f.d.pr.prCheck = f.put('pr.txt', 'pr-check: verified some old SHA'); }, 'pr', /PR-check identity/],
]) test(`tool gate rejects ${label}`, t => { const f = fixture(t); change(f); assert.throws(() => validateTool(f.d, stage, f.dir), error); });

test('sealed corpus needs versioned unique file references and matching oracle bytes', t => {
  const f = fixture(t);
  assert.equal(verifySealedCorpus(f.dir, f.manifest), 1);
  assert.throws(() => verifySealedCorpus(f.dir, { files: ['test'] }), /identity/);
  assert.throws(() => verifySealedCorpus(f.dir, { ...f.manifest, files: ['test'] }), /digest/);
  assert.throws(() => verifySealedCorpus(f.dir, { ...f.manifest, files: [f.manifest.files[0], f.manifest.files[0]] }), /duplicate/);
  writeFileSync(join(f.dir, 'input/oracle.json'), 'changed oracle');
  assert.throws(() => verifySealedCorpus(f.dir, f.manifest), /changed evidence/);
});

for (const verdict of ['defer', 'reject', 'retain']) test(`${verdict} is complete desk evidence, never trial success`, t => {
  const f = fixture(t); f.d.deskDecision = verdict; f.sealDesk(); delete f.d.trial;
  assert.equal(validateTool(f.d, 'desk', f.dir).verdict, verdict);
  for (const stage of ['trial', 'decision', 'pr']) assert.throws(() => validateTool(f.d, stage, f.dir), /not a trial pass/);
});

const observationNow = '2026-11-10T02:45:00.000Z';
const syntheticIssue = (number, state = 'open') => ({
  nodeID: `synthetic-node-${number}`, number, state, title: `Synthetic item ${number}`,
  body: 'Synthetic café 🦉\r\n- unchecked source text\r\n', updatedAt: '2026-11-10T02:40:00.000Z',
});
function observation(issues = [syntheticIssue(1), syntheticIssue(2)]) {
  return {
    context: { repoID: 'synthetic-repo', owner: 'synthetic-owner', name: 'synthetic-project',
      sourceCommit: h, sourceInputSha256: 'a'.repeat(64) },
    provenance: 'synthetic', scope: { state: 'all', filter: 'issues only; no additional filter' },
    bounds: { maxPages: 4, maxIssues: 8, maxUtf8Bytes: 16384, maxAgeMs: 600000, maxDurationMs: 60000 },
    capture: { startedAt: '2026-11-10T02:40:00.000Z', endedAt: '2026-11-10T02:40:01.000Z',
      status: 'observed', terminal: 'observed', truncated: false },
    pages: [{ number: 1, cursor: null, nextCursor: null, status: 'observed', issues }],
  };
}
const freezeObservation = value => freezeIssueObservation(value, observationNow);

test('issue observations detach literal source, normalize only record order and recheck integrity', () => {
  const input = observation(), frozen = freezeObservation(input);
  const reversed = observation(structuredClone(input.pages[0].issues).reverse());
  assert.deepEqual(freezeObservation(reversed), frozen);
  input.pages[0].issues[0].body = 'caller mutation';
  input.context.owner = 'changed-owner';
  assert.equal(frozen.declaration.pages[0].issues[0].body, syntheticIssue(1).body);
  assert.equal(frozen.bodyDigests[0].sha256, sha256(syntheticIssue(1).body));
  assert.equal(Object.isFrozen(frozen.declaration.pages[0].issues[0]), true);
  const same = compareIssueObservations(frozen, freezeObservation(reversed), observationNow);
  assert.deepEqual(same.bodyChanged, []); assert.deepEqual(same.metadataChanged, []);
  assert.deepEqual(same.uncertainty, []); assert.equal(same.approval, 'not-established');
  const tampered = structuredClone(frozen);
  tampered.declaration.pages[0].issues[0].body = 'changed after sealing';
  assert.throws(() => compareIssueObservations(tampered, frozen, observationNow), /snapshot integrity/);
  const context = structuredClone(frozen); context.declaration.context.repoID = 'other-repo';
  assert.throws(() => compareIssueObservations(context, frozen, observationNow), /snapshot integrity/);
  const digest = structuredClone(frozen); digest.bodyDigests[0].sha256 = 'f'.repeat(64);
  assert.throws(() => compareIssueObservations(digest, frozen, observationNow), /snapshot integrity/);
});

test('issue observations retain closed, reopened, new, missing, body and metadata changes independently', () => {
  const before = observation([syntheticIssue(1), syntheticIssue(2, 'closed'), syntheticIssue(4)]);
  const after = observation([syntheticIssue(1, 'closed'), syntheticIssue(2), syntheticIssue(3)]);
  after.pages[0].issues[0].body += 'changed source';
  after.pages[0].issues[0].title = 'Different synthetic title';
  const a = freezeObservation(before), b = freezeObservation(after);
  const delta = compareIssueObservations(a, b, observationNow);
  for (const [field, expected] of [['new', [3]], ['missingFromObservedScope', [4]],
    ['closed', [1]], ['reopened', [2]], ['bodyChanged', [1]], ['metadataChanged', [1]]])
    assert.deepEqual(delta[field].map(item => item.number), expected);
  assert.notEqual(a.bodyDigests[0].sha256, b.bodyDigests[0].sha256);
  assert.equal(delta.compatible, true); assert.equal(delta.approval, 'not-established');
});

test('issue observations never infer missing from truncated, nonterminal or incompatible scope captures', () => {
  const before = freezeObservation(observation());
  for (const mode of ['truncated', 'nonterminal', 'open-only', 'filter-change']) {
    const after = observation([syntheticIssue(1)]);
    if (mode === 'truncated') after.capture.truncated = true;
    if (mode === 'nonterminal') {
      after.pages[0].nextCursor = 'synthetic-next'; after.capture.terminal = 'unknown';
    }
    if (mode === 'open-only') after.scope.state = 'open';
    if (mode === 'filter-change') after.scope.filter = 'synthetic different filter';
    const delta = compareIssueObservations(before, freezeObservation(after), observationNow);
    assert.deepEqual(delta.missingFromObservedScope, [], mode);
    assert.ok(delta.uncertainty.length > 0, mode);
  }
  const otherRepo = observation(); otherRepo.context.repoID = 'different-synthetic-repo';
  const incompatible = compareIssueObservations(before, freezeObservation(otherRepo), observationNow);
  assert.equal(incompatible.compatible, false);
  assert.ok(incompatible.uncertainty.includes('incompatible-repository'));
});

test('issue observations bind a contiguous page chain, not page order normalization', () => {
  const input = observation([syntheticIssue(1)]);
  input.pages[0].nextCursor = 'synthetic-page-two';
  input.pages.push({ number: 2, cursor: 'synthetic-page-two', nextCursor: null,
    status: 'observed', issues: [syntheticIssue(2)] });
  const frozen = freezeObservation(input);
  assert.deepEqual(frozen.bodyDigests.map(i => i.number), [1, 2]);
  assert.deepEqual(compareIssueObservations(frozen, frozen, observationNow).uncertainty, []);
  input.pages.reverse();
  assert.throws(() => freezeObservation(input), /page chain/);
});

test('issue observations do not compare different declared source inputs, bounds or provenance as compatible', () => {
  const before = freezeObservation(observation());
  for (const [change, reason] of [
    [d => { d.context.sourceCommit = 'b'.repeat(40); }, 'incompatible-source-inputs'],
    [d => { d.context.sourceInputSha256 = 'b'.repeat(64); }, 'incompatible-source-inputs'],
    [d => { d.bounds.maxIssues = 9; }, 'incompatible-bounds'],
    [d => { d.provenance = 'declared'; }, 'incompatible-provenance'],
  ]) {
    const after = observation([syntheticIssue(1), syntheticIssue(3)]);
    after.pages[0].issues[0].body = 'different declared body';
    change(after);
    const delta = compareIssueObservations(before, freezeObservation(after), observationNow);
    assert.equal(delta.compatible, false, reason);
    assert.ok(delta.uncertainty.includes(reason));
    for (const field of ['new', 'missingFromObservedScope', 'bodyChanged']) assert.deepEqual(delta[field], []);
  }
});

test('issue observations do not promote records on unavailable pages into observed changes', () => {
  const before = freezeObservation(observation());
  const after = observation([syntheticIssue(1, 'closed')]);
  after.pages[0].issues[0].body = 'unavailable cached body';
  after.capture.status = after.capture.terminal = after.pages[0].status = 'unavailable';
  const delta = compareIssueObservations(before, freezeObservation(after), observationNow);
  assert.ok(delta.uncertainty.includes('after:page:1:unavailable'));
  for (const field of ['new', 'missingFromObservedScope', 'closed', 'bodyChanged']) assert.deepEqual(delta[field], []);
});

test('issue observations retain common observed changes while later pages are unavailable', () => {
  const before = freezeObservation(observation());
  const after = observation([syntheticIssue(1, 'closed')]);
  after.pages[0].issues[0].body = 'observed changed body';
  after.pages[0].nextCursor = 'synthetic-unavailable-page';
  after.pages.push({ number: 2, cursor: 'synthetic-unavailable-page', nextCursor: null, status: 'unavailable', issues: [] });
  after.capture.terminal = 'unavailable';
  const delta = compareIssueObservations(before, freezeObservation(after), observationNow);
  assert.ok(delta.uncertainty.includes('after:page:2:unavailable'));
  assert.deepEqual(delta.closed.map(i => i.number), [1]);
  assert.deepEqual(delta.bodyChanged.map(i => i.number), [1]);
  assert.deepEqual(delta.missingFromObservedScope, []);
});

test('issue observations preserve explicit unavailable states and freshness/duration uncertainty', () => {
  const before = freezeObservation(observation());
  for (const status of ['absent', 'denied', 'unavailable', 'unknown', 'unsupported', 'stale', 'unstable']) {
    const after = observation([]);
    after.capture.status = status; after.capture.terminal = status;
    after.pages = [];
    const frozen = freezeObservation(after);
    assert.equal(frozen.declaration.capture.status, status);
    const delta = compareIssueObservations(before, frozen, observationNow);
    assert.ok(delta.uncertainty.includes(`after:capture:${status}`));
    assert.deepEqual(delta.missingFromObservedScope, []);
  }
  const stale = observation([]); stale.bounds.maxAgeMs = 100;
  assert.ok(compareIssueObservations(before, freezeObservation(stale), observationNow)
    .uncertainty.includes('after:stale'));
  const slow = observation([]); slow.bounds.maxDurationMs = 100;
  assert.ok(compareIssueObservations(before, freezeObservation(slow), observationNow)
    .uncertainty.includes('after:capture-duration'));
  const page = observation([]); page.pages[0].status = 'denied'; page.capture.terminal = 'denied';
  assert.ok(compareIssueObservations(before, freezeObservation(page), observationNow)
    .uncertainty.includes('after:page:1:denied'));
});

test('issue observations reject malformed identities, duplicate chains, invalid time and unsafe budgets', () => {
  for (const [change, error] of [
    [d => { d.pages[0].issues[1].number = 1; }, /duplicate issue number/],
    [d => { d.pages[0].issues[1].nodeID = d.pages[0].issues[0].nodeID; }, /duplicate issue node/],
    [d => { d.pages.push(structuredClone(d.pages[0])); }, /page chain/],
    [d => { d.pages[0].nextCursor = 'loop'; d.pages.push({ ...d.pages[0], number: 2, cursor: 'loop', issues: [] }); }, /cursor/],
    [d => { d.capture.endedAt = 'not-a-date'; }, /date/],
    [d => { d.capture.endedAt = '2026-11-10T02:39:00.000Z'; }, /interval/],
    [d => { d.bounds.maxIssues = Infinity; }, /budget/],
    [d => { d.bounds.maxPages = 0; }, /budget/],
    [d => { d.bounds.maxIssues = 1; }, /issue budget/],
    [d => { d.bounds.maxDurationMs = Number.MAX_SAFE_INTEGER + 1; }, /budget/],
    [d => { d.bounds.maxUtf8Bytes = 1; }, /UTF8 byte budget/],
    [d => { d.pages[0].issues[0].body = '\ud800'; }, /Unicode/],
    [d => { d.pages[0].issues[0].state = 'done'; }, /issue state/],
    [d => { d.context.repoID = ''; }, /repoID/],
    [d => { d.pages[0].nextCursor = 'not-terminal'; }, /terminal/],
  ]) {
    const d = observation(); change(d); assert.throws(() => freezeObservation(d), error);
  }
  const before = freezeObservation(observation()), after = observation();
  after.pages[0].issues[0].nodeID = 'synthetic-replacement';
  assert.throws(() => compareIssueObservations(before, freezeObservation(after), observationNow), /issue identity mapping/);
  const renumbered = observation(); renumbered.pages[0].issues[0].number = 3;
  assert.throws(() => compareIssueObservations(before, freezeObservation(renumbered), observationNow), /issue identity mapping/);
  assert.throws(() => freezeIssueObservation(observation()), /date/);
});

test('CLI errors and global research gates cannot be hidden by passing foundation', () => {
  for (const args of [[], ['bogus'], ['foundation', '--skip'], ['tool', '--id', '../escape', '--stage', 'desk'],
    ['specification'], ['pseudocode'], ['architecture'], ['refinement'], ['completion'], ['all']]) assert.throws(() => run(args, root));
  const cli = join(root, 'scripts/research/check-code-insights.mjs');
  const pass = spawnSync(process.execPath, [cli, 'foundation'], { cwd: tmpdir(), encoding: 'utf8' });
  assert.equal(pass.status, 0, pass.stderr);
  const fail = spawnSync(process.execPath, [cli, 'all'], { encoding: 'utf8' });
  assert.equal(fail.status, 1); assert.match(fail.stderr, /gate remains incomplete/);
});
