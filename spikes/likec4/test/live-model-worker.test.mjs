import {test} from 'node:test';
import assert from 'node:assert/strict';

test('worker protocol echoes request identity and reports bounded-input failure',async()=>{
  const original=globalThis.self,messages=[];
  globalThis.self={onmessage:null,postMessage:message=>messages.push(message)};
  try {
    await import('../src/live-model.worker.ts');
    const graph={nodes:[{id:'node',label:'Live worker'}],edges:[]};
    await self.onmessage({data:{requestId:7,graph,snapshot:'selected-revision',associationIds:[]}});
    assert.equal(messages[0].requestId,7);assert.equal(messages[0].ok,true);
    assert.equal(messages[0].result.snapshot,'selected-revision');
    assert.ok(messages[0].result.modelData.views.flow.nodes.some(n=>n.title==='Live worker'));
    await self.onmessage({data:{requestId:8,graph:{nodes:[],edges:[{id:'broken',from:'a',to:'b'}]},snapshot:'invalid',associationIds:[]}});
    assert.deepEqual(messages[1],{requestId:8,ok:false,error:'Dangling edge endpoint'});
  } finally {globalThis.self=original;}
});
