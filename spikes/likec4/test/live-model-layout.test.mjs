import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {buildLiveModel} from '../src/live-model.ts';

const fixture=JSON.parse(readFileSync(new URL('./all-repositories.graph.json',import.meta.url),'utf8'));
const input=graph=>({graph,snapshot:'compact-quality',associationIds:[]});
function inspect(result) {
  for(const view of Object.values(result.modelData.views)) {
    for(const node of view.nodes) {
      assert.ok([node.x,node.y,node.width,node.height].every(Number.isFinite));
      if(!node.children.length){assert.ok(node.width>=320);assert.equal(node.height,180);}
      if(node.parent) {
        const parent=view.nodes.find(n=>n.id===node.parent);assert.ok(parent);
        assert.ok(node.x>=parent.x&&node.y>=parent.y&&node.x+node.width<=parent.x+parent.width&&node.y+node.height<=parent.y+parent.height);
      }
    }
    for(let i=0;i<view.nodes.length;i++)for(let j=i+1;j<view.nodes.length;j++) {
      const a=view.nodes[i],b=view.nodes[j];if(a.parent!==b.parent)continue;
      assert.ok(a.x+a.width<=b.x||b.x+b.width<=a.x||a.y+a.height<=b.y||b.y+b.height<=a.y,`${view.id}: overlapping siblings ${a.id} / ${b.id}`);
    }
  }
}
test('compact fixture retains readable leaf proportions, bounded width and requested view directions',()=>{
  const result=buildLiveModel(input(fixture));inspect(result);
  // Associations must not force a long horizontal chain. Retain enough corridor
  // space to route every relationship while matching native leaf proportions.
  assert.ok(result.modelData.views.flow.bounds.width<=2100);
  assert.ok(result.modelData.views.flow.bounds.height<=2800);
  assert.equal(result.modelData.views.flow.autoLayout.direction,'LR');
  assert.equal(result.modelData.views.vertical.autoLayout.direction,'TB');
  assert.deepEqual(result,buildLiveModel(input({...fixture,nodes:[...fixture.nodes].reverse(),edges:[...fixture.edges].reverse()})));
});
test('undirected associations leave rank direction unconstrained while directed relationships preserve it',()=>{
  const nodes=[{id:'a',label:'Left'},{id:'b',label:'Right'}];
  const associated=buildLiveModel(input({nodes,edges:[{id:'same',from:'a',to:'b',kind:'same',certainty:'human'}]}));
  const view=associated.modelData.views.flow;
  assert.equal(view.nodes[0].x,view.nodes[1].x,'association-only nodes can share a rank');
  assert.equal(view.edges.length,1);assert.equal(view.edges[0].head,'none');
  assert.deepEqual(associated.edgeSourceIds[view.edges[0].id],['same']);
  const directed=buildLiveModel(input({nodes,edges:[{id:'call',from:'a',to:'b',kind:'call',certainty:'proven'}]}));
  const from=directed.modelData.views.flow.nodes.find(n=>directed.nodeSourceIds[n.id].includes('a'));
  const to=directed.modelData.views.flow.nodes.find(n=>directed.nodeSourceIds[n.id].includes('b'));
  assert.ok(from.x<to.x);
});
test('bounded cyclic models have finite disjoint sibling geometry and deterministic source identities',()=>{
  for(let seed=1;seed<=12;seed++) {
    let state=seed;const random=()=>{state=(state*1664525+1013904223)>>>0;return state;};
    const nodes=Array.from({length:20},(_,i)=>({id:`node-${i}`,label:`Node ${i}`,category:'service',repositoryId:`repo-${i%4}`}));
    const edges=Array.from({length:60},(_,i)=>({id:`edge-${i}`,from:nodes[random()%20].id,to:nodes[random()%20].id,kind:i%5===0?'sameName':'call',certainty:i%3===0?'candidate':'proven'}));
    const result=buildLiveModel(input({nodes,edges}));inspect(result);
    assert.deepEqual(result,buildLiveModel(input({nodes:[...nodes].reverse(),edges:[...edges].reverse()})));
  }
});
