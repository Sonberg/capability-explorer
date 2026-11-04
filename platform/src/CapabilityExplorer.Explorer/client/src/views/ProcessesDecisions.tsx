import { useEffect, useMemo, useRef, useState } from "react";
import {
  type AuthoredModelEvidence, type CandidateArtifact,
  type ModelEvidence, type ModelEvidenceView, type ModelList, type ModelScope, type ModelSourceView,
  type ModelSummary, isModelScope, sameScope,
} from "../models/contracts";
import { createModelSandbox, type ModelSandbox, type SandboxEvent } from "../models/viewer-sandbox";
import { CandidateEvidence, Diagnostics, DmnTables, ElementList, Provenance } from "./ModelEvidence";
import { href } from "../state";

type Props = { scope: string; params: URLSearchParams; mode: "files" | "database" };
type LoadState<T> = { data?: T; error?: string; loading: boolean; key?: string };
type Tab = "diagram" | "elements" | "requirements" | "table";
const button = "rounded-md border border-zinc-300 px-3 py-1.5 text-sm hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 disabled:opacity-50 dark:border-zinc-700 dark:hover:bg-zinc-800";
const input = "rounded-md border border-zinc-300 bg-white px-3 py-2 dark:border-zinc-700 dark:bg-zinc-900";
const card = "rounded-lg border border-zinc-200 bg-white p-3 dark:border-zinc-800 dark:bg-zinc-900";
const linkStyle = "text-blue-700 underline decoration-transparent underline-offset-2 hover:decoration-current focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 dark:text-blue-300";

async function getJson<T>(url: string, signal: AbortSignal, maxBytes: number): Promise<T> {
  const response = await fetch(url, { signal, headers: { accept: "application/json" } });
  if (!response.body) throw new Error("The server response cannot be read safely.");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.byteLength;
    if (length > maxBytes) {
      await reader.cancel();
      throw new Error("413 Bounded model response exceeds its serialized byte limit.");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  let body: unknown;
  try { body = length ? JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) : undefined; } catch { body = undefined; }
  if (!response.ok) {
    const problem = body && typeof body === "object" ? body as Record<string, unknown> : {};
    throw new Error(String(problem.detail ?? problem.error ?? problem.title ?? `${response.status} ${response.statusText}`));
  }
  if (body === undefined) throw new Error("The server returned an empty or invalid JSON response.");
  return body as T;
}

function validSummary(value: unknown): value is ModelSummary {
  if (!value || typeof value !== "object") return false;
  const item = value as ModelSummary;
  return typeof item.id === "string" && item.id.length > 0 &&
    (item.kind === "authored" || item.kind === "candidate") &&
    typeof item.repositoryId === "string" && typeof item.commitSha === "string" &&
    typeof item.unitPath === "string" &&
    (item.modelKind === "bpmn" || item.modelKind === "dmn" || item.modelKind === "candidate") &&
    (item.coverage === "full" || item.coverage === "partial") &&
    Number.isSafeInteger(item.diagnosticCount) && item.diagnosticCount >= 0 &&
    !!item.source && ["available", "missing", "denied"].includes(item.source.status) &&
    (item.source.sha256 === undefined || /^[a-f0-9]{64}$/i.test(item.source.sha256)) &&
    (item.kind === "candidate") === (item.modelKind === "candidate");
}

function validModelList(value: unknown): value is ModelList {
  if (!value || typeof value !== "object") return false;
  const list = value as ModelList;
  return list.schema === "ce.model-views.v1" && isModelScope(list) &&
    (list.mode === "files" || list.mode === "database") &&
    Array.isArray(list.items) && list.items.length <= 50 && list.items.every(validSummary) &&
    Array.isArray(list.problems) && list.problems.every((problem) => typeof problem === "string") &&
    (list.nextCursor === null || typeof list.nextCursor === "string");
}

function scopeFrom(value: { snapshot: string; generation: number; selection: Record<string, string> }): ModelScope {
  return { snapshot: value.snapshot, generation: value.generation, selection: value.selection };
}

function scopedRequest(scope: string, snapshot: ModelScope, cursor?: string) {
  const query = new URLSearchParams(scope);
  query.set("snapshot", snapshot.snapshot);
  query.set("generation", String(snapshot.generation));
  if (cursor) query.set("cursor", cursor);
  return query.toString();
}

