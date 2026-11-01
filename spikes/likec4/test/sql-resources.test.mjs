import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {sqlResources,sqlObjectDescription} from '../src/sql-resources.mjs';
const graph=JSON.parse(readFileSync(new URL('./weekly-sales.graph.json',import.meta.url),'utf8'));
test('five SQL objects roll up into three exact logical scopes, not physical databases',()=>{
 const resources=sqlResources(graph);
 assert.equal(resources.length,3);
 assert.deepEqual(resources.map(s=>s.kind),['databaseReference','sqlModel','databaseReference']);
 assert.ok(resources.every(s=>s.binding==='unbound'));
 assert.deepEqual(resources.map(s=>s.nodeIds.length),[1,2,2]);
 assert.notEqual(resources[0].storeRef,resources[2].storeRef,'same Sales alias cannot merge connection scopes');
 assert.deepEqual(resources,sqlResources({...graph,nodes:[...graph.nodes].reverse()}));
 const labels=graph.nodes.filter(n=>n.category==='datastore').map(n=>sqlObjectDescription(n,graph));
 assert.equal(labels.filter(s=>s==='Declared SQL table').length,2);
 assert.equal(labels.filter(s=>s.startsWith('SQL object reference')).length,3);
});
test('only a concrete store reference gets the resolved database kind',()=>{
 const resources=sqlResources({nodes:[{id:'a',category:'datastore',symbolKey:'sql|sql-sales/SalesDb|dbo.Sales'},
 {id:'b',category:'datastore',symbolKey:'sql|@builtin:tsql|SUM'},
 {id:'c',category:'datastore',symbolKey:'sql|invalid'}]});
 assert.equal(resources.length,1);assert.equal(resources[0].kind,'database');
 assert.equal(resources[0].binding,'bound');
 assert.match(resources[0].description,/deployment not verified/);
});
