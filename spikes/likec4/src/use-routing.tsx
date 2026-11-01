import {useEffect,useMemo,useRef,useState} from 'react';
import type {LayoutedView} from 'likec4/model';
import type {RoutingRequest,RoutingResult} from './types.ts';
import {previewRoutes} from './incremental-routing.ts';
type Completed={layout:LayoutedView}&RoutingResult<LayoutedView>;
export function useRouting(layout:LayoutedView,enabled:boolean,retry:number) {
  const generation=useRef(0),previous=useRef<LayoutedView|null>(null),cache=useRef(new WeakMap<LayoutedView,Completed>());
  const [completed,setCompleted]=useState<Completed|null>(null);
  const preview=useMemo(()=>{const start=performance.now();const view:LayoutedView=enabled?previewRoutes(previous.current,layout):{...layout,routing:'spline'};
    return {view,generation:++generation.current,milliseconds:performance.now()-start};},[layout,enabled]);
  const result=completed?.layout===layout?completed:cache.current.get(layout);
  useEffect(()=>{
    if(!enabled||cache.current.has(layout))return;
    setCompleted(null);
    let active=true;
    const worker=new Worker(new URL('./routing.worker.ts',import.meta.url),{type:'module'});
    worker.onmessage=({data}:MessageEvent<RoutingResult<LayoutedView>>)=>{
      if(!active)return;
      const value:Completed={layout,...data};
      if(data.ok){previous.current=data.view;cache.current.set(layout,value);}
      setCompleted(value);
    };
    worker.onerror=()=>setCompleted({layout,ok:false,error:'Background routing failed; provisional routes remain visible.'});
    worker.postMessage({view:layout} satisfies RoutingRequest<LayoutedView>);
    // New geometry cancels obsolete work: no stale result can replace the latest move.
    return ()=>{active=false;worker.terminate();};
  },[layout,enabled,retry]);
  return {view:enabled&&result?.ok?result.view:preview.view,state:!enabled?'off':result?result.ok?'ready':'error':'pending',generation:preview.generation,previewMs:preview.milliseconds,refinementMs:result?.ok?result.milliseconds:undefined};
}
