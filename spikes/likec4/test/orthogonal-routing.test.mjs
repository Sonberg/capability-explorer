import {test} from 'node:test';
import assert from 'node:assert/strict';
import {routeOrthogonal} from '../src/orthogonal-routing.ts';
const node=(id,x,y)=>({id,x,y,width:120,height:96,children:[]});
const edge=(id,source,target)=>({id,source,target,relations:[id],label:id,labelBBox:{x:0,y:0,width:60,height:18},points:[[120,48],[160,48],[360,48],[480,48]]});
const fixture={bounds:{x:0,y:0,width:600,height:400},nodes:[node('a',0,120),node('blocker',240,120),node('b',480,120)],edges:[edge('one','a','b'),edge('two','a','b'),edge('reverse','b','a')]};
const segments=e=>e.points.filter((_,i)=>i%3===0).slice(1).map((p,i)=>[e.points[i*3],p]);
function sharedLength(a,b){if(a[0][1]===a[1][1]&&b[0][1]===b[1][1]&&a[0][1]===b[0][1])return Math.max(0,Math.min(Math.max(a[0][0],a[1][0]),Math.max(b[0][0],b[1][0]))-Math.max(Math.min(a[0][0],a[1][0]),Math.min(b[0][0],b[1][0])));if(a[0][0]===a[1][0]&&b[0][0]===b[1][0]&&a[0][0]===b[0][0])return sharedLength(a.map(([x,y])=>[y,x]),b.map(([x,y])=>[y,x]));return 0;}
test('routes parallel and opposing relationships around obstacles in separate lanes',()=>{
  const result=routeOrthogonal(fixture);
  assert.deepEqual(result.nodes,fixture.nodes);
  for(const e of result.edges){assert.deepEqual(e.relations,[e.id]);assert.equal(e.controlPoints,null);
    for(const [a,b] of segments(e)){assert.ok(a[0]===b[0]||a[1]===b[1]);
      assert.ok(!(a[1]===b[1]&&a[1]>120&&a[1]<216&&Math.max(a[0],b[0])>240&&Math.min(a[0],b[0])<360),'horizontal node intrusion');
      assert.ok(!(a[0]===b[0]&&a[0]>240&&a[0]<360&&Math.max(a[1],b[1])>120&&Math.min(a[1],b[1])<216),'vertical node intrusion');
    }
  }
  let shared=0;for(let i=0;i<result.edges.length;i++)for(let j=i+1;j<result.edges.length;j++)for(const a of segments(result.edges[i]))for(const b of segments(result.edges[j]))shared+=sharedLength(a,b);
  assert.equal(shared,0,'parallel edges must not share line segments');
});
test('routing is stable under input order and leaves source geometry untouched',()=>{
  const before=structuredClone(fixture),a=routeOrthogonal(fixture),b=routeOrthogonal({...fixture,nodes:[...fixture.nodes].reverse(),edges:[...fixture.edges].reverse()});
  assert.deepEqual(fixture,before);
  const geometry=v=>Object.fromEntries(v.edges.map(e=>[e.id,{points:e.points,label:e.labelBBox}]));
  assert.deepEqual(geometry(a),geometry(b));
});
test('oversized layouts retain native fallback and every relationship',()=>{
  const large={...fixture,bounds:{x:0,y:0,width:100000,height:100000}};
  const result=routeOrthogonal(large);assert.equal(result.routing,'ortho');assert.deepEqual(result.edges,large.edges);
});

test('repeated snapshots do not grow bounds and irregular nodes keep axis-aligned routes',()=>{
  const view={...fixture,nodes:fixture.nodes.map(n=>({...n,x:n.x+5,y:n.y+7,width:113,height:83}))};
  const a=routeOrthogonal(view),b=routeOrthogonal(a);
  assert.deepEqual(b.bounds,a.bounds);
  for(const e of a.edges)for(const [p,q] of segments(e))assert.ok(p[0]===q[0]||p[1]===q[1]);
});

