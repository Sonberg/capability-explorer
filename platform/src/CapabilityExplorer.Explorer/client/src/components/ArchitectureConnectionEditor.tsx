import { useDeferredValue, useId, useState } from "react";
import { Link2, Save, Trash2, X } from "lucide-react";
import { request, type CapabilitySummary, type SearchHit } from "../api";
import type { ConnectionDraft, ManualConnectionSnapshot } from "../architecture-types";
import { Button, inputClass } from "./ui";
import { symbolHref, useApi, useSelection } from "../state";

export const connectionKinds = [
  ["http", "HTTP"], ["message", "Messaging"], ["navigation", "Navigation"],
  ["data", "Data"], ["schedule", "Schedule / cron"], ["dependency", "Dependency"],
] as const;

export const graphIconButton = "inline-flex size-8 shrink-0 items-center justify-center rounded border border-zinc-300 bg-white text-zinc-700 hover:bg-zinc-100 disabled:opacity-40 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-200 dark:hover:bg-zinc-800";

export function ArchitectureConnectionEditor({ draft, capabilities, snapshot, onClose, onSaved, onReload }: {
  draft: ConnectionDraft;
  capabilities: CapabilitySummary[];
  snapshot?: ManualConnectionSnapshot;
  onClose: () => void;
  onSaved: () => void;
  onReload: () => void;
}) {
  const prefix = useId();
  const { scope } = useSelection();
  const [from, setFrom] = useState(draft.fromCapabilityId);
  const [to, setTo] = useState(draft.toCapabilityId);
  const defaultRepository = (capabilityId: string) => {
    const repositories = capabilities.find(capability => capability.id === capabilityId)?.repositories ?? [];
    return repositories.length === 1 ? repositories[0] : "";
  };
  const [sourceRepository, setSourceRepository] = useState(draft.sourceRepositoryId ?? defaultRepository(from));
  const [sourceKey, setSourceKey] = useState(draft.sourceKey ?? "");
  const [targetRepository, setTargetRepository] = useState(draft.targetRepositoryId ?? defaultRepository(to));
  const [targetKey, setTargetKey] = useState(draft.targetKey ?? "");
  const [kind, setKind] = useState(draft.kind);
  const [note, setNote] = useState(draft.note ?? "");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string>();
  const headers = { "Content-Type": "application/json", "X-CE-Architecture-Connections": "1" };
  const save = async () => {
    if (!snapshot) return;
    setPending(true); setError(undefined);
    try {
      await request<ManualConnectionSnapshot>(`/api/architecture/connections${draft.id ? `/${encodeURIComponent(draft.id)}` : ""}`, {
        method: draft.id ? "PUT" : "POST", headers, signal: AbortSignal.timeout(15000),
        body: JSON.stringify({ fromCapabilityId: from, toCapabilityId: to, kind, note,
          sourceRepositoryId: sourceRepository || undefined, sourceKey: sourceKey || undefined,
          targetRepositoryId: targetRepository || undefined, targetKey: targetKey || undefined,
          view: new URLSearchParams(scope).get("view") ?? undefined, expectedVersion: snapshot.version }),
      });
      onSaved();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Connection could not be saved.");
      onReload();
    } finally { setPending(false); }
  };
  const remove = async () => {
    if (!snapshot || !draft.id || !window.confirm("Remove this manual connection?")) return;
    setPending(true); setError(undefined);
    try {
      await request<ManualConnectionSnapshot>(`/api/architecture/connections/${encodeURIComponent(draft.id)}?expectedVersion=${snapshot.version}`, {
        method: "DELETE", headers, body: "{}", signal: AbortSignal.timeout(15000),
      });
      onSaved();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Connection could not be removed.");
      onReload();
    } finally { setPending(false); }
  };
  return <form className="space-y-3" aria-label="Manual capability connection" onSubmit={event => { event.preventDefault(); void save(); }}>
    <div className="flex items-center gap-2">
      <Link2 size={16} aria-hidden="true" />
      <h2 className="min-w-0 flex-1 text-sm font-semibold">{draft.id ? "Edit connection" : "Connect capabilities"}</h2>
      <button type="button" className={graphIconButton} aria-label="Close connection editor" title="Close connection editor" disabled={pending} onClick={onClose}><X size={16} /></button>
    </div>
    <div className="text-xs font-medium text-amber-700 dark:text-amber-300">Manual connection</div>
    <fieldset disabled={pending} className="min-w-0 space-y-3">
      <label className="block space-y-1" htmlFor={`${prefix}-from`}><span>From capability</span>
        <select id={`${prefix}-from`} aria-label="From capability" className={inputClass} value={from} autoFocus onChange={event => {
          const next = event.target.value;
          setFrom(next); setSourceRepository(defaultRepository(next)); setSourceKey("");
          if (next === to) { setTo(""); setTargetRepository(""); setTargetKey(""); }
        }} required>
          <option value="" disabled>Select capability</option>
          {capabilities.map(capability => <option key={capability.id} value={capability.id}>{capability.name}</option>)}
        </select>
      </label>
      <AnchorPicker key={`from|${from}|${sourceRepository}`} side="From" capability={capabilities.find(capability => capability.id === from)}
        repository={sourceRepository} nodeKey={sourceKey} scope={scope}
        onRepository={repository => { setSourceRepository(repository); setSourceKey(""); }} onNode={setSourceKey} />
      <label className="block space-y-1" htmlFor={`${prefix}-to`}><span>To capability</span>
        <select id={`${prefix}-to`} aria-label="To capability" className={inputClass} value={to} onChange={event => {
          setTo(event.target.value); setTargetRepository(defaultRepository(event.target.value)); setTargetKey("");
        }} required>
          <option value="" disabled>Select capability</option>
          {capabilities.filter(capability => capability.id !== from).map(capability => <option key={capability.id} value={capability.id}>{capability.name}</option>)}
        </select>
      </label>
      <AnchorPicker key={`to|${to}|${targetRepository}`} side="To" capability={capabilities.find(capability => capability.id === to)}
        repository={targetRepository} nodeKey={targetKey} scope={scope}
        onRepository={repository => { setTargetRepository(repository); setTargetKey(""); }} onNode={setTargetKey} />
      <label className="block space-y-1" htmlFor={`${prefix}-kind`}><span>Connection type</span>
        <select id={`${prefix}-kind`} aria-label="Connection type" className={inputClass} value={kind} onChange={event => setKind(event.target.value)}>
          {connectionKinds.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </select>
      </label>
      <label className="block space-y-1" htmlFor={`${prefix}-note`}><span>Connection note</span>
        <textarea id={`${prefix}-note`} aria-label="Connection note" className={`${inputClass} min-h-20 resize-y`} maxLength={1000} value={note} onChange={event => setNote(event.target.value)} />
      </label>
    </fieldset>
    {error && <div role="alert" className="break-words text-sm text-red-700 dark:text-red-300">{error}</div>}
    <div className="flex flex-wrap items-center gap-2">
      <Button type="submit" disabled={pending || !snapshot || !from || !to || from === to} title="Save connection"><span className="inline-flex items-center gap-1.5"><Save size={15} aria-hidden="true" />{pending ? "Saving..." : "Save connection"}</span></Button>
      {draft.id && <button type="button" className={`${graphIconButton} text-red-700 dark:text-red-300`} aria-label="Remove connection" title="Remove connection" disabled={pending || !snapshot} onClick={() => { void remove(); }}><Trash2 size={16} /></button>}
    </div>
  </form>;
}

function AnchorPicker({ side, capability, repository, nodeKey, scope, onRepository, onNode }: {
  side: "From" | "To";
  capability?: CapabilitySummary;
  repository: string;
  nodeKey: string;
  scope: string;
  onRepository: (repository: string) => void;
  onNode: (key: string) => void;
}) {
  const prefix = useId();
  const [query, setQuery] = useState("");
  const search = useDeferredValue(query);
  const parameters = new URLSearchParams(scope);
  parameters.set("cap", capability?.id ?? "");
  parameters.set("repo", repository);
  parameters.set("q", search);
  const nodes = useApi<SearchHit[]>(repository && capability ? `/api/search?${parameters}` : null);
  const selected = nodes.data?.find(node => node.key === nodeKey);
  return <div className="min-w-0 space-y-2 border-l-2 border-zinc-200 pl-3 dark:border-zinc-700">
    <label className="block space-y-1" htmlFor={`${prefix}-repository`}><span>{side} repository</span>
      <select id={`${prefix}-repository`} aria-label={`${side} repository`} className={inputClass} value={repository} disabled={!capability}
        onChange={event => onRepository(event.target.value)}>
        <option value="">Capability only</option>
        {capability?.repositories.map(id => <option key={id} value={id}>{id}</option>)}
      </select>
    </label>
    {repository && <>
      <label className="block space-y-1" htmlFor={`${prefix}-search`}><span>Search {side.toLowerCase()} analyzed nodes</span>
        <input id={`${prefix}-search`} aria-label={`Search ${side.toLowerCase()} analyzed nodes`} type="search" className={inputClass}
          maxLength={500} value={query} onChange={event => setQuery(event.target.value)} />
      </label>
      <label className="block space-y-1" htmlFor={`${prefix}-node`}><span>{side} analyzed node</span>
        <select id={`${prefix}-node`} aria-label={`${side} analyzed node`} className={inputClass} value={nodeKey}
          disabled={nodes.loading || !!nodes.error} onChange={event => onNode(event.target.value)}>
          <option value="">Repository only</option>
          {nodeKey && !selected && <option value={nodeKey}>Selected node (outside search results)</option>}
          {nodes.data?.map(node => <option key={node.key} value={node.key} title={node.key}>{node.kind}: {node.name} · {node.unit} · {node.key.split("|").slice(3).join("|") || node.key}</option>)}
        </select>
      </label>
      {nodes.loading && <div role="status" className="text-xs text-zinc-500">Loading analyzed nodes...</div>}
      {nodes.error && <div role="alert" className="break-words text-xs text-red-700 dark:text-red-300">{nodes.error}</div>}
      {!nodes.loading && !nodes.error && nodes.data?.length === 0 && <div role="status" className="text-xs text-zinc-500">No analyzed nodes found.</div>}
      {nodeKey && <a className="block break-all text-xs text-blue-700 underline dark:text-blue-300" title={nodeKey} href={symbolHref(nodeKey)}>{selected?.name ?? "Selected node"}</a>}
    </>}
  </div>;
}