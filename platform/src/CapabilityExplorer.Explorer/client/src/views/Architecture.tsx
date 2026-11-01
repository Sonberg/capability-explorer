import * as dagre from "@dagrejs/dagre";
import {
  Background,
  Controls,
  Handle,
  MarkerType,
  MiniMap,
  Position,
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
  type Edge as FlowEdge,
  type Node as FlowNode,
  type NodeChange,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useEffect, useMemo, useRef, useState } from "react";
import type { Evidence, SymbolRef } from "../api";
import { EvidenceLinks } from "../components/source";
import { ResolutionPill, SymbolLink } from "../components/symbols";
import { Button, ErrorBox, Key, Mono, Pill, type Tone } from "../components/ui";
import { usePublishFocus } from "../focus";
import { href, symbolHref, useApi, useSelection } from "../state";

// ---------------------------------------------------------------- API shapes

interface ArchNode {
  id: string;
  category: string;
  label: string;
  sublabel?: string;
  group?: string;
  symbolKey?: string;
  repositoryId?: string;
  drill?: { level: string; focus: string };
  badges: string[];
}

interface ArchFact {
  relation: string;
  from: SymbolRef;
  to?: SymbolRef;
  resolution: "resolved" | "static-target" | "candidate" | "unresolved";
  unresolvedReason?: string;
  derivation: string;
  rule?: string;
  evidence: Evidence[];
}

interface ArchEdge {
  id: string;
  from: string;
  to: string;
  kind: string;
  label: string;
  count: number;
  certainty: "proven" | "static" | "bound" | "broken" | "unbound" | "candidate" | "unresolved";
  facts: ArchFact[];
}

interface Graph {
  level: "landscape" | "system" | "repository" | "component" | "requests";
  focus?: string;
  title: string;
  breadcrumbs: { label: string; level: string; focus?: string }[];
  nodes: ArchNode[];
  edges: ArchEdge[];
  notes: string[];
}

// ---------------------------------------------------------------- styling

const categoryStyle: Record<string, { box: string; strip: string; name: string }> = {
  capability: { box: "border-indigo-400 dark:border-indigo-700", strip: "bg-indigo-600", name: "capability" },
  service: { box: "border-blue-300 dark:border-blue-800", strip: "bg-blue-500", name: "service" },
  application: { box: "border-sky-300 dark:border-sky-800", strip: "bg-sky-500", name: "application" },
  external: { box: "border-zinc-300 border-dashed dark:border-zinc-600", strip: "bg-zinc-400", name: "other system" },
  boundary: { box: "border-blue-400 dark:border-blue-700", strip: "bg-blue-600", name: "this repository" },
  component: { box: "border-slate-300 dark:border-slate-700", strip: "bg-slate-500", name: "component" },
  interface: { box: "border-indigo-300 border-dashed dark:border-indigo-700", strip: "bg-indigo-400", name: "interface" },
  module: { box: "border-teal-300 dark:border-teal-800", strip: "bg-teal-500", name: "module" },
  member: { box: "border-zinc-300 dark:border-zinc-700", strip: "bg-zinc-500", name: "member" },
  endpoint: { box: "border-emerald-300 dark:border-emerald-800", strip: "bg-emerald-500", name: "endpoint" },
  datastore: { box: "border-amber-300 dark:border-amber-800", strip: "bg-amber-500", name: "data" },
  channel: { box: "border-violet-300 dark:border-violet-800", strip: "bg-violet-500", name: "channel" },
  unmatched: { box: "border-red-300 border-dashed dark:border-red-800", strip: "bg-red-500", name: "unresolved" },
};

const kindColor: Record<string, string> = {
  http: "#3b82f6",
  message: "#8b5cf6",
  data: "#f59e0b",
  call: "#94a3b8",
  implements: "#818cf8",
  registers: "#14b8a6",
  handles: "#10b981",
  declares: "#a1a1aa",
  sameName: "#fb7185",
  shares: "#6366f1",
};

