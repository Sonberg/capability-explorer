import { Component, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  type AuthoredModelEvidence, type CandidateArtifact,
  type ModelEvidence, type ModelEvidenceView, type ModelList, type ModelScope, type ModelSourceView,
  type ModelSummary, sameScope,
} from "../models/contracts";
import { CandidateEvidence, Diagnostics, DmnTables, ModelRail, Provenance } from "./ModelEvidence";
import { buildModelIndex, type ModelDiagram } from "../models/model-index";
import { ModelElements } from "./ModelElements";
import { useModelViewer } from "./useModelViewer";
import { DecisionReferences, useDecisionReferences } from "./DecisionReferences";
import { useDecisionNavigation, type DecisionCaller } from "./useDecisionNavigation";
import type { DecisionResolution } from "../models/decision-links";
import { failureDescription, getJson, modelSelectionUpdates, scopeFrom, scopedRequest, validateEvidence, validateSource, validModelList } from "./model-view-data";
import { href } from "../state";

type Props = { scope: string; params: URLSearchParams; mode: "files" | "database" };
type LoadState<T> = { data?: T; error?: string; loading: boolean; key?: string };
type Tab = "diagram" | "elements" | "requirements" | "table";
const button = "rounded-md border border-zinc-300 px-3 py-1.5 text-sm hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 disabled:opacity-50 dark:border-zinc-700 dark:hover:bg-zinc-800";
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
  const picker = useRef<HTMLDetailsElement>(null);
  const listScope = useRef<ModelScope | undefined>(undefined);
  const loadedQuery = useRef("");
  const pageRequest = useRef<AbortController | null>(null);
  const deepLinkCursors = useRef(new Set<string>());
  const requestedTab = params.get("tab");
  const tab: Tab = requestedTab === "elements" || requestedTab === "table" || requestedTab === "requirements" || requestedTab === "diagram"
    ? requestedTab
    : "diagram";
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
  const decisions = useDecisionNavigation({ scope, snapshot: listScope.current, selected, updateRoute });

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
    if (picker.current) picker.current.open = false;
    updateRoute(modelSelectionUpdates(id));
  };

  return <section className="space-y-4" aria-labelledby="processes-heading">
    <header>
      <h1 id="processes-heading" className="text-xl font-bold text-zinc-900 dark:text-zinc-100">Processes &amp; decisions</h1>
      <p className="text-sm text-zinc-600 dark:text-zinc-400">Read-only evidence at selected revisions; nothing is executed or approved.</p>
    </header>
    {listState.error && <div role="alert" className={`${card} border-red-300 text-red-800 dark:border-red-900 dark:text-red-300`}>{listState.error} <button type="button" className={linkStyle} onClick={() => setListEpoch((value) => value + 1)}>Retry</button></div>}
    {!listState.error && <div className={`grid min-w-0 grid-cols-1 gap-4 ${selected?.kind === "candidate"
      ? "min-[1024px]:grid-cols-[minmax(0,1fr)_clamp(280px,24vw,360px)] xl:grid-cols-[clamp(210px,16vw,260px)_minmax(0,1fr)_clamp(250px,17vw,340px)]"
      : "min-[1024px]:grid-cols-1 xl:grid-cols-[clamp(210px,16vw,260px)_minmax(0,1fr)]"}`}>
      <aside className={`${card} hidden min-w-0 xl:block`} aria-label="Model list">
        <ModelRail idPrefix="wide" items={filtered} allItems={summaries} selectedId={selectedId} query={query} setQuery={setQuery} kind={kind} setKind={setKind} selectModel={selectModel} listLoading={listState.loading || !scopeReady} deepLinkLoading={deepLinkLoading} cursor={cursor} pageBusy={pageBusy} appendPage={appendPage} candidateKindPending={kind !== "all" && summaries.some((item) => item.kind === "candidate" && item.id !== selectedId)} />
      </aside>
      <details ref={picker} className="col-span-full xl:hidden">
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
                scope={scope}
                summaries={summaries}
                onOpenDecision={decisions.open}
                caller={decisions.caller}
                onBack={decisions.back}
                restore={decisions.restore}
                onRestored={decisions.restored}
                initialDiagram={params.get("diagram") || undefined}
                initialInsights={decisions.restore?.insightsOpen ?? selected.id === decisions.destination}
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
      {selected?.kind === "candidate" && <ModelViewErrorBoundary key={`provenance:${selected.id}:${scope}:${listEpoch}`} onRetry={() => setListEpoch((value) => value + 1)}>
        <Provenance summary={selected} evidence={currentEvidence.data} snapshot={listScope.current} selectedElement={selectedElement} sourceVerified={!!currentSource.data} renderStatus={viewerReport.key === selectedKey ? viewerReport.status : "not rendered"} />
      </ModelViewErrorBoundary>}
    </div>}
  </section>;
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
  scope: string;
  summary: ModelSummary;
  summaries: ModelSummary[];
  onOpenDecision(resolution: DecisionResolution, caller: DecisionCaller): Promise<void>;
  caller?: DecisionCaller;
  onBack(): void;
  restore?: DecisionCaller;
  onRestored(): void;
  initialDiagram?: string;
  initialInsights?: boolean;
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
  const workspace = useRef<HTMLElement>(null);
  const insightsButton = useRef<HTMLButtonElement>(null);
  const [navigationNotice, setNavigationNotice] = useState("");
  const [insightsOpen, setInsightsOpen] = useState(!!props.initialInsights);
  const index = useMemo(() => authored && buildModelIndex(authored), [authored]);
  const [importDiagram, setImportDiagram] = useState<string | undefined>(props.initialDiagram);
  const [activeDiagram, setActiveDiagram] = useState<string | undefined>(props.initialDiagram);
  const diagramId = activeDiagram || importDiagram || index?.diagrams[0]?.id;
  const decisionRefs = useDecisionReferences({ source: source.data, summary, summaries: props.summaries,
    scope: props.scope, snapshot,
    getSandbox: () => sandbox.current, diagramId, insightsOpen, onOpen: props.onOpenDecision,
    select: id => { setSelectedElement(id); setInsightsOpen(true); } });
  const { frameHost, sandbox, frameStatus, frameError, renderedIds, measured } = useModelViewer({
    summary, source: source.data, snapshot, authored, index, diagramId: importDiagram, selectedElement, setSelectedElement,
    decisionLinks: decisionRefs.overlays, onDecisionReference: decisionRefs.activate,
    onDecisionPreview: decisionRefs.preview,
    restore: props.restore, onRestored: props.onRestored,
    onViewerState, onDiagramRoot(rootId) {
      const diagram = index?.diagrams.find(item => item.rootId === rootId);
      if (diagram) setActiveDiagram(diagram.id);
    }, onNotice(message) { setNavigationNotice(message); setInsightsOpen(true); },
  });
  const isDmn = summary.modelKind === "dmn";
  const activeTab = isDmn && tab === "diagram" ? "requirements" : !isDmn && (tab === "requirements" || tab === "table") ? "diagram" : tab;

  useEffect(() => {
    if (!source.data || (activeTab !== "diagram" && activeTab !== "requirements")) return;
    const align = () => {
      if (innerWidth < 1280 && workspace.current && workspace.current.getBoundingClientRect().top > 56) {
        workspace.current.scrollIntoView({ block: "start" });
      }
    };
    const frame = requestAnimationFrame(align);
    window.addEventListener("resize", align);
    return () => { cancelAnimationFrame(frame); window.removeEventListener("resize", align); };
  }, [source.data?.sha256, activeTab, insightsOpen]);

  useEffect(() => {
    if (!insightsOpen) return;
    const close = (event: KeyboardEvent) => {
      if (event.key === "Escape") { setInsightsOpen(false); insightsButton.current?.focus(); }
    };
    window.addEventListener("keydown", close);
    return () => window.removeEventListener("keydown", close);
  }, [insightsOpen]);


  const elements = authored?.elements ?? [];
  const orderedElements = useMemo(() => [...elements].sort((a, b) => a.order - b.order), [elements]);
  const availableTabs: Array<[Tab, string]> = isDmn
    ? [["requirements", "Decision requirements"], ["table", "Decision table"], ["elements", "Elements"]]
    : [["diagram", "Diagram"], ["elements", "Elements"]];

  const selectElement = (id: string) => {
    setSelectedElement(id);
    setInsightsOpen(true);
    setNavigationNotice("");
    const relationship = authored?.relationships.find((item) => item.identity === id);
    if (relationship && relationship.kind !== "reference") {
      const sourceId = authored?.elements.find((item) => item.identity === relationship.source)?.id;
      const targetId = authored?.elements.find((item) => item.identity === relationship.target)?.id;
      if (sourceId && targetId) sandbox.current?.selectRelationship(sourceId, targetId);
      else sandbox.current?.selectElement(id);
    } else if (index?.occurrences.has(id)) sandbox.current?.selectElement(id);
    const element = authored?.elements.find((item) => item.id === id);
    if (isDmn && element?.type.toLowerCase().includes("decisiontable")) setTab("table");
  };
  const openDiagram = (diagram: ModelDiagram, id = "") => {
    setNavigationNotice("");
    setActiveDiagram(diagram.id);
    setImportDiagram(diagram.id);
    setSelectedElement(id);
    setTab(isDmn ? "requirements" : "diagram");
    if (diagram.id === importDiagram || !importDiagram && diagram.id === index?.diagrams[0]?.id) {
      if (id) sandbox.current?.selectElement(id);
      sandbox.current?.zoom("fit");
    }
  };
  const openLogic = (id: string) => { setSelectedElement(id); setTab("table"); setInsightsOpen(false); };
  useEffect(() => {
    if (activeTab === "diagram" || activeTab === "requirements") {
      const frame = requestAnimationFrame(() => sandbox.current?.zoom("fit"));
      return () => cancelAnimationFrame(frame);
    }
    if (activeTab === "table" && selectedElement) {
      const frame = requestAnimationFrame(() => document.getElementById(`model-logic-${selectedElement}`)?.scrollIntoView({ block: "nearest" }));
      return () => cancelAnimationFrame(frame);
    }
  }, [activeTab]);

  return <article className="flex min-w-0 flex-col gap-3">
    {index && <header className="min-w-0 space-y-1">
      <h2 className="text-lg font-semibold">{index.title}</h2>
      <p className="break-all text-xs text-zinc-500">Effective model source: {summary.repositoryId} @ {summary.commitSha.slice(0, 12)} · {summary.unitPath}. Bound to this model snapshot, not a deployment record.</p>
      {summary.repositoryId === "synthetic-model-fixtures" && <p className="text-xs font-medium">Synthetic test example · not an organisational process or policy.</p>}
      <p className="text-xs text-zinc-500">Business owner / capability mapping / approval: not supplied by this evidence.</p>
    </header>}
    {summary.kind === "candidate" && <div role="alert" className="rounded-lg border-2 border-amber-500 bg-amber-50 p-3 text-amber-950 dark:bg-amber-950 dark:text-amber-100">
      <strong>Code candidate · UNREVIEWED · not approved · not executable</strong>
      <p>Recipe: {candidate?.recipe ?? "Candidate evidence not loaded"}. This is bounded static evidence, not authored BPMN/DMN and not an evaluated decision.</p>
    </div>}
    <div className={`${card} order-last`}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-semibold">{summary.kind === "candidate" ? "Static candidate evidence" : summary.modelKind === "bpmn" ? "BPMN model evidence" : "DMN model evidence"}</span>
        <span className={`rounded-full border px-2 py-0.5 text-xs ${summary.coverage === "full" ? "border-zinc-300 text-zinc-700 dark:border-zinc-700 dark:text-zinc-300" : "border-amber-400 text-amber-800 dark:text-amber-200"}`}>{summary.coverage} extraction coverage</span>
        {authored?.contentProfile === "structural" && <span className="text-xs text-zinc-500">Structural profile · expressions may be omitted</span>}
        {summary.kind === "authored" && <a className={`${linkStyle} ml-auto text-xs`} href={`${import.meta.env.BASE_URL}licenses/bpmn-io-viewers.txt`} target="_blank" rel="noreferrer">bpmn.io viewer license and attribution</a>}
      </div>
      <p className="text-xs text-zinc-500">Extraction coverage is not diagram coverage, business completeness, validity or approval.</p>
      {authored?.coverage.limitations.map((limitation) => <p key={limitation} className="mt-1 text-xs text-amber-800 dark:text-amber-200">{limitation}</p>)}
    </div>
    {evidence.loading && <p role="status" className={card}>Loading selected model evidence…</p>}
    {evidence.error && <p role="alert" className={`${card} text-red-700 dark:text-red-300`}>{evidence.error}</p>}
    {authored && index && <section ref={workspace} className="model-workspace scroll-mt-14 rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
      <div className="flex flex-wrap items-center gap-2 border-b border-zinc-200 px-3 py-2 text-sm dark:border-zinc-800">
        <label htmlFor="model-diagram" className="shrink-0 font-medium">Diagram {Math.max(0, index.diagrams.findIndex(item => item.id === diagramId) + 1)} of {index.diagrams.length}</label>
        <select id="model-diagram" className="min-w-0 flex-1 rounded border border-zinc-300 bg-white p-1 dark:border-zinc-700 dark:bg-zinc-900" value={diagramId || ""} disabled={!index.diagrams.length}
          onChange={event => { const target = index.diagrams.find(item => item.id === event.target.value); if (target) openDiagram(target); }}>
          {!index.diagrams.length && <option value="">No diagram placement captured</option>}
          {index.diagrams.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
        </select>
      </div>
      <nav aria-label="Model evidence" className="flex items-center gap-1 overflow-x-auto whitespace-nowrap border-b border-zinc-200 p-2 dark:border-zinc-800">
        {availableTabs.map(([id, label]) => <button key={id} type="button" aria-pressed={activeTab === id} onClick={() => setTab(id)} className={`${button} ${activeTab === id ? "border-blue-500 bg-blue-50 text-blue-800 dark:bg-blue-950 dark:text-blue-200" : ""}`}>{label}</button>)}
        <div className="ml-auto flex shrink-0 gap-1">
          <button ref={insightsButton} type="button" className={`${button} ${insightsOpen ? "border-blue-500 bg-blue-50 text-blue-800 dark:bg-blue-950 dark:text-blue-200" : ""}`}
            aria-expanded={insightsOpen} aria-controls={insightsOpen ? "model-insights-panel" : undefined} onClick={() => setInsightsOpen((open) => !open)}>
            Insights
          </button>
          <button type="button" className={button} aria-label="Fit diagram" onClick={() => sandbox.current?.zoom("fit")}>Fit</button>
          <button type="button" className={`${button} hidden sm:inline-flex`} aria-label="Zoom out" onClick={() => sandbox.current?.zoom("out")}>−</button>
          <button type="button" className={`${button} hidden sm:inline-flex`} aria-label="Reset zoom" onClick={() => sandbox.current?.zoom("reset")}>100%</button>
          <button type="button" className={`${button} hidden sm:inline-flex`} aria-label="Zoom in" onClick={() => sandbox.current?.zoom("in")}>+</button>
        </div>
      </nav>
      <p id="diagram-help" className="sr-only">Read-only diagram. Select a box for insights. Activate its plus button or double-click to open nested content or a local called process. Return using breadcrumbs or Back to the caller. The Elements tab provides a keyboard-accessible text alternative. No model editing commands are available.</p>
      <div className="relative">
      {props.caller && <nav aria-label="Decision caller navigation"
        className="model-drill-navigation">
        <button type="button" title={`Back to ${props.caller.label}`}
          onClick={props.onBack}>Back to {props.caller.label}</button>
        <span title={`Decision model: ${index.title}`}>Decision model: {index.title}</span>
      </nav>}
      <div aria-hidden={activeTab !== "diagram" && activeTab !== "requirements"}
        className={activeTab === "diagram" || activeTab === "requirements" ? "relative" : "invisible pointer-events-none absolute inset-x-0 top-0"}>
        {source.loading && <p role="status" className="p-3 text-sm text-zinc-500">Loading and verifying permitted original XML…</p>}
        {source.error && <p role="alert" className="p-3 text-sm text-red-700 dark:text-red-300">{source.error}</p>}
        {!source.data && !source.loading && !source.error && <p className="p-3 text-sm text-zinc-500">Original source is unavailable. Showing only separately validated textual evidence.</p>}
        <div ref={frameHost} className={`model-viewer-frame ${source.data ? "" : "hidden"}`} role="region" aria-label={isDmn ? "DMN requirements diagram" : "BPMN process diagram"} aria-describedby="diagram-help" />
        {frameStatus !== "idle" && <p className="px-3 py-1 text-xs text-zinc-500" role="status">{frameStatus}</p>}
        <p className="px-3 pb-2 text-xs text-zinc-500">{source.data
          ? "Original source SHA-256 verified. Viewer projection does not replace source evidence."
          : "Original XML is not verified; only separately validated textual evidence is available."}</p>
        {frameError && <p className="mx-3 mb-3 rounded border border-amber-400 bg-amber-50 p-2 text-sm text-amber-900 dark:bg-amber-950 dark:text-amber-100" role="alert">{frameError}</p>}
        {isDmn && activeTab === "requirements" && <p className="px-3 pb-3 text-xs text-zinc-500">Decision requirements diagram (DRD): dependencies, not execution flow. Captured rules and expressions are available in Decision table and Elements; profile limitations still apply.</p>}
      </div>
      <div className={props.caller && (activeTab === "elements" || activeTab === "table") ? "pt-24 sm:pt-16" : ""}>
      {activeTab === "elements" && <ModelElements index={index} diagramId={diagramId} selected={selectedElement} renderedIds={renderedIds} measured={measured} onSelect={selectElement} onOpen={openDiagram} onLogic={openLogic} />}
      {activeTab === "table" && <DmnTables elements={orderedElements} selected={selectedElement} onSelect={(id) => { setSelectedElement(id); sandbox.current?.selectElement(id); }} />}
      </div>
      <div id="model-insights-panel" hidden={!insightsOpen}
        className="model-insights-panel fixed inset-x-2 bottom-2 z-30 max-h-[55dvh] overflow-y-auto overscroll-contain rounded-lg shadow-xl sm:absolute sm:bottom-auto sm:left-auto sm:right-2 sm:top-2 sm:w-[min(360px,calc(100%-1rem))]">
        <button type="button" className={`${button} absolute right-2 top-2 bg-white dark:bg-zinc-900`} aria-label="Close model insights"
          onClick={() => { setInsightsOpen(false); insightsButton.current?.focus(); }}>Close</button>
        {navigationNotice && <p role="status" className="rounded border border-amber-300 bg-amber-50 p-3 pr-20 text-sm text-amber-900">{navigationNotice}</p>}
        <DecisionReferences state={decisionRefs} selected={selectedElement} />
        <Provenance summary={summary} evidence={evidence.data} snapshot={snapshot} selectedElement={selectedElement} sourceVerified={!!source.data} renderStatus={frameStatus} />
      </div>
      </div>
    </section>}
    {candidate && <CandidateEvidence candidate={candidate} />}
    {authored && <Diagnostics diagnostics={authored.diagnostics} elements={authored.elements} />}
    {candidate && <Diagnostics diagnostics={candidate.diagnostics} />}
  </article>;
}
