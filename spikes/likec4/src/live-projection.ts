import {Fqn,ProjectId,RelationId,ViewId} from '@likec4/core/types';
import type {Element,ElementStyle,ParsedElementView,ParsedLikeC4ModelData,Relationship,CustomColor} from '@likec4/core/types';
import type {Group} from './types.ts';
import type {LiveModelInput,LiveNode} from './live-contracts.ts';
import {LIVE_MODEL_VERSION,UNREGISTERED_HTTP_TARGET_BADGE,UNREGISTERED_HTTP_TARGET_CATEGORY} from './live-contracts.ts';
import {canonical,stableToken,validateLiveInput} from './live-model-validation.ts';
import {ledgerPalette,ledgerToken} from './ledger-palette.ts';
import {serializeLiveProjection} from './live-model-source.ts';

export interface LiveProjection {
  readonly parsed:ParsedLikeC4ModelData;
  readonly source:string;
  readonly sourceNodeIds:Record<string,string>;
  readonly nodeSourceIds:Record<string,string[]>;
  readonly relationSourceIds:Record<string,string>;
  readonly associationSourceIds:readonly string[];
  readonly groups:Group[];
}
const order=(a:{id:string},b:{id:string})=>a.id<b.id?-1:a.id>b.id?1:0;
const paletteColor=(key:string)=>ledgerToken(key) as CustomColor;
// Leaf opacity is not rendered by LikeC4, so the near-transparent fill is an alpha custom colour.
const UNREGISTERED_TARGET_COLOR='unregisteredTarget' as CustomColor;
const unregisteredTargetColor={elements:{fill:'#9ca3af1a',stroke:'#6b7280',hiContrast:'#1f2937',loContrast:'#4b5563'},
  relationships:{line:'#6b7280',label:'#4b5563',labelBg:'#ffffff'}} as const;
// LikeC4 emits one palette per custom colour for both schemes, so the dark canvas swaps in light text.
const unregisteredTargetDarkColor={elements:{...unregisteredTargetColor.elements,hiContrast:'#e5e7eb',loContrast:'#d1d5db'},
  relationships:{...unregisteredTargetColor.relationships,label:'#d1d5db',labelBg:'#1f2937'}} as const;
