import {test} from 'node:test';
import assert from 'node:assert/strict';
import {routeOrthogonal,cubicPoints} from '../src/orthogonal-routing.ts';

const overlap=(a,b)=>a.x<b.x+b.width&&b.x<a.x+a.width&&a.y<b.y+b.height&&b.y<a.y+a.height;
test('native fallback connectors retain geometry and evidence while overlapping labels are placed separately',()=>{
  const points=cubicPoints([[320,90],[800,90]]);
  const edges=['first','second','third'].map(id=>({id,source:'a',target:'b',points,label:'Separate source relationship',labelBBox:{x:410,y:90,width:260,height:44},relations:[id],head:'none',tail:'none'}));
  const input={id:'flow',nodes:[{id:'a',x:0,y:0,width:320,height:180,children:[]},{id:'b',x:800,y:0,width:320,height:180,children:[]}],edges,bounds:{x:0,y:0,width:1120,height:180}};
  const before=structuredClone(input);
  // No replacement route is available; labels still must be usable.
  const result=routeOrthogonal(input,undefined,()=>null);
  assert.deepEqual(input,before,'routing cannot mutate original evidence or geometry');
  assert.deepEqual(result.edges.map(({id,source,target,points,relations,head,tail})=>({id,source,target,points,relations,head,tail})),edges.map(({id,source,target,points,relations,head,tail})=>({id,source,target,points,relations,head,tail})));
  for(let i=0;i<result.edges.length;i++)for(let j=i+1;j<result.edges.length;j++) {
    const padded=box=>({x:box.x-10,y:box.y-10,width:box.width+20,height:box.height+20});
    assert.equal(overlap(padded(result.edges[i].labelBBox),padded(result.edges[j].labelBBox)),false,'native label padding must not collide');
  }
  assert.deepEqual(routeOrthogonal(input,undefined,()=>null),result,'fallback placement is deterministic');
});
