import type {Point,MutablePoint} from './types.ts';
export interface RouteEnd {point:MutablePoint;stub:MutablePoint;side:number}
export interface SearchGrid {
 width:number;height:number;left:number;top:number;blocked:Uint8Array;headers:Uint8Array;
 horizontal:Uint16Array;vertical:Uint16Array;terminalClearance:Uint16Array;
}
export type SearchFunction=(grid:SearchGrid,start:RouteEnd,end:RouteEnd)=>Point[]|null;
const GRID=12,MIN_LINE_GAP=24,DIRECTIONS=[[1,0],[0,1],[-1,0],[0,-1]];
// Preserve the original heap's priority-only tie order exactly. Typed storage
// removes per-push tuples; one search session reuses scratch across all routes.
class Heap {
 scores=new Float64Array(1024);states=new Uint32Array(1024);size=0;lastScore=0;
 clear(){this.size=0;}
 push(score:number,state:number){
  let i=this.size++;
  if(this.size>this.scores.length){const scores=new Float64Array(this.scores.length*2),states=new Uint32Array(this.states.length*2);scores.set(this.scores);states.set(this.states);this.scores=scores;this.states=states;}
  while(i){const p=(i-1)>>1;if(this.scores[p]<=score)break;this.scores[i]=this.scores[p];this.states[i]=this.states[p];i=p;}
  this.scores[i]=score;this.states[i]=state;
 }
 pop(){
  const first=this.states[0];this.lastScore=this.scores[0];
  const last=--this.size,score=this.scores[last],state=this.states[last];
  if(this.size){let i=0;while(i*2+1<this.size){let c=i*2+1;if(c+1<this.size&&this.scores[c+1]<this.scores[c])c++;if(this.scores[c]>=score)break;this.scores[i]=this.scores[c];this.states[i]=this.states[c];i=c;}this.scores[i]=score;this.states[i]=state;}
  return first;
 }
}
export function createSearch():SearchFunction {
 let distance=new Float64Array(0),parent=new Int32Array(0),epochs=new Uint32Array(0),epoch=0;
 const heap=new Heap();
return (grid:SearchGrid,start:RouteEnd,end:RouteEnd):Point[]|null=>{
  const {width,height,left,top,blocked,headers,horizontal,vertical}=grid;
  const cell=(p:Point)=>Math.round((p[1]-top)/GRID)*width+Math.round((p[0]-left)/GRID);
  const horizontalJourney=Math.abs(end.stub[0]-start.stub[0])>=Math.abs(end.stub[1]-start.stub[1]);
  const midX=((start.stub[0]+end.stub[0])/2-left)/GRID,midY=((start.stub[1]+end.stub[1])/2-top)/GRID;
  const first=cell(start.stub),last=cell(end.stub),tx=last%width,ty=Math.floor(last/width);
  if(blocked[first]||blocked[last])return null;
  const size=width*height*4;
  if(distance.length<size){distance=new Float64Array(size);parent=new Int32Array(size);epochs=new Uint32Array(size);}
  epoch=(epoch+1)>>>0;if(epoch===0){epochs.fill(0);epoch=1;}
  heap.clear();
  const heuristic=(c:number)=>Math.abs(c%width-tx)+Math.abs(Math.floor(c/width)-ty);
  const initial=first*4+start.side;distance[initial]=0;parent[initial]=-1;epochs[initial]=epoch;heap.push(heuristic(first),initial);
  while(heap.size){const state=heap.pop(),score=heap.lastScore,c=state>>2,dir=state&3,x=c%width,y=Math.floor(c/width);
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
      if(epochs[ns]!==epoch||nd<distance[ns]){epochs[ns]=epoch;distance[ns]=nd;parent[ns]=state;heap.push(nd+heuristic(nc),ns);}
    }
  }
  return null;
}
}
