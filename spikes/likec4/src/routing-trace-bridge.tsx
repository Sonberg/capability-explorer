import {useLayoutEffect} from 'react';
import {useDiagramSelector} from 'likec4/react';
import type {LayoutedView} from 'likec4/model';
import {routingTrace,traceEvent,traceSpan,finishTrace,timestamp} from './routing-trace.ts';
import type {TraceContext} from './routing-trace.ts';
export function RoutingTraceBridge({view,context,ready}:{view:LayoutedView;context:TraceContext|undefined;ready:boolean}) {
  const applied=useDiagramSelector(s=>s.context.view===view);
  useLayoutEffect(()=>{
    if(!context||!ready||!applied||!routingTrace.enabled)return;
    const record=routingTrace.get(context.id);
    if(record?.status!=='pending')return;
    const appliedAt=timestamp();
    traceEvent(context,'native-view.applied',appliedAt);
    const delivered=record.events.findLast(e=>e.name===`g${context.generation}.main.result`);
    if(delivered)traceSpan(context,'result-to-native-view',delivered.at,appliedAt);
    let second=0;
    const first=requestAnimationFrame(()=>{
      traceEvent(context,'frame.first');
      second=requestAnimationFrame(()=>{
        if(record.status!=='pending'||record.generations.at(-1)!==context.generation)return;
        traceEvent(context,'frame.second');
        traceSpan(context,'native-view-to-frame-opportunity',appliedAt,timestamp());
        const release=record.events.findLast(e=>e.name==='pointer.release');
        if(release)traceSpan(context,'release-to-frame-opportunity',release.at,timestamp());
        finishTrace(context,'complete');
      });
    });
    return ()=>{cancelAnimationFrame(first);cancelAnimationFrame(second);};
  },[applied,context,ready,view]);
  return null;
}
