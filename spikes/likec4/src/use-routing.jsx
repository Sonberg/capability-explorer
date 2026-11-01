import {useEffect,useMemo,useRef,useState} from 'react';
import {previewRoutes} from './incremental-routing.mjs';
export function useRouting(layout,enabled,retry) {
  const generation=useRef(0),previous=useRef(null),cache=useRef(new WeakMap());
  const [completed,setCompleted]=useState(null);
  const preview=useMemo(()=>{const start=performance.now();const view=enabled?previewRoutes(previous.current,layout):{...layout,routing:'spline'};
    return {view,generation:++generation.current,milliseconds:performance.now()-start};},[layout,enabled]);
  const result=completed?.layout===layout?completed:cache.current.get(layout);
  useEffect(()=>{
    if(!enabled||cache.current.has(layout))return;
    setCompleted(null);
    let active=true;
    const worker=new Worker(new URL('./routing.worker.mjs',import.meta.url),{type:'module'});
    worker.onmessage=({data})=>{
      if(!active)return;
      const value={layout,...data};
      if(data.view){previous.current=data.view;cache.current.set(layout,value);}
      setCompleted(value);
    };
    worker.onerror=()=>setCompleted({layout,error:'Background routing failed; provisional routes remain visible.'});
    worker.postMessage(layout);
    // New geometry cancels obsolete work: no stale result can replace the latest move.
    return ()=>{active=false;worker.terminate();};
  },[layout,enabled,retry]);
  return {view:enabled?(result?.view??preview.view):preview.view,state:!enabled?'off':result?.error?'error':result?.view?'ready':'pending',generation:preview.generation,previewMs:preview.milliseconds,refinementMs:result?.milliseconds};
}
