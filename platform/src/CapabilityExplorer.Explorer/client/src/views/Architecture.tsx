import { useEffect, useMemo, useRef, useState } from "react";
import type { ArchNode, ArchEdge, Graph } from "../architecture-types";
import { LiveArchitectureCanvas } from "../components/live-architecture";
import { EvidenceLinks } from "../components/source";
import { ResolutionPill, SymbolLink } from "../components/symbols";
import { Button, ErrorBox, Key, Mono, Pill, type Tone } from "../components/ui";
import { usePublishFocus } from "../focus";
import { href, symbolHref, useApi, useSelection } from "../state";
import { ArchitectureFilterControls, type ArchitectureKindFacet } from "../components/architecture-filter-controls";
import { ArchitectureRecoveryLists } from "../components/architecture-recovery-lists";

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
  host: { box: "border-teal-300 border-dashed dark:border-teal-800", strip: "bg-teal-400", name: "external host" },
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
  "human-assertion": "#a855f7",
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
  "human-assertion": "human assertion",
  shares: "shares repository",
};

const certaintyDash: Record<ArchEdge["certainty"], string | undefined> = {
  human: "8 3 2 3",
  proven: undefined,
  static: undefined,
  bound: undefined,
  broken: "3 3",
  unbound: "6 4",
  candidate: "10 5",
  unresolved: "2 4",
};

const certaintyText: Record<ArchEdge["certainty"], { label: string; tone: Tone; title: string }> = {
  human: { label: "human assertion", tone: "info", title: "Reviewed assertion of intended meaning with a self-reported reviewer label; no observed runtime value equality is implied" },
  proven: { label: "proven", tone: "ok", title: "Every underlying fact is resolved" },
  static: { label: "static target", tone: "info", title: "Static target proven; the runtime target is not (interface dispatch, DI)" },
  bound: { label: "bound", tone: "ok", title: "Bound by the service map to a declared endpoint (human-derived link; extractor output unchanged)" },
  broken: { label: "broken", tone: "bad", title: "Bound by the service map, but no such endpoint exists in the selected revision (for example a renamed route)" },
  unbound: { label: "unbound", tone: "warn", title: "Target known only by a configuration name (connection string, env var, config key)" },
  candidate: { label: "candidate", tone: "warn", title: "A match by route or name, or one of several possible targets" },
  unresolved: { label: "unresolved", tone: "bad", title: "Target not statically known" },
};

const MAX_NODE_BUDGET = 100;
const MAX_EDGE_BUDGET = 100;
const MAX_PINS = 20;
const MAX_OMITTED_NODES = 200;
const DEFAULT_HIDDEN_KINDS = ["declares"];

interface ArchitectureFilters {
  nodeBudget: number;
  edgeBudget: number;
  query: string;
  hideKinds: string[];
  include: string[];
}

function sortedUnique(values: string[]): string[] {
  return [...new Set(values)].sort();
}

function sameStrings(left: string[] | undefined, right: string[]): boolean {
  return left !== undefined && JSON.stringify(sortedUnique(left)) === JSON.stringify(right);
}

function graphMatchesFilters(graph: Graph | undefined, filters: ArchitectureFilters): graph is Graph {
  const budget = graph?.budget;
  const applied = graph?.filters;
  return !!budget && !!applied &&
    budget.nodeBudget === filters.nodeBudget &&
    budget.edgeBudget === filters.edgeBudget &&
    (applied.query ?? "") === filters.query &&
    sameStrings(applied.hideKinds, filters.hideKinds) &&
    sameStrings(applied.include, filters.include);
}

// ---------------------------------------------------------------- view

export function ArchitectureView({ params }: { params: URLSearchParams }) {
  const { capability, scope } = useSelection();
  const level = params.get("level") ?? (capability ? "system" : "landscape");
  const focus = params.get("focus") ?? undefined;
  return <Canvas key={`${scope}|${level}|${focus ?? ""}`} level={level} focus={focus} />;
}

type Selected = { type: "node"; node: ArchNode } | { type: "edge"; edge: ArchEdge } | null;

let rememberedHidden = new Set<string>(DEFAULT_HIDDEN_KINDS);

