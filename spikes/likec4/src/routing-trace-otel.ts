import type {SpanExporter} from './routing-trace-core.ts';
// Structural subset of the public OTel Tracer interface. The host supplies its
// configured tracer; this adapter installs no SDK, provider, collector or network.
type HrTime=[number,number];
export interface Tracer {
  startSpan(name:string,options:{startTime:HrTime;attributes:Record<string,string|number>}):{end:(time:HrTime)=>void};
}
const hrTime=(milliseconds:number):HrTime=>{const seconds=Math.floor(milliseconds/1000);return [seconds,Math.round((milliseconds-seconds*1000)*1e6)];};
export function createOtelExporter(tracer:Tracer):SpanExporter {
  return (span,context)=>{
    const exported=tracer.startSpan(`diagram.routing.${span.name}`,{
      startTime:hrTime(span.start),
      attributes:{'diagram.interaction.id':context.id,'diagram.routing.generation':context.generation,'diagram.timing.clock':'performance.timeOrigin+now'}
    });
    exported.end(hrTime(span.end));
  };
}
