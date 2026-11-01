import {test} from 'node:test';
import assert from 'node:assert/strict';
import {attachImmediateLayout,geometryKey} from '../src/immediate-layout.ts';

function setup() {
  const base={id:'flow',_layout:'auto',drifts:['changed'],bounds:{x:0,y:0,width:600,height:400},
    nodes:[{id:'group',kind:'@group',x:0,y:0,width:300,height:200,labelBBox:{x:10,y:10,width:100,height:30}},
      {id:'a',parent:'group',x:40,y:60,width:80,height:50,modelRef:'repository.a',metadata:{evidence:'keep'}}],
    edges:[{id:'edge',source:'a',target:'a',points:[[40,60],[80,60]],relations:['evidence'],dir:'back'}]};
  let context={history:null,redo:null,editing:null};
  let listener;
  const editor={getSnapshot:()=>({context}),subscribe:callback=>{listener=callback;return {unsubscribe:()=>{listener=undefined;}};}};
  const internal=new Map(base.nodes.map(n=>[n.id,{data:{shape:'rectangle',color:'slate',style:{opacity:12}},internals:{positionAbsolute:{x:n.x+24.9,y:n.y+36.8}},measured:{width:n.width+.2,height:n.height+.4}}]));
  const edges=[{id:'edge',data:{points:[[65,97],[100,97]],controlPoints:[{x:500.6,y:300.4}],labelBBox:{x:110.6,y:120.4,width:70.6,height:20.3},isLabelCustomized:true}}];
  const initialized={diagram:{currentView:base,editorActor:()=>editor},xyflow:{getInternalNode:id=>internal.get(id),getEdges:()=>edges}};
  const emit=patch=>{context={...context,...patch};listener?.({context});};
  return {base,initialized,emit,internal,edges};
}
test('completed native edit captures absolute group/member geometry, edges and native bounds without changing history',async()=>{
  const s=setup(),layouts=[];const detach=attachImmediateLayout(s.initialized,l=>layouts.push(l));
  const before=structuredClone(s.base),history={head:{},tail:null};
  s.emit({editing:{subject:'node'}});assert.equal(layouts.length,0);
  s.emit({editing:null,history});await Promise.resolve();
  assert.equal(layouts.length,1);const layout=layouts[0];
  assert.equal(layout._layout,'manual');assert.equal('drifts' in layout,false);
  assert.deepEqual(layout.nodes.map(n=>[n.x,n.y,n.width,n.height]),[[24,36,301,201],[64,96,81,51]]);
  assert.equal(layout.nodes[1].modelRef,'repository.a');assert.deepEqual(layout.nodes[1].metadata,{evidence:'keep'});
  assert.deepEqual(layout.edges[0].relations,['evidence']);assert.equal(layout.edges[0].dir,'back');
  assert.deepEqual(layout.edges[0].controlPoints,[{x:501,y:300}]);
  assert.deepEqual(layout.edges[0].labelBBox,{x:111,y:120,width:71,height:20});
  assert.deepEqual(layout.bounds,{x:4,y:16,width:517,height:304});
  assert.deepEqual(s.base,before);assert.equal(s.initialized.diagram.editorActor().getSnapshot().context.history,history);
  detach();
});
test('history navigation captures after native actions, dedupes equal geometry, and disposal cancels queued delivery',async()=>{
  const s=setup(),layouts=[];const detach=attachImmediateLayout(s.initialized,l=>layouts.push(l));
  s.emit({history:{head:{}}});await Promise.resolve();
  s.emit({history:{head:{}},redo:{head:{}}});await Promise.resolve();assert.equal(layouts.length,1);
  s.emit({history:{head:{}},redo:null});s.internal.get('a').internals.positionAbsolute.x=200;
  await Promise.resolve();assert.equal(layouts.length,2);assert.equal(layouts[1].nodes[1].x,200);
  s.emit({history:{head:{}}});detach();await Promise.resolve();assert.equal(layouts.length,2);
});
test('cancelled edit and queue bookkeeping do not emit; geometry keys ignore evidence changes',async()=>{
  const s=setup(),layouts=[];const detach=attachImmediateLayout(s.initialized,l=>layouts.push(l));
  s.emit({editing:{subject:'node'}});s.emit({editing:null});s.emit({processing:'sync-snapshot'});
  await Promise.resolve();assert.equal(layouts.length,0);
  assert.equal(geometryKey(s.base),geometryKey({...s.base,nodes:s.base.nodes.map(n=>({...n,metadata:{changed:'evidence'}}))}));
  assert.notEqual(geometryKey(s.base),geometryKey({...s.base,nodes:s.base.nodes.map(n=>({...n,x:n.x+1}))}));
  assert.notEqual(geometryKey(s.base),geometryKey({...s.base,nodes:s.base.nodes.map(n=>({...n,color:'blue'}))}));
  detach();
});
