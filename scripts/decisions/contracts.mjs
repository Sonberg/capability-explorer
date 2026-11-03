import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { key, relationError, validateCycles } from './semantics.mjs';
import { strictJSON } from './json-source.mjs';

export const readJSON = path => strictJSON(readFileSync(path, 'utf8'));
export function specification(contract) {
  assert.equal(contract.version, 'decision-ledger.v1', 'specification version');
  assert.deepEqual(contract.requirements.map(r => r.id).sort(), [1, 2, 3, 4, 5, 6, 7], 'seven unique obligations');
  for (const r of contract.requirements) {
    for (const field of ['question', 'boundary']) assert.ok(r[field]?.length > 20, `${r.id}: ${field}`);
    assert.ok(r.tests.length >= 1 && r.tests.every(t => typeof t === 'string' && t.length), 'test traceability');
    assert.ok(['reject', 'unresolved', 'retain'].includes(r.negative), 'negative outcome');
  }
  assert.deepEqual([...contract.branchPolicy.required].sort(), [
    'freshExactHead', 'mainBasedMergedPR', 'mergedResultReachableMain', 'noOpenPR', 'notAdvanced',
    'notShared', 'notProtected', 'notDefaultReleaseStage', 'noActiveClaim', 'durableRecovery',
    'explicitSquashRebaseProvenance', 'immediateExactSHARecheck', 'separateAuthorization'
  ].sort(), 'guarded branch eligibility');
  assert.equal(contract.branchPolicy.unknown, 'retain');
  assert.equal(contract.branchPolicy.executor, 'lead-only');
}

export function branchEligibility(facts, policy) {
  return policy.required.every(key => facts[key] === true) ? 'eligible-for-lead-recheck' : 'retain';
}

export function pseudocode(examples) {
  assert.deepEqual(examples.precedence, ['source-bytes', 'adapter-metadata', 'explicit-status-map',
    'reviewed-sidecar', 'validation', 'projection']);
  assert.ok(examples.cases.length >= 6, 'positive and negative executable cases');
  for (const example of examples.cases) {
    const edge = { ...example, from: key('a', 'one'), to: { project: 'b', id: 'one' } };
    assert.equal(relationError(edge), example.expected, example.name);
  }
  const cycle = type => [
    { type, from: key('a', 'one'), to: { project: 'a', id: 'two', state: 'resolved' } },
    { type, from: key('a', 'two'), to: { project: 'a', id: 'one', state: 'resolved' } }
  ];
  validateCycles(cycle('related-to'));
  validateCycles(cycle('references'));
  assert.throws(() => validateCycles(cycle('depends-on')), /cycle: depends-on/);
  assert.throws(() => validateCycles(cycle('full-supersedes')), /cycle: full-supersedes/);
}
