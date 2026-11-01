import type { Graph, ArchNode, ArchEdge } from "../architecture-types";
import { LiveArchitectureCanvas } from "./live-architecture";

/** The caller resolves original IDs to exact occurrence/review evidence. */
export function SuppliedArchitectureCanvas({ graph, snapshotKey, selectedId, associationIds, onNodeSelect, onEdgeSelect }: {
  graph: Graph;
  snapshotKey: string;
  selectedId?: string;
  associationIds: ReadonlySet<string>;
  onNodeSelect: (node: ArchNode) => void;
  onEdgeSelect: (edge: ArchEdge) => void;
}) {
  return <div className="relative h-[70dvh] min-h-[720px] max-[650px]:h-[1100px] overflow-hidden rounded-lg border border-zinc-200 dark:border-zinc-800">
    <LiveArchitectureCanvas graph={graph} snapshotKey={snapshotKey} selectedId={selectedId} associationIds={associationIds}
      onNodeSelect={item => { const node = graph.nodes.find(node => node.id === item.id); if (node) onNodeSelect(node); }}
      onEdgeSelect={item => { const edge = graph.edges.find(edge => edge.id === item.id); if (edge) onEdgeSelect(edge); }} />
  </div>;
}
