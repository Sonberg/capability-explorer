import {Graphviz} from '@hpcc-js/wasm-graphviz';
import type {ComputedElementView,DiagramEdge,DiagramNode,LayoutedElementView} from '@likec4/core/types';
import {liveModelLimits,stableToken} from './live-model-validation.ts';

export type LiveViewLayout=(view:ComputedElementView)=>LayoutedElementView;
type JsonRecord=Record<string,unknown>;
const quote=(value:string)=>JSON.stringify(value);
const labelSize=(label:string|null)=>({width:Math.min(360,Math.max(40,(label?.length??0)*7.5+20)),height:44});
const table=(width:number,height:number)=>`<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="0" FIXEDSIZE="TRUE" WIDTH="${width}" HEIGHT="${height}"><TR><TD> </TD></TR></TABLE>>`;
function record(value:unknown):JsonRecord {
  if(!value||typeof value!=='object'||Array.isArray(value))throw new Error('Invalid Graphviz record');
  return value as JsonRecord;
}
function numbers(value:unknown,size:number):number[] {
  if(typeof value!=='string')throw new Error('Missing Graphviz geometry');
  const parts=value.split(',');
  if(parts.some(part=>!part.trim()))throw new Error('Invalid Graphviz geometry');
  const result=parts.map(Number);
  if(result.length!==size||result.some(n=>!Number.isFinite(n)||Math.abs(n)>10_000_000))throw new Error('Invalid Graphviz geometry');
  return result;
}
function box(value:unknown) {
  const [x1,y1,x2,y2]=numbers(value,4) as [number,number,number,number];
  return {x:Math.min(x1,x2),y:Math.min(y1,y2),width:Math.abs(x2-x1),height:Math.abs(y2-y1)};
}
/** Only generated identifiers and bounded numeric dimensions enter DOT/HTML. */
export function graphvizDot(view:ComputedElementView):string {
  // A computed view can include the bounded 128 synthetic ownership scopes.
  if(view.nodes.length>liveModelLimits.nodes+128||view.edges.length>liveModelLimits.edges)throw new Error('Graphviz view exceeds input limits');
  if(!['LR','RL','TB','BT'].includes(view.autoLayout.direction))throw new Error('Invalid Graphviz layout direction');
  const aliases=new Map(view.nodes.map(node=>[node.id,`n_${stableToken(node.id)}`]));
  const nodes=new Map(view.nodes.map(node=>[node.id,node]));
  const containerEndpoints=new Set(view.edges.flatMap(edge=>[edge.source,edge.target]).filter(id=>nodes.get(id)?.children.length));
  if(new Set(aliases.values()).size!==view.nodes.length)throw new Error('Graphviz node identity collision');
  const children=new Map<string|null,typeof view.nodes[number][]>();
  for(const node of view.nodes){const siblings=children.get(node.parent)??[];siblings.push(node);children.set(node.parent,siblings);}
  const lines=['digraph live {',`graph [rankdir=${view.autoLayout.direction}, compound=true, nodesep=1.527777778, ranksep=2, pad=0, margin=0, newrank=false];`,
    'node [shape=box, fixedsize=true, label="", margin=0];','edge [arrowhead=none, arrowtail=none];'];
  const emitted=new Set<string>();
  function emit(parent:string|null):void {
    for(const node of [...(children.get(parent)??[])].sort((a,b)=>a.id<b.id?-1:a.id>b.id?1:0)) {
      if(emitted.has(node.id))throw new Error('Cyclic Graphviz hierarchy');emitted.add(node.id);
      const alias=aliases.get(node.id)!;
      if(node.children.length) {
        lines.push(`subgraph ${quote(`cluster_${alias}`)} {`,`graph [label=${table(200,44)}, labelloc=t, labeljust=l, margin=40];`);
        if(containerEndpoints.has(node.id))lines.push(`${quote(`anchor_${alias}`)} [shape=point, width=0.01, height=0.01, style=invis];`);
        emit(node.id);lines.push('}');
      } else {
        const width=Math.min(420,Math.max(320,Math.max(...node.title.split('\n').map(line=>line.length))*7.5+40));
        lines.push(`${quote(alias)} [width=${(width/72).toFixed(6)}, height=2.5];`);
      }
    }
  }
  emit(null);
  if(emitted.size!==view.nodes.length)throw new Error('Disconnected Graphviz hierarchy');
  const edgeAliases=new Set<string>();
  for(const edge of [...view.edges].sort((a,b)=>a.id<b.id?-1:a.id>b.id?1:0)) {
    const source=nodes.get(edge.source),target=nodes.get(edge.target);
    if(!source||!target)throw new Error('Missing Graphviz endpoint');
    const sourceAlias=aliases.get(source.id)!,targetAlias=aliases.get(target.id)!,id=`e_${stableToken(edge.id)}`;
    if(edgeAliases.has(id))throw new Error('Graphviz edge identity collision');edgeAliases.add(id);
    const size=labelSize(edge.label);
    const attributes=[`id=${quote(id)}`,...(edge.label?[`xlabel=${table(size.width,size.height)}`]:[]),
      ...(edge.kind==='association'?['constraint=false','minlen=0','dir=none']:[]),
      ...(source.children.length&&source.id!==target.id?[`ltail=${quote(`cluster_${sourceAlias}`)}`]:[]),
      ...(target.children.length&&source.id!==target.id?[`lhead=${quote(`cluster_${targetAlias}`)}`]:[])];
    lines.push(`${quote(source.children.length?`anchor_${sourceAlias}`:sourceAlias)} -> ${quote(target.children.length?`anchor_${targetAlias}`:targetAlias)} [${attributes.join(',')}];`);
  }
  lines.push('}');return lines.join('\n');
}
function points(value:unknown):DiagramEdge['points'] {
  if(!Array.isArray(value)||!value.length||value.length>100_000||(value.length-1)%3!==0)throw new Error('Invalid Graphviz spline');
  const result=value.map(value=>{
    if(!Array.isArray(value)||value.length!==2||value.some(n=>typeof n!=='number'||!Number.isFinite(n)||Math.abs(n)>10_000_000))throw new Error('Invalid Graphviz point');
    return [value[0],value[1]] as [number,number];
  });
  return [result[0]!,...result.slice(1)];
}
/** Adapter consumes the public Graphviz JSON format; no LikeC4 private imports. */
export function layoutGraphvizView(view:ComputedElementView,runtime:Pick<Graphviz,'layout'>):LayoutedElementView {
  if(!view.nodes.length) {
    if(view.edges.length)throw new Error('Missing Graphviz endpoint');
    return {...view,_stage:'layouted',routing:'ortho',nodes:[],edges:[],bounds:{x:0,y:0,width:0,height:0}};
  }
  const raw=runtime.layout(graphvizDot(view),'json','dot',{yInvert:true});
  if(raw.length>64_000_000)throw new Error('Graphviz result exceeds limit');
  const json=record(JSON.parse(raw)),objects=json.objects;
  if(!Array.isArray(objects)||objects.length>view.nodes.length*3)throw new Error('Invalid Graphviz objects');
  const byName=new Map<string,JsonRecord>();
  for(const value of objects){const object=record(value);if(typeof object.name!=='string'||byName.has(object.name))throw new Error('Invalid Graphviz object identity');byName.set(object.name,object);}
  const nodes:DiagramNode[]=view.nodes.map(node=>{
    const alias=`n_${stableToken(node.id)}`,object=byName.get(node.children.length?`cluster_${alias}`:alias);
    if(!object)throw new Error('Graphviz omitted model node');
    let geometry;
    if(node.children.length)geometry=box(object.bb);
    else {
      const [x,y]=numbers(object.pos,2) as [number,number],width=Math.round(Number(object.width)*72),height=Math.round(Number(object.height)*72);
      if(!Number.isFinite(width)||!Number.isFinite(height)||width<=0||height<=0)throw new Error('Invalid Graphviz node size');
      geometry={x:x-width/2,y:y-height/2,width,height};
    }
    return {...node,...geometry,labelBBox:{x:geometry.x+16,y:geometry.y+12,width:Math.max(0,geometry.width-32),height:node.children.length?32:geometry.height-24}};
  });
  const rawEdges=json.edges??[];
  if(!Array.isArray(rawEdges)||rawEdges.length!==view.edges.length)throw new Error('Graphviz omitted model relationships');
  const edgeById=new Map<string,JsonRecord>();
  for(const value of rawEdges){const edge=record(value);if(typeof edge.id!=='string'||edgeById.has(edge.id))throw new Error('Invalid Graphviz edge identity');edgeById.set(edge.id,edge);}
  const edges:DiagramEdge[]=view.edges.map(edge=>{
    const object=edgeById.get(`e_${stableToken(edge.id)}`);if(!object)throw new Error('Graphviz omitted model edge');
    if(!Array.isArray(object._draw_))throw new Error('Missing Graphviz edge drawing');
    const spline=object._draw_.map(record).find(operation=>operation.op==='b');
    if(!spline)throw new Error('Missing Graphviz edge spline');
    const size=labelSize(edge.label),position=edge.label?numbers(object.lp??object.xlp,2):null;
    return {...edge,points:points(spline.points),controlPoints:null,labelBBox:position?{x:position[0]!-size.width/2,y:position[1]!-size.height/2,...size}:null};
  });
  return {...view,_stage:'layouted',routing:'ortho',nodes,edges,bounds:box(json.bb)};
}
export async function loadGraphvizLayout():Promise<LiveViewLayout> {
  const runtime=await Graphviz.load();
  return view=>layoutGraphvizView(view,runtime);
}
