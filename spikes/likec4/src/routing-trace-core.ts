// Local, opt-in, bounded diagnostics. No model payloads, labels or network export.
export interface TraceEvent {name:string;at:number}
export interface TraceSpan {name:string;start:number;end:number}
export interface RouteTrace {
  id:number;kind:'node'|'group'|'layout';status:'pending'|'complete'|'cancelled'|'error';
  events:TraceEvent[];spans:TraceSpan[];invalidTimings:{name:string;start:number|null;end:number|null;reason:'non-finite'|'reversed'}[];generations:number[];nodes?:number;edges?:number;
}
export interface TraceContext {id:number;generation:number}
export type SpanExporter=(span:TraceSpan,context:TraceContext)=>void;
export const timestamp=()=>performance.timeOrigin+performance.now();
export class TraceRecorder {
  enabled=false;
  private next=0;
  private records:RouteTrace[]=[];
  dropped=0;
  exporterErrors=0;
  exporter:SpanExporter|undefined;
  private clock:()=>number;
  private capacity:number;
  private eventLimit:number;
  constructor(clock:()=>number=timestamp,capacity=100,eventLimit=128) {this.clock=clock;this.capacity=capacity;this.eventLimit=eventLimit;}
  cancelPending() {for(const record of this.records)if(record.status==='pending')record.status='cancelled';}
  reset() {this.records=[];this.dropped=0;this.exporterErrors=0;}
  begin(kind:RouteTrace['kind']):number|undefined {
    if(!this.enabled)return;
    const id=++this.next;
    if(this.records.length>=this.capacity){this.records.shift();this.dropped++;}
    this.records.push({id,kind,status:'pending',events:[],spans:[],invalidTimings:[],generations:[]});
    this.event(id,'interaction.start');return id;
  }
  get(id:number|undefined) {return this.records.find(record=>record.id===id);}
  event(id:number|undefined,name:string,at=this.clock()) {
    if(!this.enabled)return;
    const record=this.get(id);if(!record)return;
    if(record.events.length>=this.eventLimit){this.dropped++;return;}
    record.events.push({name,at});
  }
  span(context:TraceContext|undefined,name:string,start:number,end:number) {
    if(!this.enabled||!context)return false;
    const record=this.get(context.id);if(!record)return false;
    if(!Number.isFinite(start)||!Number.isFinite(end)||end<start){
      if(record.invalidTimings.length>=this.eventLimit)this.dropped++;
      else record.invalidTimings.push({name,start:Number.isFinite(start)?start:null,end:Number.isFinite(end)?end:null,reason:Number.isFinite(start)&&Number.isFinite(end)?'reversed':'non-finite'});
      return false;
    }
    if(record.spans.length>=this.eventLimit){this.dropped++;return false;}
    const span={name,start,end};record.spans.push(span);
    try {this.exporter?.(span,context);}catch{this.exporterErrors++;}
    return true;
  }
  finish(id:number|undefined,status:RouteTrace['status']) {const record=this.get(id);if(record?.status==='pending')record.status=status;}
  snapshot() {return structuredClone({version:1,clock:'epoch milliseconds from performance.timeOrigin + performance.now',dropped:this.dropped,exporterErrors:this.exporterErrors,records:this.records});}
}
