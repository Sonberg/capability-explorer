import { useEffect, useRef, useState } from "react";
import { ScanSearch, X } from "lucide-react";
import { request } from "../api";
import { useSelection } from "../state";
import { Button, ErrorBox, Pill } from "./ui";

interface SuggestionEvidence {
  id: number; side: string; repositoryId: string; commitSha: string; path: string;
  startLine: number; endLine: number; text: string;
}
interface Suggestion {
  status: string; dependency: string; compatibility: string; transport: string; explanation: string;
  citations: number[]; model: string; inputHash: string; evidence: SuggestionEvidence[];
}

const selectClass = "mt-1 w-full min-w-0 rounded border border-zinc-300 bg-transparent p-1.5 dark:border-zinc-600";

export function IntegrationSuggestionInspector({ scope, onClose }: { scope: string; onClose: () => void }) {
  const { meta, capability } = useSelection();
  const [source, setSource] = useState(capability || meta.capabilities[0]?.id || "");
  const [target, setTarget] = useState(meta.capabilities.find(item => item.id !== (capability || meta.capabilities[0]?.id))?.id || "");
  const [repository, setRepository] = useState("");
  const [query, setQuery] = useState("");
  const [result, setResult] = useState<Suggestion>();
  const [error, setError] = useState<string>();
  const [pending, setPending] = useState(false);
  const active = useRef<AbortController | null>(null);
  useEffect(() => {
    active.current?.abort();
    setResult(undefined);
    setError(undefined);
    setPending(false);
    return () => active.current?.abort();
  }, [scope, source, target, repository, query]);

  const assess = async () => {
    active.current?.abort();
    const controller = new AbortController();
    active.current = controller;
    setPending(true);
    setResult(undefined);
    setError(undefined);
    try {
      const response = await request<Suggestion>("/api/architecture/integration-suggestions", {
        method: "POST", headers: { "Content-Type": "application/json", "X-CE-Integration-Suggestions": "1" },
        body: JSON.stringify({ sourceCapabilityId: source, targetCapabilityId: target,
          sourceRepositoryId: repository || undefined, sourceQuery: query || undefined,
          view: new URLSearchParams(scope).get("view") ?? undefined }),
        signal: controller.signal,
      });
      if (!controller.signal.aborted && active.current === controller) setResult(response);
    } catch (failure) {
      if (!controller.signal.aborted && active.current === controller)
        setError(failure instanceof Error ? failure.message : "Integration assessment unavailable.");
    } finally {
      if (!controller.signal.aborted && active.current === controller) setPending(false);
    }
  };

  return <section aria-label="Model integration assessment" className="min-w-0 space-y-3">
    <div className="flex items-center justify-between gap-2">
      <h2 className="text-sm font-semibold">Integration assessment</h2>
      <button type="button" title="Close assessment" aria-label="Close assessment" onClick={onClose}><X size={16} /></button>
    </div>
    <label className="block text-xs">Source capability<select aria-label="Source capability" className={selectClass} value={source} onChange={event => {
      const value = event.target.value;
      setSource(value); setRepository("");
      if (value === target) setTarget(meta.capabilities.find(item => item.id !== value)?.id ?? "");
    }}>{meta.capabilities.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
    <label className="block text-xs">Source repository<select aria-label="Source repository" className={selectClass} value={repository} onChange={event => setRepository(event.target.value)}>
      <option value="">All source repositories</option>
      {meta.capabilities.find(item => item.id === source)?.repositories.map(item => <option key={item} value={item}>{item}</option>)}
    </select></label>
    <label className="block text-xs">Target capability<select aria-label="Target capability" className={selectClass} value={target} onChange={event => setTarget(event.target.value)}>
      {meta.capabilities.filter(item => item.id !== source).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select></label>
    <label className="block text-xs">Integration query<input aria-label="Integration query" className={selectClass} value={query} maxLength={200} onChange={event => setQuery(event.target.value)} /></label>
    <Button disabled={pending || !source || !target || !meta.interpretation.enabled} onClick={() => { void assess(); }}>
      <span className="inline-flex items-center gap-1.5"><ScanSearch size={15} />{pending ? "Assessing integration" : "Assess integration"}</span>
    </Button>
    {error && <ErrorBox message={error} />}
    {result && <div className="min-w-0 space-y-3 border-t border-zinc-200 pt-3 dark:border-zinc-700" aria-live="polite">
      <div className="flex flex-wrap gap-1"><Pill tone={result.compatibility === "incompatible" ? "bad" : "warn"}>{result.status}</Pill><Pill>{result.transport}</Pill></div>
      <p className="text-xs">Source dependency: {result.dependency}</p>
      <p className="text-xs">Target compatibility: {result.compatibility}</p>
      <p className="break-words text-sm">{result.explanation}</p>
      <p className="break-all text-xs text-zinc-500">{result.model} / {result.inputHash.slice(0, 12)}</p>
      <div className="space-y-2">{result.evidence.filter(item => result.citations.includes(item.id)).map(item => <details key={item.id} className="min-w-0 text-xs">
        <summary className="cursor-pointer break-all">[{item.id}] {item.side}: {item.path}:{item.startLine}</summary>
        <p className="my-2 break-all text-zinc-500">{item.repositoryId} @ {item.commitSha.slice(0, 12)} / lines {item.startLine}-{item.endLine}</p>
        <pre className="max-h-64 max-w-full overflow-auto whitespace-pre-wrap break-words rounded border border-zinc-200 p-2 dark:border-zinc-700">{item.text}</pre>
      </details>)}</div>
    </div>}
  </section>;
}