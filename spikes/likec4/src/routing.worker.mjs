import {routeOrthogonal} from './orthogonal-routing.mjs';
self.onmessage=({data})=>{
  const start=performance.now();
  try {self.postMessage({view:routeOrthogonal(data),milliseconds:performance.now()-start});}
  catch(error){self.postMessage({error:String(error)});}
};
