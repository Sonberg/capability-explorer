import test from 'node:test';
import assert from 'node:assert/strict';
import {createWasmSearch} from './wasm-search.mjs';

// Independent shortest-path oracle for tiny encoded graphs, not a WASM speed claim.
class ReferenceRouter {
  static latest;
  constructor(count,endpoints,costs,turns){
    this.count=count;this.edges=Array.from({length:count},()=>[]);this.freed=false;
    assert.equal(turns.length,0);
    for(let i=0;i<costs.length;i++)this.edges[endpoints[i*2]].push([endpoints[i*2+1],costs[i]]);
    ReferenceRouter.latest=this;
  }
  route(source,target){
    const distance=Array(this.count).fill(Infinity),parent=Array(this.count).fill(-1),done=new Set();distance[source]=0;
    for(;;){
      let current=-1;
      for(let i=0;i<this.count;i++)if(!done.has(i)&&distance[i]<Infinity&&(current<0||distance[i]<distance[current]))current=i;
      if(current<0)return null;
      if(current===target){const nodes=[];for(let n=current;n>=0;n=parent[n])nodes.unshift(n);this.cost=distance[current];return {nodes,cost:this.cost};}
      done.add(current);
      for(const [next,cost] of this.edges[current])if(distance[current]+cost<distance[next]){distance[next]=distance[current]+cost;parent[next]=current;}
    }
  }
  free(){this.freed=true;}
}
const grid=(width,height)=>({width,height,left:0,top:0,blocked:new Uint8Array(width*height),headers:new Uint8Array(width*height),horizontal:new Uint16Array(width*height),vertical:new Uint16Array(width*height),terminalClearance:new Uint16Array(width*height)});
const end=(x,y,side)=>({point:[x*12,y*12],stub:[x*12,y*12],side});
const search=createWasmSearch(ReferenceRouter);

test('heading-state adapter preserves straight route and frees native instance',()=>{
  assert.deepEqual(search(grid(4,1),end(0,0,0),end(3,0,2)),[[0,0],[12,0],[24,0],[36,0]]);
  assert.equal(ReferenceRouter.latest.cost,600);assert.equal(ReferenceRouter.latest.freed,true);
});
test('goal heading is required and immediate reversals are forbidden',()=>{
  assert.equal(search(grid(4,1),end(0,0,2),end(3,0,2)),null);
  assert.equal(search(grid(4,1),end(0,0,0),end(3,0,0)),null);
});
test('blocked endpoints and disconnected cells cannot produce routes',()=>{
  const g=grid(4,1);g.blocked[2]=1;assert.equal(search(g,end(0,0,0),end(3,0,2)),null);
  g.blocked[0]=1;assert.equal(search(g,end(0,0,0),end(3,0,2)),null);
});
test('occupancy, crossing, headers, terminal and nearby penalties retain exact scale',()=>{
  const g=grid(3,3);g.horizontal[4]=2;g.vertical[4]=3;g.headers[4]=1;g.terminalClearance[4]=4;g.horizontal[1]=5;g.horizontal[7]=6;
  search(g,end(0,1,0),end(2,1,2));
  const entry=ReferenceRouter.latest.edges[3*4].find(([target])=>target===4*4);
  assert.equal(entry[1],(4000+11000+1+30+2000+600)*200);
  const turn=ReferenceRouter.latest.edges[3*4+1].find(([target])=>target===4*4);
  assert.equal(turn[1],entry[1]+16*200);
});
test('half-grid corridor preference is represented without truncation',()=>{
  search(grid(4,3),end(0,1,0),end(3,1,2));
  const vertical=ReferenceRouter.latest.edges[4*4].find(([target])=>target===8*4+1);
  assert.equal(vertical[1],(1+16)*200+3);
});
test('native failure still frees the instance',()=>{
  class FailingRouter extends ReferenceRouter {route(){throw new Error('budget');}}
  assert.throws(()=>createWasmSearch(FailingRouter)(grid(4,1),end(0,0,0),end(3,0,2)),/budget/);
  assert.equal(ReferenceRouter.latest.freed,true);
});

// Opt-in integration lane: caller supplies a verified, sandbox-mounted package.
// The normal project test suite does not install an experimental dependency.
if(process.env.CE_WASM_PACKAGE){
  test('published WASM agrees with shortest-path oracle on 100 deterministic grids',async()=>{
    const {default:pkg}=await import(process.env.CE_WASM_PACKAGE);
    let actualCost;
    class ObservedRouter {
      constructor(...args){this.inner=new pkg.WasmRoadRouter(...args);}
      route(...args){const result=this.inner.route(...args);actualCost=result?.cost??null;return result;}
      free(){this.inner.free();}
    }
    const wasm=createWasmSearch(ObservedRouter);
    let seed=123456789;
    const random=()=>{seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed/2**32;};
    for(let sample=0;sample<100;sample++){
      const g=grid(5,5);
      for(let i=0;i<25;i++){
        g.blocked[i]=Number(random()<.12);g.headers[i]=Number(random()<.15);
        g.horizontal[i]=Number(random()<.15);g.vertical[i]=Number(random()<.15);
        g.terminalClearance[i]=Number(random()<.15);
      }
      g.blocked[10]=g.blocked[14]=0;
      const start=end(0,2,0),finish=end(4,2,2),expected=search(g,start,finish);
      const expectedCost=ReferenceRouter.latest.cost??null;
      const actual=wasm(g,start,finish);
      assert.equal(actual===null,expected===null,`reachability sample ${sample}`);
      assert.equal(actualCost,expectedCost,`minimum cost sample ${sample}`);
      if(actual){assert.deepEqual(actual[0],start.stub);assert.deepEqual(actual.at(-1),finish.stub);}
    }
  });
}