function Canvas({ level, focus }: { level: string; focus?: string }) {
  const { scope, setCapability } = useSelection();
  const [queryInput, setQueryInput] = useState("");
  const [nodeBudgetInput, setNodeBudgetInput] = useState(String(MAX_NODE_BUDGET));
  const [edgeBudgetInput, setEdgeBudgetInput] = useState(String(MAX_EDGE_BUDGET));
  const [hiddenKinds, setHiddenKinds] = useState<Set<string>>(rememberedHidden);
  const [pins, setPins] = useState<string[]>([]);
  const [filters, setFilters] = useState<ArchitectureFilters>({
    nodeBudget: MAX_NODE_BUDGET,
    edgeBudget: MAX_EDGE_BUDGET,
    query: "",
    hideKinds: sortedUnique([...rememberedHidden]),
    include: [],
  });
  const [filterError, setFilterError] = useState("");
  const [awaitingRefresh, setAwaitingRefresh] = useState(false);
  const [refreshStarted, setRefreshStarted] = useState(false);
  const [selected, setSelected] = useState<Selected>(null);
  const params = useMemo(() => {
    const search = new URLSearchParams({ level, nodeBudget: String(filters.nodeBudget), edgeBudget: String(filters.edgeBudget) });
    if (focus) search.set("focus", focus);
    if (filters.query) search.set("q", filters.query);
    for (const kind of filters.hideKinds) search.append("hideKind", kind);
    for (const id of filters.include) search.append("include", id);
    return `${search.toString()}&${scope}`;
  }, [level, focus, scope, filters]);
  const { data, error, loading, reload } = useApi<Graph>(`/api/architecture?${params}`);
  const matchesAppliedFilters = graphMatchesFilters(data, filters);
  const waitingForResponse = awaitingRefresh || loading || (!!data && !matchesAppliedFilters);
  const graph = waitingForResponse ? undefined : data;
  const budget = graph?.budget;
  const requestError = error ?? (!awaitingRefresh && !loading && data && !matchesAppliedFilters
    ? "The architecture response does not match the applied filters. Apply the filters again to retry."
    : undefined);
  usePublishFocus(selected?.type === "node" && graph
    ? { kind: selected.node.category, label: selected.node.label, key: selected.node.symbolKey } : null);
  const lastClick = useRef({ id: "", at: 0 });
  useEffect(() => {
    if (awaitingRefresh && loading) setRefreshStarted(true);
    else if (awaitingRefresh && refreshStarted && !loading) {
      setAwaitingRefresh(false);
      setRefreshStarted(false);
    }
  }, [awaitingRefresh, loading, refreshStarted]);
  const snapshotKey = JSON.stringify([scope, level, focus ?? "", filters.nodeBudget, filters.edgeBudget, filters.query, filters.hideKinds, filters.include]);

  const kinds: ArchitectureKindFacet[] = graph?.facets?.kinds ?? [];
  const selectedNodeId = selected?.type === "node" ? selected.node.id : undefined;
  const applyFilters = (extraPins: string[] = []) => {
    const nodeBudget = Number(nodeBudgetInput);
    const edgeBudget = Number(edgeBudgetInput);
    const query = queryInput.trim();
    const hideKinds = sortedUnique([...hiddenKinds]);
    const include = sortedUnique([...pins, ...extraPins, ...(selectedNodeId ? [selectedNodeId] : [])]);
    let validationError = "";
    if (!Number.isInteger(nodeBudget) || nodeBudget < 1 || nodeBudget > MAX_NODE_BUDGET) {
      validationError = `Node budget must be between 1 and ${MAX_NODE_BUDGET}.`;
    } else if (!Number.isInteger(edgeBudget) || edgeBudget < 1 || edgeBudget > MAX_EDGE_BUDGET) {
      validationError = `Edge budget must be between 1 and ${MAX_EDGE_BUDGET}.`;
    } else if (query.length > 200) {
      validationError = "Find elements must be 200 characters or fewer.";
    } else if (hideKinds.length > 32) {
      validationError = "Select no more than 32 hidden kinds.";
    } else if (include.length > MAX_PINS || include.some(id => id.length > 4096)) {
      validationError = `Use no more than ${MAX_PINS} pins, each with an ID of at most 4096 characters.`;
    } else if (include.length > nodeBudget) {
      validationError = "Node budget must be at least the number of pinned elements.";
    }
    if (validationError) {
      setFilterError(validationError);
      return;
    }
    setFilterError("");
    setQueryInput(query);
    setPins(include);
    setFilters({ nodeBudget, edgeBudget, query, hideKinds, include });
    rememberedHidden = new Set(hideKinds);
    setAwaitingRefresh(true);
    setRefreshStarted(false);
    reload();
  };
  const resetFilters = () => {
    const hideKinds = sortedUnique(DEFAULT_HIDDEN_KINDS);
    const include = selectedNodeId ? [selectedNodeId] : [];
    setQueryInput("");
    setNodeBudgetInput(String(MAX_NODE_BUDGET));
    setEdgeBudgetInput(String(MAX_EDGE_BUDGET));
    setHiddenKinds(new Set(hideKinds));
    setPins(include);
    setFilterError("");
    setFilters({ nodeBudget: MAX_NODE_BUDGET, edgeBudget: MAX_EDGE_BUDGET, query: "", hideKinds, include });
    rememberedHidden = new Set(hideKinds);
    setAwaitingRefresh(true);
    setRefreshStarted(false);
    reload();
  };
  // Opening a capability selects it: the levels below are scoped by the selection, not by focus.
  const open = (to: { level: string; focus?: string }) => {
    if (to.level === "system" && to.focus) setCapability(to.focus);
    window.location.hash = href("architecture", { level: to.level, focus: to.level === "system" ? undefined : to.focus });
  };
  const drill = (n: ArchNode) => n.drill && open(n.drill);

  return (
    <div className="architecture-workspace flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="secondary" onClick={() => { setCapability(""); window.location.hash = href("architecture", { level: "system" }); }}>All repositories</Button>
        <Button variant="secondary" onClick={() => { setCapability(""); window.location.hash = href("architecture", { level: "landscape" }); }}>Capability landscape</Button>
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
      </div>
      <ArchitectureFilterControls
        query={queryInput} onQueryChange={setQueryInput}
        nodeBudget={nodeBudgetInput} onNodeBudgetChange={setNodeBudgetInput}
        edgeBudget={edgeBudgetInput} onEdgeBudgetChange={setEdgeBudgetInput}
        hiddenKinds={hiddenKinds} onHiddenKindsChange={setHiddenKinds}
        kinds={kinds} pins={pins} selectedNodeId={selectedNodeId} error={filterError}
        maxNodeBudget={MAX_NODE_BUDGET} maxEdgeBudget={MAX_EDGE_BUDGET}
        onApply={() => applyFilters()} onReset={resetFilters}
        onUnpin={id => setPins(current => current.filter(pin => pin !== id))}
      />
      {requestError && <ErrorBox message={requestError} />}
      <div className="architecture-panels grid min-h-0 flex-1 gap-3">
        <div role="region" aria-label="Architecture graph" data-input-identity={snapshotKey}
          data-node-budget={filters.nodeBudget} data-edge-budget={filters.edgeBudget}
          data-query={filters.query} data-hide-kinds={JSON.stringify(filters.hideKinds)}
          data-included-node-ids={JSON.stringify(filters.include)}
          className="architecture-canvas relative min-h-[420px] min-w-0 overflow-hidden rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
          {waitingForResponse && <div className="absolute inset-0 z-10 grid place-items-center bg-white/80 text-zinc-500 dark:bg-zinc-950/80">Preparing filtered graph…</div>}
          {graph && graph.nodes.length === 0 && <div className="absolute inset-0 grid place-items-center text-zinc-500">Nothing to draw at this level for the selected scope.</div>}
          {budget && <div className="absolute left-3 top-3 z-10 flex flex-wrap gap-x-3 rounded-md border border-zinc-300 bg-white/90 px-2 py-1 text-xs dark:border-zinc-700 dark:bg-zinc-950/90" data-architecture-counts
            data-total-nodes={budget.totalNodes} data-matched-nodes={budget.matchedNodes} data-shown-nodes={budget.shownNodes}
            data-total-edges={budget.totalEdges} data-matched-edges={budget.matchedEdges} data-shown-edges={budget.shownEdges}>
            <span>Nodes: {budget.totalNodes} total · {budget.matchedNodes} matched · {budget.shownNodes} shown</span>
            <span>Edges: {budget.totalEdges} total · {budget.matchedEdges} matched · {budget.shownEdges} shown</span>
          </div>}
          {budget && (budget.nodesTruncated || budget.edgesTruncated) && <div className="absolute left-3 top-12 z-10 rounded-md border border-amber-300 bg-amber-50 px-2 py-1 text-xs text-amber-900 dark:border-amber-700 dark:bg-amber-950/70 dark:text-amber-200">
            {budget.nodesTruncated && "Some matching nodes are omitted; search, pin an omitted node, or drill down."}
            {budget.nodesTruncated && budget.edgesTruncated && " "}
            {budget.edgesTruncated && "Some matching relationships are omitted by the edge budget."}
          </div>}
          {graph?.budget?.missingPins.length ? <div className="absolute bottom-3 left-3 z-10 rounded-md border border-amber-300 bg-amber-50 px-2 py-1 text-xs text-amber-900 dark:border-amber-700 dark:bg-amber-950/70 dark:text-amber-200">Pins not found in this graph: {graph.budget.missingPins.join(", ")}</div> : null}
          {graph && graph.nodes.length > 0 && <LiveArchitectureCanvas
            graph={graph} snapshotKey={snapshotKey}
            selectedId={selected?.type === "node" ? selected.node.id : selected?.edge.id}
            onNodeSelect={item => {
              const node = graph?.nodes.find(node => node.id === item.id);
              if (!node) return;
              const now = Date.now();
              if (lastClick.current.id === node.id && now - lastClick.current.at < 350) {
                lastClick.current = { id: "", at: 0 }; drill(node); return;
              }
              lastClick.current = { id: node.id, at: now };
              setSelected({ type: "node", node });
            }}
            onNodeDrill={item => { const node = graph?.nodes.find(node => node.id === item.id); if (node) drill(node); }}
            onEdgeSelect={item => { const edge = graph?.edges.find(edge => edge.id === item.id); if (edge) setSelected({ type: "edge", edge }); }}
            onClearSelection={() => setSelected(null)}
          />}
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
      {graph && <ArchitectureRecoveryLists
        graph={graph} selectedNodeId={selectedNodeId} pins={pins}
        maxPins={MAX_PINS} maxOmittedNodes={MAX_OMITTED_NODES}
        onSelect={node => setSelected({ type: "node", node })}
        onDrill={drill}
        onPin={id => applyFilters([id])}
      />}
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
      {n.members && <MemberList members={n.members} />}
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

/** Folded members grouped under the service that touches them; a member used by two services appears under both. */
function MemberList({ members }: { members: NonNullable<ArchNode["members"]> }) {
  const byService = new Map<string, { m: (typeof members)[number]; label: string; paths: string[]; bound: string[] }[]>();
  for (const m of members) {
    // One row per member and service: a store that is both read and written lists both, it is not two stores.
    const labels = new Map<string, { parts: string[]; paths: string[]; bound: string[] }>();
    for (const c of m.connections.length ? m.connections : [{ service: "(no connection)", label: "", paths: [], bound: [] }]) {
      const prev = labels.get(c.service) ?? { parts: [], paths: [], bound: [] };
      labels.set(c.service, { parts: [...prev.parts, c.label].filter(Boolean), paths: [...new Set([...prev.paths, ...c.paths])].sort(), bound: [...new Set([...prev.bound, ...c.bound])].sort() });
    }
    for (const [service, { parts, paths, bound }] of labels) {
      byService.set(service, [...(byService.get(service) ?? []), { m, label: parts.join(", "), paths, bound }]);
    }
  }
  return (
    <div className="max-h-[calc(100vh-20rem)] overflow-auto rounded-md border border-zinc-200 text-xs dark:border-zinc-800">
      {[...byService].sort(([a], [b]) => a.localeCompare(b)).map(([service, rows]) => (
        <div key={service}>
          <div className="sticky top-0 bg-zinc-100 px-2 py-1 font-semibold dark:bg-zinc-800">{service} <span className="font-normal text-zinc-500">({rows.length})</span></div>
          {rows.map(({ m, label, paths, bound }, i) => (
            <details key={i} className="border-b border-zinc-200 px-2 py-1 dark:border-zinc-800">
              <summary className="cursor-pointer truncate font-mono">{m.label} {label && <span className="text-zinc-500">· {label}</span>}</summary>
              <div className="space-y-1 py-1 pl-3 text-zinc-500">
                {m.detail && <div>{m.detail}</div>}
                {bound.length > 0 && <ul>{bound.map((v) => <li key={v} className="break-all">bound via {v}</li>)}</ul>}
                {paths.length > 0 && <ul className="font-mono text-zinc-700 dark:text-zinc-300">{paths.map((p) => <li key={p} className="break-all">{p}</li>)}</ul>}
                {m.status && <Pill>{m.status}</Pill>}
                {m.symbolKey && <Key value={m.symbolKey} />}
              </div>
            </details>
          ))}
        </div>
      ))}
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
