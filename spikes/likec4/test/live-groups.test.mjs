import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {test} from 'node:test';
import {buildLiveModel} from '../src/live-model.ts';
import {applyLiveGroups} from '../src/live-canvas-groups.ts';
import {saveGroup} from '../src/grouping.ts';

const graph=JSON.parse(readFileSync(new URL('./all-repositories.graph.json',import.meta.url),'utf8'));
const result=buildLiveModel({graph,snapshot:'group-regression',associationIds:[]});

test('presentation groups preserve source SQL hierarchy and every relationship',()=>{
  const view=result.modelData.views.objects;
  const original=JSON.stringify(view);
  const grouped=applyLiveGroups(view,result.groups);
  const sql=view.nodes.filter(node=>node.children.length&&node.kind!=='repository');
  assert.ok(sql.length>0,'real snapshot must exercise native SQL hierarchy');
  for(const node of sql){
    const actual=grouped.nodes.find(candidate=>candidate.id===node.id);
    assert.ok(actual,'source SQL container must remain visible');
    assert.deepEqual(actual.children,node.children,'source SQL hierarchy must remain intact');
  }
  assert.deepEqual(grouped.edges.map(edge=>edge.id),view.edges.map(edge=>edge.id),'all relationship IDs must survive grouping');
  assert.equal(JSON.stringify(view),original,'presentation changes must not mutate the source view');
});

test('create, rename, edit and delete groups retain exact original source members',()=>{
  const flow=applyLiveGroups(result.modelData.views.flow,result.groups);
  const originals=new Set(Object.values(result.sourceNodeIds));
  const leaves=flow.nodes.filter(node=>originals.has(node.id)&&!node.children.length&&node.kind!=='@group');
  assert.ok(leaves.length>2,'fixture must contain independently selectable original members');
  const members=leaves.slice(0,2).map(node=>node.id);
  const custom=saveGroup(result.groups,flow,{title:'Review group',members});
  const updated=applyLiveGroups(flow,custom);
  const created=custom.find(group=>group.title==='Review group');
  assert.deepEqual(updated.nodes.find(node=>node.id===created.id).children,members);
  const renamed=saveGroup(custom,updated,{id:created.id,title:'Renamed',members:[members[0]]});
  assert.deepEqual(renamed.find(group=>group.id===created.id),{id:created.id,title:'Renamed',members:[members[0]],origin:'user'});
  const ungrouped=applyLiveGroups(updated,renamed.filter(group=>group.id!==created.id));
  assert.ok(!ungrouped.nodes.some(node=>node.id===created.id),'deleting a group must remove its presentation boundary');
  for(const id of members)assert.ok(ungrouped.nodes.some(node=>node.id===id),'ungrouping must preserve every original source node');
  assert.deepEqual(ungrouped.edges.map(edge=>edge.id),flow.edges.map(edge=>edge.id),'group CRUD must preserve every relationship');
});

test('invalid and overlapping memberships fail before presentation geometry changes',()=>{
  const flow=applyLiveGroups(result.modelData.views.flow,result.groups);
  const original=JSON.stringify(flow);
  assert.throws(()=>applyLiveGroups(flow,[{id:'repository_1',title:'invalid',members:['missing'],origin:'user'}]),/Invalid/);
  const member=result.groups[0].members[0];
  assert.throws(()=>saveGroup(result.groups,flow,{title:'duplicate',members:[member,member]}),/at least two/);
  assert.throws(()=>applyLiveGroups(flow,[{id:'presentation_group_100',title:'A',members:[member],origin:'user'},{id:'presentation_group_101',title:'B',members:[member],origin:'user'}]),/overlapping/);
  assert.equal(JSON.stringify(flow),original,'failed edits must leave source geometry unchanged');
});


test('explicit source groups with empty repository IDs preserve all endpoints',()=>{
  const graph={nodes:[{id:'a',category:'component',group:'A',repositoryId:''},{id:'b',category:'component',group:'B',repositoryId:'repo-b'}],edges:[{id:'a-b',from:'a',to:'b',kind:'data'}]};
  const result=buildLiveModel({graph,snapshot:'empty-repository',associationIds:[]});
  assert.equal(result.groups.length,2);
  for(const name of ['flow','vertical','index']) {
    const view=result.modelData.views[name];
    const visible=new Set(view.nodes.map(node=>node.id));
    const groups=result.groups.filter(group=>group.members.every(id=>visible.has(id)));
    const updated=applyLiveGroups(view,groups),ids=new Set(updated.nodes.map(node=>node.id));
    assert.deepEqual(updated.edges.map(edge=>edge.id),view.edges.map(edge=>edge.id));
    for(const edge of updated.edges)assert.ok(ids.has(edge.source)&&ids.has(edge.target));
  }
});


test('collapsed isolated source containers survive grouping without edges',()=>{
  const result=buildLiveModel({graph:{nodes:[{id:'a',category:'service',repositoryId:'repo-a'}],edges:[]},snapshot:'isolated',associationIds:[]});
  const view=result.modelData.views.index,updated=applyLiveGroups(view,[]);
  assert.deepEqual(updated.nodes.map(node=>node.id),view.nodes.map(node=>node.id));
});
