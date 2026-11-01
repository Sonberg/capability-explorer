import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import test from 'node:test';
import { canonical, checkAssertions, checkInputIdentities, equalRuns, hash, inside, inventory, replayOutputPath, semanticRecord, snippet, verifyEvidence, verifyLockedFiles } from '../scripts/mapping-replay-lib.mjs';

const oldNode = { kind: 'node', id: 'n:old', nodeKind: 'http-endpoint', symbolKey: 'http|old', name: 'old', evidence: ['ev:old'] };
const newNode = { kind: 'node', id: 'n:new', nodeKind: 'http-endpoint', symbolKey: 'http|new', name: 'new', evidence: ['ev:new'] };
const expectations = [
  { name: 'old target removed', selector: { kind: 'node', symbolKey: 'http|old' }, baseline: 1, candidate: 0 },
  { name: 'new target identified', selector: { kind: 'node', symbolKey: 'http|new' }, baseline: 0, candidate: 1 },
];
const expectedChanges = { removed: [semanticRecord(oldNode)], added: [semanticRecord(newNode)] };
function temporary(t) {
  const path = mkdtempSync(resolve(tmpdir(), 'mapping-replay-'));
  t.after(() => rmSync(path, { recursive: true, force: true })); return path;
}

test('named exact counts explain a bounded replacement', () => {
  const report = checkAssertions([oldNode], [newNode], expectations, expectedChanges);
  assert.equal(report.assertions.length, 2); assert.equal(report.changedRecords, 2);
});
test('unasserted props on an allowed replacement fail the exact delta inventory', () => {
  assert.throws(() => checkAssertions([oldNode], [{ ...newNode, props: { guessed: true } }], expectations,
    expectedChanges), /exact semantic change inventory/);
});
test('nested semantic id and evidence props survive normalization', () => {
  const record = { ...oldNode, props: { id: 'domain-id', evidence: 'domain-value' } };
  assert.deepEqual(semanticRecord(record).props, record.props);
});
test('multiset comparison preserves duplicate record cardinality', () => {
  assert.throws(() => checkAssertions([oldNode, oldNode], [newNode], [
    { ...expectations[0], baseline: 2 }, expectations[1],
  ], expectedChanges), /exact semantic change inventory/);
});
test('unrelated lost record fails despite passing named assertions', () => {
  assert.throws(() => checkAssertions([oldNode, { ...oldNode, symbolKey: 'http|keep' }], [newNode], expectations), /unexplained semantic change/);
});
test('same-count property regression cannot hide under an unchanged assertion', () => {
  const assertion = [{ name: 'stable endpoint', selector: { kind: 'node', symbolKey: 'http|old' }, baseline: 1, candidate: 1 }];
  assert.throws(() => checkAssertions([oldNode], [{ ...oldNode, name: 'regressed' }], assertion), /exact semantic change inventory/);
});
test('zero, duplicate, broad and empty assertions fail closed', () => {
  for (const assertions of [[], [expectations[0], expectations[0]], [{ name: 'all', selector: { kind: 'node' }, baseline: 1, candidate: 0 }],
    [{ name: 'absent', selector: { kind: 'node', name: 'missing' }, baseline: 0, candidate: 0 }]]) {
    assert.throws(() => checkAssertions([oldNode], [newNode], assertions));
  }
});
test('incorrect named count fails', () => {
  assert.throws(() => checkAssertions([oldNode], [newNode], [{ ...expectations[0], baseline: 2 }]), /expected 2\/0, got 1\/0/);
});
test('full to partial coverage regression fails', () => {
  const complete = { kind: 'complete', status: 'full', counts: {}, coverage: { filesTotal: 1, filesAnalyzed: 1 } };
  assert.throws(() => checkAssertions([oldNode, complete], [newNode, { ...complete, status: 'partial' }], expectations,
    expectedChanges), /coverage/);
});
test('repeat proof compares filenames and every output byte hash', () => {
  equalRuns({ 'a.jsonl': hash('same') }, { 'a.jsonl': hash('same') });
  assert.throws(() => equalRuns({}, {}), /missing artifacts/);
  assert.throws(() => equalRuns({ 'a.jsonl': hash('same') }, { 'a.jsonl': hash('changed') }), /nondeterministic/);
  assert.throws(() => equalRuns({ 'a.jsonl': hash('same') }, { 'b.jsonl': hash('same') }), /nondeterministic/);
});
test('locked inventory rejects changed, missing and extra inputs', (t) => {
  const dir = temporary(t); writeFileSync(resolve(dir, 'source.ts'), 'first');
  const locked = inventory(dir); verifyLockedFiles(dir, locked);
  writeFileSync(resolve(dir, 'source.ts'), 'second'); assert.throws(() => verifyLockedFiles(dir, locked), /locked inputs differ/);
  rmSync(resolve(dir, 'source.ts')); assert.throws(() => verifyLockedFiles(dir, locked), /locked inputs differ/);
  writeFileSync(resolve(dir, 'source.ts'), 'first'); writeFileSync(resolve(dir, 'extra.ts'), 'extra');
  assert.throws(() => verifyLockedFiles(dir, locked), /locked inputs differ/);
});
test('absolute, escaping, and symlink paths are rejected', (t) => {
  const dir = temporary(t); mkdirSync(resolve(dir, 'source')); symlinkSync(tmpdir(), resolve(dir, 'source/link'));
  assert.throws(() => inside(dir, '/etc/passwd'), /relative path/);
  assert.throws(() => inside(dir, '../escape'), /escapes root/);
  assert.throws(() => inside(dir, 'source/link/anything'), /symlink/);
  assert.throws(() => inventory(resolve(dir, 'source')), /symlink/);
});
test('output traversal cannot leave the replay cache while remaining inside the checkout', (t) => {
  const dir = temporary(t);
  for (const path of ['.sandbox-cache/mapping-replay/../../other-dir',
    '.sandbox-cache/mapping-replay/../mapping-replay-other/result',
    '.sandbox-cache/mapping-replay/candidate/..']) {
    assert.throws(() => replayOutputPath(dir, path), /output must resolve under/);
  }
  assert.equal(replayOutputPath(dir, '.sandbox-cache/mapping-replay/nested/../candidate'),
    resolve(dir, '.sandbox-cache/mapping-replay/candidate'));
});
test('source evidence validates exact UTF-16 spans, revision, path and snippet', (t) => {
  const dir = temporary(t), source = 'const emoji = "😀";\r\nsecond line\n';
  writeFileSync(resolve(dir, 'a.ts'), source);
  const manifest = { repositoryId: 'mapping-replay', commitSha: 'a'.repeat(40), files: { 'a.ts': hash(source) } };
  const header = { kind: 'header', repositoryId: manifest.repositoryId, commitSha: manifest.commitSha };
  const span = { startLine: 1, startColumn: 15, endLine: 2, endColumn: 7 };
  const evidence = { ...header, kind: 'evidence', path: 'a.ts', span, snippetSha256: hash(snippet(source, span)) };
  assert.equal(verifyEvidence([header, evidence], dir, manifest), 1);
  for (const mutation of [{ commitSha: 'b'.repeat(40) }, { path: '../a.ts' }, { snippetSha256: hash('wrong') },
    { span: { ...span, endLine: 100 } }, { span: { ...span, startColumn: 0 } }]) {
    assert.throws(() => verifyEvidence([header, { ...evidence, ...mutation }], dir, manifest));
  }
  assert.throws(() => verifyEvidence([header], dir, manifest), /no source evidence/);
});
test('canonical inventories do not depend on property insertion order', () => {
  assert.equal(canonical({ z: 1, a: { y: 2, b: 3 } }), canonical({ a: { b: 3, y: 2 }, z: 1 }));
});
test('only an exact reviewed input identity transition bound to locked physical source is accepted', () => {
  const sourceHash = 'c'.repeat(64);
  const before = { language: 'ts', artifactLanguage: 'typescript', unit: 'ts/tsconfig.json', inputHash: 'a'.repeat(64) };
  const after = { ...before, inputHash: 'b'.repeat(64) };
  const transition = { name: 'include static config', language: 'typescript', unit: before.unit,
    baseline: before.inputHash, candidate: after.inputHash, lockedSourceHash: sourceHash };
  assert.equal(checkInputIdentities([before], [after], [transition], sourceHash).length, 1);
  assert.deepEqual(checkInputIdentities([before], [before], [], sourceHash), []);
  assert.throws(() => checkInputIdentities([before], [after], [], sourceHash), /unexpected input hash change/);
  for (const change of [{ inputHash: 'd'.repeat(64) }, { unit: 'other/tsconfig.json' }, { artifactLanguage: 'javascript' }]) {
    assert.throws(() => checkInputIdentities([before], [{ ...after, ...change }], [transition], sourceHash));
  }
  assert.throws(() => checkInputIdentities([before], [after], [transition], 'd'.repeat(64)), /unbound/);
  assert.throws(() => checkInputIdentities([before], [before], [transition], sourceHash), /unused/);
  assert.throws(() => checkInputIdentities([after], [before], [transition], sourceHash), /unexpected input hash change/);
});
