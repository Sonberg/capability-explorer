import type {SourceGraph,SourceNode} from './types.ts';
// ce.v1 SQL identity: sql|<exact store reference>|<schema>.<object>[#column].
// A scoped @unbound reference is not a physical database identity.
export function sqlResources(graph:Pick<SourceGraph,'nodes'>) {
  const scopes=new Map<string,{storeRef:string;nodeIds:string[]}>();
  for(const node of graph.nodes) {
    if(node.category!=='datastore'||typeof node.symbolKey!=='string'||!node.symbolKey.startsWith('sql|'))continue;
    const parts=node.symbolKey.split('|');
    if(parts.length!==3||!parts[1]||!parts[2]||parts[1].startsWith('@builtin:'))continue;
    const scope=scopes.get(parts[1])??{storeRef:parts[1],nodeIds:[]};scope.nodeIds.push(node.id);scopes.set(parts[1],scope);
  }
  return [...scopes.values()].sort((a,b)=>a.storeRef<b.storeRef?-1:a.storeRef>b.storeRef?1:0).map((scope,index)=>{
    const unbound=scope.storeRef.startsWith('@unbound:');
    const hint=/^@unbound:(.+):(conn|config|env|dbproject)=(.*)$/.exec(scope.storeRef);
    const kind=hint?.[2]==='dbproject'?'sqlModel':unbound?'databaseReference':'database';
    const title=hint?`${hint[3]} · ${hint[1]}`:scope.storeRef;
    const description=kind==='sqlModel'?'SQL source model; deployment unresolved':unbound?'Logical database reference; target unresolved':'Concrete database reference; deployment not verified';
    return {...scope,id:`sql_scope_${index}`,nodeIds:scope.nodeIds.sort(),kind,title,description,binding:unbound?'unbound':'bound'};
  });
}
export function sqlObjectDescription(node:SourceNode, graph:SourceGraph) {
  const facts=graph.edges.flatMap(e=>e.facts??[]);
  const declaration=facts.find(f=>f.relation==='DECLARES'&&f.to?.key===node.symbolKey&&f.to?.declared&&['Table','View','Routine'].includes(f.to.kind??''));
  return declaration?`Declared SQL ${declaration.to!.kind!.toLowerCase()}`:'SQL object reference · object type or declaration not established';
}