function failureDescription(error: string) {
  if (/403|denied|forbidden/i.test(error)) return "Access to this source is denied. No alternate source was requested.";
  if (/404|missing|not found/i.test(error)) return "The selected source is missing from this immutable snapshot.";
  if (/409|stale|snapshot|generation/i.test(error)) return "This model belongs to a stale snapshot. Reload the current selection.";
  if (/413|limit|too large/i.test(error)) return "The bounded response limit was reached; the server did not return truncated evidence.";
  return error;
}

function validateEvidence(summary: ModelSummary, view: ModelEvidenceView, list: ModelScope): ModelEvidence {
  if (view.schema !== "ce.model-evidence-view.v1" || view.id !== summary.id) throw new Error("Selected evidence identity does not match the model summary.");
  const returned = scopeFrom(view);
  if (!sameScope(returned, list)) throw new Error("Evidence response scope does not match the selected snapshot.");
  const evidence = view.evidence;
  if (evidence.repositoryId !== summary.repositoryId || evidence.commitSha !== summary.commitSha || evidence.unitPath !== summary.unitPath) {
    throw new Error("Evidence provenance does not match the selected model.");
  }
  if (evidence.schema === "ce.models.v1" && summary.kind !== "authored") throw new Error("Unexpected authored evidence for a candidate model.");
  if (evidence.schema === "ce.candidates.v1" && summary.kind !== "candidate") throw new Error("Unexpected candidate evidence for an authored model.");
  if (evidence.sourceSha256 !== summary.source.sha256 && summary.source.sha256) throw new Error("Evidence source digest does not match the model summary.");
  return evidence;
}

async function validateSource(summary: ModelSummary, evidence: ModelEvidence, source: ModelSourceView, list: ModelScope) {
  if (source.schema !== "ce.model-source-view.v1" || source.id !== summary.id) throw new Error("Source response identity does not match the selected model.");
  if (!sameScope(scopeFrom(source), list)) throw new Error("Source response scope does not match the selected snapshot.");
  if (source.repositoryId !== summary.repositoryId || source.commitSha !== summary.commitSha || source.unitPath !== summary.unitPath) {
    throw new Error("Source provenance does not match the selected model.");
  }
  const evidenceHash = evidence.sourceSha256;
  if (source.sha256 !== summary.source.sha256 || source.sha256 !== evidenceHash) throw new Error("Source digest does not match the summary and selected evidence.");
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(source.xml)));
  const actual = [...digest].map((byte) => byte.toString(16).padStart(2, "0")).join("");
  if (actual !== source.sha256) throw new Error("Source bytes failed SHA-256 verification; unverified content was not rendered.");
  if (summary.modelKind === "bpmn" && source.format !== "bpmn+xml" || summary.modelKind === "dmn" && source.format !== "dmn+xml") {
    throw new Error("Source format does not match the declared model kind.");
  }
}

