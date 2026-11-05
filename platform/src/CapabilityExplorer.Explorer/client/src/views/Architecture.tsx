import { useEffect, useMemo, useRef, useState } from "react";
import { Link2, Pencil, ScanSearch, Workflow } from "lucide-react";
import { HttpRecommendationInspector } from "../components/HttpRecommendations";
import { IntegrationSuggestionInspector } from "../components/IntegrationSuggestionInspector";
import type { ArchNode, ArchEdge, Graph, ConnectionDraft, ManualConnection, ManualConnectionSnapshot } from "../architecture-types";
import { ArchitectureConnectionEditor, connectionKinds, graphIconButton } from "../components/ArchitectureConnectionEditor";
import { LiveArchitectureCanvas } from "../components/live-architecture";
import { EvidenceLinks } from "../components/source";
import { ResolutionPill, SymbolLink } from "../components/symbols";
import { Button, ErrorBox, Key, Mono, Pill, type Tone } from "../components/ui";
import { usePublishFocus } from "../focus";
import { href, symbolHref, useApi, useSelection } from "../state";

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
  navigation: "#0d9488",
  schedule: "#d97706",
  dependency: "#64748b",
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
  navigation: "navigation",
  schedule: "schedule / cron",
  dependency: "dependency",
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
  human: { label: "manual", tone: "info", title: "Manually connected capabilities; not proven by extractor output" },
  proven: { label: "proven", tone: "ok", title: "Every underlying fact is resolved" },
  static: { label: "static target", tone: "info", title: "Static target proven; the runtime target is not (interface dispatch, DI)" },
  bound: { label: "bound", tone: "ok", title: "Bound by the service map to a declared endpoint (human-derived link; extractor output unchanged)" },
  broken: { label: "broken", tone: "bad", title: "Bound by the service map, but no such endpoint exists in the selected revision (for example a renamed route)" },
  unbound: { label: "unbound", tone: "warn", title: "Target known only by a configuration name (connection string, env var, config key)" },
  candidate: { label: "candidate", tone: "warn", title: "A match by route or name, or one of several possible targets" },
  unresolved: { label: "unresolved", tone: "bad", title: "Target not statically known" },
};

// ---------------------------------------------------------------- view

export function ArchitectureView({ params }: { params: URLSearchParams }) {
  const { capability, scope } = useSelection();
  const level = params.get("level") ?? (capability ? "system" : "landscape");
  const focus = params.get("focus") ?? undefined;
  const selection = new URLSearchParams(scope);
  selection.delete("g");
  return <Canvas key={`${selection}|${level}|${focus ?? ""}`} level={level} focus={focus} />;
}

type Selected = { type: "node"; node: ArchNode } | { type: "edge"; edge: ArchEdge } | null;

let rememberedHidden = new Set<string>(["declares"]);

