import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {Graphviz} from '@hpcc-js/wasm-graphviz';
import {LikeC4Model} from '@likec4/core/model';
import {computeParsedModelData} from '@likec4/core/compute-view';
import {buildLiveModel} from '../src/live-model.ts';
import {projectLiveGraph} from '../src/live-projection.ts';
import {graphvizDot,layoutGraphvizView,loadGraphvizLayout} from '../src/live-model-graphviz.ts';

const input=graph=>({graph,snapshot:'graphviz-production',associationIds:[]});
const fixture=JSON.parse(readFileSync(new URL('./all-repositories.graph.json',import.meta.url),'utf8'));
const layout=await loadGraphvizLayout();
const computed=graph=>computeParsedModelData(projectLiveGraph(input(graph)).parsed).views.flow;
function validate(view) {
  assert.ok([view.bounds.x,view.bounds.y,view.bounds.width,view.bounds.height].every(Number.isFinite));
  for(const node of view.nodes) {
    assert.ok([node.x,node.y,node.width,node.height].every(Number.isFinite));
    if(node.parent) {
      const parent=view.nodes.find(n=>n.id===node.parent);assert.ok(parent);
      assert.ok(node.x>=parent.x-0.01&&node.y>=parent.y-0.01&&node.x+node.width<=parent.x+parent.width+0.01&&node.y+node.height<=parent.y+parent.height+0.01);
    }
  }
  for(const edge of view.edges) {
    assert.equal((edge.points.length-1)%3,0);assert.ok(edge.points.every(p=>p.every(Number.isFinite)));
    if(edge.label)assert.equal(edge.labelBBox.height,44);
  }
}
test('production public Graphviz adapter builds every typed viewpoint with exact identities and relationships',()=>{
  const result=buildLiveModel(input(fixture),layout),native=LikeC4Model.create(result.modelData);
  assert.equal([...native.views()].length,18);
  for(const view of Object.values(result.modelData.views)) {
    validate(view);
    for(const node of view.nodes)assert.ok(result.nodeSourceIds[node.id]?.length);
    for(const edge of view.edges)assert.ok(result.edgeSourceIds[edge.id]?.length);
    for(let i=0;i<view.nodes.length;i++)for(let j=i+1;j<view.nodes.length;j++) {
      const a=view.nodes[i],b=view.nodes[j];if(a.parent!==b.parent)continue;
      assert.ok(a.x+a.width<=b.x+0.01||b.x+b.width<=a.x+0.01||a.y+a.height<=b.y+0.01||b.y+b.height<=a.y+0.01);
    }
  }
  const reverse=buildLiveModel(input({...fixture,nodes:[...fixture.nodes].reverse(),edges:[...fixture.edges].reverse()}),layout);
  assert.deepEqual(result,reverse);
});
test('public Graphviz preserves opposite directions, parallel associations and self edges',()=>{
  const graph={nodes:[{id:'a',label:'A'},{id:'b',label:'B'}],edges:[
    {id:'forward',from:'a',to:'b',kind:'call',certainty:'proven'},
    {id:'backward',from:'b',to:'a',kind:'call',certainty:'proven'},
    {id:'same',from:'a',to:'b',kind:'same',certainty:'human'},
    {id:'alias',from:'a',to:'b',kind:'alias',certainty:'human'},
  ]};
  const view=computed(graph),self={...view.edges.find(edge=>edge.kind==='observed'),id:'self',source:view.nodes[0].id,target:view.nodes[0].id,label:'self'};
  const output=layout({...view,edges:[...view.edges,self]});validate(output);
  assert.equal(output.edges.length,view.edges.length+1);
  for(const edge of view.edges) {
    const rendered=output.edges.find(e=>e.id===edge.id);
    assert.equal(rendered.source,edge.source);assert.equal(rendered.target,edge.target);
    assert.deepEqual(rendered.relations,edge.relations);assert.equal(rendered.head,edge.head);
  }
  assert.equal(output.edges.filter(e=>e.head==='none').length,2);
});
test('public Graphviz handles compound endpoints without exposing synthetic anchors as source nodes',()=>{
  const view=computed({nodes:[{id:'a',label:'A',repositoryId:'repo'},{id:'b',label:'B',repositoryId:'repo'},{id:'outside',label:'Outside'}],edges:[{id:'e',from:'a',to:'outside',kind:'call',certainty:'proven'}]});
  const parent=view.nodes.find(n=>n.children.length),outside=view.nodes.find(n=>!n.parent&&n.id!==parent.id),base=view.edges[0];
  const edges=[{...base,id:'out',source:parent.id,target:outside.id},{...base,id:'in',source:outside.id,target:parent.id},{...base,id:'loop',source:parent.id,target:parent.id}];
  const output=layout({...view,edges});validate(output);
  assert.deepEqual(output.nodes.map(n=>n.id),view.nodes.map(n=>n.id));
  assert.deepEqual(output.edges.map(e=>[e.id,e.source,e.target]),edges.map(e=>[e.id,e.source,e.target]));
});
test('generated DOT uses bounded aliases and numeric HTML reservations for hostile display strings',()=>{
  const malicious='\"] ; evil [image="https://invalid.example/secret"]; <script>\\n';
  const view=computed({nodes:[{id:malicious,label:malicious},{id:'b',label:'B'}],edges:[{id:malicious,from:malicious,to:'b',kind:'call',label:malicious,certainty:'proven'}]});
  const dot=graphvizDot(view);assert.ok(!dot.includes('invalid.example'));assert.ok(!dot.includes('<script>'));
  validate(layout(view));
  assert.throws(()=>graphvizDot({...view,autoLayout:{direction:'LR;evil'}}),/direction/);
  assert.throws(()=>graphvizDot({...view,nodes:Array(2129).fill(view.nodes[0])}),/limits/);
});
test('Graphviz layout errors and malformed JSON fail visibly without silently selecting Dagre',()=>{
  const view=computed({nodes:[{id:'a',label:'A'}],edges:[]});
  assert.throws(()=>layoutGraphvizView(view,{layout(){throw new Error('WASM layout failed');}}),/WASM layout failed/);
  assert.throws(()=>layoutGraphvizView(view,{layout(){return '{}';}}),/objects/);
  assert.throws(()=>layoutGraphvizView(view,{layout(){return '{broken';}}),SyntaxError);
  assert.deepEqual(layout({...view,nodes:[],edges:[]}).bounds,{x:0,y:0,width:0,height:0});
});
test('WASM initialization rejection becomes a worker error carrying the request identity',async()=>{
  const originalSelf=globalThis.self,originalLoad=Graphviz.load,messages=[];
  globalThis.self={onmessage:null,postMessage:message=>messages.push(message)};
  Graphviz.load=async()=>{throw new Error('WASM initialization unavailable');};
  try {
    await import('../src/live-model.worker.ts?wasm-failure');
    await self.onmessage({data:{requestId:93,graph:{nodes:[],edges:[]},snapshot:'failure',associationIds:[]}});
    assert.deepEqual(messages,[{requestId:93,ok:false,error:'WASM initialization unavailable'}]);
  } finally {Graphviz.load=originalLoad;globalThis.self=originalSelf;}
});