const kindLabel: Record<string, string> = {
  http: "HTTP",
  message: "messaging",
  data: "data",
  call: "calls / references",
  implements: "implements",
  registers: "DI registration",
  handles: "handles endpoint",
  declares: "declares",
  sameName: "same name (unproven)",
  shares: "shares repository",
};

const certaintyDash: Record<ArchEdge["certainty"], string | undefined> = {
  proven: undefined,
  static: undefined,
  bound: undefined,
  broken: "3 3",
  unbound: "6 4",
  candidate: "10 5",
  unresolved: "2 4",
};

const certaintyText: Record<ArchEdge["certainty"], { label: string; tone: Tone; title: string }> = {
  proven: { label: "proven", tone: "ok", title: "Every underlying fact is resolved" },
  static: { label: "static target", tone: "info", title: "Static target proven; the runtime target is not (interface dispatch, DI)" },
  bound: { label: "bound", tone: "ok", title: "Bound by the service map to a declared endpoint (human-derived link; extractor output unchanged)" },
  broken: { label: "broken", tone: "bad", title: "Bound by the service map, but no such endpoint exists in the selected revision (for example a renamed route)" },
  unbound: { label: "unbound", tone: "warn", title: "Target known only by a configuration name (connection string, env var, config key)" },
  candidate: { label: "candidate", tone: "warn", title: "A match by route or name, or one of several possible targets" },
  unresolved: { label: "unresolved", tone: "bad", title: "Target not statically known" },
};

// ---------------------------------------------------------------- layout

const NODE_W = 220;
const NODE_H = 64;

type CanvasNodeData = { node: ArchNode; selected: boolean; dimmed: boolean };
type GroupNodeData = { label: string };

