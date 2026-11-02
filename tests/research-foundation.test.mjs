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

test('CLI errors and global research gates cannot be hidden by passing foundation', () => {
  for (const args of [[], ['bogus'], ['foundation', '--skip'], ['tool', '--id', '../escape', '--stage', 'desk'],
    ['specification'], ['pseudocode'], ['architecture'], ['refinement'], ['completion'], ['all']]) assert.throws(() => run(args, root));
  const cli = join(root, 'scripts/research/check-code-insights.mjs');
  const pass = spawnSync(process.execPath, [cli, 'foundation'], { cwd: tmpdir(), encoding: 'utf8' });
  assert.equal(pass.status, 0, pass.stderr);
  const fail = spawnSync(process.execPath, [cli, 'all'], { encoding: 'utf8' });
  assert.equal(fail.status, 1); assert.match(fail.stderr, /gate remains incomplete/);
});
