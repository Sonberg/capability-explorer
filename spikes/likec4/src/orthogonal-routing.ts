export type PhaseReporter=(name:string,start:number,end:number)=>void;
const clock=()=>performance.timeOrigin+performance.now();
import type {Point,MutablePoint,Box,DiagramNode,DiagramEdge,DiagramView} from './types.ts';
// Deterministic presentation routing: no evidence or node positions are changed.
// Inspired by the supplied HTML: spaced ports, obstacle-aware A*, lane costs.
export const MIN_LINE_GAP=24;
import {createSearch} from './routing-search.ts';
import type {RouteEnd as End,SearchFunction} from './routing-search.ts';
interface PortEntry {edge:string;node:DiagramNode;side:number;index:number;wrap:boolean;other:number}
const GRID=12, MARGIN=144;
const routedCounts=new WeakMap<DiagramView,number>();
const compare=(a:string,b:string)=>a<b?-1:a>b?1:0;
const overlaps=(a:Box,b:Box)=>a.x<b.x+b.width&&b.x<a.x+a.width&&a.y<b.y+b.height&&b.y<a.y+a.height;
const center=(n:Box):Point=>[n.x+n.width/2,n.y+n.height/2];
export function simplify(points:readonly Point[]):Point[] {
  const out:Point[]=[];
  for(const p of points){const a=out.at(-2),b=out.at(-1);if(b&&p[0]===b[0]&&p[1]===b[1])continue;
    if(a&&b&&((a[0]===b[0]&&b[0]===p[0])||(a[1]===b[1]&&b[1]===p[1])))out[out.length-1]=p;else out.push(p);}
  return out;
}
export function cubicPoints(points:readonly Point[]):[Point,...Point[]] {
  if(!points.length)throw new Error('A route needs at least one point');
  return [points[0],...points.slice(1).flatMap((p,i)=>[points[i],p,p])];
}
export const routeSegments=(points:readonly Point[]):[Point,Point][]=>points.slice(1).map((p,i)=>[points[i],p]);
function ports(edges:DiagramEdge[],nodes:Map<string,DiagramNode>) {
  const ends=new Map<string,End[]>(), buckets=new Map<string,PortEntry[]>();
  for(const e of edges){const a=nodes.get(e.source),b=nodes.get(e.target);if(!a||!b)continue;
    const ca=center(a),cb=center(b),dx=cb[0]-ca[0],dy=cb[1]-ca[1];
    const gapX=Math.max(a.x,b.x)-Math.min(a.x+a.width,b.x+b.width);
    const gapY=Math.max(a.y,b.y)-Math.min(a.y+a.height,b.y+b.height);
    const horizontal=gapX>=gapY;
    let sides=e.source===e.target?[0,1]:horizontal?(dx>=0?[0,2]:[2,0]):(dy>=0?[1,3]:[3,1]);
    // Aligned endpoints with intervening nodes need a shared outside corridor.
    // Facing ports otherwise trap long connections behind their own endpoints.
    const lo=horizontal?Math.max(a.y,b.y):Math.max(a.x,b.x);
    const hi=horizontal?Math.min(a.y+a.height,b.y+b.height):Math.min(a.x+a.width,b.x+b.width);
    const leaves=[...nodes.values()].filter(n=>!n.children?.length);
    if(e.source!==e.target&&hi>lo){
      const along=(lo+hi)/2;
      const p:Point=horizontal?[Math.min(a.x+a.width,b.x+b.width),along]:[along,Math.min(a.y+a.height,b.y+b.height)];
      const q:Point=horizontal?[Math.max(a.x,b.x),along]:[along,Math.max(a.y,b.y)];
      if(leaves.some(n=>n.id!==a.id&&n.id!==b.id&&segmentHits(p,q,n))){
        const low=horizontal?Math.min(...leaves.map(n=>n.y)):Math.min(...leaves.map(n=>n.x));
        const high=horizontal?Math.max(...leaves.map(n=>n.y+n.height)):Math.max(...leaves.map(n=>n.x+n.width));
        const side=along-low<=high-along?(horizontal?3:2):(horizontal?1:0);
        sides=[side,side];
      }
    }
    ends.set(e.id,[]);
    [a,b].forEach((n,i)=>{const side=sides[i],key=`${n.id}:${side}`,list=buckets.get(key)??[];
      list.push({edge:e.id,node:n,side,index:i,wrap:sides[0]===sides[1],other:(i===0?cb:ca)[side%2===0?1:0]});buckets.set(key,list);});
  }
  for(const list of buckets.values()){
    // Same-side connections nest: reverse the remote order at shared terminals.
    list.sort((a,b)=>Number(a.wrap)-Number(b.wrap)||(a.wrap?-1:1)*(a.other-b.other)||compare(a.edge,b.edge));
    list.forEach((entry,i)=>{const {node:n,side}=entry,vertical=side%2===0;
      const lo=vertical?n.y:n.x,len=vertical?n.height:n.width;
      const min=Math.ceil((lo+8)/GRID)*GRID,max=Math.floor((lo+len-8)/GRID)*GRID;
      if(min>max)return; // Tiny nodes keep native routing rather than diagonal connectors.
      const along=Math.max(min,Math.min(max,Math.round((lo+len*(i+1)/(list.length+1))/GRID)*GRID));
      const point:MutablePoint=vertical?[side===0?n.x+n.width:n.x,along]:[along,side===1?n.y+n.height:n.y];
      const stub:MutablePoint=[...point];stub[vertical?0:1]=(side<2?Math.ceil:Math.floor)((point[vertical?0:1]+(side<2?24:-24))/GRID)*GRID;
      ends.get(entry.edge)![entry.index]={point,stub,side};
    });
  }
  // Facing ports should share a straight line if it preserves the port spacing.
  for(const edge of edges){const pair=ends.get(edge.id);if(!pair?.[0]||!pair?.[1]||(pair[0].side+2)%4!==pair[1].side)continue;
    const axis=pair[0].side%2===0?1:0;
    for(const i of [0,1]){const end=pair[i],value=pair[1-i].point[axis],n=nodes.get(i===0?edge.source:edge.target)!;
      const lo=axis===0?n.x:n.y,len=axis===0?n.width:n.height;
      if(value<lo+8||value>lo+len-8)continue;
      const bucket=buckets.get(`${n.id}:${end.side}`)!;
      if(bucket.some(item=>item.edge!==edge.id&&Math.abs(ends.get(item.edge)?.[item.index]?.point[axis]! -value)<MIN_LINE_GAP))continue;
      end.point[axis]=value;end.stub[axis]=value;break;
    }
  }
  return ends;
}
function segmentHits(a:Point,b:Point,n:Box) {
  return a[0]===b[0]?a[0]>n.x&&a[0]<n.x+n.width&&Math.max(a[1],b[1])>n.y&&Math.min(a[1],b[1])<n.y+n.height:
    a[1]>n.y&&a[1]<n.y+n.height&&Math.max(a[0],b[0])>n.x&&Math.min(a[0],b[0])<n.x+n.width;
}
function gridFor(view:DiagramView) {
  const left=Math.floor((Math.min(view.bounds.x,...view.nodes.map(n=>n.x))-MARGIN)/GRID)*GRID;
  const top=Math.floor((Math.min(view.bounds.y,...view.nodes.map(n=>n.y))-MARGIN)/GRID)*GRID;
  const right=Math.max(view.bounds.x+view.bounds.width,...view.nodes.map(n=>n.x+n.width))+MARGIN;
  const bottom=Math.max(view.bounds.y+view.bounds.height,...view.nodes.map(n=>n.y+n.height))+MARGIN;
  const width=Math.ceil((right-left)/GRID)+1,height=Math.ceil((bottom-top)/GRID)+1;
  // Bound synchronous work for unusually large/manual layouts.
  if(width*height>180000)return null;
  const blocked=new Uint8Array(width*height), headers=new Uint8Array(width*height);
  const obstacles=view.nodes.filter(n=>!n.children?.length).map(n=>({x:n.x-8,y:n.y-8,width:n.width+16,height:n.height+16}));
  const titles=view.nodes.filter(n=>n.children?.length).map(n=>({x:n.x,y:n.y,width:n.width,height:40}));
  function mark(rect:Box,buffer:Uint8Array){for(let y=Math.max(0,Math.ceil((rect.y-top)/GRID));y<=Math.min(height-1,Math.floor((rect.y+rect.height-top)/GRID));y++)for(let x=Math.max(0,Math.ceil((rect.x-left)/GRID));x<=Math.min(width-1,Math.floor((rect.x+rect.width-left)/GRID));x++)buffer[y*width+x]=1;}
  obstacles.forEach(r=>mark(r,blocked));titles.forEach(r=>mark(r,headers));
  return {left,top,width,height,blocked,headers,obstacles,titles,terminalClearance:new Uint16Array(width*height),horizontal:new Uint16Array(width*height),vertical:new Uint16Array(width*height)};
}
// Native labels add padding around their allocated text box.
const labelPadding=16;
const padded=(box:Box):Box=>({x:box.x-labelPadding,y:box.y-labelPadding,width:box.width+labelPadding*2,height:box.height+labelPadding*2});
function placeLabel(edge:DiagramEdge,points:readonly Point[],occupied:Box[],lineSegments:[Point,Point][]) {
  if(!edge.labelBBox)return edge.labelBBox;
  const {width,height}=edge.labelBBox;
  const segments=routeSegments(points).sort(([a,b],[c,d])=>(Math.abs(d[0]-c[0])+Math.abs(d[1]-c[1]))-(Math.abs(b[0]-a[0])+Math.abs(b[1]-a[1])));
  let best=edge.labelBBox,bestCost=Infinity;
  // Search along each segment at label-sized intervals, not just three fractions.
  // Include every routed line and padded label so text cannot hide a connection.
  for(const [a,b] of segments){
    const horizontal=a[1]===b[1],length=Math.abs(b[0]-a[0])+Math.abs(b[1]-a[1]);
    const step=horizontal?width+labelPadding*2:height+labelPadding*2;
    const fractions=[.5,...Array.from({length:Math.min(80,Math.floor(length/step))},(_,i)=>(i+.5)*step/length)];
    for(let band=0;band<8;band++)for(const fraction of fractions)for(const side of [-1,1]){
      const x=a[0]+(b[0]-a[0])*fraction,y=a[1]+(b[1]-a[1])*fraction;
      const offset=16+band*((horizontal?height:width)+labelPadding*2);
      const box={x:horizontal?x-width/2:x+(side<0?-width-offset:offset),y:horizontal?y+(side<0?-height-offset:offset):y-height/2,width,height};
      const clearance=padded(box);
      const cost=occupied.filter(r=>overlaps(clearance,r)).length*100+lineSegments.filter(([p,q])=>segmentHits(p,q,clearance)).length;
      if(cost<bestCost){best=box;bestCost=cost;}if(cost===0){occupied.push(clearance);return box;}
    }
  }
  occupied.push(padded(best));return best!;
}
function routeOnce(view:DiagramView,order:number,report:PhaseReporter|undefined,searcher:SearchFunction):DiagramView {
  if(!view.nodes.length||!view.edges.length||view.edges.length>250)return {...view,routing:'ortho'};
  const gridStart=report?clock():0;
  const grid=gridFor(view);if(!grid)return {...view,routing:'ortho'};
  const nodes=new Map(view.nodes.map(n=>[n.id,n])),ordered=[...view.edges].sort((a,b)=>compare(a.id,b.id)),endpoints=ports(ordered,nodes),routes=new Map<string,Point[]>();
  // Reserve every endpoint's short exit lane before routing any relationship.
  // Otherwise an earlier path can occupy a later edge's unavoidable node connector.
  const reserve=(ends:End[],delta:number)=>{for(const end of ends){if(!end)continue;
    const vertical=end.point[0]===end.stub[0],axis=vertical?1:0,buffer=vertical?grid.vertical:grid.horizontal;
    for(let value=Math.ceil(Math.min(end.point[axis],end.stub[axis])/GRID)*GRID;value<=Math.max(end.point[axis],end.stub[axis]);value+=GRID){
      const p=end.point.slice();p[axis]=value;const cell=Math.round((p[1]-grid.top)/GRID)*grid.width+Math.round((p[0]-grid.left)/GRID);buffer[cell]+=delta;
      const cx=cell%grid.width,cy=Math.floor(cell/grid.width);
      for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++){if(cx+dx>=0&&cx+dx<grid.width&&cy+dy>=0&&cy+dy<grid.height)grid.terminalClearance[cell+dy*grid.width+dx]+=delta;}
    }
  }};
  for(const ends of endpoints.values())reserve(ends,1);
  ordered.sort((a,b)=>{const length=(e:DiagramEdge)=>{const p=endpoints.get(e.id);return p?.[0]&&p?.[1]?Math.abs(p[0].point[0]-p[1].point[0])+Math.abs(p[0].point[1]-p[1].point[1]):Infinity;};return order===2?compare(a.id,b.id):(order===1?-1:1)*(length(a)-length(b))||compare(a.id,b.id);});
  report?.(`order${order}.grid-ports`,gridStart,clock());
  const searchStart=report?clock():0;
  for(const edge of ordered){const ends=endpoints.get(edge.id);if(!ends?.[0]||!ends?.[1])continue;
    const stubBlocked=ends.some((end,i)=>view.nodes.some(n=>!n.children?.length&&n.id!==(i===0?edge.source:edge.target)&&segmentHits(end.point,end.stub,n)));
    if(stubBlocked)continue;
    reserve(ends,-1);
    const path=searcher(grid,ends[0],ends[1]);
    reserve(ends,1);
    if(!path)continue;
    const points=simplify([ends[0].point,...path,ends[1].point]);routes.set(edge.id,points);
    for(let i=1;i<path.length;i++){const buffer=path[i][1]===path[i-1][1]?grid.horizontal:grid.vertical;for(const p of [path[i-1],path[i]])buffer[Math.round((p[1]-grid.top)/GRID)*grid.width+Math.round((p[0]-grid.left)/GRID)]++;}
  }
  report?.(`order${order}.search`,searchStart,clock());
  const labelStart=report?clock():0;
  const fallback=ordered.filter(e=>!routes.has(e.id));
  const occupied=[...grid.obstacles,...grid.titles];
  // Retained native geometry still occupies space when a route cannot be replaced.
  const lineSegments=[...routes.values(),...fallback.map(e=>(e.points??[]).filter((_,i)=>i%3===0))].flatMap(routeSegments);
  // A retained connector still needs label placement: native initial labels may overlap.
  const labels=new Map(ordered.map(e=>[e.id,placeLabel(e,routes.get(e.id)??e.points.filter((_,i)=>i%3===0),occupied,lineSegments)]));
  report?.(`order${order}.labels`,labelStart,clock());
  const applyStart=report?clock():0;
  const edges=view.edges.map(edge=>routes.has(edge.id)?{...edge,points:cubicPoints(edge.dir==='back'?[...routes.get(edge.id)!].reverse():routes.get(edge.id)!),controlPoints:null,labelBBox:labels.get(edge.id),isLabelCustomized:true}:{...edge,labelBBox:labels.get(edge.id),isLabelCustomized:true});
  const points=edges.flatMap(e=>e.points??[]),boxes=[...view.nodes,...occupied];
  const x=Math.min(...boxes.map(b=>b.x),...points.map(p=>p[0]))-12,y=Math.min(...boxes.map(b=>b.y),...points.map(p=>p[1]))-12;
  const right=Math.max(...boxes.map(b=>b.x+b.width),...points.map(p=>p[0]))+12,bottom=Math.max(...boxes.map(b=>b.y+b.height),...points.map(p=>p[1]))+12;
  const result:DiagramView={...view,routing:'ortho',edges,bounds:{x,y,width:right-x,height:bottom-y}};
  routedCounts.set(result,routes.size);
  report?.(`order${order}.geometry`,applyStart,clock());
  return result;
}

