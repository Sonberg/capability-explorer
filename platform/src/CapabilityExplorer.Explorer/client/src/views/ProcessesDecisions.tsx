import { Component, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  type AuthoredModelEvidence, type CandidateArtifact,
  type ModelEvidence, type ModelEvidenceView, type ModelList, type ModelScope, type ModelSourceView,
  type ModelSummary, sameScope,
} from "../models/contracts";
import { createModelSandbox, loadTrustedViewerAssets, type ModelSandbox, type SandboxEvent } from "../models/viewer-sandbox";
import { CandidateEvidence, Diagnostics, DmnTables, ElementList, Provenance } from "./ModelEvidence";
import { failureDescription, getJson, scopeFrom, scopedRequest, validateEvidence, validateSource, validModelList } from "./model-view-data";
import { href } from "../state";

type Props = { scope: string; params: URLSearchParams; mode: "files" | "database" };
type LoadState<T> = { data?: T; error?: string; loading: boolean; key?: string };
type Tab = "diagram" | "elements" | "requirements" | "table";
const button = "rounded-md border border-zinc-300 px-3 py-1.5 text-sm hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 disabled:opacity-50 dark:border-zinc-700 dark:hover:bg-zinc-800";
const input = "rounded-md border border-zinc-300 bg-white px-3 py-2 dark:border-zinc-700 dark:bg-zinc-900";
const card = "rounded-lg border border-zinc-200 bg-white p-3 dark:border-zinc-800 dark:bg-zinc-900";
const linkStyle = "text-blue-700 underline decoration-transparent underline-offset-2 hover:decoration-current focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 dark:text-blue-300";

