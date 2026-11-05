import type { Graph } from "../architecture-types";
import { Button } from "./ui";

export type ArchitectureKindFacet = NonNullable<NonNullable<Graph["facets"]>["kinds"]>[number];

interface ArchitectureFilterControlsProps {
  query: string;
  onQueryChange: (value: string) => void;
  nodeBudget: string;
  onNodeBudgetChange: (value: string) => void;
  edgeBudget: string;
  onEdgeBudgetChange: (value: string) => void;
  hiddenKinds: Set<string>;
  onHiddenKindsChange: (update: (current: Set<string>) => Set<string>) => void;
  kinds: ArchitectureKindFacet[];
  pins: string[];
  selectedNodeId?: string;
  error: string;
  maxNodeBudget: number;
  maxEdgeBudget: number;
  onApply: () => void;
  onReset: () => void;
  onUnpin: (id: string) => void;
}

function kindName(value: string): string {
  return value.length ? `${value[0].toUpperCase()}${value.slice(1)}` : value;
}

export function ArchitectureFilterControls(props: ArchitectureFilterControlsProps) {
  return (
    <form className="flex flex-col gap-2 rounded-lg border border-zinc-200 p-3 text-xs dark:border-zinc-800" onSubmit={event => { event.preventDefault(); props.onApply(); }}>
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex min-w-48 flex-1 flex-col gap-1">
          Find elements
          <input aria-label="Find elements" maxLength={200} value={props.query} onChange={event => props.onQueryChange(event.target.value)} className="rounded border border-zinc-300 bg-white px-2 py-1.5 dark:border-zinc-700 dark:bg-zinc-950" />
        </label>
        <label className="flex flex-col gap-1">
          Node budget
          <input aria-label="Node budget" type="number" min={1} max={props.maxNodeBudget} step={1} value={props.nodeBudget} onChange={event => props.onNodeBudgetChange(event.target.value)} className="w-24 rounded border border-zinc-300 bg-white px-2 py-1.5 dark:border-zinc-700 dark:bg-zinc-950" />
        </label>
        <label className="flex flex-col gap-1">
          Edge budget
          <input aria-label="Edge budget" type="number" min={1} max={props.maxEdgeBudget} step={1} value={props.edgeBudget} onChange={event => props.onEdgeBudgetChange(event.target.value)} className="w-24 rounded border border-zinc-300 bg-white px-2 py-1.5 dark:border-zinc-700 dark:bg-zinc-950" />
        </label>
        <Button type="submit">Apply graph filters</Button>
        <Button type="button" variant="secondary" onClick={props.onReset}>Reset filters</Button>
      </div>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2" aria-label="Relationship kind filters">
        {props.kinds.map(({ value, total, matched }) => (
          <label key={value} className="flex cursor-pointer items-center gap-1" title={`${total} total, ${matched} matched`}>
            <input aria-label={kindName(value)} type="checkbox" checked={!props.hiddenKinds.has(value)} onChange={event => props.onHiddenKindsChange(current => {
              const next = new Set(current);
              if (event.target.checked) next.delete(value);
              else next.add(value);
              return next;
            })} />
            {kindName(value)} <span className="text-zinc-500">({total} total · {matched} matched)</span>
          </label>
        ))}
        {!props.kinds.length && <span className="text-zinc-500">Relationship kinds load with the graph.</span>}
      </div>
      {props.pins.length > 0 && <div className="flex flex-wrap gap-2" aria-label="Pinned elements">
        <span>Pinned:</span>
        {props.pins.map(id => (
          <button key={id} type="button" disabled={id === props.selectedNodeId} aria-label={`Unpin ${id}`} title={id === props.selectedNodeId ? "The selected element is pinned on Apply" : "Remove this pin"} onClick={() => props.onUnpin(id)} className="rounded border border-zinc-300 px-2 py-1 disabled:opacity-60 dark:border-zinc-700">
            {id === props.selectedNodeId ? `Selected · ${id}` : `Unpin · ${id}`}
          </button>
        ))}
      </div>}
      {props.error && <p className="text-red-700 dark:text-red-300" role="alert">{props.error}</p>}
    </form>
  );
}
