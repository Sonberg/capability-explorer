import {test} from 'node:test';
import assert from 'node:assert/strict';
import {previewRoutes} from '../src/incremental-routing.ts';
import {cubicPoints} from '../src/orthogonal-routing.ts';
const view={nodes:[{id:'a',x:0,y:0,width:100,height:100},{id:'b',x:400,y:0,width:100,height:100}],edges:[{id:'e',source:'a',target:'b',points:cubicPoints([[100,50],[400,50]]),labelBBox:{x:200,y:20,width:50,height:20}}]};
test('provisional routing attaches moved endpoints and stays orthogonal without changing evidence',()=>{
 const next={...view,nodes:[{...view.nodes[0],x:24,y:36},view.nodes[1]]},before=structuredClone(view);
 const result=previewRoutes(view,next),points=result.edges[0].points.filter((_,i)=>i%3===0);
 assert.deepEqual(points[0],[124,86]);assert.deepEqual(points.at(-1),[400,50]);
 for(let i=1;i<points.length;i++)assert.ok(points[i][0]===points[i-1][0]||points[i][1]===points[i-1][1]);
 assert.equal(result.edges[0].source,'a');assert.deepEqual(view,before);
});
test('group translation preserves internal geometry and labels, unchanged routes reuse points',()=>{
 const next={...view,nodes:view.nodes.map(n=>({...n,x:n.x+24,y:n.y+36}))};
 const result=previewRoutes(view,next);
 assert.deepEqual(result.edges[0].points,view.edges[0].points.map(p=>[p[0]+24,p[1]+36]));
 assert.equal(result.edges[0].labelBBox.x,224);assert.equal(result.edges[0].labelBBox.y,56);
 assert.equal(previewRoutes(view,view).edges[0].points,view.edges[0].points);
});
test('backward drawings preserve terminal direction during preview',()=>{
 const back={...view,edges:[{...view.edges[0],dir:'back',points:[...view.edges[0].points].reverse()}]};
 const result=previewRoutes(back,{...back,nodes:[{...view.nodes[0],x:24,y:36},view.nodes[1]]});
 assert.deepEqual(result.edges[0].points[0],[400,50]);assert.deepEqual(result.edges[0].points.at(-1),[124,86]);
});

test('elbow previews preserve differing departure and arrival orientations',()=>{
 const elbow={...view,edges:[{...view.edges[0],points:cubicPoints([[100,50],[450,50],[450,0]])}]};
 const result=previewRoutes(elbow,{...elbow,nodes:[{...view.nodes[0],x:24,y:36},view.nodes[1]]});
 const points=result.edges[0].points.filter((_,i)=>i%3===0);
 assert.equal(points[0][1],points[1][1]);assert.equal(points.at(-1)[0],points.at(-2)[0]);
 assert.deepEqual(points.at(-1),[450,0]);
});
