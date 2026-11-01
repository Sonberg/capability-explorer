import { Background, Controls, MiniMap, ReactFlow, ReactFlowProvider } from "@xyflow/react";
import { useMemo } from "react";
import { layout, nodeTypes, type Graph, type ArchNode, type ArchEdge, type CanvasNodeData } from "../views/Architecture";

/** Supplied data uses the Architecture renderer; the caller owns evidence and selection semantics. */
export function SuppliedArchitectureCanvas({ graph, selectedId, associationIds, onNodeSelect, onEdgeSelect }: {
  graph: Graph;
  selectedId?: string;
  associationIds: ReadonlySet<string>;
  onNodeSelect: (node: ArchNode) => void;
  onEdgeSelect: (edge: ArchEdge) => void;
}) {
  const positioned = useMemo(() => layout(graph, new Set()), [graph]);
  const edges = useMemo(() => positioned.edges.map((edge) => associationIds.has(edge.id) ? { ...edge, markerEnd: undefined } : edge), [positioned, associationIds]);
  const nodes = useMemo(() => positioned.nodes.map((node) => node.type === "arch" ? {
    ...node, data: { ...node.data, selected: node.id === selectedId, dimmed: false } as CanvasNodeData,
  } : node), [positioned, selectedId]);
  return <div aria-label="Glossary connection canvas" className="relative h-[55dvh] min-h-[360px] overflow-hidden rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
    <ReactFlowProvider><ReactFlow
      key={graph.nodes.map((node) => node.id).join("|")}
      nodes={nodes} edges={edges} nodeTypes={nodeTypes}
      fitView fitViewOptions={{ padding: 0.18 }} colorMode="system" minZoom={0.1} maxZoom={2}
      nodesConnectable={false} nodesDraggable={false} zoomOnDoubleClick={false}
      proOptions={{ hideAttribution: true }}
      onNodeClick={(_, node) => node.type === "arch" && onNodeSelect((node.data as CanvasNodeData).node)}
      onEdgeClick={(_, edge) => onEdgeSelect((edge.data as { edge: ArchEdge }).edge)}
    ><Background gap={20} size={1} /><Controls showInteractive={false} /><MiniMap pannable zoomable className="!hidden sm:!block" /></ReactFlow></ReactFlowProvider>
  </div>;
}
