import {Graphviz} from '@hpcc-js/wasm-graphviz';
import {LikeC4Styles} from '@likec4/core/styles';
import {flattenMarkdownOrString} from '@likec4/core';
import {hierarchyDistance} from '@likec4/core/utils';
import type {ComputedElementView,DiagramEdge,DiagramNode,LayoutedElementView} from '@likec4/core/types';
import {liveModelLimits,stableToken} from './live-model-validation.ts';

export type LiveViewLayout=(view:ComputedElementView)=>LayoutedElementView;
type JsonRecord=Record<string,unknown>;
const quote=(value:string)=>JSON.stringify(value);
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
/** Layout text is a bounded, escaped HTML subset. Original model strings remain untouched. */
function text(value:string):string {
  return value.replace(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g,'\ufffd').replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\ufffe\uffff]/gu,'\ufffd')
    .replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
}
function wrapped(value:string,width:number,maxLines:number):string {
  const lines:string[]=[];
  for(const paragraph of value.slice(0,liveModelLimits.string).split(/\r?\n/u)) {
    const words=paragraph.match(new RegExp(`.{1,${width}}([\\s\u200b]+|$)|[^\\s\u200b]+?([\\s\u200b]+|$)`,'gu'))??[''];
    for(const word of words) {
      // Bound even an unbroken token; this affects layout text only.
      const trimmed=word.trim();
      for(let start=0;start<Math.max(1,trimmed.length);start+=width) {
        lines.push(text(trimmed.slice(start,start+width)));
        if(lines.length===maxLines)return lines.join('<BR/>');
      }
    }
  }
  return lines.join('<BR/>');
}
const inches=(px:number)=>Math.ceil(px/72*1000)/1000;
function leafAttributes(node:ComputedElementView['nodes'][number]):string {
  const styles=LikeC4Styles.DEFAULT,{sizes,values}=styles.nodeSizes(node.style),fit=styles.fitNodeContent(node);
  const font=Math.ceil(fit.textSize),small=sizes.size==='xs'||sizes.size==='sm',wide=sizes.size==='lg'||sizes.size==='xl';
  const title=wrapped(node.title,small?30:wide?55:40,small?1:fit.maxLines);
  const description=node.description?flattenMarkdownOrString(node.description):null;
  const rows=[`<TR><TD><FONT POINT-SIZE="${font}">${title||' '}</FONT></TD></TR>`];
  if(node.technology?.trim())rows.push(`<TR><TD><FONT POINT-SIZE="${Math.ceil(font*.65)}">${wrapped(node.technology,45,1)}</FONT></TD></TR>`);
  if(description?.trim())rows.push(`<TR><TD><FONT POINT-SIZE="${Math.ceil(font*.75)}">${wrapped(description,45,small?3:5)}</FONT></TD></TR>`);
  const component=node.shape==='component',cylinder=node.shape==='storage';
  const width=values.sizes.width+(component?10:0),paddingX=values.padding+(component?20:0),paddingY=cylinder?0:values.padding;
  return `shape=${cylinder?'cylinder':'rect'},width=${inches(width)},height=${inches(values.sizes.height)},margin="${inches(paddingX)},${inches(paddingY)}",label=<<TABLE BORDER="0" CELLPADDING="0" CELLSPACING="4">${rows.join('')}</TABLE>>`;
}
/** All identifiers/attributes are generated; source text enters escaped label bodies only. */
export function graphvizDot(view:ComputedElementView):string {
  if(view.nodes.length>liveModelLimits.nodes+128||view.edges.length>liveModelLimits.edges)throw new Error('Graphviz view exceeds input limits');
  if(!['LR','RL','TB','BT'].includes(view.autoLayout.direction))throw new Error('Invalid Graphviz layout direction');
  const aliases=new Map(view.nodes.map(node=>[node.id,`n_${stableToken(node.id)}`]));
  const nodes=new Map<string,typeof view.nodes[number]>(view.nodes.map(node=>[node.id,node]));
  const containerEndpoints=new Set(view.edges.flatMap(edge=>[edge.source,edge.target]).filter(id=>nodes.get(id)?.children.length));
  if(new Set(aliases.values()).size!==view.nodes.length)throw new Error('Graphviz node identity collision');
  const children=new Map<string|null,typeof view.nodes[number][]>();
  const outgoing=new Map<string,typeof view.edges[number][]>();
  for(const node of view.nodes){const siblings=children.get(node.parent)??[];siblings.push(node);children.set(node.parent,siblings);}
  for(const edge of view.edges){const edges=outgoing.get(edge.source)??[];edges.push(edge);outgoing.set(edge.source,edges);}
  const ancestors=new Map<string,Set<string>>(),leafCounts=new Map<string,number>(),internalPairs=new Map<string,Set<string>>();
  for(const node of view.nodes) {
    const chain=new Set<string>();let parent=node.parent;
    while(parent) {
      if(chain.has(parent)||parent===node.id)throw new Error('Cyclic Graphviz hierarchy');
      const container=nodes.get(parent);if(!container||!container.children.includes(node.id)&&parent===node.parent)throw new Error('Disconnected Graphviz hierarchy');
      chain.add(parent);if(chain.size>liveModelLimits.depth)throw new Error('Graphviz hierarchy exceeds depth limit');parent=container.parent;
    }
    if(node.children.length!==(children.get(node.id)?.length??0))throw new Error('Disconnected Graphviz hierarchy');
    ancestors.set(node.id,chain);
    if(!node.children.length)for(const id of chain)leafCounts.set(id,(leafCounts.get(id)??0)+1);
  }
  const pair=(source:string,target:string)=>JSON.stringify([source,target].sort());
  const maximum=new Map<string,number>(),degree=new Map<string,number>(),distance=new Map<string,number>();
  const directions=new Set(view.edges.map(edge=>JSON.stringify([edge.source,edge.target])));
  for(const edge of view.edges) {
    const source=nodes.get(edge.source),target=nodes.get(edge.target);
    if(!source||!target)throw new Error('Missing Graphviz endpoint');
    const value=source.modelRef&&target.modelRef?hierarchyDistance(source.modelRef,target.modelRef):1;
    distance.set(edge.id,value);
    for(const id of [edge.source,edge.target]){maximum.set(id,Math.max(maximum.get(id)??0,value));degree.set(id,(degree.get(id)??0)+1);}
    for(const id of ancestors.get(edge.source)!)if(ancestors.get(edge.target)!.has(id)) {
      const pairs=internalPairs.get(id)??new Set<string>();pairs.add(pair(edge.source,edge.target));internalPairs.set(id,pairs);
    }
  }
  function rankAttributes(edge:typeof view.edges[number]):string[] {
    const value=distance.get(edge.id)!,max=Math.max(maximum.get(edge.source)??0,maximum.get(edge.target)??0);
    const weight=directions.has(JSON.stringify([edge.target,edge.source]))?1:max>value?max-value+1:degree.get(edge.source)===1&&degree.get(edge.target)===1&&value>1?value:1;
    const pairs=edge.parent?internalPairs.get(edge.parent):null;
    const isolated=edge.kind!=='association'&&edge.parent&&(leafCounts.get(edge.parent)??0)<=3&&
      (!pairs?.size||pairs.size===1&&pairs.has(pair(edge.source,edge.target)));
    return [...(weight>1?[`weight=${weight}`]:[]),...(isolated?['minlen=0']:[])];
  }
  // Computed view order is semantic and deterministic after canonical projection sorting.
  const lines=['digraph live {',`graph [rankdir=${view.autoLayout.direction},TBbalance=min,compound=true,fontname=Arial,fontsize=20,labeljust=l,labelloc=t,nodesep=1.528,ranksep=1.667,pad=0.209,outputorder=nodesfirst,splines=spline];`,
    'node [shape=rect,penwidth=0,fontname=Arial];','edge [arrowsize=0.75,fontname=Arial,fontsize=14,penwidth=2];'];
  const emitted=new Set<string>(),active=new Set<string>();
  function emit(node:typeof view.nodes[number]):void {
    if(active.has(node.id))throw new Error('Cyclic Graphviz hierarchy');
    if(emitted.has(node.id))return;
    active.add(node.id);emitted.add(node.id);
    const alias=aliases.get(node.id)!;
    if(node.children.length) {
      lines.push(`subgraph ${quote(`cluster_${alias}`)} {`,`graph [label=<<FONT POINT-SIZE="11"><B>${wrapped(node.title.toUpperCase(),60,3)||' '}</B></FONT>>,margin=${node.children.length===1?32:40}];`);
      if(containerEndpoints.has(node.id))lines.push(`${quote(`anchor_${alias}`)} [shape=point,width=0.01,height=0.01,style=invis,label=""];`);
      for(const child of children.get(node.id)??[])emit(child);
      lines.push('}');
    } else lines.push(`${quote(alias)} [${leafAttributes(node)}];`);
    active.delete(node.id);
  }
  // Declare compounds before adjacency traversal so their descendants remain scoped.
  for(const node of children.get(null)??[])if(node.children.length)emit(node);
  const edgeAliases=new Set<string>();
  for(const node of view.nodes) {
    if(!emitted.has(node.id)) {
      if(node.parent)throw new Error('Disconnected Graphviz hierarchy');
      emit(node);
    }
    for(const edge of outgoing.get(node.id)??[]) {
      const source=nodes.get(edge.source),target=nodes.get(edge.target);
      if(!source||!target)throw new Error('Missing Graphviz endpoint');
      if(!emitted.has(target.id)){if(target.parent)throw new Error('Disconnected Graphviz hierarchy');emit(target);}
      const sourceAlias=aliases.get(source.id)!,targetAlias=aliases.get(target.id)!,id=`e_${stableToken(edge.id)}`;
      if(edgeAliases.has(id))throw new Error('Graphviz edge identity collision');edgeAliases.add(id);
      const compound=source.children.length||target.children.length;
      const label=edge.label?`<<TABLE BORDER="0" CELLPADDING="3" CELLSPACING="0"><TR><TD ALIGN="TEXT" BALIGN="LEFT">${wrapped(edge.label,40,5)}</TD></TR></TABLE>>`:null;
      const attributes=[`id=${quote(id)}`,...rankAttributes(edge),...(label?[`${compound?'xlabel':'label'}=${label}`]:[]),
        ...(edge.kind==='association'?['constraint=false','minlen=0','dir=none','arrowhead=none','arrowtail=none']:[]),
        ...(source.children.length&&source.id!==target.id?[`ltail=${quote(`cluster_${sourceAlias}`)}`]:[]),
        ...(target.children.length&&source.id!==target.id?[`lhead=${quote(`cluster_${targetAlias}`)}`]:[])];
      lines.push(`${quote(source.children.length?`anchor_${sourceAlias}`:sourceAlias)} -> ${quote(target.children.length?`anchor_${targetAlias}`:targetAlias)} [${attributes.join(',')}];`);
    }
  }
  if(emitted.size!==view.nodes.length||edgeAliases.size!==view.edges.length)throw new Error('Disconnected Graphviz hierarchy or endpoint');
  lines.push('}');return lines.join('\n');
}
function points(value:unknown):DiagramEdge['points'] {
  if(!Array.isArray(value)||value.length<4||value.length>100_000||(value.length-1)%3!==0)throw new Error('Invalid Graphviz spline');
  const result=value.map(value=>{
    if(!Array.isArray(value)||value.length!==2||value.some(n=>typeof n!=='number'||!Number.isFinite(n)||Math.abs(n)>10_000_000))throw new Error('Invalid Graphviz point');
    return [value[0],value[1]] as [number,number];
  });
  return [result[0]!,...result.slice(1)];
}
/** Graphviz text drawing reports the wrapped lines; reserve native label outer padding too. */
function labelGeometry(object:JsonRecord):{width:number;height:number} {
  if(!Array.isArray(object._ldraw_))throw new Error('Missing Graphviz label drawing');
  const lines=object._ldraw_.map(record).filter(operation=>operation.op==='T');
  if(!lines.length||lines.length>5)throw new Error('Invalid Graphviz label drawing');
  let width=0,minY=Infinity,maxY=-Infinity;
  for(const line of lines) {
    if(typeof line.width!=='number'||!Number.isFinite(line.width)||line.width<0||line.width>10_000_000||!Array.isArray(line.pt)||line.pt.length!==2||line.pt.some(n=>typeof n!=='number'||!Number.isFinite(n)||Math.abs(n)>10_000_000))throw new Error('Invalid Graphviz label geometry');
    width=Math.max(width,line.width);minY=Math.min(minY,line.pt[1] as number);maxY=Math.max(maxY,line.pt[1] as number);
  }
  return {width:Math.max(40,Math.ceil(width+26)),height:Math.max(44,Math.ceil(maxY-minY+41))};
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
      if(!Number.isFinite(width)||!Number.isFinite(height)||width<=0||height<=0||width>10_000_000||height>10_000_000)throw new Error('Invalid Graphviz node size');
      geometry={x:x-width/2,y:y-height/2,width,height};
    }
    return {...node,...geometry,labelBBox:{x:geometry.x+16,y:geometry.y+12,width:Math.max(0,geometry.width-32),height:Math.max(0,node.children.length?32:geometry.height-24)}};
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
    const size=edge.label?labelGeometry(object):null,position=edge.label?numbers(object.lp??object.xlp,2):null;
    return {...edge,points:points(spline.points),controlPoints:null,labelBBox:position?{x:position[0]!-size!.width/2,y:position[1]!-size!.height/2,...size!}:null};
  });
  return {...view,_stage:'layouted',routing:'ortho',nodes,edges,bounds:box(json.bb)};
}
export async function loadGraphvizLayout():Promise<LiveViewLayout> {
  const runtime=await Graphviz.load();
  return view=>layoutGraphvizView(view,runtime);
}
