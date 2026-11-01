import {test} from 'node:test';
import assert from 'node:assert/strict';
import {groupView} from '../src/grouping.ts';
const view={bounds:{x:0,y:0,width:300,height:200},nodes:[{id:'a',parent:null,x:0,y:0,width:80,height:50,level:0,modelRef:'a'},{id:'b',parent:null,x:120,y:100,width:80,height:50,level:0,modelRef:'b'}],edges:[{id:'e',source:'a',target:'b',certainty:'candidate'}]};
test('presentation groups preserve evidence and member positions while expanding bounds',()=>{
 const grouped=groupView(view,['a','b'],'Review group');
 assert.equal(grouped.edges[0].certainty,view.edges[0].certainty);
 assert.equal(grouped.edges[0].id,view.edges[0].id);
 assert.deepEqual(grouped.nodes[0].children,['a','b']);
 assert.equal(grouped.nodes[1].x,view.nodes[0].x);
 assert.equal(grouped.nodes[1].modelRef,'a');
 assert.equal(view.nodes[0].parent,null);
 assert.ok(grouped.bounds.x<=grouped.nodes[0].x);
 assert.ok(grouped.bounds.y<=grouped.nodes[0].y);
 assert.throws(()=>groupView(grouped,['a','b'],'nested'),/ungrouped/);
 assert.throws(()=>groupView(view,['a'],'one'),/at least two/);
});

test('automatic hierarchy uses explicit repository identity, never labels',async()=>{
 const {automaticGroups,modelIdentities}=await import('../src/grouping.ts');
 const graph={nodes:[{id:'b',repositoryId:'repo',label:'B'},{id:'a',label:'repo/A'},{id:'c',repositoryId:'repo'}]};
 const groups=automaticGroups(graph);
 assert.equal(groups.length,1);
 assert.deepEqual(groups[0].members,['repository_0.n1','repository_0.n2']);
 assert.equal(modelIdentities(graph).get('a'),'n0');
 assert.deepEqual(groups,automaticGroups({nodes:[...graph.nodes].reverse()}));
});
test('CRUD transfers membership once and ungrouping preserves nodes and evidence',async()=>{
 const {saveGroup,applyGroups,validateGroups}=await import('../src/grouping.ts');
 const extended={...view,nodes:[...view.nodes,{id:'c',parent:null,x:220,y:0,width:80,height:50,level:0}]};
 const first=saveGroup([],extended,{title:'Initial',members:['a','b']});
 const renamed=saveGroup(first,extended,{id:first[0].id,title:'Renamed',members:['a','b']});
 assert.equal(renamed[0].title,'Renamed');
 const moved=saveGroup(renamed,extended,{title:'Second',members:['b','c']});
 assert.equal(moved.flatMap(g=>g.members).filter(id=>id==='b').length,1);
 const grouped=applyGroups(extended,moved), ungrouped=applyGroups(grouped,[]);
 assert.deepEqual(ungrouped.nodes.map(n=>n.id),['a','b','c']);
 assert.deepEqual(ungrouped.edges.map(({parent,...edge})=>edge),extended.edges);
 assert.ok(ungrouped.edges.every(e=>!e.parent),'ungroup clears obsolete edge parents');
 assert.throws(()=>validateGroups([{...first[0],members:['missing']}],extended),/membership/);
 assert.throws(()=>validateGroups([first[0],first[0]],extended),/Invalid group/);
 const empty={nodes:[],edges:[],bounds:{x:0,y:0,width:0,height:0}};
 assert.deepEqual(applyGroups(empty,[]),empty);
});
