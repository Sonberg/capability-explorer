import {createOtelExporter} from './routing-trace-otel.ts';
import type {Tracer} from './routing-trace-otel.ts';
import {TraceRecorder,timestamp} from './routing-trace-core.ts';
import type {TraceContext,SpanExporter,RouteTrace} from './routing-trace-core.ts';
export {timestamp} from './routing-trace-core.ts';
export type {TraceContext} from './routing-trace-core.ts';
export const routingTrace=new TraceRecorder();
let active:number|undefined,activePointer:number|undefined,sequence=0;
const contexts=new WeakMap<object,TraceContext>();
const measuredNames=new Set<string>();
export function configureTracing(enabled:boolean) {
  routingTrace.enabled=enabled;active=undefined;activePointer=undefined;
  if(!enabled){routingTrace.cancelPending();for(const name of measuredNames)performance.clearMeasures(name);}
  measuredNames.clear();
}
export function tracePointerDown(kind:'node'|'group',pointerId:number) {
  if(active!==undefined&&routingTrace.get(active)?.status==='pending')routingTrace.finish(active,'cancelled');
  active=routingTrace.begin(kind);activePointer=pointerId;
}
export function tracePointerUp(event:PointerEvent) {if(event.pointerId!==activePointer)return;activePointer=undefined;if(routingTrace.get(active)?.status==='pending')routingTrace.event(active,'pointer.release');}
export function traceCancel(event:PointerEvent) {if(event.pointerId!==activePointer)return;routingTrace.event(active,'interaction.cancel');routingTrace.finish(active,'cancelled');active=undefined;activePointer=undefined;}
export function cancelTraceInteraction() {routingTrace.finish(active,'cancelled');active=undefined;activePointer=undefined;}
export function traceSnapshot(layout:object) {
  if(!routingTrace.enabled)return;
  if(active===undefined||routingTrace.get(active)?.status!=='pending')active=routingTrace.begin('layout');
  if(active===undefined)return;
  const context={id:active,generation:++sequence};contexts.set(layout,context);
  const record=routingTrace.get(active);
  if(record){if(record.generations.length>=128){record.generations.shift();routingTrace.dropped++;}record.generations.push(context.generation);}
  traceEvent(context,'snapshot.received');
  const release=routingTrace.get(active)?.events.findLast(e=>e.name==='pointer.release');
  if(release)traceSpan(context,'release-to-snapshot',release.at,timestamp());
}
export function traceContext(layout:{nodes:readonly unknown[];edges:readonly unknown[]}):TraceContext|undefined {
  if(!routingTrace.enabled)return;
  if(!contexts.has(layout)){cancelTraceInteraction();traceSnapshot(layout);}
  const context=contexts.get(layout),record=routingTrace.get(context?.id);
  if(record){record.nodes=layout.nodes.length;record.edges=layout.edges.length;}
  return context;
}
export function traceEvent(context:TraceContext|undefined,name:string,at=timestamp()) {if(context)routingTrace.event(context.id,`g${context.generation}.${name}`,at);}
export function traceSpan(context:TraceContext|undefined,name:string,start:number,end=timestamp()) {
  routingTrace.span(context,name,start,end);
  if(!context||!routingTrace.enabled)return;
  const key=`ce.routing.${context.id}.${context.generation}.${name}`;
  // User Timing is visible in DevTools. Bound its independent global buffer too.
  if(measuredNames.size>=256){const oldest=measuredNames.values().next().value!;performance.clearMeasures(oldest);measuredNames.delete(oldest);}
  try {performance.clearMeasures(key);performance.measure(key,{start:Math.max(0,start-performance.timeOrigin),end:Math.max(0,end-performance.timeOrigin)});measuredNames.add(key);}catch{/* Diagnostics cannot break routing. */}
}
export function finishTrace(context:TraceContext|undefined,status:RouteTrace['status']) {if(context&&routingTrace.get(context.id)?.generations.at(-1)===context.generation)routingTrace.finish(context.id,status);}
export function downloadTrace() {
  const url=URL.createObjectURL(new Blob([JSON.stringify(routingTrace.snapshot(),null,2)],{type:'application/json'}));
  const link=document.createElement('a');link.href=url;link.download='routing-trace.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
// An application-provided exporter can create manual OTel spans. Never send automatically.
declare global {interface Window {ceRoutingTrace:{snapshot:()=>ReturnType<TraceRecorder['snapshot']>;reset:()=>void;setOtelTracer:(tracer:Tracer|undefined)=>void;setExporter:(exporter:SpanExporter|undefined)=>void}}}
if(typeof window!=='undefined')window.ceRoutingTrace={snapshot:()=>routingTrace.snapshot(),reset:()=>routingTrace.reset(),setOtelTracer:tracer=>{routingTrace.exporter=tracer?createOtelExporter(tracer):undefined;},setExporter:exporter=>{routingTrace.exporter=exporter;}};
