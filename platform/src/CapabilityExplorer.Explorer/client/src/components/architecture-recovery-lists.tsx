import { useState } from "react";
import type { ArchNode, Graph } from "../architecture-types";
import { Button } from "./ui";

interface ArchitectureRecoveryListsProps {
  graph: Graph;
  selectedNodeId?: string;
  pins: string[];
  maxPins: number;
  maxOmittedNodes: number;
  onSelect: (node: ArchNode) => void;
  onDrill: (node: ArchNode) => void;
  onPin: (id: string) => void;
}

export function ArchitectureRecoveryLists(props: ArchitectureRecoveryListsProps) {
  const [shownOpen, setShownOpen] = useState(false);
  const [omittedOpen, setOmittedOpen] = useState(false);
  const omittedNodes = props.graph.omitted?.nodes.slice(0, props.maxOmittedNodes) ?? [];
  const omittedNodeCount = props.graph.omitted?.nodeCount ?? 0;
  return (
    <div className="grid gap-3 xl:grid-cols-2">
      <section aria-label="Shown architecture elements" className="max-h-64 overflow-y-auto rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
        <details onToggle={event => setShownOpen(event.currentTarget.open)}>
          <summary aria-label="Show shown elements" className="cursor-pointer text-sm font-semibold">Show shown elements · {props.graph.nodes.length}</summary>
          {shownOpen && <ul className="mt-2 space-y-1">
            {props.graph.nodes.map(node => (
              <li key={node.id} className="flex flex-wrap items-center gap-2">
                <button type="button" data-architecture-node-id={node.id} aria-pressed={props.selectedNodeId === node.id} onClick={() => props.onSelect(node)} className="truncate text-left text-blue-700 hover:underline dark:text-blue-300">
                  {node.label}
                </button>
                <span className="text-xs text-zinc-500">{node.category}{node.symbolKey ? ` · ${node.symbolKey}` : ""}</span>
                {node.drill && <Button variant="secondary" onClick={() => props.onDrill(node)}>Open {node.drill.level}</Button>}
              </li>
            ))}
          </ul>}
        </details>
      </section>
      <section aria-label="Omitted architecture elements" className="max-h-64 overflow-y-auto rounded-lg border border-zinc-200 p-3 dark:border-zinc-800">
        <details onToggle={event => setOmittedOpen(event.currentTarget.open)}>
          <summary aria-label="Show omitted elements" className="cursor-pointer text-sm font-semibold">Show omitted elements · {omittedNodes.length} of {omittedNodeCount} listed</summary>
          {omittedOpen && <>
            {omittedNodes.length > 0 ? <ul className="mt-2 space-y-1">
              {omittedNodes.map(node => (
                <li key={node.id} className="flex flex-wrap items-center gap-2">
                  <span>{node.label}</span>
                  <span className="text-xs text-zinc-500">{node.category} · degree {node.degree} · {node.id}</span>
                  <Button type="button" variant="secondary" disabled={props.pins.includes(node.id) || props.pins.length >= props.maxPins} onClick={() => props.onPin(node.id)} aria-label={`Pin ${node.label}`}>
                    {props.pins.includes(node.id) ? "Pinned" : "Pin element"}
                  </Button>
                </li>
              ))}
            </ul> : <p className="text-xs text-zinc-500">No omitted nodes are listed for this graph.</p>}
            {omittedNodes.length < omittedNodeCount && <p className="mt-2 text-xs text-zinc-500">Only the first {omittedNodes.length} of {omittedNodeCount} omitted nodes are listed.</p>}
          </>}
        </details>
      </section>
    </div>
  );
}
