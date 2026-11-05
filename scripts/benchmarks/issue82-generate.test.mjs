import assert from 'node:assert/strict';
import test from 'node:test';
import { createHash } from 'node:crypto';
import { records } from './issue82-generate.mjs';

test('dense fixture is reproducible and exceeds worker edges without duplicate module links', () => {
  const first = [...records()];
  const digest = rows => createHash('sha256').update(rows.map(row => JSON.stringify(row)).join('\n')).digest('hex');
  assert.equal(digest(first), digest([...records()]));
  const nodes = first.filter(row => row.kind === 'node');
  const edges = first.filter(row => row.kind === 'edge');
  assert.equal(nodes.length, 2400);
  assert.equal(new Set(nodes.map(row => row.symbolKey.split('|')[3])).size, nodes.length);
  assert.ok(edges.length > 8000);
  assert.equal(new Set(edges.map(row => `${row.from}\0${row.to}\0${row.relation}`)).size, edges.length);
  const known = new Set(nodes.map(row => row.symbolKey));
  assert.ok(edges.every(row => known.has(row.from) && known.has(row.to) && row.from !== row.to));
  assert.deepEqual(first.at(-1).counts, { node: nodes.length, edge: edges.length, evidence: nodes.length, calculation: 0, diagnostic: 0 });
});
