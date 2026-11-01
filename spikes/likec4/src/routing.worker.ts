import {routeOrthogonal} from './orthogonal-routing.ts';
import type {LayoutedView} from 'likec4/model';
import type {RoutingRequest,RoutingResult} from './types.ts';
self.onmessage=({data}:MessageEvent<RoutingRequest<LayoutedView>>)=>{
  const start=performance.now();
  let result:RoutingResult<LayoutedView>;
  try {result={ok:true,view:routeOrthogonal(data.view),milliseconds:performance.now()-start};}
  catch(error){result={ok:false,error:String(error)};}
  self.postMessage(result);
};
