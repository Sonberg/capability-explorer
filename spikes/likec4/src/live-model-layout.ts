import {graphlib,layout} from '@dagrejs/dagre';
import type {ComputedElementView,ComputedNode,DiagramEdge,DiagramNode,LayoutedElementView} from '@likec4/core/types';
import {cubicPoints} from './orthogonal-routing.ts';

/** Bottom-up sibling layout avoids Dagre's compound-endpoint restrictions. */
export function layoutLiveView(view:ComputedElementView):LayoutedElementView {
  const direction=view.autoLayout.direction,vertical=direction==='TB'||direction==='BT';
  const byId=new Map<string,ComputedNode>(view.nodes.map(node=>[node.id,node]));
  const boxes=new Map<string,{x:number;y:number;width:number;height:number}>();
  const children=new Map<string|null,ComputedNode[]>();
  for(const node of view.nodes){const list=children.get(node.parent)??[];list.push(node);children.set(node.parent,list);}
  const branch=(id:string,parent:string|null):string|null=>{
    let node=byId.get(id);while(node&&node.parent!==parent)node=node.parent?byId.get(node.parent):undefined;
    return node?.id??null;
  };
  function arrange(parent:string|null):{width:number;height:number} {
    const siblings=[...(children.get(parent)??[])].sort((a,b)=>a.id<b.id?-1:a.id>b.id?1:0);
    // Parallel evidence edges share one layout constraint; evidence remains in view.edges.
    const graph=new graphlib.Graph().setGraph({rankdir:direction,nodesep:50,ranksep:100,marginx:0,marginy:0,ranker:'longest-path'}).setDefaultEdgeLabel(()=>({}));
    for(const node of siblings) {
      const compound=(children.get(node.id)?.length??0)>0;
      const nested=compound?arrange(node.id):null;
      const width=nested?Math.max(nested.width+64,240):Math.min(360,Math.max(200,Math.max(...node.title.split('\n').map(line=>line.length))*7.5+40));
      const height=nested?nested.height+100:92;
      boxes.set(node.id,{x:0,y:0,width,height});graph.setNode(node.id,{width,height});
    }
    for(const edge of view.edges) {
      const source=branch(edge.source,parent),target=branch(edge.target,parent);
      if(source&&target&&source!==target)graph.setEdge(source,target,{});
    }
    if(siblings.length)layout(graph);
    let width=0,height=0;
    for(const node of siblings) {
      const center=graph.node(node.id),box=boxes.get(node.id)!;
      box.x=center.x-box.width/2;box.y=center.y-box.height/2;
      width=Math.max(width,box.x+box.width);height=Math.max(height,box.y+box.height);
    }
    return {width,height};
  }
  arrange(null);
  function absolute(node:ComputedNode):{x:number;y:number} {
    const box=boxes.get(node.id)!;
    if(!node.parent)return {x:box.x+40,y:box.y+40};
    const parent=byId.get(node.parent);if(!parent)throw new Error('Missing layout parent');
    const offset=absolute(parent);return {x:box.x+offset.x+32,y:box.y+offset.y+68};
  }
  const nodes:DiagramNode[]=view.nodes.map(node=>{
    const box=boxes.get(node.id)!,position=absolute(node),compound=!!node.children.length;
    return {...node,...box,...position,labelBBox:{x:position.x+16,y:position.y+12,width:box.width-32,height:compound?32:box.height-24}};
  });
  const positioned=new Map(nodes.map(n=>[n.id,n]));
  const edges:DiagramEdge[]=view.edges.map((edge,index)=>{
    const from=positioned.get(edge.source)!,to=positioned.get(edge.target)!;
    let points:DiagramEdge['points'];
    if(from.id===to.id)points=[[from.x+from.width,from.y+from.height/2],[from.x+from.width+40,from.y+from.height/2],[from.x+from.width+40,from.y-30],[from.x+from.width/2,from.y-30],[from.x+from.width/2,from.y]];
    else if(vertical) {
      const down=to.y>=from.y,start:[number,number]=[from.x+from.width/2,down?from.y+from.height:from.y],end:[number,number]=[to.x+to.width/2,down?to.y:to.y+to.height];
      const mid=(start[1]+end[1])/2;points=[start,[start[0],mid],[end[0],mid],end];
    } else {
      const right=to.x>=from.x,start:[number,number]=[right?from.x+from.width:from.x,from.y+from.height/2],end:[number,number]=[right?to.x:to.x+to.width,to.y+to.height/2];
      const mid=(start[0]+end[0])/2;points=[start,[mid,start[1]],[mid,end[1]],end];
    }
    const middle=points[Math.floor(points.length/2)]!,labelWidth=Math.min(360,Math.max(40,(edge.label?.length??0)*6.5));
    const controlPoints:NonNullable<DiagramEdge['controlPoints']>=[{x:points[0][0],y:points[0][1]},...points.slice(1).map(([x,y])=>({x,y}))];
    return {...edge,points:cubicPoints(points),controlPoints,labelBBox:edge.label?{x:middle[0]-labelWidth/2,y:middle[1]-28-(index%3)*4,width:labelWidth,height:24}:null};
  });
  const all=[...nodes,...edges.flatMap(e=>e.labelBBox?[e.labelBBox]:[]),...edges.flatMap(e=>e.points.map(([x,y])=>({x,y,width:0,height:0})))];
  const x=all.length?Math.min(...all.map(b=>b.x))-24:0,y=all.length?Math.min(...all.map(b=>b.y))-24:0;
  const width=all.length?Math.max(...all.map(b=>b.x+b.width))-x+24:0,height=all.length?Math.max(...all.map(b=>b.y+b.height))-y+24:0;
  return {...view,_stage:'layouted',nodes,edges,bounds:{x,y,width,height},routing:'ortho'};
}