export function ProcessesDecisions({ scope, params, mode }: Props) {
  const selectedId = params.get("id") ?? "";
  const [query, setQuery] = useState("");
  const [kind, setKind] = useState<"all" | "process" | "decision">("all");
  const [listState, setListState] = useState<LoadState<ModelList>>({ loading: true });
  const [cursor, setCursor] = useState<string | null>(null);
  const [pageBusy, setPageBusy] = useState(false);
  const [deepLinkLoading, setDeepLinkLoading] = useState(false);
  const [listEpoch, setListEpoch] = useState(0);
  const [evidenceState, setEvidenceState] = useState<LoadState<ModelEvidence>>({ loading: false });
  const [sourceState, setSourceState] = useState<LoadState<ModelSourceView>>({ loading: false });
  const [problems, setProblems] = useState<string[]>([]);
  const [viewerReport, setViewerReport] = useState<{ key: string; status: string }>({ key: "", status: "" });
  const [narrow, setNarrow] = useState(() => typeof matchMedia !== "undefined" && matchMedia("(max-width: 649px)").matches);
  const listScope = useRef<ModelScope | undefined>(undefined);
  const loadedQuery = useRef("");
  const pageRequest = useRef<AbortController | null>(null);
  const deepLinkCursors = useRef(new Set<string>());
  const requestedTab = params.get("tab");
  const tab: Tab = requestedTab === "elements" || requestedTab === "table" || requestedTab === "requirements" || requestedTab === "diagram"
    ? requestedTab
    : narrow ? "elements" : "diagram";
  const selectedElement = params.get("element") ?? "";
  const updateRoute = (updates: Record<string, string | undefined>) => {
    const query = window.location.hash.split("?")[1] ?? "";
    const next = Object.fromEntries(new URLSearchParams(query));
    for (const [key, value] of Object.entries(updates)) {
      if (value) next[key] = value;
      else delete next[key];
    }
    window.location.hash = href("processes-decisions", next);
  };
  const setTab = (next: Tab) => updateRoute({ tab: next });
  const setSelectedElement = (id: string) => updateRoute({ element: id || undefined });

  useEffect(() => {
    const media = matchMedia("(max-width: 649px)");
    const update = () => setNarrow(media.matches);
    update();
    media.addEventListener("change", update);
    return () => media.removeEventListener("change", update);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    let live = true;
    deepLinkCursors.current.clear();
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

  useEffect(() => {
    if (!selectedId || listState.loading || !listState.data || !cursor ||
        listState.data.items.some((item) => item.id === selectedId)) return;
    const controller = new AbortController();
    let live = true;
    pageRequest.current?.abort();
    pageRequest.current = controller;
    setDeepLinkLoading(true);
    void (async () => {
      let nextCursor: string | null = cursor;
      const pages: ModelList[] = [];
      for (let page = 0; nextCursor && page < 8; page++) {
        if (deepLinkCursors.current.has(nextCursor)) break;
        deepLinkCursors.current.add(nextCursor);
        const currentScope = listScope.current;
        if (!currentScope) break;
        const pageResponse: ModelList = await getJson<ModelList>(`/api/models?${scopedRequest(scope, currentScope, nextCursor)}`, controller.signal, 1_048_576);
        if (!validModelList(pageResponse) || !sameScope(scopeFrom(pageResponse), currentScope)) throw new Error("Deep-linked model pagination returned a stale or invalid snapshot.");
        pages.push(pageResponse);
        nextCursor = pageResponse.nextCursor;
        if (pageResponse.items.some((item) => item.id === selectedId)) break;
      }
      if (!live || pages.length === 0) return;
      setListState((old) => old.data ? {
        data: { ...old.data, items: [...old.data.items, ...pages.flatMap((page) => page.items)],
          problems: [...old.data.problems, ...pages.flatMap((page) => page.problems)], nextCursor },
        loading: false
      } : old);
      setCursor(nextCursor);
    })().catch((error: Error) => {
      if (live && error.name !== "AbortError") setProblems((old) => [...old, `Unable to restore the linked model selection: ${failureDescription(error.message)}`]);
    }).finally(() => {
      if (pageRequest.current === controller) pageRequest.current = null;
      if (live) setDeepLinkLoading(false);
    });
    return () => { live = false; controller.abort(); };
  }, [selectedId, scope, listEpoch, listState.loading, listState.data, cursor]);

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

  const selectedKey = selected ? `${scope}\u0000${selected.id}` : "";
  const currentEvidence: LoadState<ModelEvidence> = selected && evidenceState.key === selectedKey ? evidenceState : { loading: !!selected };
  const currentSource: LoadState<ModelSourceView> = selected && sourceState.key === selectedKey ? sourceState : { loading: false };
  const candidateKinds = currentEvidence.data?.schema === "ce.candidates.v1"
    ? new Set((currentEvidence.data as CandidateArtifact).candidates.map((candidate) => candidate.kind))
    : new Set<string>();
  const filtered = useMemo(() => summaries.filter((item) =>
    (kind === "all" || (kind === "process"
      ? item.modelKind === "bpmn" || (item.kind === "candidate" && item.id === selectedId && candidateKinds.has("process"))
      : item.modelKind === "dmn" || (item.kind === "candidate" && item.id === selectedId && candidateKinds.has("decision")))) &&
    (!query || `${item.repositoryId} ${item.unitPath} ${item.commitSha} ${item.modelKind}`.toLowerCase().includes(query.toLowerCase()))
  ), [summaries, kind, query, selectedId, currentEvidence.data]);
  const selectModel = (id: string) => {
    updateRoute({ id, element: undefined });
  };

  return <section className="space-y-4" aria-labelledby="processes-heading">
    <header>
      <h1 id="processes-heading" className="text-xl font-bold text-zinc-900 dark:text-zinc-100">Processes &amp; decisions</h1>
      <p className="text-sm text-zinc-600 dark:text-zinc-400">Read-only evidence at selected revisions; nothing is executed or approved.</p>
    </header>
    {listState.error && <div role="alert" className={`${card} border-red-300 text-red-800 dark:border-red-900 dark:text-red-300`}>{listState.error} <button type="button" className={linkStyle} onClick={() => setListEpoch((value) => value + 1)}>Retry</button></div>}
    {!listState.error && <div className="grid min-w-0 grid-cols-1 gap-4 min-[1024px]:grid-cols-[minmax(0,1fr)_clamp(300px,28vw,420px)] xl:grid-cols-[280px_minmax(0,1fr)_clamp(300px,20vw,420px)]">
      <aside className={`${card} hidden min-w-0 xl:block`} aria-label="Model list">
        <ModelRail idPrefix="wide" items={filtered} allItems={summaries} selectedId={selectedId} query={query} setQuery={setQuery} kind={kind} setKind={setKind} selectModel={selectModel} listLoading={listState.loading || !scopeReady} deepLinkLoading={deepLinkLoading} cursor={cursor} pageBusy={pageBusy} appendPage={appendPage} candidateKindPending={kind !== "all" && summaries.some((item) => item.kind === "candidate" && item.id !== selectedId)} />
      </aside>
      <details className="col-span-full xl:hidden">
        <summary className={`${button} cursor-pointer`}>Model picker{selected ? ` · ${selected.unitPath.split("/").at(-1)}` : ""}</summary>
        <div className={`${card} mt-2 max-h-[60vh] overflow-y-auto`}>
          <ModelRail idPrefix="compact" items={filtered} allItems={summaries} selectedId={selectedId} query={query} setQuery={setQuery} kind={kind} setKind={setKind} selectModel={selectModel} listLoading={listState.loading || !scopeReady} deepLinkLoading={deepLinkLoading} cursor={cursor} pageBusy={pageBusy} appendPage={appendPage} candidateKindPending={kind !== "all" && summaries.some((item) => item.kind === "candidate" && item.id !== selectedId)} />
        </div>
      </details>
      <div className="min-w-0 space-y-3">
        {problems.length > 0 && <div role="status" className="rounded border border-amber-300 bg-amber-50 p-2 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950 dark:text-amber-200">{problems.map((problem, index) => <p key={`${index}:${problem}`}>{problem}</p>)}</div>}
        {selected
          ? <ModelViewErrorBoundary key={`${selected.id}:${scope}:${listEpoch}`} onRetry={() => setListEpoch((value) => value + 1)}>
              <ModelWorkspace
                key={`${selected.id}:${listScope.current?.snapshot ?? ""}:${listScope.current?.generation ?? ""}`}
                summary={selected}
                evidence={currentEvidence}
                source={currentSource}
                snapshot={listScope.current}
                tab={tab}
                setTab={setTab}
                selectedElement={selectedElement}
                setSelectedElement={setSelectedElement}
                onViewerState={(status) => setViewerReport({ key: selectedKey, status })}
              />
            </ModelViewErrorBoundary>
          : <div className={card}><p className="font-semibold">Select a model</p><p className="mt-1 text-sm text-zinc-500">{selectedId ? deepLinkLoading ? "Finding the selected model in this immutable snapshot…" : "This ID is not in the loaded pages. Continue loading summaries or return to the list." : "Choose an authored model or a separately labelled code candidate."}</p></div>}
      </div>
      {selected && <ModelViewErrorBoundary key={`provenance:${selected.id}:${scope}:${listEpoch}`} onRetry={() => setListEpoch((value) => value + 1)}>
        <Provenance summary={selected} evidence={currentEvidence.data} snapshot={listScope.current} selectedElement={selectedElement} sourceVerified={!!currentSource.data} renderStatus={viewerReport.key === selectedKey ? viewerReport.status : "not rendered"} />
      </ModelViewErrorBoundary>}
    </div>}
  </section>;
}

function ModelRail(props: {
  idPrefix: string;
  items: ModelSummary[];
  allItems: ModelSummary[];
  selectedId: string;
  query: string;
  setQuery: (value: string) => void;
  kind: "all" | "process" | "decision";
  setKind: (value: "all" | "process" | "decision") => void;
  selectModel: (id: string) => void;
  listLoading: boolean;
  deepLinkLoading: boolean;
  cursor: string | null;
  pageBusy: boolean;
  appendPage: () => Promise<void>;
  candidateKindPending: boolean;
}) {
  const { idPrefix, items, allItems, selectedId, query, setQuery, kind, setKind, selectModel, listLoading, deepLinkLoading, cursor, pageBusy, appendPage, candidateKindPending } = props;
  return <>
    <label className="block text-xs font-semibold" htmlFor={`${idPrefix}-model-search`}>Search models</label>
    <input id={`${idPrefix}-model-search`} className={`${input} mt-1 w-full`} value={query} onChange={(event) => setQuery(event.target.value)} />
    <div className="mt-2 flex flex-wrap gap-1" role="group" aria-label="Filter processes and decisions">
      {(["all", "process", "decision"] as const).map((value) => <button key={value} type="button" aria-pressed={kind === value} onClick={() => setKind(value)} className={`${button} px-2 py-1 text-xs`}>{value === "all" ? "All" : value === "process" ? "Processes" : "Decisions"}</button>)}
    </div>
    <h2 className="mt-4 font-semibold">Authored models</h2>
    <ul className="mt-1 space-y-1">
      {items.filter((item) => item.kind === "authored").map((item) => <ModelButton key={item.id} item={item} selected={item.id === selectedId} onClick={() => selectModel(item.id)} />)}
    </ul>
    <h2 className="mt-4 font-semibold">Code candidates</h2>
    {candidateKindPending && kind !== "all" && <p className="mt-1 text-xs text-zinc-500">Candidate process/decision kind is not included in summaries. Select All to load candidate evidence before filtering its kind.</p>}
    <ul className="mt-1 space-y-1">
      {items.filter((item) => item.kind === "candidate").map((item) => <ModelButton key={item.id} item={item} selected={item.id === selectedId} onClick={() => selectModel(item.id)} />)}
    </ul>
    {(listLoading || deepLinkLoading) && <p role="status" className="mt-3 text-sm text-zinc-500">{deepLinkLoading ? "Finding linked model…" : "Loading bounded model summaries…"}</p>}
    {!listLoading && allItems.length === 0 && <p className="mt-3 text-sm text-zinc-500">No model summaries are available in this scope.</p>}
    {cursor && <button type="button" className={`${button} mt-3 w-full`} disabled={pageBusy || deepLinkLoading} onClick={() => void appendPage()}>{pageBusy ? "Loading…" : "Load next page"}</button>}
  </>;
}

function ModelButton({ item, selected, onClick }: { item: ModelSummary; selected: boolean; onClick: () => void }) {
  return <li><button type="button" aria-current={selected ? "true" : undefined} onClick={onClick} className={`w-full rounded-md border p-2 text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 ${selected ? "border-blue-500 bg-blue-50 dark:bg-blue-950/50" : "border-zinc-200 hover:bg-zinc-50 dark:border-zinc-800 dark:hover:bg-zinc-800"}`}>
    <span className="flex items-center justify-between gap-2 font-medium"><span className="truncate">{item.unitPath.split("/").at(-1)}</span><span className="shrink-0 text-[11px] text-zinc-500">{item.coverage}</span></span>
    <span className="block truncate text-xs text-zinc-500">{item.repositoryId} · {item.modelKind}</span>
    <span className="block truncate font-mono text-[10px] text-zinc-500">{item.unitPath}</span>
  </button></li>;
}

class ModelViewErrorBoundary extends Component<{ children: ReactNode; onRetry: () => void }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    if (this.state.failed) {
      return <div role="alert" className={`${card} border-red-300 text-red-800 dark:border-red-900 dark:text-red-300`}>
        <p>The selected model view could not be displayed. Other explorer views remain available.</p>
        <button type="button" className={`${button} mt-2`} onClick={() => { this.setState({ failed: false }); this.props.onRetry(); }}>Reload this model</button>
      </div>;
    }
    return this.props.children;
  }
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
  onViewerState: (status: string) => void;
}) {
  const { summary, evidence, source, snapshot, tab, setTab, selectedElement, setSelectedElement, onViewerState } = props;
  const authored = evidence.data?.schema === "ce.models.v1" ? evidence.data as AuthoredModelEvidence : undefined;
  const candidate = evidence.data?.schema === "ce.candidates.v1" ? evidence.data as CandidateArtifact : undefined;
  const frameHost = useRef<HTMLDivElement>(null);
  const sandbox = useRef<ModelSandbox | undefined>(undefined);
  const [frameStatus, setFrameStatus] = useState("idle");
  const [frameError, setFrameError] = useState("");
  const [renderedIds, setRenderedIds] = useState<string[]>([]);
  const onViewerStateRef = useRef(onViewerState);
  onViewerStateRef.current = onViewerState;
  const selectedElementRef = useRef(selectedElement);
  selectedElementRef.current = selectedElement;

  useEffect(() => {
    const verifiedSource = source.data;
    if (!frameHost.current || summary.kind !== "authored" || !verifiedSource || !snapshot) return;
    const controller = new AbortController();
    let live = true;
    let instance: ModelSandbox | undefined;
    let unsubscribe: (() => void) | undefined;
    setFrameError("");
    setRenderedIds([]);
    setFrameStatus("Loading trusted read-only viewer assets…");
    onViewerStateRef.current("Loading trusted read-only viewer assets…");
    void loadTrustedViewerAssets(controller.signal).then((assets) => {
      if (!live || !frameHost.current) return;
      instance = createModelSandbox(frameHost.current, assets);
      sandbox.current = instance;
      unsubscribe = instance.subscribe((event: SandboxEvent) => {
        if (!event.acceptedByParent || event.sourceId !== summary.id || event.generation !== snapshot.generation || event.scope !== snapshot.snapshot) return;
        if (event.status === "rendered") {
          setFrameError("");
          setRenderedIds(event.renderedIds ?? []);
          setFrameStatus(`Rendered ${event.renderedCount ?? 0} diagram elements`);
          onViewerStateRef.current(`Rendered ${event.renderedCount ?? 0} diagram elements`);
          if (selectedElementRef.current) {
            const relationship = authored?.relationships.find((item) => item.identity === selectedElementRef.current);
            const sourceId = relationship && authored?.elements.find((item) => item.identity === relationship.source)?.id;
            const targetId = relationship && authored?.elements.find((item) => item.identity === relationship.target)?.id;
            if (sourceId && targetId) instance?.selectRelationship(sourceId, targetId);
            else instance?.selectElement(selectedElementRef.current);
          }
        } else if (event.status === "selection" && event.elementId) {
          const matches = authored?.relationships.filter((relationship) => {
            const from = authored.elements.find((element) => element.identity === relationship.source)?.id;
            const to = authored.elements.find((element) => element.identity === relationship.target)?.id;
            return from === event.relationshipSourceId && to === event.relationshipTargetId;
          }) ?? [];
          setSelectedElement(matches.length === 1 ? matches[0].identity : event.elementId);
        } else if (event.status === "selection-result") {
          setFrameError(event.relationshipMatchCount === 1 ? "" :
            "Text relationship selection could not be matched to exactly one viewer connection; textual selection remains available.");
        }
        else if (event.status !== "accepted" && event.status !== "import-started") {
          setFrameError(`${event.status}: ${event.message ?? "The read-only renderer could not display this source."}`);
          setFrameStatus("Import failed");
          onViewerStateRef.current(`${event.status}: ${event.message ?? "The read-only renderer could not display this source."}`);
        }
      });
      setFrameStatus("Loading read-only viewer…");
      instance.render({
        kind: summary.modelKind as "bpmn" | "dmn",
        source: verifiedSource.xml,
        scope: snapshot.snapshot,
        generation: snapshot.generation,
        sourceId: summary.id,
      });
    }).catch((error: Error) => {
      if (live && error.name !== "AbortError") {
        setFrameStatus("Viewer unavailable");
        setFrameError(`Trusted viewer assets could not be loaded: ${error.message}`);
        onViewerStateRef.current(`Viewer assets unavailable: ${error.message}`);
      }
    });
    return () => {
      live = false;
      controller.abort();
      unsubscribe?.();
      instance?.dispose();
      if (sandbox.current === instance) sandbox.current = undefined;
    };
  }, [summary.id, summary.modelKind, source.data?.sha256, snapshot?.snapshot, snapshot?.generation]);

  const elements = authored?.elements ?? [];
  const orderedElements = useMemo(() => [...elements].sort((a, b) => a.order - b.order), [elements]);
  const isDmn = summary.modelKind === "dmn";
  const activeTab = isDmn && tab === "diagram" ? "requirements" : !isDmn && (tab === "requirements" || tab === "table") ? "diagram" : tab;
  const availableTabs: Array<[Tab, string]> = isDmn
    ? [["requirements", "Requirements"], ["table", "Decision table"], ["elements", "Elements"]]
    : [["diagram", "Diagram"], ["elements", "Elements"]];

  const selectElement = (id: string) => {
    setSelectedElement(id);
    const relationship = authored?.relationships.find((item) => item.identity === id);
    if (relationship) {
      const sourceId = authored?.elements.find((item) => item.identity === relationship.source)?.id;
      const targetId = authored?.elements.find((item) => item.identity === relationship.target)?.id;
      if (sourceId && targetId) sandbox.current?.selectRelationship(sourceId, targetId);
      else sandbox.current?.selectElement(id);
    } else sandbox.current?.selectElement(id);
    const element = authored?.elements.find((item) => item.id === id);
    if (isDmn && element?.type.toLowerCase().includes("decisiontable")) setTab("table");
  };

  return <article className="min-w-0 space-y-3">
    {summary.kind === "candidate" && <div role="alert" className="rounded-lg border-2 border-amber-500 bg-amber-50 p-3 text-amber-950 dark:bg-amber-950 dark:text-amber-100">
      <strong>Code candidate · UNREVIEWED · not approved · not executable</strong>
      <p>Recipe: {candidate?.recipe ?? "Candidate evidence not loaded"}. This is bounded static evidence, not authored BPMN/DMN and not an evaluated decision.</p>
    </div>}
    <div className={card}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-semibold">{summary.kind === "candidate" ? "Static candidate evidence" : summary.modelKind === "bpmn" ? "BPMN model evidence" : "DMN model evidence"}</span>
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
        <p className="px-3 pt-2 text-xs text-zinc-500">
          {source.data && frameStatus.startsWith("Rendered")
            ? "Original source SHA-256 verified; the viewer rendered its authored DI. This projection does not replace source evidence."
            : source.data
              ? "Original source SHA-256 verified; viewer geometry is unavailable or unverified. No generated layout replaces source evidence."
              : "No verified original XML is displayed; only separately validated textual evidence may be available."}
        </p>
        {source.loading && <p role="status" className="p-3 text-sm text-zinc-500">Loading and verifying permitted original XML…</p>}
        {source.error && <p role="alert" className="p-3 text-sm text-red-700 dark:text-red-300">{source.error}</p>}
        {!source.data && !source.loading && !source.error && <p className="p-3 text-sm text-zinc-500">Original source is unavailable. Showing only separately validated textual evidence.</p>}
        <div ref={frameHost} className={`model-viewer-frame ${source.data ? "" : "hidden"}`} role="region" aria-label={isDmn ? "DMN requirements diagram" : "BPMN process diagram"} aria-describedby="diagram-help" />
        {frameStatus !== "idle" && <p className="px-3 py-1 text-xs text-zinc-500" role="status">{frameStatus}</p>}
        {frameError && <p className="mx-3 mb-3 rounded border border-amber-400 bg-amber-50 p-2 text-sm text-amber-900 dark:bg-amber-950 dark:text-amber-100" role="alert">{frameError}</p>}
        {isDmn && activeTab === "requirements" && <p className="px-3 pb-3 text-xs text-zinc-500">Requirements diagram is read-only. Complete decision rules and expressions are available in the Decision table and Elements tabs.</p>}
      </div>
      {activeTab === "elements" && <ElementList elements={orderedElements} relationships={authored.relationships} selected={selectedElement} renderedIds={renderedIds} onSelect={selectElement} />}
      {activeTab === "table" && <DmnTables elements={orderedElements} selected={selectedElement} onSelect={(id) => { setSelectedElement(id); sandbox.current?.selectElement(id); }} />}
    </section>}
    {candidate && <CandidateEvidence candidate={candidate} />}
    {authored && <Diagnostics diagnostics={authored.diagnostics} />}
    {candidate && <Diagnostics diagnostics={candidate.diagnostics} />}
  </article>;
}
