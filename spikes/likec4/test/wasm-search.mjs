// Benchmark-only adapter; deliberately absent from the production dependency graph.
// Source contract: ruvector/crates/ruvector-mincut-wasm/src/routing.rs (2.3.0).
// Every call includes graph conversion, WASM construction, routing and disposal.
// Equal-cost paths may differ from the TypeScript heap's tie ordering.
const GRID=12, SCALE=200, DIRECTIONS=[[1,0],[0,1],[-1,0],[0,-1]];

export function createWasmSearch(WasmRoadRouter) {
  return function wasmSearch(grid,start,end) {
    const {width,height,left,top,blocked,headers,horizontal,vertical,terminalClearance}=grid;
    const count=width*height;
    if(!Number.isSafeInteger(count)||count<1||count*4>1_000_000)throw new RangeError('WASM heading-state node limit');
    const cell=p=>Math.round((p[1]-top)/GRID)*width+Math.round((p[0]-left)/GRID);
    const first=cell(start.stub),last=cell(end.stub);
    if(first<0||last<0||first>=count||last>=count)throw new RangeError('Endpoint outside grid');
    if(blocked[first]||blocked[last])return null;
    const horizontalJourney=Math.abs(end.stub[0]-start.stub[0])>=Math.abs(end.stub[1]-start.stub[1]);
    const midX=((start.stub[0]+end.stub[0])/2-left)/GRID,midY=((start.stub[1]+end.stub[1])/2-top)/GRID;
    // Four arrival headings per cell encode the no-immediate-reversal constraint.
    // Each directed move is reachable from three headings at the previous cell.
    const endpoints=new Uint32Array(count*4*3*2),costs=new Uint32Array(count*4*3);
    let arcs=0;
    for(let c=0;c<count;c++){
      if(blocked[c])continue;
      const x=c%width,y=Math.floor(c/width);
      for(let d=0;d<4;d++){
        const nx=x+DIRECTIONS[d][0],ny=y+DIRECTIONS[d][1];
        if(nx<0||ny<0||nx>=width||ny>=height)continue;
        const nc=ny*width+nx;if(blocked[nc])continue;
        const same=d%2===0?horizontal:vertical,other=d%2===0?vertical:horizontal;
        let nearby=0;
        if(d%2===0){if(ny>=1)nearby+=same[nc-width];if(ny+1<height)nearby+=same[nc+width];}
        else {if(nx>=1)nearby+=same[nc-1];if(nx+1<width)nearby+=same[nc+1];}
        const corridor=horizontalJourney?(d%2===1?Math.abs(nx-midX)*.01:0):(d%2===0?Math.abs(ny-midY)*.01:0);
        const base=terminalClearance[nc]*1000+corridor+nearby*1000+1+(headers[nc]?30:0)+same[nc]*1000+other[nc]*200;
        for(let dir=0;dir<4;dir++){
          if(d===(dir+2)%4)continue;
          const scaled=(base+(dir!==d?16:0))*SCALE,cost=Math.round(scaled);
          // Published RoadRouter costs are bounded u32 integers (<= 1e9).
          if(cost>1_000_000_000||Math.abs(scaled-cost)>1e-5)throw new RangeError('Unrepresentable routing cost');
          endpoints[arcs*2]=c*4+dir;endpoints[arcs*2+1]=nc*4+d;costs[arcs++]=cost;
        }
      }
    }
    const router=new WasmRoadRouter(count*4,endpoints.subarray(0,arcs*2),costs.subarray(0,arcs),new Uint32Array());
    try {
      const route=router.route(first*4+start.side,last*4+(end.side+2)%4,false,200_000_000);
      return route?Array.from(route.nodes,state=>{
        const c=state>>2;return [left+(c%width)*GRID,top+Math.floor(c/width)*GRID];
      }):null;
    } finally {router.free();}
  };
}
