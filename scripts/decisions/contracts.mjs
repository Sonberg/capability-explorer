import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

export const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));
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