function layout(graph: Graph, hidden: Set<string>): { nodes: FlowNode[]; edges: FlowEdge[] } {
  const edges = graph.edges.filter((e) => !hidden.has(e.kind));
  const g = new dagre.graphlib.Graph({ compound: true, multigraph: true });
  g.setGraph({ rankdir: "LR", nodesep: 22, ranksep: 90, edgesep: 12, marginx: 20, marginy: 20 });
  g.setDefaultEdgeLabel(() => ({}));
  // Only code elements are clustered (by unit or owning component); systems, data stores and channels are
  // told apart by colour. Large mixed clusters make layered layouts unreadable.
  const codeLike = (n: ArchNode) => !!n.group && ["component", "interface", "module", "member", "endpoint"].includes(n.category);
  const sizes = new Map<string, number>();
  for (const n of graph.nodes.filter(codeLike)) sizes.set(n.group!, (sizes.get(n.group!) ?? 0) + 1);
  // A cluster only helps when it separates things: two or more groups, each with two or more members.
  const useful = [...sizes.entries()].filter(([, count]) => count >= 2).map(([group]) => group);
  const clustered = (n: ArchNode) => codeLike(n) && useful.length >= 2 && useful.includes(n.group!);
  const groups = useful.length >= 2 ? useful : [];
  for (const group of groups) g.setNode(`group:${group}`, { label: group });
  for (const n of graph.nodes) {
    g.setNode(n.id, { width: NODE_W, height: NODE_H });
    if (clustered(n)) g.setParent(n.id, `group:${n.group}`);
  }
  // Lay out in a canonical direction (callers → callees → resources) whatever the arrow says, so reads and
  // writes on the same store do not pull the graph both ways. Arrows still render the real direction.
  const category = new Map(graph.nodes.map((n) => [n.id, n.category]));
  const sink = (id: string) => ["datastore", "channel", "unmatched", "external"].includes(category.get(id) ?? "");
  for (const e of edges) {
    if (e.kind === "sameName") continue; // a hint between two resources; it should not shape the layout
    const flip = sink(e.from) && !sink(e.to);
    g.setEdge(flip ? e.to : e.from, flip ? e.from : e.to, {}, e.id);
  }
  dagre.layout(g);

  const groupNodes: FlowNode[] = groups.flatMap((group) => {
    const box = g.node(`group:${group}`);
    if (!box || !box.width) return [];
    return [
      {
        id: `group:${group}`,
        type: "group",
        position: { x: box.x - box.width / 2 - 10, y: box.y - box.height / 2 - 26 },
        data: { label: group } satisfies GroupNodeData,
        style: { width: box.width + 20, height: box.height + 36 },
        selectable: false,
        draggable: false,
        zIndex: -1,
      },
    ];
  });
  const nodes: FlowNode[] = graph.nodes.map((n) => {
    const p = g.node(n.id);
    return { id: n.id, type: "arch", position: { x: p.x - NODE_W / 2, y: p.y - NODE_H / 2 }, data: { node: n, selected: false, dimmed: false } satisfies CanvasNodeData };
  });
  // Leave and enter on the facing sides, so an edge pointing against the layout direction does not loop around its nodes.
  const x = new Map(nodes.map((n) => [n.id, n.position.x]));
  const flowEdges: FlowEdge[] = edges.map((e) => ({
    id: e.id,
    source: e.from,
    target: e.to,
    ...((x.get(e.to) ?? 0) < (x.get(e.from) ?? 0) ? { sourceHandle: "sl", targetHandle: "tr" } : { sourceHandle: "sr", targetHandle: "tl" }),
    label: e.label,
    data: { edge: e },
    markerEnd: e.kind === "sameName" ? undefined : { type: MarkerType.ArrowClosed, color: kindColor[e.kind] ?? "#94a3b8", width: 16, height: 16 },
    style: { stroke: kindColor[e.kind] ?? "#94a3b8", strokeWidth: Math.min(1.25 + Math.log2(e.count + 1), 4), strokeDasharray: certaintyDash[e.certainty] },
    labelStyle: { fontSize: 10, fill: "currentColor" },
    labelBgStyle: { fillOpacity: 0.85 },
    labelBgPadding: [4, 2] as [number, number],
    labelBgBorderRadius: 4,
  }));
  return { nodes: [...groupNodes, ...nodes], edges: flowEdges };
}

// ---------------------------------------------------------------- node renderers

function ArchNodeView({ data }: NodeProps<FlowNode<CanvasNodeData>>) {
  const n = data.node;
  const style = categoryStyle[n.category] ?? categoryStyle.member;
  return (
    <div
      title={n.drill ? "Double-click to open the next level" : undefined}
      className={`relative flex h-[64px] w-[220px] overflow-hidden rounded-lg border bg-white shadow-sm transition-opacity dark:bg-zinc-900 ${style.box} ${data.selected ? "ring-2 ring-blue-500" : ""} ${data.dimmed ? "opacity-30" : ""}`}
    >
      <Handle id="tl" type="target" position={Position.Left} className="!size-1.5 !border-0 !bg-zinc-400" />
      <Handle id="tr" type="target" position={Position.Right} className="!size-1.5 !border-0 !bg-zinc-400" />
      <Handle id="sl" type="source" position={Position.Left} className="!size-1.5 !border-0 !bg-zinc-400" />
      <div className={`w-1.5 shrink-0 ${style.strip}`} />
      <div className="min-w-0 flex-1 px-2.5 py-1.5">
        <div className="flex items-center gap-1">
          <span className="truncate text-[12.5px] font-semibold" title={n.label}>
            {n.label}
          </span>
          {n.drill && <span className="ml-auto shrink-0 text-[10px] text-blue-600 dark:text-blue-400">⤢</span>}
        </div>
        <div className="truncate text-[10.5px] text-zinc-500" title={n.sublabel}>
          {n.sublabel ?? style.name}
        </div>
        <div className="mt-0.5 flex gap-1 overflow-hidden">
          {n.badges.filter(Boolean).slice(0, 3).map((b) => (
            <span
              key={b}
              className={`whitespace-nowrap rounded px-1 text-[9.5px] font-semibold ${
                b === "unbound" || b === "partial" || b === "candidate" ? "bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300" : b === "unmatched" || b === "unresolved" || b === "broken" ? "bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300" : "bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300"
              }`}
            >
              {b}
            </span>
          ))}
        </div>
      </div>
      <Handle id="sr" type="source" position={Position.Right} className="!size-1.5 !border-0 !bg-zinc-400" />
    </div>
  );
}