type ThemedSpecification={readonly specification:{readonly customColors?:Readonly<Record<string,unknown>>|undefined}};
export function withCanvasColorScheme<T extends ThemedSpecification>(data:T,dark:boolean):T {
  const customColors=data.specification.customColors;
  if(!dark||!customColors||!Object.hasOwn(customColors,UNREGISTERED_TARGET_COLOR)) return data;
  return {...data,specification:{...data.specification,customColors:{...customColors,[UNREGISTERED_TARGET_COLOR]:unregisteredTargetDarkColor}}};
}
export const elementStyles:Record<string,ElementStyle>={
  repository:{color:'slate',opacity:12},artifact:{color:'gray'},
  service:{color:paletteColor('application')},application:{color:paletteColor('application')},
  datastore:{color:paletteColor('itComponent')},sqlObject:{color:paletteColor('dataObject'),shape:'rectangle'},
  database:{color:paletteColor('itComponent'),shape:'storage'},
  databaseReference:{color:paletteColor('itComponent'),shape:'component',border:'dashed',opacity:12},
  sqlModel:{color:paletteColor('itComponent'),shape:'component',opacity:12},
  channel:{color:paletteColor('interface')},
  capability:{color:paletteColor('businessCapability')},boundary:{color:paletteColor('businessContext')},
  component:{color:paletteColor('itComponent')},module:{color:paletteColor('itComponent')},member:{color:paletteColor('itComponent')},
  interface:{color:paletteColor('interface'),border:'dashed'},endpoint:{color:paletteColor('interface')},
  external:{color:'gray',border:'dashed'},unregisteredTarget:{color:UNREGISTERED_TARGET_COLOR,shape:'rectangle',border:'dashed'},unmatched:{color:'red',border:'dashed'},term:{color:paletteColor('dataObject')},
};
export function projectLiveGraph(input:LiveModelInput):LiveProjection {
  validateLiveInput(input);
  const nodes=[...input.graph.nodes].sort(order),edges=[...input.graph.edges].sort(order);
  const elements:Record<string,Element>={},relations:Record<string,Relationship>={},views:Record<string,ParsedElementView>={};
  const sourceNodeIds:Record<string,string>=Object.create(null),nodeSourceIds:Record<string,string[]>=Object.create(null),relationSourceIds:Record<string,string>=Object.create(null);
  const owners=new Map<string,{id:string;title:string;kind:string;description:string;members:LiveNode[];owner?:string}>();
  const codeLike=(node:LiveNode)=>!!node.group&&['component','interface','module','member','endpoint'].includes(node.category??'');
  const identities=new Map<string,string>();
  function identity(prefix:string,source:string):string {
    const id=`${prefix}_${stableToken(source)}`,prior=identities.get(id);
    if(prior!==undefined&&prior!==source)throw new Error('Stable source identity collision');
    identities.set(id,source);return id;
  }
  for(const node of nodes) {
    const unregistered=node.category===UNREGISTERED_HTTP_TARGET_CATEGORY;
    const sql=node.category==='datastore'&&node.symbolKey?.startsWith('sql|')?node.symbolKey.split('|'):[];
    let owner:ReturnType<typeof owners.get>;
    if(sql.length===3&&sql[1]&&sql[2]&&!sql[1].startsWith('@builtin:')) {
      const store=sql[1],unbound=store.startsWith('@unbound:'),hint=/^@unbound:(.+):(conn|config|env|dbproject)=(.*)$/.exec(store);
      const key=`sql:${store}`,kind=hint?.[2]==='dbproject'?'sqlModel':unbound?'databaseReference':'database';
      owner=owners.get(key)??{id:identity('sql_scope',store),title:hint?`${hint[3]} · ${hint[1]}`:store,kind,
        description:kind==='sqlModel'?'SQL source model; deployment unresolved':unbound?'Logical database reference; target unresolved':'Concrete database reference; deployment not verified',members:[]};
      owners.set(key,owner);
    } else if(codeLike(node)) {
      const key=`source-group:${canonical([node.repositoryId??null,node.group])}`;
      owner=owners.get(key)??{id:identity('repository',key),title:node.group!.slice(0,80),kind:'repository',description:`Explicit source group: ${node.group}`,owner:node.repositoryId?.trim()||node.group!,members:[]};
      owners.set(key,owner);
    } else if(node.repositoryId?.trim()&&!unregistered) {
      const key=`repo:${node.repositoryId}`;
      owner=owners.get(key)??{id:identity('repository',node.repositoryId),title:node.repositoryId.slice(0,80),kind:'repository',description:`Explicit repository membership: ${node.repositoryId}`,owner:node.repositoryId,members:[]};
      owners.set(key,owner);
    }
    const local=identity('n',node.id),id=owner?`${owner.id}.${local}`:local;
    owner?.members.push(node);sourceNodeIds[node.id]=id;nodeSourceIds[id]=[node.id];
    const kind=unregistered?'unregisteredTarget':node.category==='datastore'&&sql.length?'sqlObject':node.category&&Object.hasOwn(elementStyles,node.category)&&node.category!=='repository'?node.category:'artifact';
    let description=node.sublabel??node.category??'Architecture artifact';
    if(kind==='sqlObject') {
      const declaration=edges.flatMap(e=>e.facts??[]).find((fact):fact is {relation:string;to:{key:string;declared:boolean;kind:string}}=>{
        if(!fact||typeof fact!=='object'||!('relation' in fact)||fact.relation!=='DECLARES'||!('to' in fact)||!fact.to||typeof fact.to!=='object')return false;
        return 'key' in fact.to&&fact.to.key===node.symbolKey&&'declared' in fact.to&&fact.to.declared===true&&'kind' in fact.to&&typeof fact.to.kind==='string'&&['Table','View','Routine'].includes(fact.to.kind);
      });
      description=`${declaration?`Declared SQL ${declaration.to.kind.toLowerCase()}`:'SQL object reference · object type or declaration not established'} · ${description}`;
    }
    elements[id]={id:Fqn(id),kind,title:node.label??node.id,description:{txt:description},...(unregistered?{technology:UNREGISTERED_HTTP_TARGET_BADGE}:{}),style:elementStyles[kind]!,metadata:{source:canonical(node)}};
  }
  const groups:Group[]=[];
  if(owners.size>128)throw new Error('Graph exceeds 128 hierarchy scopes');
  for(const owner of [...owners.values()].sort(order)) {
    nodeSourceIds[owner.id]=owner.members.map(n=>n.id);
    elements[owner.id]={id:Fqn(owner.id),kind:owner.kind,title:owner.title,description:{txt:owner.description},style:elementStyles[owner.kind]!,metadata:{source:canonical({id:owner.id,sourceIds:nodeSourceIds[owner.id],description:owner.description})}};
    if(owner.owner)groups.push({id:owner.id,title:owner.title,members:owner.members.map(n=>sourceNodeIds[n.id]!),origin:'repository',owner:owner.owner});
  }
  const associations=new Set([...input.associationIds,...edges.filter(e=>['sameName','same','alias'].includes(e.kind??'')).map(e=>e.id)]);
  for(const edge of edges) {
    const id=RelationId(identity('r',edge.id)),certainty=edge.certainty??'unresolved';
    const association=associations.has(edge.id),uncertain=['candidate','unresolved','unbound','broken'].includes(certainty);
    // Association endpoints remain in original order; the arrowless style is semantic.
    relations[id]={id,source:{model:sourceNodeIds[edge.from]!},target:{model:sourceNodeIds[edge.to]!},kind:association?'association':uncertain?'uncertain':'observed',
      title:`${edge.label??edge.kind??'relation'} [${certainty}]`,description:{txt:`${certainty}; ${edge.count??edge.facts?.length??0} source facts. Metadata retains the original evidence.`},
      color:paletteColor('interface'),line:association||uncertain?'dashed':'solid',head:association?'none':'normal',tail:'none',metadata:{source:canonical(edge)}};
    relationSourceIds[id]=edge.id;
  }
  const addView=(id:string,title:string,refs:readonly string[],direction:'LR'|'TB'='LR',viewOf?:string)=>{
    views[id]={id:ViewId(id),title,description:null,_stage:'parsed',_type:'element',routing:'ortho',...(viewOf?{viewOf:Fqn(viewOf)}:{}),
      rules:[{include:refs.map(ref=>ref==='*'?{wildcard:true}:{ref:{model:ref.replace(/\.\*$/,'')},...(ref.endsWith('.*')?{selector:'children' as const}:{})})},{direction}]};
  };
  const title=input.graph.title??'Architecture';
  addView('index',`${title} · landscape`,['*']);
  const repositoryRefs=groups.map(g=>`${g.id}.*`);
  addView('flow',`${title} · resource overview`,['*',...repositoryRefs]);
  addView('vertical',`${title} · resource overview`,['*',...repositoryRefs],'TB');
  addView('objects',`${title} · SQL object detail`,['*',...repositoryRefs,...[...owners.values()].filter(o=>o.kind!=='repository').map(o=>`${o.id}.*`)]);
  for(const [id,label,categories,neighbors] of [
    ['applications','Application landscape',['service','application'],false],['data','Data dependencies',['datastore'],true],['messaging','Messaging dependencies',['channel'],true],
  ] as const) {
    const category=new Set(nodes.filter(n=>(categories as readonly string[]).includes(n.category??'')).map(n=>n.id));
    if(!category.size)continue;
    const selected=new Set(category);
    if(neighbors)for(const edge of edges){if(category.has(edge.from))selected.add(edge.to);if(category.has(edge.to))selected.add(edge.from);}
    const refs=[...new Set(nodes.filter(n=>selected.has(n.id)).map(n=>sourceNodeIds[n.id]!.startsWith('sql_scope_')?sourceNodeIds[n.id]!.split('.')[0]!:sourceNodeIds[n.id]!))];
    addView(id,label,refs);
  }
  for(const owner of [...owners.values()].sort(order))addView(`${owner.id}_detail`,`${owner.title} · ${owner.kind==='repository'?'repository':'SQL objects'}`,['*'],'LR',owner.id);
  if(Object.keys(views).length>135)throw new Error('Graph exceeds 135 generated views');
  const parsed:ParsedLikeC4ModelData={_stage:'parsed',projectId:ProjectId('live'),project:{id:ProjectId('live'),title},elements:Object.fromEntries(Object.values(elements).sort(order).map(element=>[element.id,element])),relations,views,
    deployments:{elements:{},relations:{}},imports:{},globals:{predicates:{},dynamicPredicates:{},styles:{}},
    specification:{tags:{},deployments:{},elements:Object.fromEntries(Object.entries(elementStyles).map(([kind,style])=>[kind,{style}])),
      relationships:{observed:{line:'solid'},association:{line:'dashed',head:'none',tail:'none',multiple:true},uncertain:{line:'dashed'}},
      customColors:Object.fromEntries([[UNREGISTERED_TARGET_COLOR,unregisteredTargetColor],...ledgerPalette.map(item=>[ledgerToken(item.key),{elements:{fill:item.color as `#${string}`,stroke:item.color as `#${string}`,hiContrast:item.foreground as `#${string}`,loContrast:item.foreground as `#${string}`},relationships:{line:item.color as `#${string}`,label:item.color as `#${string}`,labelBg:'#ffffff'}}])])}};
  return {parsed,source:`// Projection ${LIVE_MODEL_VERSION}; snapshot ${JSON.stringify(input.snapshot)}\n${serializeLiveProjection(parsed)}`,sourceNodeIds,nodeSourceIds,relationSourceIds,associationSourceIds:[...associations].sort(),groups};
}