export function ProcessesDecisions({ scope, params, mode }: Props) {
  const selectedId = params.get("id") ?? "";
  const [query, setQuery] = useState("");
  const [kind, setKind] = useState<"all" | "authored" | "candidate">("all");
  const [listState, setListState] = useState<LoadState<ModelList>>({ loading: true });
  const [cursor, setCursor] = useState<string | null>(null);
  const [pageBusy, setPageBusy] = useState(false);
  const [listEpoch, setListEpoch] = useState(0);
  const [evidenceState, setEvidenceState] = useState<LoadState<ModelEvidence>>({ loading: false });
  const [sourceState, setSourceState] = useState<LoadState<ModelSourceView>>({ loading: false });
  const [tab, setTab] = useState<Tab>("diagram");
  const [selectedElement, setSelectedElement] = useState("");
  const [problems, setProblems] = useState<string[]>([]);
  const listScope = useRef<ModelScope | undefined>(undefined);
  const loadedQuery = useRef("");
  const pageRequest = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let live = true;
    setListState({ loading: true });
    setEvidenceState({ loading: false });
    setSourceState({ loading: false });
    setCursor(null);
    setProblems([]);
    listScope.current = undefined;
    loadedQuery.current = "";
    void getJson<ModelList>(`/api/models?${scope}`, controller.signal, 1_048_576).then((response) => {
      if (!live) return;
      if (!validModelList(response) || response.generation !== Number(new URLSearchParams(scope).get("g") ?? response.generation)) {
        throw new Error("Model list has an invalid schema or scope.");
      }
      listScope.current = scopeFrom(response);
      loadedQuery.current = scope;
      setListState({ data: response, loading: false });
      setCursor(response.nextCursor);
      setProblems(response.problems ?? []);
      if (response.mode !== mode) setProblems((current) => [...current, `Server returned ${response.mode} mode while this view is in ${mode} mode.`]);
    }).catch((error: Error) => {
      if (live && error.name !== "AbortError") setListState({ error: failureDescription(error.message), loading: false });
    });
    return () => {
      live = false;
      controller.abort();
      pageRequest.current?.abort();
      pageRequest.current = null;
    };
  }, [scope, listEpoch, mode]);

  const appendPage = async () => {
    if (!cursor || !listState.data || !listScope.current || pageBusy) return;
    const controller = new AbortController();
    pageRequest.current = controller;
    setPageBusy(true);
    try {
      const currentScope = listScope.current;
      const url = `/api/models?${scopedRequest(scope, currentScope, cursor)}`;
      const next = await getJson<ModelList>(url, controller.signal, 1_048_576);
      if (!listScope.current || !sameScope(listScope.current, currentScope)) return;
      if (!validModelList(next) || !sameScope(scopeFrom(next), currentScope)) throw new Error("Page cursor returned a stale or mismatched snapshot.");
      setListState((old) => old.data ? { data: { ...next, items: [...old.data.items, ...next.items], problems: [...old.data.problems, ...next.problems] }, loading: false } : old);
      setCursor(next.nextCursor);
      setProblems((old) => [...old, ...next.problems]);
    } catch (error) {
      if ((error as Error).name !== "AbortError") {
        setProblems((old) => [...old, `Unable to continue this immutable model snapshot: ${failureDescription((error as Error).message)}`]);
      }
    } finally {
      if (pageRequest.current === controller) {
        pageRequest.current = null;
        setPageBusy(false);
      }
    }
  };

  const scopeReady = loadedQuery.current === scope;
  const summaries = scopeReady ? listState.data?.items ?? [] : [];
  const selected = summaries.find((item) => item.id === selectedId);

  useEffect(() => {
    if (!selected || !listScope.current) {
      setEvidenceState({ loading: false });
      setSourceState({ loading: false });
      return;
    }
    const controller = new AbortController();
    let live = true;
    const requestScope = listScope.current;
    const requestKey = `${scope}\u0000${selected.id}`;
    setEvidenceState({ loading: true, key: requestKey });
    setSourceState({ loading: false, key: requestKey });
    setSelectedElement("");
    let evidenceLoaded = false;
    const url = `/api/models/evidence?id=${encodeURIComponent(selected.id)}&${scopedRequest(scope, requestScope)}`;
    void getJson<ModelEvidenceView>(url, controller.signal, 16_777_216).then((view) => {
      if (!live) return;
      const evidence = validateEvidence(selected, view, requestScope);
      evidenceLoaded = true;
      setEvidenceState({ data: evidence, loading: false, key: requestKey });
      if (selected.source.status !== "available" || evidence.schema !== "ce.models.v1") return;
      setSourceState({ loading: true, key: requestKey });
      const sourceUrl = `/api/models/source?id=${encodeURIComponent(selected.id)}&${scopedRequest(scope, requestScope)}`;
      return getJson<ModelSourceView>(sourceUrl, controller.signal, 8_388_608).then(async (source) => {
        await validateSource(selected, evidence, source, requestScope);
        if (live) setSourceState({ data: source, loading: false, key: requestKey });
      });
    }).catch((error: Error) => {
      if (live && error.name !== "AbortError") {
        const message = failureDescription(error.message);
        if (!evidenceLoaded) setEvidenceState({ error: message, loading: false, key: requestKey });
        else setSourceState({ error: message, loading: false, key: requestKey });
      }
    });
    return () => { live = false; controller.abort(); };
  }, [selected?.id, scope, listEpoch]);

  const filtered = useMemo(() => summaries.filter((item) =>
    (kind === "all" || item.kind === kind) &&
    (!query || `${item.repositoryId} ${item.unitPath} ${item.commitSha} ${item.modelKind}`.toLowerCase().includes(query.toLowerCase()))
  ), [summaries, kind, query]);
  const selectedKey = selected ? `${scope}\u0000${selected.id}` : "";
  const currentEvidence: LoadState<ModelEvidence> = selected && evidenceState.key === selectedKey ? evidenceState : { loading: !!selected };
  const currentSource: LoadState<ModelSourceView> = selected && sourceState.key === selectedKey ? sourceState : { loading: false };
  const selectModel = (id: string) => {
    setSelectedElement("");
    setEvidenceState({ loading: true });
    setSourceState({ loading: false });
    window.location.hash = href("processes-decisions", { ...Object.fromEntries(params), id });
  };

  return <section className="space-y-4" aria-labelledby="processes-heading">
    <header>
      <h1 id="processes-heading" className="text-xl font-bold text-zinc-900 dark:text-zinc-100">Processes &amp; decisions</h1>
      <p className="text-sm text-zinc-600 dark:text-zinc-400">Read-only evidence at selected revisions; nothing is executed or approved.</p>
    </header>
    {listState.error && <div role="alert" className={`${card} border-red-300 text-red-800 dark:border-red-900 dark:text-red-300`}>{listState.error} <button type="button" className={linkStyle} onClick={() => setListEpoch((value) => value + 1)}>Retry</button></div>}
    {!listState.error && <div className="grid min-w-0 gap-4 xl:grid-cols-[280px_minmax(0,1fr)_clamp(300px,20vw,420px)]">
      <aside className={`${card} min-w-0`} aria-label="Model list">
        <label className="block text-xs font-semibold" htmlFor="model-search">Search models</label>
        <input id="model-search" className={`${input} mt-1 w-full`} value={query} onChange={(event) => setQuery(event.target.value)} />
        <div className="mt-2 flex gap-1" role="group" aria-label="Filter model kind">
          {(["all", "authored", "candidate"] as const).map((value) => <button key={value} type="button" aria-pressed={kind === value} onClick={() => setKind(value)} className={`${button} px-2 py-1 text-xs`}>{value === "all" ? "All" : value === "authored" ? "Authored" : "Code candidates"}</button>)}
        </div>
        <h2 className="mt-4 font-semibold">Authored models</h2>
        <ul className="mt-1 space-y-1">
          {filtered.filter((item) => item.kind === "authored").map((item) => <ModelButton key={item.id} item={item} selected={item.id === selectedId} onClick={() => selectModel(item.id)} />)}
        </ul>
        <h2 className="mt-4 font-semibold">Code candidates</h2>
        <ul className="mt-1 space-y-1">
          {filtered.filter((item) => item.kind === "candidate").map((item) => <ModelButton key={item.id} item={item} selected={item.id === selectedId} onClick={() => selectModel(item.id)} />)}
        </ul>
        {(listState.loading || !scopeReady) && <p role="status" className="mt-3 text-sm text-zinc-500">Loading bounded model summaries…</p>}
        {!listState.loading && summaries.length === 0 && <p className="mt-3 text-sm text-zinc-500">No model summaries are available in this scope.</p>}
        {cursor && <button type="button" className={`${button} mt-3 w-full`} disabled={pageBusy} onClick={() => void appendPage()}>{pageBusy ? "Loading…" : "Load next page"}</button>}
      </aside>
      <div className="min-w-0 space-y-3">
        {problems.length > 0 && <div role="status" className="rounded border border-amber-300 bg-amber-50 p-2 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950 dark:text-amber-200">{problems.map((problem, index) => <p key={`${index}:${problem}`}>{problem}</p>)}</div>}
        {selected ? <ModelWorkspace
          key={`${selected.id}:${listScope.current?.snapshot ?? ""}:${listScope.current?.generation ?? ""}`}
          summary={selected}
          evidence={currentEvidence}
          source={currentSource}
          snapshot={listScope.current}
          tab={tab}
          setTab={setTab}
          selectedElement={selectedElement}
          setSelectedElement={setSelectedElement}
        /> : <div className={card}><p className="font-semibold">Select a model</p><p className="mt-1 text-sm text-zinc-500">{selectedId ? "This ID is not in the loaded pages. Load more summaries or return to the list." : "Choose an authored model or a separately labelled code candidate."}</p></div>}
      </div>
      {selected && <Provenance summary={selected} evidence={currentEvidence.data} snapshot={listScope.current} selectedElement={selectedElement} />}
    </div>}
  </section>;
}