function GroupView({ data }: NodeProps<FlowNode<GroupNodeData>>) {
  return (
    <div className="h-full w-full rounded-xl border border-dashed border-zinc-300 bg-zinc-100/50 dark:border-zinc-700 dark:bg-zinc-800/30">
      <div className="px-2.5 pt-1 text-[11px] font-semibold uppercase tracking-wide text-zinc-500">{data.label}</div>
    </div>
  );
}

const nodeTypes = { arch: ArchNodeView, group: GroupView };

// ---------------------------------------------------------------- view

export function ArchitectureView({ params }: { params: URLSearchParams }) {
  const { capability } = useSelection();
  return (
    <ReactFlowProvider>
      <Canvas level={params.get("level") ?? (capability ? "system" : "landscape")} focus={params.get("focus") ?? undefined} />
    </ReactFlowProvider>
  );
}

type Selected = { type: "node"; node: ArchNode } | { type: "edge"; edge: ArchEdge } | null;

let rememberedHidden = new Set<string>(["declares"]);

function Canvas({ level, focus }: { level: string; focus?: string }) {
  const { scope, setCapability } = useSelection();
  const { data: graph, error, loading } = useApi<Graph>(`/api/architecture?level=${level}${focus ? `&focus=${encodeURIComponent(focus)}` : ""}&${scope}`);
  const [hidden, setHidden] = useState<Set<string>>(rememberedHidden);
  const [selected, setSelected] = useState<Selected>(null);
  const { fitView } = useReactFlow();

  useEffect(() => setSelected(null), [level, focus]);
  usePublishFocus(selected?.type === "node" ? { kind: selected.node.category, label: selected.node.label, key: selected.node.symbolKey } : null);
  useEffect(() => {
    rememberedHidden = hidden;
  }, [hidden]);

  const laidOut = useMemo(() => (graph ? layout(graph, hidden) : { nodes: [], edges: [] }), [graph, hidden]);
  // Dragged positions, kept until the next layout. ponytail: not persisted; store per level/focus if wanted.
  const [moved, setMoved] = useState<Record<string, { x: number; y: number }>>({});
  useEffect(() => setMoved({}), [laidOut]);
  const onNodesChange = (changes: NodeChange[]) => {
    const next: Record<string, { x: number; y: number }> = {};
    for (const c of changes) if (c.type === "position" && c.position) next[c.id] = c.position;
    if (Object.keys(next).length) setMoved((m) => ({ ...m, ...next }));
  };
  // Refit only when the set of visible edge kinds changes. (fitView's identity is not stable across renders;
  // depending on it would refit on every selection and move nodes between the two clicks of a double-click.)
  const fitRef = useRef(fitView);
  fitRef.current = fitView;
  const firstFit = useRef(true);
  const lastClick = useRef({ id: "", at: 0 });
  useEffect(() => {
    if (firstFit.current) {
      firstFit.current = false;
      return;
    }
    const t = setTimeout(() => fitRef.current({ padding: 0.12, duration: 200 }), 120);
    return () => clearTimeout(t);
  }, [hidden]);

  // Highlight the neighbourhood of the selection.
  const neighbours = useMemo(() => {
    if (!graph || !selected) return null;
    if (selected.type === "edge") return new Set([selected.edge.from, selected.edge.to]);
    const ids = new Set([selected.node.id]);
    for (const e of graph.edges) {
      if (hidden.has(e.kind)) continue;
      if (e.from === selected.node.id) ids.add(e.to);
      if (e.to === selected.node.id) ids.add(e.from);
    }
    return ids;
  }, [graph, selected, hidden]);

  const nodes = useMemo(
    () =>
      laidOut.nodes.map((n) =>
        n.type === "arch"
          ? { ...n, position: moved[n.id] ?? n.position, data: { ...(n.data as CanvasNodeData), selected: selected?.type === "node" && selected.node.id === n.id, dimmed: !!neighbours && !neighbours.has(n.id) } }
          : n,
      ),
    [laidOut, selected, neighbours, moved],
  );
  const edges = useMemo(
    () =>
      laidOut.edges.map((e) => {
        const active = !neighbours || (neighbours.has(e.source) && neighbours.has(e.target) && (selected?.type !== "edge" || selected.edge.id === e.id));
        return { ...e, style: { ...e.style, opacity: active ? 1 : 0.12 }, labelStyle: { ...e.labelStyle, opacity: active ? 1 : 0 } };
      }),
    [laidOut, neighbours, selected],
  );

  const kinds = useMemo(() => [...new Set(graph?.edges.map((e) => e.kind) ?? [])].sort(), [graph]);
  // Opening a capability selects it: the levels below are scoped by the selection, not by focus.
  const open = (to: { level: string; focus?: string }) => {
    if (to.level === "system" && to.focus) setCapability(to.focus);
    window.location.hash = href("architecture", { level: to.level, focus: to.level === "system" ? undefined : to.focus });
  };
  const drill = (n: ArchNode) => n.drill && open(n.drill);

  return (
    <div className="flex h-[calc(100vh-150px)] min-h-[520px] flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <nav aria-label="Abstraction level" className="flex items-center gap-1 text-[13px]">
          {(graph?.breadcrumbs ?? [{ label: "Landscape", level: "landscape" }]).map((c, i, all) => (
            <span key={c.level} className="flex items-center gap-1">
              {i > 0 && <span className="text-zinc-400">›</span>}
              {i === all.length - 1 ? (
                <span className="font-semibold">{c.label}</span>
              ) : (
                <a
                  href={href("architecture", { level: c.level, focus: c.level === "system" ? undefined : c.focus })}
                  onClick={(e) => {
                    e.preventDefault();
                    open(c);
                  }}
                  className="text-blue-700 hover:underline dark:text-blue-300"
                >
                  {c.label}
                </a>
              )}
            </span>
          ))}
        </nav>
        <Pill tone="info">{{ landscape: "1 · landscape", system: "2 · capability", repository: "3 · components", component: "4 · code", requests: "3 · requests" }[level] ?? level}</Pill>
        <span className="ml-auto flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
          {kinds.map((k) => (
            <label key={k} className="flex cursor-pointer items-center gap-1">
              <input
                type="checkbox"
                checked={!hidden.has(k)}
                onChange={(e) => setHidden((h) => { const next = new Set(h); if (e.target.checked) next.delete(k); else next.add(k); return next; })}
              />
              <span className="inline-block h-0.5 w-4" style={{ background: kindColor[k] }} />
              {kindLabel[k] ?? k}
            </label>
          ))}
        </span>
      </div>
      {error && <ErrorBox message={error} />}
      <div className="grid min-h-0 flex-1 gap-3 lg:grid-cols-[minmax(0,1fr)_340px]">
        <div className="relative min-h-[420px] overflow-hidden rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
          {loading && !graph && <div className="absolute inset-0 grid place-items-center text-zinc-500">Laying out…</div>}
          {graph && graph.nodes.length === 0 && <div className="absolute inset-0 grid place-items-center text-zinc-500">Nothing to draw at this level for the selected scope.</div>}
          <ReactFlow
            key={`${level}|${focus ?? ""}|${graph ? "ready" : "loading"}`}
            fitView
            fitViewOptions={{ padding: 0.12 }}
            onInit={(instance) => {
              // Large graphs: never fit below a readable zoom; the minimap and panning cover the rest.
              setTimeout(() => {
                if (instance.getZoom() < 0.5) instance.zoomTo(0.5);
              }, 60);
            }}
            zoomOnDoubleClick={false}
            nodes={nodes}
            onNodesChange={onNodesChange}
            edges={edges}
            nodeTypes={nodeTypes}
            colorMode="system"
            nodesConnectable={false}
            nodesDraggable
            minZoom={0.1}
            maxZoom={2}
            proOptions={{ hideAttribution: true }}
            onNodeClick={(_, n) => {
              if (n.type !== "arch") return;
              const node = (n.data as CanvasNodeData).node;
              // Our own double-click detection: React Flow's drag handling can retarget the native dblclick.
              const now = Date.now();
              if (lastClick.current.id === n.id && now - lastClick.current.at < 350) {
                lastClick.current = { id: "", at: 0 };
                drill(node);
                return;
              }
              lastClick.current = { id: n.id, at: now };
              setSelected({ type: "node", node });
            }}
            onNodeDoubleClick={(_, n) => n.type === "arch" && drill((n.data as CanvasNodeData).node)}
            onEdgeClick={(_, e) => setSelected({ type: "edge", edge: (e.data as { edge: ArchEdge }).edge })}
            onPaneClick={() => setSelected(null)}
          >
            <Background gap={20} size={1} />
            <Controls showInteractive={false} />
            <MiniMap pannable zoomable nodeColor={(n) => (n.type === "group" ? "transparent" : kindColor.call)} className="!hidden sm:!block" />
          </ReactFlow>
        </div>
        <aside className="min-h-0 overflow-y-auto rounded-lg border border-zinc-200 bg-white p-3 text-[13px] dark:border-zinc-800 dark:bg-zinc-900">
          {selected?.type === "node" ? (
            <NodePanel node={selected.node} onDrill={() => drill(selected.node)} graph={graph} />
          ) : selected?.type === "edge" ? (
            <EdgePanel edge={selected.edge} graph={graph} />
          ) : (
            <Legend graph={graph} />
          )}
        </aside>
      </div>
    </div>
  );
}

