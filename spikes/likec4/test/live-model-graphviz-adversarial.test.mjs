import {test} from 'node:test';
import assert from 'node:assert/strict';
import {Graphviz} from '@hpcc-js/wasm-graphviz';
import {computeParsedModelData} from '@likec4/core/compute-view';
import {buildLiveModel} from '../src/live-model.ts';
import {projectLiveGraph} from '../src/live-projection.ts';
import {graphvizDot,layoutGraphvizView} from '../src/live-model-graphviz.ts';

const runtime=await Graphviz.load();
const input=graph=>({graph,snapshot:'independent-adversarial',associationIds:graph.edges.filter(e=>e.id==='explicit-association').map(e=>e.id)});
const computed=graph=>computeParsedModelData(projectLiveGraph(input(graph)).parsed).views.flow;
const layout=view=>layoutGraphvizView(view,runtime);
const graph={nodes:[
  {id:'a',label:'Application',category:'service',repositoryId:'left'},
  {id:'b',label:'Component',category:'component',repositoryId:'left'},
  {id:'c',label:'Database',category:'datastore',repositoryId:'right'},
  {id:'d',label:'Channel',category:'channel'},
],edges:[
  {id:'proven',from:'a',to:'b',kind:'call',certainty:'proven',count:7,facts:[{file:'a.ts',line:12,extra:{unchanged:true}}]},
  {id:'candidate',from:'b',to:'c',kind:'call',certainty:'candidate',facts:[{reason:'symbol evidence'}]},
  {id:'opposite',from:'c',to:'b',kind:'call',certainty:'proven'},
  {id:'alias',from:'b',to:'c',kind:'alias',certainty:'human'},
  {id:'same',from:'b',to:'c',kind:'same',certainty:'human'},
  {id:'explicit-association',from:'d',to:'c',kind:'derived_from',certainty:'human'},
]};
const edgeIdentity=({id,source,target,relations,head,tail,line,kind,label})=>({id,source,target,relations,head,tail,line,kind,label});
function geometry(view) {
  const finite=values=>assert.ok(values.every(Number.isFinite),'finite geometry');
  finite(Object.values(view.bounds));
  for(const node of view.nodes) {
    finite([node.x,node.y,node.width,node.height,...Object.values(node.labelBBox)]);
    assert.ok(node.width>0&&node.height>0&&node.labelBBox.width>=0&&node.labelBBox.height>=0);
    const parent=view.nodes.find(n=>n.id===node.parent);
    if(parent)assert.ok(node.x>=parent.x-.1&&node.y>=parent.y-.1&&node.x+node.width<=parent.x+parent.width+.1&&node.y+node.height<=parent.y+parent.height+.1,'child remains inside its compound');
  }
  for(const edge of view.edges) {
    assert.ok(edge.points.length>=4&&(edge.points.length-1)%3===0);
    for(const point of edge.points)finite(point);
    if(edge.labelBBox){finite(Object.values(edge.labelBBox));assert.ok(edge.labelBBox.width>0&&edge.labelBBox.height>0);}
  }
}
test('changing the layout engine preserves exact evidence, source and mappings in every generated view',()=>{
  const original=structuredClone(graph),dagre=buildLiveModel(input(graph)),wasm=buildLiveModel(input(graph),layout);
  for(const key of ['snapshot','source','sourceNodeIds','nodeSourceIds','edgeSourceIds','groups'])assert.deepEqual(wasm[key],dagre[key],key);
  for(const key of ['elements','relations','specification'])assert.deepEqual(wasm.modelData[key],dagre.modelData[key],key);
  for(const [id,view] of Object.entries(wasm.modelData.views)) {
    geometry(view);
    assert.deepEqual(view.nodes.map(({id,title,description,metadata,parent,children})=>({id,title,description,metadata,parent,children})),dagre.modelData.views[id].nodes.map(({id,title,description,metadata,parent,children})=>({id,title,description,metadata,parent,children})));
    assert.deepEqual(view.edges.map(edgeIdentity),dagre.modelData.views[id].edges.map(edgeIdentity));
  }
  assert.deepEqual(graph,original,'layout does not mutate the source graph');
  const shuffled=buildLiveModel(input({...graph,nodes:[...graph.nodes].reverse(),edges:[...graph.edges].reverse()}),layout);
  assert.deepEqual(wasm,shuffled,'all generated views remain deterministic under input shuffling');
});
test('hostile HTML, DOT attributes, XML controls and broken UTF16 stay inert without losing original strings',()=>{
  const payload='</TD><TD HREF="https://invalid.example/a"><IMG SRC="file:///secret"/></TD><script>alert(1)</script> "] ; injected [URL="https://invalid.example/b"]; \u0000\u000b\ufffe';
  const source={nodes:[{id:'__proto__',label:payload,sublabel:payload},{id:'constructor',label:'B'}],edges:[{id:'toString',from:'__proto__',to:'constructor',kind:'call',label:payload,certainty:'proven'}]};
  // LikeC4's earlier view-hash boundary rejects lone surrogates. Exercise this adapter
  // independently by placing one in an already-computed model, without changing IDs.
  const clean=computed(source),view={...clean,nodes:clean.nodes.map(n=>({...n,title:n.title+'\ud800'})),edges:clean.edges.map(e=>({...e,label:e.label+'\ud800'}))};
  const before=structuredClone(view),dot=graphvizDot(view);
  assert.ok(dot.includes('&lt;/TD&gt;')&&dot.includes('&quot;'));
  assert.ok(!dot.includes('<IMG ')&&!dot.includes('<script>')&&!dot.includes('[URL="'));
  assert.ok(!/[\u0000\u000b\ufffe]/u.test(dot),'layout XML cannot contain illegal controls');
  const raw=JSON.parse(runtime.layout(dot,'json','dot',{yInvert:true}));
  for(const object of [raw,...raw.objects,...raw.edges])for(const key of ['URL','href','image','target'])assert.equal(object[key],undefined,`${key} cannot become a Graphviz attribute`);
  const output=layout(view);geometry(output);
  assert.deepEqual(output.nodes.map(n=>[n.id,n.title,n.description]),view.nodes.map(n=>[n.id,n.title,n.description]));
  assert.deepEqual(output.edges.map(edgeIdentity),view.edges.map(edgeIdentity));
  assert.deepEqual(view,before);
  const projected=projectLiveGraph(input(source));
  const element=projected.parsed.elements[projected.sourceNodeIds.__proto__];
  assert.equal(JSON.parse(element.metadata.source).label,payload);
});
test('multiline descriptions, unbroken titles and relationship labels reserve measured geometry',()=>{
  const title='🙂'.repeat(70)+'x'.repeat(140),description='Detailed description with spaces. '.repeat(12);
  const make=label=>computed({nodes:[{id:'a',label:title,sublabel:description},{id:'b',label:'B'}],edges:[{id:'e',from:'a',to:'b',kind:'call',label,certainty:'proven'}]});
  const short=make('short'),long=make('line one\nline two\nline three\nline four');
  const shortOutput=layout(short),longOutput=layout(long);geometry(longOutput);
  assert.ok(longOutput.edges[0].labelBBox.height>shortOutput.edges[0].labelBBox.height,'four lines need a taller label reservation than one line');
  const node=longOutput.nodes.find(n=>n.title===title);assert.ok(node);
  assert.equal(node.title,title);assert.deepEqual(node.description,long.nodes.find(n=>n.id===node.id).description);
  assert.equal(longOutput.edges[0].label,long.edges[0].label);
  const dot=graphvizDot(long);assert.ok(dot.includes('<BR/>'));assert.ok(!dot.includes('x'.repeat(41)));
  assert.ok(node.width<1000,'a bounded title wraps rather than forcing one enormous line');
});
test('all rank directions preserve compound/self/opposite/parallel edge identity and containment',()=>{
  const view=computed(graph),parent=view.nodes.find(n=>n.children.length),outside=view.nodes.find(n=>!n.parent&&n.id!==parent.id),base=view.edges[0];
  const edges=[...view.edges,{...base,id:'compound-out',source:parent.id,target:outside.id},{...base,id:'compound-in',source:outside.id,target:parent.id},{...base,id:'compound-self',source:parent.id,target:parent.id}];
  for(const direction of ['LR','RL','TB','BT']) {
    const candidate={...view,edges,autoLayout:{...view.autoLayout,direction}},output=layout(candidate);geometry(output);
    assert.deepEqual(output.nodes.map(n=>n.id),candidate.nodes.map(n=>n.id));
    assert.deepEqual(output.edges.map(edgeIdentity),candidate.edges.map(edgeIdentity));
  }
});
test('malformed external geometry and duplicate/omitted identities fail closed',()=>{
  const view=computed({nodes:[{id:'a',label:'A'},{id:'b',label:'B'}],edges:[{id:'e',from:'a',to:'b',kind:'call',label:'Label',certainty:'proven'}]});
  const valid=JSON.parse(runtime.layout(graphvizDot(view),'json','dot',{yInvert:true}));
  const corruptions=[
    ['duplicate node',raw=>raw.objects.push(structuredClone(raw.objects[0]))],
    ['omitted node',raw=>raw.objects.shift()],
    ['unknown edge identity',raw=>raw.edges[0].id='unmapped'],
    ['duplicate edge',raw=>raw.edges.push(structuredClone(raw.edges[0]))],
    ['nonfinite position',raw=>raw.objects[0].pos='Infinity,0'],
    ['blank coordinate',raw=>raw.objects[0].pos=',0'],
    ['unbounded node size',raw=>raw.objects[0].width='1e100'],
    ['single-point spline',raw=>raw.edges[0]._draw_.find(op=>op.op==='b').points=[[0,0]]],
    ['unbounded spline',raw=>raw.edges[0]._draw_.find(op=>op.op==='b').points[0][0]=1e100],
    ['unbounded label',raw=>raw.edges[0]._ldraw_.find(op=>op.op==='T').width=1e100],
    ['missing label drawing',raw=>delete raw.edges[0]._ldraw_],
  ];
  for(const [name,mutate] of corruptions) {
    const raw=structuredClone(valid);mutate(raw);
    assert.throws(()=>layoutGraphvizView(view,{layout:()=>JSON.stringify(raw)}),/Graphviz/,name);
  }
  const tiny=structuredClone(valid);tiny.objects[0].height='0.02';
  const tinyResult=layoutGraphvizView(view,{layout:()=>JSON.stringify(tiny)});
  assert.ok(tinyResult.nodes.every(n=>n.labelBBox.width>=0&&n.labelBBox.height>=0));
});
test('invalid endpoint and hierarchy structure fails before invoking the WASM engine',()=>{
  const view=computed(graph),leaf=view.nodes.find(n=>n.parent),parent=view.nodes.find(n=>n.id===leaf.parent);
  let calls=0;const unexpected={layout(){calls++;throw new Error('runtime must not run');}};
  const invalid=[
    {...view,edges:[{...view.edges[0],source:'missing'}]},
    {...view,nodes:[...view.nodes,view.nodes[0]]},
    {...view,nodes:view.nodes.map(n=>n.id===leaf.id?{...n,parent:'missing'}:n)},
    {...view,nodes:view.nodes.map(n=>n.id===parent.id?{...n,parent:leaf.id}:n.id===leaf.id?{...n,children:[parent.id]}:n)},
  ];
  for(const candidate of invalid)assert.throws(()=>layoutGraphvizView(candidate,unexpected),/Graphviz/);
  assert.equal(calls,0);
});