function Canvas({ level, focus }: { level: string; focus?: string }) {
  const { scope, setCapability, capability, meta } = useSelection();
  const { data, error, loading, reload } = useApi<Graph>(`/api/architecture?level=${level}${focus ? `&focus=${encodeURIComponent(focus)}` : ""}&${scope}`);
  const connections = useApi<ManualConnectionSnapshot>("/api/architecture/connections");
  const [editor, setEditor] = useState<ConnectionDraft | null>(null);
  const [recommendationsOpen, setRecommendationsOpen] = useState(false);
  const [suggestionsOpen, setSuggestionsOpen] = useState(false);
  const [hidden, setHidden] = useState<Set<string>>(rememberedHidden);
  const [selected, setSelected] = useState<Selected>(null);
  const graph = loading ? undefined : data;
  const hiddenEdgeIds = useMemo(() => new Set(graph?.edges.filter(edge => hidden.has(edge.kind)).map(edge => edge.id) ?? []), [graph, hidden]);
  usePublishFocus(selected?.type === "node" && graph
    ? { kind: selected.node.category, label: selected.node.label, key: selected.node.symbolKey } : null);
  const lastClick = useRef({ id: "", at: 0 });
  useEffect(() => { rememberedHidden = hidden; }, [hidden]);
  const snapshotKey = `${scope}|${level}|${focus ?? ""}`;

  const kinds = useMemo(() => [...new Set(graph?.edges.map((e) => e.kind) ?? [])].sort(), [graph]);
  // Opening a capability selects it: the levels below are scoped by the selection, not by focus.
  const open = (to: { level: string; focus?: string }) => {
    if (to.level === "system" && to.focus) setCapability(to.focus);
    window.location.hash = href("architecture", { level: to.level, focus: to.level === "system" ? undefined : to.focus });
  };
  const drill = (n: ArchNode) => n.drill && open(n.drill);
  const connect = (existing?: ManualConnection) => {
    if (existing) { setEditor(existing); return; }
    const node = selected?.type === "node" ? selected.node : undefined;
    const edge = selected?.type === "edge" ? selected.edge : node && node.category !== "capability"
      ? graph?.edges.find(item => item.to === node.id) : undefined;
    const fact = edge?.facts[0];
    const repository = fact?.from.repositoryId ?? node?.repositoryId;
    const owner = node?.category === "capability" ? node.id.slice(4)
      : meta.capabilities.find(item => item.repositories.includes(repository ?? ""))?.id ?? capability ?? "";
    const from = owner || meta.capabilities[0]?.id || "";
    setRecommendationsOpen(false);
    setSuggestionsOpen(false);
    setEditor({ fromCapabilityId: from, toCapabilityId: meta.capabilities.find(item => item.id !== from)?.id ?? "",
      kind: connectionKinds.some(([kind]) => kind === edge?.kind) ? edge!.kind : "dependency",
      sourceRepositoryId: repository, sourceKey: fact?.from.key ?? (repository ? node?.symbolKey : undefined) });
  };
  const saved = () => { setEditor(null); setSelected(null); connections.reload(); reload(); };
  const capabilityName = (id: string) => meta.capabilities.find(item => item.id === id)?.name ?? id;
  const selectedHttpFact = selected?.type === "edge" ? selected.edge.facts.find(fact => fact.relation === "REQUESTS")
    : selected?.type === "node" && !selected.node.members ? graph?.edges.find(edge => edge.to === selected.node.id && edge.kind === "http")?.facts.find(fact => fact.relation === "REQUESTS") : undefined;
  const recommendationFocus = selectedHttpFact?.to?.key.split("|")[1];

  return (
    <div className={`architecture-workspace ${editor || recommendationsOpen || suggestionsOpen ? "is-connecting " : ""}flex flex-col gap-2`}>
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="secondary" onClick={() => { setCapability(""); window.location.hash = href("architecture", { level: "system" }); }}>All repositories</Button>
        <Button variant="secondary" onClick={() => { setCapability(""); window.location.hash = href("architecture", { level: "landscape" }); }}>Capability landscape</Button>
        <button type="button" className={graphIconButton} title="Connect capabilities" aria-label="Connect capabilities" disabled={meta.capabilities.length < 2} onClick={() => connect()}><Link2 size={17} /></button>
        <button type="button" className={graphIconButton} title="Recommend capabilities" aria-label="Recommend capabilities" onClick={() => { setEditor(null); setSuggestionsOpen(false); setRecommendationsOpen(open => !open); }}><ScanSearch size={17} /></button>
        <button type="button" className={graphIconButton} title="Validate integrations with model" aria-label="Validate integrations with model" disabled={!meta.interpretation.enabled || meta.capabilities.length < 2}
          onClick={() => { setEditor(null); setRecommendationsOpen(false); setSuggestionsOpen(open => !open); }}><Workflow size={17} /></button>
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
      <div className="architecture-panels grid min-h-0 flex-1 gap-3">
        <div className="architecture-canvas relative min-h-[420px] min-w-0 overflow-hidden rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
          {loading && !graph && <div className="absolute inset-0 grid place-items-center text-zinc-500">Laying out…</div>}
          {graph && graph.nodes.length === 0 && <div className="absolute inset-0 grid place-items-center text-zinc-500">Nothing to draw at this level for the selected scope.</div>}
          {graph && graph.nodes.length > 0 && <LiveArchitectureCanvas
            graph={graph} hiddenEdgeIds={hiddenEdgeIds} snapshotKey={snapshotKey}
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
              if (node.category === "host" || node.category === "unmatched") setRecommendationsOpen(true);
            }}
            onNodeDrill={item => { const node = graph?.nodes.find(node => node.id === item.id); if (node) drill(node); }}
            onEdgeSelect={item => {
              const edge = graph?.edges.find(edge => edge.id === item.id);
              if (!edge) return;
              setSelected({ type: "edge", edge });
              setSuggestionsOpen(false);
              setRecommendationsOpen(!edge.id.startsWith("automatic:") && edge.kind === "http"
                && ["candidate", "unresolved", "unbound", "broken"].includes(edge.certainty));
            }}
            onClearSelection={() => setSelected(null)}
          />}

        </div>
        <aside className="min-h-0 overflow-y-auto rounded-lg border border-zinc-200 bg-white p-3 text-[13px] dark:border-zinc-800 dark:bg-zinc-900">
          {suggestionsOpen ? <IntegrationSuggestionInspector scope={scope} onClose={() => setSuggestionsOpen(false)} /> : recommendationsOpen ? <HttpRecommendationInspector scope={scope} focus={recommendationFocus} onClose={() => setRecommendationsOpen(false)} /> : editor ? <ArchitectureConnectionEditor key={editor.id ?? `${editor.fromCapabilityId}|${editor.sourceKey ?? "new"}`} draft={editor} capabilities={meta.capabilities}
            snapshot={connections.data} onClose={() => setEditor(null)} onSaved={saved} onReload={connections.reload} /> : selected?.type === "node" ? (
            <NodePanel node={selected.node} onDrill={() => drill(selected.node)} graph={graph} />
          ) : selected?.type === "edge" ? (
            <EdgePanel edge={selected.edge} graph={graph} />
          ) : (
            <Legend graph={graph} />
          )}
          {!editor && selected && meta.capabilities.length > 1 && <div className="mt-3 border-t border-zinc-200 pt-3 dark:border-zinc-700">
            <Button variant="secondary" onClick={() => {
              const id = selected.type === "edge" ? selected.edge.manualConnectionId : undefined;
              connect(connections.data?.connections.find(connection => connection.id === id));
            }}><span className="inline-flex items-center gap-1.5"><Link2 size={15} aria-hidden="true" />{selected.type === "edge" && selected.edge.manualConnectionId ? "Edit connection" : "Connect to capability"}</span></Button>
          </div>}
          {!editor && connections.data && connections.data.connections.length > 0 && <section className="mt-4 border-t border-zinc-200 pt-3 dark:border-zinc-700" aria-label="Manual connections">
            <h2 className="mb-2 text-xs font-semibold text-zinc-500">Manual connections</h2>
            <ul className="space-y-2">{connections.data.connections.map(connection => <li key={connection.id} className="flex min-w-0 items-start gap-2">
              <div className="min-w-0 flex-1 break-words"><div>{capabilityName(connection.fromCapabilityId)} → {capabilityName(connection.toCapabilityId)}</div><span className="text-xs text-zinc-500">{connectionKinds.find(([kind]) => kind === connection.kind)?.[1] ?? connection.kind}</span></div>
              <button type="button" className={graphIconButton} title="Edit connection" aria-label={`Edit ${connection.kind} connection from ${capabilityName(connection.fromCapabilityId)} to ${capabilityName(connection.toCapabilityId)}`} onClick={() => connect(connection)}><Pencil size={14} /></button>
            </li>)}</ul>
          </section>}
          {!editor && connections.error && <div role="alert" className="mt-3 break-words text-xs text-red-700 dark:text-red-300">{connections.error}</div>}
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
      {e.note && <div className="break-words text-zinc-600 dark:text-zinc-300">{e.note}</div>}
      {e.manualConnectionId ? <Pill tone="info">Manual connection</Pill> : e.kind === "sameName" ? (
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
