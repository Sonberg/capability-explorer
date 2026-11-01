import {routeOrthogonal} from './orthogonal-routing.ts';
import type {LayoutedView} from 'likec4/model';
import type {RoutingRequest,RoutingResult} from './types.ts';
import type {TraceSpan} from './routing-trace-core.ts';
const now=()=>performance.timeOrigin+performance.now();
const booted=now();
self.onmessage=({data}:MessageEvent<RoutingRequest<LayoutedView>>)=>{
  const start=performance.now(),received=now(),phases:TraceSpan[]=[];
  let result:RoutingResult<LayoutedView>;
  try {
    const view=routeOrthogonal(data.view,data.trace?(name,start,end)=>phases.push({name,start,end}):undefined);
    result={ok:true,view,milliseconds:performance.now()-start};
  } catch(error){result={ok:false,error:String(error)};}
  if(data.trace){result.trace=data.trace;result.timing={booted,received,posting:now(),phases};}
  self.postMessage(result);
};
