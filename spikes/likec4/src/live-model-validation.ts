import type {LiveModelInput} from './live-contracts.ts';

export const liveModelLimits=Object.freeze({nodes:2000,edges:8000,bytes:16_000_000,string:32768,id:1024,depth:32,values:500000});
const record=(value:unknown):value is Record<string,unknown>=>!!value&&typeof value==='object'&&!Array.isArray(value);
function text(value:unknown,name:string,limit:number=liveModelLimits.string):asserts value is string {
  if(typeof value!=='string'||!value.length||value.length>limit)throw new Error(`Invalid ${name}`);
}
/** Validate before traversal, serialization or layout. Never truncate evidence to fit. */
export function validateLiveInput(input:LiveModelInput):void {
  validateJSON(input);
  if(!record(input)||!record(input.graph))throw new Error('Expected architecture graph');
  text(input.snapshot,'snapshot',4096);
  const {graph}=input;
  for(const key of ['title','level','focus'] as const)if(graph[key]!==undefined&&typeof graph[key]!=='string')throw new Error(`Invalid graph ${key}`);
  if(!Array.isArray(graph.nodes)||!Array.isArray(graph.edges)||graph.nodes.length>liveModelLimits.nodes||graph.edges.length>liveModelLimits.edges)throw new Error('Graph exceeds node/edge limits');
  const nodes=new Set<string>(),edges=new Set<string>();
  for(const [items,seen] of [[graph.nodes,nodes],[graph.edges,edges]] as const)for(const item of items) {
    if(!record(item))throw new Error('Invalid graph record');
    text(item.id,'record ID',liveModelLimits.id);
    if(seen.has(item.id))throw new Error('Duplicate record ID');
    seen.add(item.id);
  }
  for(const node of graph.nodes)for(const field of ['label','category','repositoryId','symbolKey','sublabel','group'] as const) {
    const value=node[field];if(value!==undefined&&value!==null&&typeof value!=='string')throw new Error(`Invalid node ${field}`);
  }
  for(const node of graph.nodes) {
    if(node.badges!==undefined&&node.badges!==null&&(!Array.isArray(node.badges)||node.badges.some((value:unknown)=>typeof value!=='string')))throw new Error('Invalid node badges');
    if(node.drill!==undefined&&node.drill!==null&&(!record(node.drill)||typeof node.drill.level!=='string'||typeof node.drill.focus!=='string'))throw new Error('Invalid node drill');
  }
  for(const edge of graph.edges) {
    if(!nodes.has(edge.from)||!nodes.has(edge.to))throw new Error('Dangling edge endpoint');
    if(edge.certainty!==undefined&&!['human','proven','static','bound','broken','unbound','candidate','unresolved'].includes(edge.certainty))throw new Error('Unknown certainty');
    if(edge.count!==undefined&&(!Number.isSafeInteger(edge.count)||edge.count<0))throw new Error('Invalid evidence count');
    if(edge.facts!==undefined&&(!Array.isArray(edge.facts)||edge.facts.some((fact:unknown)=>!record(fact))))throw new Error('Invalid facts');
    for(const field of ['kind','label'] as const)if(edge[field]!==undefined&&typeof edge[field]!=='string')throw new Error(`Invalid edge ${field}`);
  }
  if(!Array.isArray(input.associationIds)||input.associationIds.length>liveModelLimits.edges||input.associationIds.some(id=>typeof id!=='string'||!edges.has(id)))throw new Error('Unknown association edge ID');
  // Strict JSON input prevents cycles, getters, hostile prototypes, excessive nesting,
  // non-finite numbers and unbounded evidence payloads crossing the worker boundary.
  function validateJSON(value:unknown):void {
  const active=new Set<object>();let values=0,bytes=0;
  function visit(value:unknown,depth:number):void {
    if(++values>liveModelLimits.values||depth>liveModelLimits.depth)throw new Error('Graph exceeds evidence complexity limit');
    if(value===undefined||value===null||typeof value==='boolean')return;
    if(typeof value==='string') {bytes+=value.length*2;if(value.length>liveModelLimits.string)throw new Error('Graph string exceeds limit');}
    else if(typeof value==='number') {if(!Number.isFinite(value))throw new Error('Non-finite graph number');bytes+=16;}
    else if(typeof value==='object') {
      if(active.has(value))throw new Error('Cyclic graph input');
      const proto=Object.getPrototypeOf(value);
      if(!Array.isArray(value)&&proto!==Object.prototype&&proto!==null)throw new Error('Expected plain graph records');
      active.add(value);
      for(const [key,descriptor] of Object.entries(Object.getOwnPropertyDescriptors(value))) {
        if(!('value' in descriptor))throw new Error('Graph accessors are unsupported');
        bytes+=key.length*2;visit(descriptor.value,depth+1);
      }
      active.delete(value);
    } else throw new Error('Expected JSON graph values');
    if(bytes>liveModelLimits.bytes)throw new Error('Graph exceeds evidence byte limit');
  }
  visit(value,0);
  }
}
export function canonical(value:unknown):string {
  return JSON.stringify(value,(_key,value:unknown)=>{
    if(value&&typeof value==='object'&&!Array.isArray(value))return Object.fromEntries(Object.entries(value).sort(([a],[b])=>a<b?-1:a>b?1:0));
    return value;
  });
}
/** Source-bound hash; collisions are checked before model construction. */
export function stableToken(value:string):string {
  let hash=14695981039346656037n;
  for(let i=0;i<value.length;i++){hash^=BigInt(value.charCodeAt(i));hash=BigInt.asUintN(64,hash*1099511628211n);}
  return hash.toString(10);
}
