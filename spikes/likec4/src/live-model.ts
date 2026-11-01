import {computeParsedModelData} from '@likec4/core/compute-view';
import type {LayoutedLikeC4ModelData} from '@likec4/core/types';
import type {LiveModelInput,LiveModelResult} from './live-contracts.ts';
import {projectLiveGraph} from './live-projection.ts';
import {layoutLiveView} from './live-model-layout.ts';

export function buildLiveModel(input:LiveModelInput):LiveModelResult {
  const projection=projectLiveGraph(input),computed=computeParsedModelData(projection.parsed);
  const edgeSourceIds:Record<string,string[]>=Object.create(null);
  const sourceEdges=new Map(input.graph.edges.map(e=>[e.id,e]));
  const associations=new Set(projection.associationSourceIds);
  const views:LayoutedLikeC4ModelData['views']={};
  for(const [id,view] of Object.entries(computed.views)) {
    if(view._type!=='element')throw new Error('Unexpected live view type');
    const edges=view.edges.map(edge=>{
      const ids=[...new Set(edge.relations.map(id=>projection.relationSourceIds[id]).filter((id):id is string=>id!==undefined))].sort();
      if(ids.length!==edge.relations.length)throw new Error('Unmapped rendered relationship');
      if(edgeSourceIds[edge.id]&&JSON.stringify(edgeSourceIds[edge.id])!==JSON.stringify(ids))throw new Error('Ambiguous rendered relationship ID');
      edgeSourceIds[edge.id]=ids;
      const allAssociations=ids.every(id=>associations.has(id));
      const dashed=ids.some(id=>associations.has(id)||['candidate','unresolved','unbound','broken'].includes(sourceEdges.get(id)?.certainty??'unresolved'));
      return {...edge,line:dashed?'dashed' as const:'solid' as const,head:allAssociations?'none' as const:'normal' as const,tail:'none' as const};
    });
    views[id]=layoutLiveView({...view,edges});
  }
  const modelData:LayoutedLikeC4ModelData={...computed,_stage:'layouted',views};
  return {snapshot:input.snapshot,source:projection.source,modelData,nodeSourceIds:projection.nodeSourceIds,edgeSourceIds,sourceNodeIds:projection.sourceNodeIds,groups:projection.groups};
}
