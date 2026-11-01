import type {DiagramEdge,LayoutedView} from 'likec4/model';
import type {OnInitialized} from 'likec4/react';

type Initialized=Parameters<OnInitialized>[0];
// A stable key deliberately excludes evidence, selection and editor bookkeeping.
export function geometryKey(layout:LayoutedView):string {
  return JSON.stringify({id:layout.id,bounds:layout.bounds,
    nodes:layout.nodes.map(n=>[n.id,n.x,n.y,n.width,n.height,n.labelBBox]),
    edges:layout.edges.map(e=>[e.id,e.points,e.controlPoints??null,e.labelBBox??null,e.isLabelCustomized??false])});
}

/** Match LikeC4 1.60 createViewChange: absolute coordinates and native rounding. */
function capture({diagram,xyflow}:Initialized):LayoutedView {
  const {drifts:_drifts,_layout:_layout,...base}=diagram.currentView;
  const nodes=base.nodes.map(node=>{
    const internal=xyflow.getInternalNode(node.id);
    if(!internal)return node;
    const data=internal.data,position=internal.internals.positionAbsolute;
    return {...node,
      shape:'shape' in data?data.shape:node.shape,
      color:'color' in data?data.color:node.color,
      style:'style' in data?{...data.style}:node.style,
      x:Math.floor(position.x),y:Math.floor(position.y),
      width:Math.ceil(internal.measured?.width??internal.width??internal.initialWidth??0),
      height:Math.ceil(internal.measured?.height??internal.height??internal.initialHeight??0)};
  });
  const liveEdges=new Map(xyflow.getEdges().map(edge=>[edge.id,edge]));
  const edges:DiagramEdge[]=base.edges.map(edge=>{
    const live=liveEdges.get(edge.id);
    if(!live)return edge;
    const data=live.data;
    const controls='controlPoints' in data?data.controlPoints:null;
    return {...edge,points:data.points,
      labelBBox:data.labelBBox?{x:Math.round(data.labelBBox.x),y:Math.round(data.labelBBox.y),width:Math.round(data.labelBBox.width),height:Math.round(data.labelBBox.height)}:null,
      ...('isLabelCustomized' in data&&data.isLabelCustomized!==undefined?{isLabelCustomized:data.isLabelCustomized}:{}),
      controlPoints:controls?.length?[{x:Math.round(controls[0].x),y:Math.round(controls[0].y)},...controls.slice(1).map(p=>({x:Math.round(p.x),y:Math.round(p.y)}))]:null};
  });
  // Native calcViewBounds includes control points instead of spline points when
  // supplied, labels and nodes, then expands the resulting rectangle by 20.
  const boxes=[...nodes];
  let left=Infinity,top=Infinity,right=-Infinity,bottom=-Infinity;
  const point=(x:number,y:number)=>{left=Math.min(left,x);top=Math.min(top,y);right=Math.max(right,x);bottom=Math.max(bottom,y);};
  for(const box of boxes){point(box.x,box.y);point(box.x+box.width,box.y+box.height);}
  for(const edge of edges){
    if(edge.controlPoints)for(const p of edge.controlPoints)point(p.x,p.y);
    else for(const [x,y] of edge.points)point(x,y);
    if(edge.labelBBox){const b=edge.labelBBox;point(b.x,b.y);point(b.x+b.width,b.y+b.height);}
  }
  const bounds=Number.isFinite(left)&&Number.isFinite(top)?{x:left-20,y:top-20,width:right-left+40,height:bottom-top+40}:base.bounds;
  return {...base,_layout:'manual',nodes,edges,bounds};
}

/** Observe native history; never replace drag handlers or send editor events. */
export function attachImmediateLayout(initialized:Initialized,onLayout:(layout:LayoutedView)=>void):()=>void {
  const editor=initialized.diagram.editorActor();
  let previous=editor.getSnapshot().context,disposed=false,scheduled=false;
  let lastKey:string|undefined;
  const subscription=editor.subscribe(snapshot=>{
    const next=snapshot.context;
    const historyChanged=next.history!==previous.history||next.redo!==previous.redo;
    previous=next;
    if(next.editing||!historyChanged||scheduled)return;
    scheduled=true;
    // Let the native action finish applying undo/redo geometry before reading it.
    queueMicrotask(()=>{
      scheduled=false;
      if(disposed||editor.getSnapshot().context.editing)return;
      const layout=capture(initialized),key=geometryKey(layout);
      if(key!==lastKey){lastKey=key;onLayout(layout);}
    });
  });
  return ()=>{disposed=true;subscription.unsubscribe();};
}
