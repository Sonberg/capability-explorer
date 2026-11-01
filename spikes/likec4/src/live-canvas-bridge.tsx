import {useLayoutEffect, useRef} from 'react';
import {useDiagramSelector} from 'likec4/react';
import type {LiveGraph, LiveModelResult} from './live-contracts.ts';

/** Source identity is attached to rendered wrappers, never inferred from labels. */
export function LiveSourceBridge({graph, result, selectedId}:{graph:LiveGraph; result:LiveModelResult; selectedId?:string}) {
  const marker = useRef<HTMLSpanElement>(null);
  const view = useDiagramSelector(state => state.context.view);
  useLayoutEffect(() => {
    const root = marker.current?.closest('.ce-live-diagram') ?? marker.current?.getRootNode();
    if (!(root instanceof Element || root instanceof ShadowRoot)) return;
    const selectedNode = graph.nodes.find(node => node.id === selectedId);
    const selectedEdge = graph.edges.find(edge => edge.id === selectedId);
    const neighbors = new Set<string>();
    if (selectedNode) {
      neighbors.add(selectedNode.id);
      for (const edge of graph.edges) if (edge.from === selectedNode.id || edge.to === selectedNode.id) {neighbors.add(edge.from); neighbors.add(edge.to);}
    }
    if (selectedEdge) {neighbors.add(selectedEdge.from); neighbors.add(selectedEdge.to);}
    const edgesById = new Map(graph.edges.map(edge => [edge.id, edge]));
    const sourceIds = (id:string):readonly string[] => {
      const node = view.nodes.find(node => node.id === id);
      const known = result.nodeSourceIds[id];
      if (node?.kind !== '@group' && known?.length) return known;
      return [...new Set((node?.children ?? []).flatMap(child => sourceIds(child)))];
    };
    const sync = () => {
      for (const element of root.querySelectorAll<HTMLElement>('.react-flow__node')) {
        const id = element.getAttribute('data-id') ?? '';
        const ids = sourceIds(id);
        element.dataset.sourceNodeIds = JSON.stringify(ids);
        element.dataset.presentationGroup = String(view.nodes.find(node => node.id === id)?.kind === '@group');
        element.dataset.neighborhood = !selectedId || !neighbors.size || ids.some(id => neighbors.has(id)) ? 'inside' : 'outside';
        element.dataset.sourceSelected = String(ids.includes(selectedId ?? ''));
      }
      for (const element of root.querySelectorAll<SVGElement>('.react-flow__edge')) {
        const ids = result.edgeSourceIds[element.getAttribute('data-id') ?? ''] ?? [];
        element.dataset.sourceEdgeIds = JSON.stringify(ids);
        const incident = ids.some(id => {
          const edge = edgesById.get(id);
          return edge && (selectedNode ? edge.from === selectedNode.id || edge.to === selectedNode.id : id === selectedId);
        });
        element.dataset.neighborhood = !selectedId || !neighbors.size || incident ? 'inside' : 'outside';
        element.dataset.sourceSelected = String(ids.includes(selectedId ?? ''));
      }
    };
    sync();
    // Only child insertion is observed: writing diagnostic attributes cannot loop.
    const observer = new MutationObserver(sync);
    observer.observe(root, {childList:true, subtree:true});
    return () => observer.disconnect();
  }, [graph, result, selectedId, view]);
  return <span ref={marker} hidden aria-hidden="true"/>;
}
