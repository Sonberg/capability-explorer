import {traceContext,traceEvent,traceSpan,finishTrace,timestamp} from './routing-trace.ts';
import {useEffect,useLayoutEffect,useMemo,useRef,useState} from 'react';
import type {LayoutedView} from 'likec4/model';
import type {RoutingRequest,RoutingResult} from './types.ts';
import {previewRoutes} from './incremental-routing.ts';
type Completed={layout:LayoutedView}&RoutingResult<LayoutedView>;
export function useRouting(layout:LayoutedView,enabled:boolean,retry:number,tracing:boolean) {
  const generation=useRef(0),previous=useRef<LayoutedView|null>(null),cache=useRef(new WeakMap<LayoutedView,Completed>());
  const [completed,setCompleted]=useState<Completed|null>(null);
  const preview=useMemo(()=>{const context=traceContext(layout),start=performance.now(),started=timestamp();const view:LayoutedView=enabled?previewRoutes(previous.current,layout):{...layout,routing:'spline'};
    return {view,started,trace:context,generation:++generation.current,milliseconds:performance.now()-start};},[layout,enabled,tracing]);
  const result=completed?.layout===layout?completed:cache.current.get(layout);
  useEffect(()=>{
    if(!enabled){finishTrace(preview.trace,'cancelled');return;}
    if(cache.current.has(layout))return;
    setCompleted(null);
    let active=true;
    const context=preview.trace,created=timestamp();
    traceEvent(context,'worker.construct',created);
    traceSpan(context,'preview',preview.started,preview.started+preview.milliseconds);
    traceSpan(context,'preview-to-worker-effect',preview.started+preview.milliseconds,created);
    let worker:Worker|undefined;
    let deadline:ReturnType<typeof setTimeout>|undefined;
    const stop=()=>{clearTimeout(deadline);worker?.terminate();};
    const fail=()=>{if(!active)return;stop();traceEvent(context,'worker.error');finishTrace(context,'error');setCompleted({layout,ok:false,error:'Background routing failed; provisional routes remain visible.'});};
    try {
    worker=new Worker(new URL('./routing.worker.ts',import.meta.url),{type:'module'});
    deadline=setTimeout(fail,30_000);
    worker.onmessage=({data}:MessageEvent<RoutingResult<LayoutedView>>)=>{
      if(!active)return;
      stop();
      const received=timestamp();
      traceEvent(context,'main.result',received);
      if(context&&data.timing){
        traceSpan(context,'worker-startup-and-request-delivery',created,data.timing.received);
        traceSpan(context,'worker-startup',created,data.timing.booted);
        traceSpan(context,'worker-boot-to-request',data.timing.booted,data.timing.received);
        traceSpan(context,'worker-total',data.timing.received,data.timing.posting);
        traceSpan(context,'result-delivery',data.timing.posting,received);
        for(const phase of data.timing.phases)traceSpan(context,phase.name,phase.start,phase.end);
      }
      if(!data.ok)finishTrace(context,'error');
      const value:Completed={layout,...data};
      if(data.ok){previous.current=data.view;cache.current.set(layout,value);}
      setCompleted(value);
    };
    worker.onerror=fail;
    worker.onmessageerror=fail;
    const posting=timestamp();
    traceEvent(context,'main.post',posting);
    worker.postMessage({view:layout,trace:context} satisfies RoutingRequest<LayoutedView>);
    traceSpan(context,'postMessage-call',posting);
    } catch {fail();}
    // New geometry cancels obsolete work: no stale result can replace the latest move.
    return ()=>{active=false;stop();traceEvent(context,'worker.disposed');finishTrace(context,'cancelled');};
  },[layout,enabled,retry]);
  useLayoutEffect(()=>{if(result?.ok)traceEvent(preview.trace,'react.result-commit');},[result,preview.trace]);
  return {trace:preview.trace,view:enabled&&result?.ok?result.view:preview.view,state:!enabled?'off':result?result.ok?'ready':'error':'pending',generation:preview.generation,previewMs:preview.milliseconds,refinementMs:result?.ok?result.milliseconds:undefined};
}