function NodePanel({ node: n, onDrill, graph }: { node: ArchNode; onDrill: () => void; graph?: Graph }) {
  const style = categoryStyle[n.category] ?? categoryStyle.member;
  const incoming = graph?.edges.filter((e) => e.to === n.id).length ?? 0;
  const outgoing = graph?.edges.filter((e) => e.from === n.id).length ?? 0;
  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2">
        <span className={`inline-block size-2.5 rounded-sm ${style.strip}`} />
        <span className="text-xs uppercase tracking-wide text-zinc-500">{style.name}</span>
      </div>
      <div className="text-base font-semibold">{n.label}</div>
      {n.sublabel && <div className="text-zinc-500">{n.sublabel}</div>}
      {n.symbolKey && <Key value={n.symbolKey} />}
      <div className="flex flex-wrap gap-1">
        {n.badges.filter(Boolean).map((b) => (
          <Pill key={b}>{b}</Pill>
        ))}
      </div>
      <div className="text-xs text-zinc-500">
        {incoming} incoming · {outgoing} outgoing connection{outgoing === 1 ? "" : "s"}
      </div>
      <div className="flex flex-wrap gap-2 pt-1">
        {n.drill && <Button onClick={onDrill}>Open {{ system: "capability", repository: "components", requests: "routes" }[n.drill.level] ?? "code"} →</Button>}
        {n.symbolKey && n.category !== "datastore" && n.category !== "channel" && (
          <a href={symbolHref(n.symbolKey)} className="rounded-md border border-zinc-300 px-3 py-1.5 text-[13px] font-semibold hover:bg-zinc-50 dark:border-zinc-700 dark:hover:bg-zinc-800">
            Open in Explore
          </a>
        )}
      </div>
    </div>
  );
}