function ModelButton({ item, selected, onClick }: { item: ModelSummary; selected: boolean; onClick: () => void }) {
  return <li><button type="button" aria-current={selected ? "true" : undefined} onClick={onClick} className={`w-full rounded-md border p-2 text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 ${selected ? "border-blue-500 bg-blue-50 dark:bg-blue-950/50" : "border-zinc-200 hover:bg-zinc-50 dark:border-zinc-800 dark:hover:bg-zinc-800"}`}>
    <span className="flex items-center justify-between gap-2 font-medium"><span className="truncate">{item.unitPath.split("/").at(-1)}</span><span className="shrink-0 text-[11px] text-zinc-500">{item.coverage}</span></span>
    <span className="block truncate text-xs text-zinc-500">{item.repositoryId} · {item.modelKind}</span>
    <span className="block truncate font-mono text-[10px] text-zinc-500">{item.unitPath}</span>
  </button></li>;
}

function ModelWorkspace(props: {
  summary: ModelSummary;
  evidence: LoadState<ModelEvidence>;
  source: LoadState<ModelSourceView>;
  snapshot?: ModelScope;
  tab: Tab;
  setTab: (tab: Tab) => void;
  selectedElement: string;
  setSelectedElement: (id: string) => void;
}) {
  const { summary, evidence, source, snapshot, tab, setTab, selectedElement, setSelectedElement } = props;
  const authored = evidence.data?.schema === "ce.models.v1" ? evidence.data as AuthoredModelEvidence : undefined;
  const candidate = evidence.data?.schema === "ce.candidates.v1" ? evidence.data as CandidateArtifact : undefined;
  const frameHost = useRef<HTMLDivElement>(null);
  const sandbox = useRef<ModelSandbox | undefined>(undefined);
  const [frameStatus, setFrameStatus] = useState("idle");
  const [frameError, setFrameError] = useState("");
  const stylesheetUrl = `${import.meta.env.BASE_URL}assets/model-viewer-frame.css`;

  useEffect(() => {
    if (!frameHost.current || summary.kind !== "authored" || !source.data || !snapshot) return;
    const instance = createModelSandbox(frameHost.current, stylesheetUrl);
    sandbox.current = instance;
    const unsubscribe = instance.subscribe((event: SandboxEvent) => {
      if (!event.acceptedByParent || event.sourceId !== summary.id || event.generation !== snapshot.generation || event.scope !== snapshot.snapshot) return;
      if (event.status === "rendered") {
        setFrameError("");
        setFrameStatus(`Rendered ${event.renderedCount ?? 0} diagram elements`);
      }
      else if (event.status === "selection" && event.elementId) setSelectedElement(event.elementId);
      else if (event.status !== "accepted" && event.status !== "import-started") {
        setFrameError(`${event.status}: ${event.message ?? "The read-only renderer could not display this source."}`);
        setFrameStatus("Import failed");
      }
    });
    setFrameError("");
    setFrameStatus("Loading read-only viewer…");
    instance.render({
      kind: summary.modelKind as "bpmn" | "dmn",
      source: source.data.xml,
      scope: snapshot.snapshot,
      generation: snapshot.generation,
      sourceId: summary.id,
    });
    return () => {
      unsubscribe();
      instance.dispose();
      sandbox.current = undefined;
      setFrameStatus("Disposed");
    };
  }, [summary.id, summary.modelKind, source.data?.sha256, snapshot?.snapshot, snapshot?.generation, stylesheetUrl]);

  const elements = authored?.elements ?? [];
  const orderedElements = useMemo(() => [...elements].sort((a, b) => a.order - b.order), [elements]);
  const isDmn = summary.modelKind === "dmn";
  const activeTab = isDmn && tab === "diagram" ? "requirements" : !isDmn && (tab === "requirements" || tab === "table") ? "diagram" : tab;
  const availableTabs: Array<[Tab, string]> = isDmn
    ? [["requirements", "Requirements"], ["table", "Decision table"], ["elements", "Elements"]]
    : [["diagram", "Diagram"], ["elements", "Elements"]];

  const selectElement = (element: AuthoredModelEvidence["elements"][number]) => {
    setSelectedElement(element.id);
    sandbox.current?.selectElement(element.id);
    if (isDmn && element.type.toLowerCase().includes("decisiontable")) setTab("table");
  };

  return <article className="min-w-0 space-y-3">
    {summary.kind === "candidate" && <div role="alert" className="rounded-lg border-2 border-amber-500 bg-amber-50 p-3 text-amber-950 dark:bg-amber-950 dark:text-amber-100">
      <strong>Code candidate · UNREVIEWED · not approved · not executable</strong>
      <p>Recipe: {candidate?.recipe ?? "Candidate evidence not loaded"}. This is bounded static evidence, not authored BPMN/DMN and not an evaluated decision.</p>
    </div>}
    <div className={card}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-semibold">{summary.kind === "candidate" ? "Static candidate evidence" : summary.modelKind === "bpmn" ? "Authored BPMN process" : "Authored DMN decision"}</span>
        <span className={`rounded-full border px-2 py-0.5 text-xs ${summary.coverage === "full" ? "border-zinc-300 text-zinc-700 dark:border-zinc-700 dark:text-zinc-300" : "border-amber-400 text-amber-800 dark:text-amber-200"}`}>{summary.coverage} coverage</span>
        {authored?.contentProfile === "structural" && <span className="text-xs text-zinc-500">Structural profile · expressions may be omitted</span>}
        {summary.kind === "authored" && <a className={`${linkStyle} ml-auto text-xs`} href={`${import.meta.env.BASE_URL}licenses/bpmn-io-viewers.txt`} target="_blank" rel="noreferrer">bpmn.io viewer license and attribution</a>}
      </div>
      {authored?.coverage.limitations.map((limitation) => <p key={limitation} className="mt-1 text-xs text-amber-800 dark:text-amber-200">{limitation}</p>)}
    </div>
    {evidence.loading && <p role="status" className={card}>Loading selected model evidence…</p>}
    {evidence.error && <p role="alert" className={`${card} text-red-700 dark:text-red-300`}>{evidence.error}</p>}
    {authored && <section className="overflow-hidden rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
      <nav aria-label="Model evidence" className="flex flex-wrap gap-1 border-b border-zinc-200 p-2 dark:border-zinc-800">
        {availableTabs.map(([id, label]) => <button key={id} type="button" aria-pressed={activeTab === id} onClick={() => setTab(id)} className={`${button} ${activeTab === id ? "border-blue-500 bg-blue-50 text-blue-800 dark:bg-blue-950 dark:text-blue-200" : ""}`}>{label}</button>)}
        {!isDmn && <div className="ml-auto flex gap-1">
          <button type="button" className={button} aria-label="Fit diagram" onClick={() => sandbox.current?.zoom("fit")}>Fit</button>
          <button type="button" className={button} aria-label="Zoom out" onClick={() => sandbox.current?.zoom("out")}>−</button>
          <button type="button" className={button} aria-label="Reset zoom" onClick={() => sandbox.current?.zoom("reset")}>100%</button>
          <button type="button" className={button} aria-label="Zoom in" onClick={() => sandbox.current?.zoom("in")}>+</button>
        </div>}
      </nav>
      <p id="diagram-help" className="sr-only">Read-only diagram. Use the Elements tab for a complete keyboard-accessible text alternative. No model commands are available.</p>
      <div className={activeTab === "diagram" || activeTab === "requirements" ? "block" : "hidden"}>
        <p className="px-3 pt-2 text-xs text-zinc-500">Diagram geometry is from authored DI; no generated layout replaces source evidence.</p>
        {source.loading && <p role="status" className="p-3 text-sm text-zinc-500">Loading and verifying permitted original XML…</p>}
        {source.error && <p role="alert" className="p-3 text-sm text-red-700 dark:text-red-300">{source.error}</p>}
        {!source.data && !source.loading && !source.error && <p className="p-3 text-sm text-zinc-500">Original source is unavailable. Showing only separately validated textual evidence.</p>}
        <div ref={frameHost} className={`model-viewer-frame ${source.data ? "" : "hidden"}`} role="region" aria-label={isDmn ? "DMN requirements diagram" : "BPMN process diagram"} aria-describedby="diagram-help" />
        {frameStatus !== "idle" && <p className="px-3 py-1 text-xs text-zinc-500" role="status">{frameStatus}</p>}
        {frameError && <p className="mx-3 mb-3 rounded border border-amber-400 bg-amber-50 p-2 text-sm text-amber-900 dark:bg-amber-950 dark:text-amber-100" role="alert">{frameError}</p>}
        {isDmn && activeTab === "requirements" && <p className="px-3 pb-3 text-xs text-zinc-500">Requirements diagram is read-only. Complete decision rules and expressions are available in the Decision table and Elements tabs.</p>}
      </div>
      {activeTab === "elements" && <ElementList elements={orderedElements} relationships={authored.relationships} selected={selectedElement} onSelect={selectElement} />}
      {activeTab === "table" && <DmnTables elements={orderedElements} selected={selectedElement} onSelect={(id) => { setSelectedElement(id); sandbox.current?.selectElement(id); }} />}
    </section>}
    {candidate && <CandidateEvidence candidate={candidate} />}
    {authored && <Diagnostics diagnostics={authored.diagnostics} />}
    {candidate && <Diagnostics diagnostics={candidate.diagnostics} />}
  </article>;
}
