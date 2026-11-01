// Real generated model, exact output comparison, then optional full-pipeline WASM.
import {LikeC4} from 'likec4';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {routeOrthogonal} from '../src/orthogonal-routing.ts';
import {searchBaseline} from './search-baseline.ts';
import {automaticGroups,applyGroups} from '../src/grouping.ts';
const source=await readFile('src/model/architecture.c4','utf8');
const compiler=await LikeC4.fromWorkspace('src/model',{logger:false,throwIfInvalid:true});
const model=await compiler.layoutedModel();
const graph=JSON.parse(await readFile('test/all-repositories.graph.json','utf8'));
const overview=applyGroups(model.$data.views.flow,automaticGroups(graph));
const cases=[['overview',overview],['vertical',applyGroups(model.$data.views.vertical,automaticGroups(graph))],['landscape',model.$data.views.index],['data',model.$data.views.data]];
const node=overview.nodes.find(n=>n.id==='repository_0.n1');
cases.push(['node-move',{...overview,nodes:overview.nodes.map(n=>n.id===node.id?{...n,x:n.x+30,y:n.y+8}:n)}]);
const rows=[];
for(const [name,view] of cases){
 const expected=routeOrthogonal(view,undefined,searchBaseline),actual=routeOrthogonal(view);
 assert.deepEqual(actual,expected,`${name}: exact output`);
 const times={baseline:[],optimized:[]};
 for(let run=0;run<7;run++)for(const implementation of (run%2?['optimized','baseline']:['baseline','optimized'])){
  const start=performance.now();routeOrthogonal(view,undefined,implementation==='baseline'?searchBaseline:undefined);
  if(run>1)times[implementation].push(performance.now()-start);
 }
 rows.push({name,nodes:view.nodes.length,edges:view.edges.length,times});console.log(JSON.stringify(rows.at(-1)));
}
const wasm=[];
if(process.env.WASM_MODULE){
 const {createWasmSearch}=await import('./wasm-search.mjs');
 const module=await import(process.env.WASM_MODULE);
 for(const [name,view] of cases.slice(0,2)){
  const times=[];let output;
  for(let run=0;run<7;run++){const start=performance.now();output=routeOrthogonal(view,undefined,createWasmSearch(module.WasmRoadRouter));if(run>1)times.push(performance.now()-start);}
  const baseline=routeOrthogonal(view,undefined,searchBaseline);
  let identical=true;try{assert.deepEqual(output,baseline);}catch{identical=false;}
  wasm.push({name,times,identicalOutput:identical});console.log(JSON.stringify(wasm.at(-1)));
 }
}
await mkdir('/work/.sandbox-cache/likec4-browser',{recursive:true});
await writeFile('/work/.sandbox-cache/likec4-browser/search-performance.json',JSON.stringify({modelSha256:createHash('sha256').update(source).digest('hex'),rows,wasm,note:'Node sandbox CPU timings of the full routing pipeline. Two warmups, five samples alternating implementation order. WASM includes per-search graph conversion/construction/free, excludes module load; no browser latency claim.'},null,2));
await compiler.dispose();