function quality(view:DiagramView) {
  const segments=view.edges.flatMap(e=>routeSegments(e.points.filter((_,i)=>i%3===0)).map(([a,b])=>({id:e.id,a,b})));
  let shared=0,near=0,crossings=0,length=0;
  for(const {a,b} of segments)length+=Math.abs(b[0]-a[0])+Math.abs(b[1]-a[1]);
  for(let i=0;i<segments.length;i++)for(let j=i+1;j<segments.length;j++){
    const {a,b,id}=segments[i],{a:c,b:d,id:other}=segments[j];if(id===other)continue;
    const vertical=a[0]===b[0],otherVertical=c[0]===d[0];
    if(vertical===otherVertical){const axis=vertical?0:1,along=1-axis;if(Math.min(Math.max(a[along],b[along]),Math.max(c[along],d[along]))>Math.max(Math.min(a[along],b[along]),Math.min(c[along],d[along]))){if(a[axis]===c[axis])shared++;else if(Math.abs(a[axis]-c[axis])<MIN_LINE_GAP)near++;}}
    else {const [v,h]=vertical?[[a,b],[c,d]]:[[c,d],[a,b]];if(v[0][0]>Math.min(h[0][0],h[1][0])&&v[0][0]<Math.max(h[0][0],h[1][0])&&h[0][1]>Math.min(v[0][1],v[1][1])&&h[0][1]<Math.max(v[0][1],v[1][1]))crossings++;}
  }
  return [(view.edges.length-(routedCounts.get(view)??0)),shared+near,crossings,segments.length,length];
}
export function routeOrthogonal(view:DiagramView,report?:PhaseReporter,searcher:SearchFunction=createSearch()):DiagramView {
  if(view.edges.length>250)return {...view,routing:'ortho'};
  // Three bounded, deterministic orders reduce the greedy router's ordering bias.
  let best:DiagramView|null=null,score:number[]|null=null;
  for(const order of [0,1,2]){const candidate=routeOnce(view,order,report,searcher),qualityStart=report?clock():0,next=quality(candidate);
    report?.(`order${order}.quality`,qualityStart,clock());
    const diff=score?next.findIndex((n,i)=>n!==score![i]):-1;
    if(!best||(diff>=0&&next[diff]<score![diff])){best=candidate;score=next;}
  }
  return best!;
}