function EdgePanel({ edge: e, graph }: { edge: ArchEdge; graph?: Graph }) {
  const name = (id: string) => graph?.nodes.find((n) => n.id === id)?.label ?? id;
  const c = certaintyText[e.certainty];
  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2">
        <span className="inline-block h-0.5 w-5" style={{ background: kindColor[e.kind] }} />
        <span className="text-xs uppercase tracking-wide text-zinc-500">{kindLabel[e.kind] ?? e.kind}</span>
        <Pill tone={c.tone} title={c.title}>
          {c.label}
        </Pill>
      </div>
      <div className="font-semibold">
        {name(e.from)} → {name(e.to)}
      </div>
      <div className="text-zinc-500">{e.label}</div>
      {e.kind === "sameName" ? (
        <p className="text-xs text-zinc-500">These resources share a name but have different keys (different binding, namespace or entity type). Nothing in the code proves they are the same resource.</p>
      ) : (
        <>
          <div className="text-xs text-zinc-500">
            {e.count} underlying fact{e.count === 1 ? "" : "s"}
            {e.facts.length < e.count ? ` (first ${e.facts.length} shown)` : ""}
          </div>
          <ul className="space-y-2.5">
            {e.facts.map((f, i) => (
              <li key={i} className="rounded-md border border-zinc-200 p-2 dark:border-zinc-800">
                <div className="flex flex-wrap items-center gap-1.5">
                  <Mono className="text-xs">{f.relation}</Mono>
                  <ResolutionPill resolution={f.resolution} reason={f.unresolvedReason} />
                  <span className="text-[11px] text-zinc-500">
                    {f.derivation}
                    {f.rule ? ` · ${f.rule}` : ""}
                  </span>
                </div>
                <div className="mt-1 text-xs">
                  <SymbolLink symbol={f.from} />
                </div>
                <div className="text-xs text-zinc-400">→</div>
                <div className="text-xs">{f.to ? <SymbolLink symbol={f.to} /> : <span className="text-zinc-500">unknown target</span>}</div>
                <div className="mt-1">
                  <EvidenceLinks evidence={f.evidence} />
                </div>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

function Legend({ graph }: { graph?: Graph }) {
  return (
    <div className="space-y-3">
      <div>
        <div className="font-semibold">{graph?.title ?? "Architecture"}</div>
        <p className="text-xs text-zinc-500">Click a node or connection for details and evidence. Double-click a node marked ⤢ to go one level down.</p>
      </div>
      <div>
        <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-zinc-500">Levels</div>
        <ol className="list-decimal space-y-0.5 pl-5 text-xs">
          <li>Landscape — every capability, linked where they share or call into each other</li>
          <li>Capability — its apps, APIs and services, data stores, channels</li>
          <li>Components — types, modules and SQL objects of one repository</li>
          <li>Code — members of one component and what they touch</li>
        </ol>
      </div>
      <div>
        <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-zinc-500">Line style = certainty</div>
        <ul className="space-y-1 text-xs">
          {(Object.keys(certaintyText) as ArchEdge["certainty"][]).map((k) => (
            <li key={k} className="flex items-center gap-2">
              <svg width="34" height="6" aria-hidden="true">
                <line x1="0" y1="3" x2="34" y2="3" stroke="currentColor" strokeWidth="2" strokeDasharray={certaintyDash[k]} />
              </svg>
              <span className="font-medium">{certaintyText[k].label}</span>
              <span className="text-zinc-500">{certaintyText[k].title}</span>
            </li>
          ))}
        </ul>
      </div>
      {graph?.notes.map((n) => (
        <p key={n} className="text-xs text-zinc-500">
          {n}
        </p>
      ))}
    </div>
  );
}
