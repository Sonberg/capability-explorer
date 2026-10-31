import { test } from 'node:test';
import assert from 'node:assert/strict';
import { toLikeC4 } from '../scripts/export-graph.mjs';
const graph = { title: 'Fixture snapshot', nodes: [{id:'b',label:'Store'},{id:'a',label:'API'}], edges: [{id:'e',from:'a',to:'b',label:'reads',certainty:'candidate',facts:[{derivation:'human',rule:'explicit'}]}] };
test('canonical output ignores node order and evidence object key order', () => {
  const source = toLikeC4(graph);
  assert.equal(source, toLikeC4({...graph,nodes:[...graph.nodes].reverse(),edges:[{...graph.edges[0],facts:[{rule:'explicit',derivation:'human'}]}]}));
  assert.match(source, /reads \[candidate\]/); assert.match(source, /line dashed/); assert.match(source, /human/);
  assert.ok(source.includes('\nmodel {\n'));
});
test('rejects duplicate identities, malformed objects, dangling endpoints and unknown certainty', () => {
  assert.throws(() => toLikeC4({...graph,nodes:[graph.nodes[0],graph.nodes[0]]}), /duplicate/);
  assert.throws(() => toLikeC4({...graph,nodes:[null]}), /record id/);
  assert.throws(() => toLikeC4({...graph,edges:[{...graph.edges[0],to:'missing'}]}), /Dangling/);
  assert.throws(() => toLikeC4({...graph,edges:[{...graph.edges[0],certainty:'guess'}]}), /certainty/);
});
test('quotes and line breaks cannot inject model statements', () => {
  const source = toLikeC4({...graph,nodes:[{id:'a',label:'"}\nmodel { <script>alert(1)</script>'},graph.nodes[0]]});
  assert.ok(source.includes('\\"} model { <script>'));
  assert.equal(source.split('\nmodel {').length, 2);
});
test('absent certainty stays unresolved and node evidence is retained', () => {
  const source = toLikeC4({...graph,edges:[{...graph.edges[0],certainty:undefined}]});
  assert.match(source, /reads \[unresolved\]/); assert.match(source,/line dashed/);
});

test('real LikeC4 parser preserves quoted labels and evidence without injected elements', async () => {
  const { LikeC4 } = await import('likec4');
  const dangerous = {...graph,nodes:[{id:'a',label:'"}\nmodel { injected = artifact "bad" }'},graph.nodes[0]]};
  const instance = await LikeC4.fromSource(toLikeC4(dangerous), {throwIfInvalid:true,printErrors:false});
  try {
    const model = await instance.computedModel();
    assert.equal([...model.elements()].length,2);
    assert.equal([...model.relationships()].length,1);
    const evidence = JSON.parse([...model.relationships()][0].getMetadata('source'));
    assert.deepEqual(evidence.facts,graph.edges[0].facts);
    assert.equal(evidence.certainty,'candidate');
  } finally { await instance.dispose(); }
});
