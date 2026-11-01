// Exact search baseline from 9c2f6f3, retained only for differential benchmarks.
import type {SearchGrid as Grid,RouteEnd as End} from '../src/routing-search.ts';
import type {Point} from '../src/types.ts';
const GRID=12,MIN_LINE_GAP=24,DIRECTIONS=[[1,0],[0,1],[-1,0],[0,-1]];
class Heap {
  values:[number,number][]=[];
  push(item:[number,number]){let i=this.values.length;this.values.push(item);while(i){const p=(i-1)>>1;if(this.values[p][0]<=item[0])break;this.values[i]=this.values[p];i=p;}this.values[i]=item;}
  pop(){const first=this.values[0],last=this.values.pop()!;if(this.values.length){let i=0;while(i*2+1<this.values.length){let c=i*2+1;if(c+1<this.values.length&&this.values[c+1][0]<this.values[c][0])c++;if(this.values[c][0]>=last[0])break;this.values[i]=this.values[c];i=c;}this.values[i]=last;}return first;}
}
export function searchBaseline(grid:Grid,start:End,end:End):Point[]|null {
  const {width,height,left,top,blocked,headers,horizontal,vertical}=grid;
  const cell=(p:Point)=>Math.round((p[1]-top)/GRID)*width+Math.round((p[0]-left)/GRID);
  const horizontalJourney=Math.abs(end.stub[0]-start.stub[0])>=Math.abs(end.stub[1]-start.stub[1]);
  const midX=((start.stub[0]+end.stub[0])/2-left)/GRID,midY=((start.stub[1]+end.stub[1])/2-top)/GRID;
  const first=cell(start.stub),last=cell(end.stub),tx=last%width,ty=Math.floor(last/width);
  if(blocked[first]||blocked[last])return null;
  const distance=new Float64Array(width*height*4).fill(Infinity),parent=new Int32Array(distance.length).fill(-1),heap=new Heap();
  const heuristic=(c:number)=>Math.abs(c%width-tx)+Math.abs(Math.floor(c/width)-ty);
  distance[first*4+start.side]=0;heap.push([heuristic(first),first*4+start.side]);
  while(heap.values.length){const [score,state]=heap.pop(),c=state>>2,dir=state&3,x=c%width,y=Math.floor(c/width);
    if(score>distance[state]+heuristic(c)+.001)continue;
    if(c===last&&dir===(end.side+2)%4){const path:Point[]=[];for(let p=state;p>=0;p=parent[p]){const v=p>>2;path.push([left+(v%width)*GRID,top+Math.floor(v/width)*GRID]);}return path.reverse();}
    for(let d=0;d<4;d++){if(d===(dir+2)%4)continue;const nx=x+DIRECTIONS[d][0],ny=y+DIRECTIONS[d][1];if(nx<0||ny<0||nx>=width||ny>=height)continue;
      const nc=ny*width+nx;if(blocked[nc])continue;
      const same=d%2===0?horizontal:vertical,other=d%2===0?vertical:horizontal;
      let nearby=0;
      for(let offset=1;offset<MIN_LINE_GAP/GRID;offset++){
        if(d%2===0){if(ny>=offset)nearby+=same[nc-offset*width];if(ny+offset<height)nearby+=same[nc+offset*width];}
        else {if(nx>=offset)nearby+=same[nc-offset];if(nx+offset<width)nearby+=same[nc+offset];}
      }
      // A stable corridor preference prevents opposite-direction routes choosing
      // incompatible far-end bends. It does not depend on node/relationship names.
      const corridor=horizontalJourney?(d%2===1?Math.abs(nx-midX)*.01:0):(d%2===0?Math.abs(ny-midY)*.01:0);
      const cost=grid.terminalClearance[nc]*1000+corridor+nearby*1000+1+(dir!==d?16:0)+(headers[nc]?30:0)+same[nc]*1000+other[nc]*200;
      const ns=nc*4+d,nd=distance[state]+cost;
      if(nd<distance[ns]){distance[ns]=nd;parent[ns]=state;heap.push([nd+heuristic(nc),ns]);}
    }
  }
  return null;
}
