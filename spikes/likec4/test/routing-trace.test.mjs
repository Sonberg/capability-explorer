import {test} from 'node:test';
import assert from 'node:assert/strict';
import {TraceRecorder} from '../src/routing-trace-core.ts';
import {createOtelExporter} from '../src/routing-trace-otel.ts';
import {routeOrthogonal} from '../src/orthogonal-routing.ts';
test('diagnostics default off, bound records/events/spans, and snapshots cannot mutate storage',()=>{
 const recorder=new TraceRecorder(()=>100,2,2);
 assert.equal(recorder.begin('node'),undefined);
 recorder.enabled=true;
 const id=recorder.begin('group');recorder.event(id,'release');recorder.event(id,'extra');
 const ctx={id,generation:1};recorder.span(ctx,'work',100,110);recorder.span(ctx,'work',100,110);recorder.span(ctx,'extra',100,110);
 assert.equal(recorder.dropped,2);
 recorder.snapshot().records[0].events.length=0;
 assert.equal(recorder.snapshot().records[0].events.length,2);
 recorder.begin('node');recorder.begin('node');
 assert.equal(recorder.snapshot().records.length,2);assert.equal(recorder.get(id),undefined);
 recorder.cancelPending();assert.ok(recorder.snapshot().records.every(r=>r.status==='cancelled'));
 recorder.reset();assert.equal(recorder.snapshot().records.length,0);
});
test('bad exporters and malformed timing cannot break routing or corrupt terminal status',()=>{
 const recorder=new TraceRecorder(()=>100);recorder.enabled=true;
 const id=recorder.begin('node'),context={id,generation:1};
 recorder.exporter=()=>{throw Error('offline');};
 recorder.span(context,'work',100,110);assert.equal(recorder.exporterErrors,1);
 recorder.span(context,'bad',200,100);recorder.span(context,'bad',NaN,100);
 assert.equal(recorder.get(id).spans.length,1);
 assert.deepEqual(recorder.get(id).invalidTimings,[{name:'bad',start:200,end:100,reason:'reversed'},{name:'bad',start:null,end:100,reason:'non-finite'}]);
 assert.equal(recorder.exporterErrors,1,'invalid observations never reach exporter');
 recorder.finish(id,'complete');recorder.finish(id,'cancelled');assert.equal(recorder.get(id).status,'complete');
 recorder.enabled=false;recorder.event(id,'disabled');assert.equal(recorder.get(id).events.length,1);
});
test('optional OTel adapter preserves epoch timing and correlation without sending anything itself',()=>{
 const calls=[];
 const exporter=createOtelExporter({startSpan:(name,options)=>{calls.push({name,...options});return {end:time=>calls[0].end=time};}});
 exporter({name:'search',start:1700000000123.25,end:1700000000130.5},{id:7,generation:9});
 assert.deepEqual(calls[0].startTime,[1700000000,123250000]);assert.deepEqual(calls[0].end,[1700000000,130500000]);
 assert.equal(calls[0].attributes['diagram.routing.generation'],9);
});
test('phase tracing leaves deterministic routes and evidence unchanged',()=>{
 const view={bounds:{x:0,y:0,width:400,height:200},nodes:[{id:'a',x:0,y:0,width:60,height:40},{id:'b',x:240,y:80,width:60,height:40}],edges:[{id:'e',source:'a',target:'b',points:[[60,20],[240,100]],certainty:'candidate'}]};
 const phases=[];
 assert.deepEqual(routeOrthogonal(view,(name,start,end)=>phases.push({name,start,end})),routeOrthogonal(view));
 assert.equal(phases.length,15);assert.ok(phases.every(p=>p.end>=p.start));
 assert.ok(phases.some(p=>p.name==='order2.quality'));
});

test('unrelated layouts and explicit navigation cannot complete a pending drag',async()=>{
 const {configureTracing,routingTrace,tracePointerDown,tracePointerUp,traceSnapshot,traceContext,cancelTraceInteraction}=await import('../src/routing-trace.ts');
 configureTracing(true);routingTrace.reset();
 tracePointerDown('node',3);tracePointerUp({pointerId:3});
 const drag={nodes:[],edges:[]};traceSnapshot(drag);const old=traceContext(drag);
 tracePointerUp({pointerId:4});assert.equal(routingTrace.get(old.id).events.filter(e=>e.name==='pointer.release').length,1);
 const layout=traceContext({nodes:[],edges:[]});
 assert.equal(routingTrace.get(old.id).status,'cancelled');assert.equal(routingTrace.get(layout.id).kind,'layout');
 tracePointerDown('group',5);cancelTraceInteraction();
 assert.equal(routingTrace.snapshot().records.at(-1).status,'cancelled');
 configureTracing(false);
});

test('invalid timing evidence is bounded and never fabricated as a successful duration',()=>{
 const recorder=new TraceRecorder(()=>100,1,1);recorder.enabled=true;const id=recorder.begin('node'),ctx={id,generation:1};
 assert.equal(recorder.span(ctx,'result-delivery',100.1,100),false);recorder.span(ctx,'result-delivery',100.1,100);
 assert.equal(recorder.get(id).invalidTimings.length,1);assert.equal(recorder.dropped,1);assert.equal(recorder.get(id).spans.length,0);
 recorder.reset();assert.equal(recorder.snapshot().records.length,0);
});
