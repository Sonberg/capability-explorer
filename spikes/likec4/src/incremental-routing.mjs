import {cubicPoints,simplify} from './orthogonal-routing.mjs';
// Provisional drag geometry only. Global obstacle/label clearance is refined off-thread.
export function previewRoutes(previous,next) {
  if(!previous)return {...next,routing:'ortho'};
  const oldNodes=new Map(previous.nodes.map(n=>[n.id,n])),newNodes=new Map(next.nodes.map(n=>[n.id,n]));
  const oldEdges=new Map(previous.edges.map(e=>[e.id,e]));
  const delta=id=>{const a=oldNodes.get(id),b=newNodes.get(id);return a&&b&&a.width===b.width&&a.height===b.height?[b.x-a.x,b.y-a.y]:null;};
  const edges=next.edges.map(edge=>{
    const old=oldEdges.get(edge.id),a=delta(edge.source),b=delta(edge.target);
    if(!old||!a||!b)return edge;
    if(a.every(v=>v===0)&&b.every(v=>v===0))return {...edge,points:old.points,controlPoints:old.controlPoints,labelBBox:old.labelBBox,isLabelCustomized:old.isLabelCustomized};
    const points=old.points.filter((_,i)=>i%3===0).map(p=>[...p]);
    if(points.length<2)return edge;
    const startDelta=edge.dir==='back'?b:a,endDelta=edge.dir==='back'?a:b;
    if(a[0]===b[0]&&a[1]===b[1])return {...edge,points:old.points.map(p=>[p[0]+a[0],p[1]+a[1]]),controlPoints:null,isLabelCustomized:true,
      labelBBox:old.labelBBox?{...old.labelBBox,x:old.labelBBox.x+a[0],y:old.labelBBox.y+a[1]}:old.labelBBox};
    const firstHorizontal=points[0][1]===points[1][1],lastHorizontal=points.at(-1)[1]===points.at(-2)[1];
    points[0]=points[0].map((v,i)=>v+startDelta[i]);points[points.length-1]=points.at(-1).map((v,i)=>v+endDelta[i]);
    let route=points;
    if(points.length<4){const start=points[0],end=points.at(-1);const mid=firstHorizontal?(start[0]+end[0])/2:(start[1]+end[1])/2;
      route=firstHorizontal!==lastHorizontal?[start,firstHorizontal?[end[0],start[1]]:[start[0],end[1]],end]:firstHorizontal?[start,[mid,start[1]],[mid,end[1]],end]:[start,[start[0],mid],[end[0],mid],end];
    }else{
      points[1][firstHorizontal?1:0]=points[0][firstHorizontal?1:0];
      points[points.length-2][lastHorizontal?1:0]=points.at(-1)[lastHorizontal?1:0];
    }
    return {...edge,points:cubicPoints(simplify(route)),controlPoints:null,labelBBox:old.labelBBox,isLabelCustomized:true};
  });
  return {...next,edges,routing:'ortho'};
}
