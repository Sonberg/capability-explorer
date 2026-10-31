import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';
import { toLikeC4 } from './export-graph.mjs';
assert.equal(readFileSync('src/model/architecture.c4', 'utf8'),
  toLikeC4(JSON.parse(readFileSync('test/weekly-sales.graph.json', 'utf8'))),
  'Committed model differs from its source snapshot; regenerate with scripts/export-graph.mjs');
console.log('LikeC4 source matches the fixture graph snapshot');
