import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {LikeC4Model} from '@likec4/core/model';
import {LikeC4} from 'likec4';
import {buildLiveModel} from '../src/live-model.ts';
import {projectLiveGraph} from '../src/live-projection.ts';
import {LIVE_MODEL_VERSION} from '../src/live-contracts.ts';
import {liveModelLimits} from '../src/live-model-validation.ts';

const fixture=JSON.parse(readFileSync(process.env.CE_LIVE_GRAPH_FILE??new URL('./all-repositories.graph.json',import.meta.url),'utf8'));
const input=(graph,associationIds=[])=>({graph,associationIds,snapshot:'capability=weekly-sales;revisions=api@a,sql@b;generation=3'});
const graph={title:'Review',nodes:[{id:'left',category:'service',label:'Left',repositoryId:'repo-a',drill:{level:'component',focus:'left'},badges:['bound']},
  {id:'right',category:'datastore',label:'Right',repositoryId:'repo-b'}],edges:[
  {id:'proven',from:'left',to:'right',kind:'data',label:'reads',certainty:'proven',count:200,facts:[{relation:'READS',evidence:[{repositoryId:'repo-a',commitSha:'abc',unitPath:'src/query.sql',span:{startLine:3,endLine:5}}]}]},
  {id:'candidate',from:'left',to:'right',kind:'data',label:'reads',certainty:'candidate',count:4,facts:[{relation:'READS',resolution:'candidate'}]},
  {id:'same',from:'right',to:'left',kind:'same',label:'same',certainty:'human',count:1,facts:[{assertionId:'assertion-1',occurrenceId:'occurrence-2',inputRef:'operand-3'}]},
]};
function finiteLayout(result) {
  for(const view of Object.values(result.modelData.views)) {
    for(const box of [view.bounds,...view.nodes,...view.edges.flatMap(e=>e.labelBBox?[e.labelBBox]:[])])for(const key of ['x','y','width','height'])assert.ok(Number.isFinite(box[key]),`${view.id}: finite ${key}`);
    const nodes=new Map(view.nodes.map(n=>[n.id,n]));
    for(const node of view.nodes)if(node.parent) {
      const parent=nodes.get(node.parent);assert.ok(parent);assert.ok(parent.children.includes(node.id));
      assert.ok(node.x>=parent.x&&node.y>=parent.y&&node.x+node.width<=parent.x+parent.width&&node.y+node.height<=parent.y+parent.height);
    }
    for(const edge of view.edges) {
      assert.ok(nodes.has(edge.source)&&nodes.has(edge.target));
      assert.ok(result.edgeSourceIds[edge.id]?.length);
      assert.equal((edge.points.length-1)%3,0,'native cubic sequence, also when orthogonal refinement is disabled');
      assert.ok(edge.points.every(p=>p.every(Number.isFinite)));
    }
  }
}
test('live typed models create native LikeC4 models with finite hierarchy geometry',()=>{
  const result=buildLiveModel(input(fixture)),native=LikeC4Model.create(result.modelData);
  assert.equal([...native.elements()].length,Object.keys(result.modelData.elements).length);
  assert.equal([...native.views()].length,Object.keys(result.modelData.views).length);
  assert.ok(result.modelData.views.objects.nodes.length>result.modelData.views.flow.nodes.length,'SQL detail expands SQL objects');
  assert.ok(result.modelData.views.flow.nodes.length>result.modelData.views.index.nodes.length,'flow expands repository objects');
  assert.ok(result.groups.every(g=>/^repository_[0-9]+$/.test(g.id)));
  assert.equal(Object.keys(result.sourceNodeIds).length,fixture.nodes.length);
  finiteLayout(result);
});
test('canonical DSL and geometry are independent of incoming record order and preserve stable identities after insertions',()=>{
  const a=buildLiveModel(input(graph)),b=buildLiveModel(input({...graph,nodes:[...graph.nodes].reverse(),edges:[...graph.edges].reverse()}));
  assert.equal(a.source,b.source);assert.deepEqual(a.modelData,b.modelData);
  const c=buildLiveModel(input({...graph,nodes:[{id:'00-inserted',label:'New',repositoryId:'00-new'},...graph.nodes]}));
  for(const node of graph.nodes)assert.equal(a.sourceNodeIds[node.id],c.sourceNodeIds[node.id]);
  assert.match(a.source,new RegExp(LIVE_MODEL_VERSION));assert.ok(a.source.includes('capability=weekly-sales'));
});
test('exact source evidence, original association endpoints, capped counts and occurrence IDs survive collapse',()=>{
  const before=structuredClone(graph),result=buildLiveModel(input(graph));
  assert.deepEqual(graph,before,'projection never changes API evidence');
  for(const original of graph.nodes)assert.deepEqual(JSON.parse(result.modelData.elements[result.sourceNodeIds[original.id]].metadata.source),original);
  const relations=Object.values(result.modelData.relations).map(r=>JSON.parse(r.metadata.source));
  assert.deepEqual(relations.sort((a,b)=>a.id.localeCompare(b.id)),[...graph.edges].sort((a,b)=>a.id.localeCompare(b.id)));
  const displayed=result.modelData.views.flow.edges;
  assert.ok(displayed.some(e=>result.edgeSourceIds[e.id].includes('proven')&&result.edgeSourceIds[e.id].includes('candidate')),'collapsed relationship keeps every source ID');
  for(const edge of displayed) {
    if(result.edgeSourceIds[edge.id].includes('candidate'))assert.equal(edge.line,'dashed','mixed certainty never becomes proven styling');
    if(result.edgeSourceIds[edge.id].includes('same')){assert.deepEqual(result.edgeSourceIds[edge.id],['same']);assert.equal(edge.head,'none');assert.equal(edge.tail,'none');}
  }
});
test('explicit human aliases remain arrowless alongside directed derivations with the same endpoints',()=>{
  const g={nodes:[{id:'a',label:'Occurrence'},{id:'b',label:'Term'}],edges:[
    {id:'alias',from:'a',to:'b',kind:'human-assertion',certainty:'human'},
    {id:'derive',from:'a',to:'b',kind:'derived_from',certainty:'human'},
  ]};
  const result=buildLiveModel(input(g,['alias']));
  assert.equal(result.modelData.views.flow.edges.length,2);
  for(const edge of result.modelData.views.flow.edges)assert.equal(edge.head,result.edgeSourceIds[edge.id].includes('alias')?'none':'normal');
});
test('SQL resource scopes use exact references and declared object evidence without inventing deployment or value flow',()=>{
  const sql=JSON.parse(readFileSync(new URL('./weekly-sales.graph.json',import.meta.url),'utf8'));
  const result=buildLiveModel(input(sql)),resources=Object.values(result.modelData.elements).filter(n=>['database','databaseReference','sqlModel'].includes(n.kind));
  assert.equal(resources.length,3);assert.equal(resources.filter(n=>n.kind==='sqlModel').length,1);
  assert.ok(resources.every(n=>n.kind!=='database'));
  const details=Object.values(result.modelData.elements).filter(n=>n.kind==='sqlObject');
  assert.equal(details.filter(n=>n.description.txt.startsWith('Declared SQL table')).length,2);
  assert.equal(Object.values(result.modelData.relations).length,sql.edges.length);
});
test('downloaded DSL compiles to the same node IDs, original metadata and viewpoint membership',async()=>{
  const result=buildLiveModel(input(fixture));
  const compiler=await LikeC4.fromSource(result.source,{logger:false,printErrors:false,throwIfInvalid:true});
  try {
    assert.deepEqual(compiler.getErrors(),[]);
    const parsed=await compiler.parsedModel(),computed=await compiler.computedModel();
    assert.deepEqual([...parsed.elements()].map(e=>e.id).sort(),Object.keys(result.modelData.elements).sort());
    for(const original of fixture.nodes)assert.deepEqual(JSON.parse(parsed.element(result.sourceNodeIds[original.id]).getMetadata('source')),original);
    assert.deepEqual([...computed.views()].map(v=>v.id).sort(),Object.keys(result.modelData.views).sort());
    for(const view of computed.views())assert.deepEqual([...view.nodes()].map(n=>n.id).sort(),result.modelData.views[view.id].nodes.map(n=>n.id).sort());
  } finally {await compiler.dispose();}
});
test('JSON boundary rejects dangling endpoints, duplicate IDs, malformed evidence and oversized inputs',()=>{
  const rejects=(value,pattern)=>assert.throws(()=>projectLiveGraph(input(value)),pattern);
  rejects({...graph,nodes:[graph.nodes[0],graph.nodes[0]]},/Duplicate/);
  rejects({...graph,edges:[{id:'e',from:'left',to:'unknown'}]},/Dangling/);
  rejects({...graph,edges:[{id:'e',from:'left',to:'right',certainty:'guessed'}]},/certainty/);
  rejects({...graph,edges:[{id:'e',from:'left',to:'right',count:-1}]},/count/);
  rejects({nodes:Array.from({length:liveModelLimits.nodes+1},(_,i)=>({id:String(i)})),edges:[]},/limits/);
  rejects({nodes:[{id:'n',label:'a'.repeat(liveModelLimits.string+1)}],edges:[]},/string/);
  const cyclic={nodes:[{id:'n'}],edges:[]};cyclic.nodes[0].evidence=cyclic;
  rejects(cyclic,/Cyclic/);
  let getterRan=false;
  const accessor={nodes:[{id:'n',get label(){getterRan=true;return 'No';}}],edges:[]};
  rejects(accessor,/accessors/);assert.equal(getterRan,false);
  rejects({nodes:Array.from({length:129},(_,i)=>({id:`n${i}`,repositoryId:`repo${i}`})),edges:[]},/hierarchy scopes/);
  assert.throws(()=>projectLiveGraph(input(graph,['missing'])),/association/);
  assert.doesNotThrow(()=>buildLiveModel(input({nodes:[{id:'nulls',label:'Nullable API',repositoryId:null,symbolKey:null,sublabel:null,group:null,drill:null}],edges:[]})));
});
test('empty scopes are valid and retain their snapshot in exported source',()=>{
  const result=buildLiveModel(input({nodes:[],edges:[]}));
  assert.equal(result.modelData.views.flow.nodes.length,0);assert.equal(result.modelData.views.flow.edges.length,0);
  assert.ok(result.source.includes('generation=3'));finiteLayout(result);
});
test('source-provided code groups retain stable members and ledger category styles',()=>{
  const nodes=['component','interface','module','endpoint'].map((category,i)=>({id:`code-${i}`,category,label:category,group:i<2?'A':'B',repositoryId:'repo'}));
  const result=buildLiveModel(input({nodes,edges:[]}));
  assert.equal(result.groups.length,2);assert.deepEqual(result.groups.map(g=>g.title).sort(),['A','B']);
  for(const n of nodes)assert.equal(result.modelData.elements[result.sourceNodeIds[n.id]].kind,n.category);
  const reduced=buildLiveModel(input({nodes:[nodes[0]],edges:[]}));
  assert.equal(reduced.sourceNodeIds[nodes[0].id],result.sourceNodeIds[nodes[0].id],'presentation usefulness does not change source identity');
});
test('DSL escaping preserves original multiline labels, quotes and backslashes',async()=>{
  const g={nodes:[{id:'special',label:'Line one\nLine two "quoted" \\path\tcolumn',category:'service'}],edges:[]};
  const result=buildLiveModel(input(g));
  const compiler=await LikeC4.fromSource(result.source,{logger:false,printErrors:false,throwIfInvalid:true});
  try {
    const parsed=await compiler.parsedModel();
    assert.equal(parsed.element(result.sourceNodeIds.special).title,g.nodes[0].label);
    assert.equal(result.modelData.elements[result.sourceNodeIds.special].title,g.nodes[0].label);
  } finally {await compiler.dispose();}
});