test('aligned facing ports produce a straight route without decorative doglegs',()=>{
  const view={bounds:{x:0,y:0,width:600,height:96},nodes:[node('a',0,0),node('b',480,0)],edges:[edge('straight','a','b')]};
  const result=routeOrthogonal(view);
  assert.equal(segments(result.edges[0]).length,1);
});
test('opposing connections between offset nodes do not cross regardless of identity or direction',()=>{
  for(const [left,right,forward,reverse] of [['left','right','a','z'],['client','store','z','a'],['x','y','100','002']])for(const [ax,ay,bx,by] of [[0,240,480,0],[0,0,480,240],[240,0,0,480],[0,0,240,480]]){
    const view={bounds:{x:0,y:0,width:720,height:720},nodes:[node(left,ax,ay),node(right,bx,by)],edges:[edge(forward,left,right),edge(reverse,right,left)]};
    const result=routeOrthogonal(view),[first,second]=result.edges.map(segments);
    for(const [a,b] of first)for(const [c,d] of second){const vertical=a[0]===b[0],otherVertical=c[0]===d[0];
      if(vertical===otherVertical){const axis=vertical?0:1,along=1-axis;if(Math.min(Math.max(a[along],b[along]),Math.max(c[along],d[along]))>Math.max(Math.min(a[along],b[along]),Math.min(c[along],d[along])))assert.ok(Math.abs(a[axis]-c[axis])>=24,'parallel route clearance');}
      else {const [v,h]=vertical?[[a,b],[c,d]]:[[c,d],[a,b]];assert.ok(!(v[0][0]>Math.min(h[0][0],h[1][0])&&v[0][0]<Math.max(h[0][0],h[1][0])&&h[0][1]>Math.min(v[0][1],v[1][1])&&h[0][1]<Math.max(v[0][1],v[1][1])),'opposing routes must not cross');}
    }
  }
});
test('wide offset nodes use the open vertical corridor instead of wrapping through the narrow side gap',()=>{
  const view={bounds:{x:0,y:0,width:900,height:900},nodes:[{...node('upper',405,300),width:385,height:180},{...node('lower',0,620),width:385,height:180}],edges:[edge('out','upper','lower'),edge('in','lower','upper')]};
  const result=routeOrthogonal(view);
  const out=result.edges.find(e=>e.id==='out');
  assert.equal(out.points[0][1],480,'leave bottom of upper node');
  assert.equal(out.points.at(-1)[1],620,'enter top of lower node');
  assert.equal(segments(out).length,3,'single dogleg through open corridor');
});

test('backward drawing direction retains the semantic arrow endpoint',()=>{
  const view={bounds:{x:0,y:0,width:600,height:96},nodes:[node('a',0,0),node('b',480,0)],edges:[{...edge('back','a','b'),dir:'back',head:'normal'}]};
  const result=routeOrthogonal(view).edges[0];
  assert.equal(result.points[0][0],480);assert.equal(result.points.at(-1)[0],120);
  assert.equal(result.source,'a');assert.equal(result.target,'b');assert.equal(result.head,'normal');
});

test('long connections bypass intervening nodes through a shared outside corridor',()=>{
  for(const rotate of [false,true]){
    const nodes=[node('top',300,0),node('middle',300,300),node('lower',300,600),node('bottom',300,900)];
    const view={bounds:{x:0,y:0,width:1200,height:1200},nodes:rotate?nodes.map(n=>({...n,x:n.y,y:n.x,width:n.height,height:n.width})):nodes,
      edges:[edge('first','top','lower'),edge('second','top','bottom')]};
    const routed=routeOrthogonal(view);
    for(const e of routed.edges){
      assert.equal(segments(e).length,3,'outside corridor needs only two turns');
      const [start,end]=[e.points[0],e.points.at(-1)];
      assert.equal(rotate?start[1]:start[0],300,'exit from the outer side');
      assert.equal(rotate?end[1]:end[0],300,'enter from the same outer side');
    }
  }
});

test('labels have clearance from other labels and every routed line',()=>{
  const view={bounds:{x:0,y:0,width:1000,height:1200},nodes:[node('a',300,0),node('block',300,300),node('b',300,600),node('c',300,900)],
    edges:[{...edge('long-first','a','b'),labelBBox:{x:0,y:0,width:262,height:18}}, {...edge('long-second','a','c'),labelBBox:{x:0,y:0,width:262,height:18}}]};
  const routed=routeOrthogonal(view);
  const boxes=routed.edges.map(e=>({...e.labelBBox,x:e.labelBBox.x-8,y:e.labelBBox.y-8,width:e.labelBBox.width+16,height:e.labelBBox.height+16}));
  const [a,b]=boxes;
  assert.ok(a.x>=b.x+b.width||b.x>=a.x+a.width||a.y>=b.y+b.height||b.y>=a.y+a.height,'label clearance');
  for(const box of boxes)for(const e of routed.edges)for(const [p,q] of segments(e)){
    const hit=p[0]===q[0]?p[0]>box.x&&p[0]<box.x+box.width&&Math.max(p[1],q[1])>box.y&&Math.min(p[1],q[1])<box.y+box.height:
      p[1]>box.y&&p[1]<box.y+box.height&&Math.max(p[0],q[0])>box.x&&Math.min(p[0],q[0])<box.x+box.width;
    assert.ok(!hit,'label must not cover a route');
  }
});
