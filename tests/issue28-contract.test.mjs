import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { validateContract } from '../scripts/issue28-gate.mjs';

const contract = () => JSON.parse(readFileSync('config/issue28/contract.json', 'utf8'));
test('declared specification is bounded and keeps remaining scope visible', () => {
  assert.deepEqual(validateContract(contract()), []);
});
for (const [label, mutate] of [
  ['missing endpoint cap', c => { delete c.routes.find(r => r.path === 'search').max; }],
  ['missing stable order', c => { delete c.routes.find(r => r.path === 'search').sort; }],
  ['unbounded graph', c => { delete c.routes.find(r => r.path === 'architecture').maxEdges; }],
  ['mutable pagination', c => { c.snapshot.pages = 'current-generation'; }],
  ['missing database cost', c => { c.budgets.measure = ['app-rss']; }],
  ['false issue completion', c => { c.issueComplete = true; }],
  ['automatic extension adoption', c => { c.ruvector.adoption = 'automatic'; }],
]) {
  test(`rejects ${label}`, () => {
    const c = contract(); mutate(c);
    assert.ok(validateContract(c).length > 0);
  });
}
test('unimplemented acceptance phases fail closed', () => {
  const result = spawnSync(process.execPath, ['scripts/issue28-gate.mjs', 'completion'], { encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /incomplete/);
});
