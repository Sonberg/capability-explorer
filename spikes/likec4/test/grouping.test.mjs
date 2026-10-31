import {test} from 'node:test';
import assert from 'node:assert/strict';
import {groupView} from '../src/grouping.mjs';
const view={bounds:{x:0,y:0,width:300,height:200},nodes:[{id:'a',parent:null,x:0,y:0,width:80,height:50,level:0,modelRef:'a'},{id:'b',parent:null,x:120,y:100,width:80,height:50,level:0,modelRef:'b'}],edges:[{id:'e',source:'a',target:'b',certainty:'candidate'}]};
test('presentation groups preserve evidence and member positions while expanding bounds',()=>{
 const grouped=groupView(view,['a','b'],'Review group');
 assert.equal(grouped.edges,view.edges);
 assert.deepEqual(grouped.nodes[0].children,['a','b']);
 assert.equal(grouped.nodes[1].x,view.nodes[0].x);
 assert.equal(grouped.nodes[1].modelRef,'a');
 assert.equal(view.nodes[0].parent,null);
 assert.ok(grouped.bounds.x<=grouped.nodes[0].x);
 assert.ok(grouped.bounds.y<=grouped.nodes[0].y);
 assert.throws(()=>groupView(grouped,['a','b'],'nested'),/ungrouped/);
 assert.throws(()=>groupView(view,['a'],'one'),/at least two/);
});
