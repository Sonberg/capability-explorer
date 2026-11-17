import { useCallback, useEffect, useState, type KeyboardEvent, type MouseEvent } from "react";
import type { ApiResponse, Snapshot } from "./contracts";
import { Diagram, Events, Journey, Lifecycle, type Navigate } from "./Lenses";
import { Inspector } from "./Inspector";
import { fetchSnapshot, filterProcesses, lenses, proposal, readRoute, routeUrl, title, type Route } from "./model";
import { Paged } from "./ui";
import { catalogueRows, categoryData, processTags, records, text, texts } from "./navigator";
import { Collaboration, DocumentInspector, DocumentNavigation } from "./Collaboration";

type Loader = (signal: AbortSignal) => Promise<ApiResponse>;

function useSnapshot(load: Loader) {
  const [response, setResponse] = useState<ApiResponse>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [loadedAt, setLoadedAt] = useState("");
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    let active = true;
    const controller = new AbortController();
    const deadline = window.setTimeout(() => controller.abort(), 15000);
    setLoading(true);
    setError("");
    load(controller.signal).then(result => {
      if (active) {
        setResponse(result);
        setLoadedAt(new Date().toLocaleTimeString());
      }
    }).catch((e: unknown) => {
      if (active) setError(controller.signal.aborted ? "Request timed out."
        : e instanceof Error && e.message ? e.message : "Could not load snapshot.");
    }).finally(() => {
      window.clearTimeout(deadline);
      if (active) setLoading(false);
    });
    return () => { active = false; controller.abort(); window.clearTimeout(deadline); };
  }, [load, revision]);
  return { response, loading, error, loadedAt, refresh: () => setRevision(r => r + 1) };
}

function useRoute() {
  const [route, setRoute] = useState(() => readRoute());
  useEffect(() => {
    const sync = () => setRoute(readRoute());
    window.addEventListener("popstate", sync);
    window.addEventListener("hashchange", sync);
    return () => {
      window.removeEventListener("popstate", sync);
      window.removeEventListener("hashchange", sync);
    };
  }, []);
  const navigate: Navigate = useCallback((patch, replace = false) => {
    const next = { ...route, ...patch };
    window.history[replace ? "replaceState" : "pushState"](null, "", routeUrl(next));
    setRoute(next);
  }, [route]);
  return { route, navigate };
}

function internalClick(event: MouseEvent<HTMLAnchorElement>, action: () => void) {
  if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  action();
}

function catalogueKeys(e: KeyboardEvent<HTMLElement>) {
  if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) return;
  const links = Array.from(e.currentTarget.querySelectorAll<HTMLAnchorElement>("[data-process-link]"));
  const current = links.indexOf(document.activeElement as HTMLAnchorElement);
  if (!links.length || current < 0) return;
  e.preventDefault();
  const index = e.key === "Home" ? 0 : e.key === "End" ? links.length - 1
    : (current + (e.key === "ArrowDown" ? 1 : -1) + links.length) % links.length;
  links[index].focus();
}

function Filters({ snapshot: s, route, navigate }: { snapshot: Snapshot; route: Route; navigate: Navigate }) {
  const tags = (kind: "context" | "phase") => [...new Set([
    ...s.processes.flatMap(p => processTags(s, p, kind)),
    ...s.overlays[kind].map(o => o.tag),
    ...records(categoryData(s, kind === "context" ? "contexts" : "phases")).flatMap(row =>
      [text(row.id), text(row.name)].filter(Boolean)),
    ...records(categoryData(s, "catalogue")).flatMap(row => texts(kind === "context" ? row.ctx : row.phases)),
    ...(route[kind] ? [route[kind]] : []),
  ])].sort();
  return <div className="filters">
    {(["context", "phase"] as const).map(kind => <label key={kind}>
      {kind === "context" ? "Context" : "Phase"} filter
      <select value={route[kind]} onChange={e => navigate({ [kind]: e.target.value })}>
        <option value="">All {kind === "context" ? "contexts" : "phases"}</option>
        {[...(route[kind] ? [route[kind]] : []), ...tags(kind).filter(tag => tag !== route[kind])]
          .slice(0, 500).map(tag => <option key={tag}>{tag}</option>)}
      </select>
      {tags(kind).length > 500 && <small>First 500 tags shown.</small>}
    </label>)}
    <label>Overlay<select value={route.overlay}
      onChange={e => navigate({ overlay: e.target.value })}>
      <option value="">None</option><option value="context">Context membership</option><option value="phase">Phase membership</option>
    </select></label>
    <button onClick={() => navigate({ q: "", context: "", phase: "", overlay: "" })}>Clear filters</button>
  </div>;
}

function Workspace({ snapshot: s, response, route, navigate }: {
  snapshot: Snapshot; response: ApiResponse; route: Route; navigate: Navigate;
}) {
  const processes = filterProcesses(s, route);
  const selected = route.process ? s.processes.find(p => p.processIdentity === route.process) : processes[0];
  const document = route.document ? s.documents?.find(doc => doc.path === route.document) : s.documents?.[0];
  const hits = route.q.trim() ? s.searchIndex.filter(e =>
    e.term.toLocaleLowerCase().includes(route.q.trim().toLocaleLowerCase())
    && processes.some(p => p.processIdentity === e.processIdentity)) : [];
  return <>
    <div className="toolbar">
      <label className="search">Search processes and elements
        <input type="search" value={route.q} placeholder="Name, identifier, element…"
          onChange={e => navigate({ q: e.target.value }, true)} />
      </label>
      <Filters snapshot={s} route={route} navigate={navigate} />
    </div>
    <div className="workspace">
      <aside className="catalogue" aria-label="Process catalogue">
        <div className="section-heading"><h2>Catalogue</h2><span className="count">{processes.length} / {s.processes.length}</span></div>
        <p className="muted small">↑ ↓ to move focus · Enter to open</p>
        <nav aria-label="Processes" onKeyDown={catalogueKeys}>
          <Paged key={`${route.q}/${route.context}/${route.phase}`} label="Processes" items={processes} render={p =>
            <a data-process-link href={routeUrl({ ...route, process: p.processIdentity, element: "" })}
              aria-current={p.processIdentity === selected?.processIdentity ? "page" : undefined}
              onClick={e => internalClick(e, () => navigate({ process: p.processIdentity, element: "" }))}>
              <strong>{title(p)}</strong><small>{p.processId || p.path}</small>
              {catalogueRows(s, p).slice(0, 3).map((row, i) => <small key={i}>
                Source catalogue: {[text(row.group), text(row.name)].filter(Boolean).join(" · ")}
              </small>)}
              <small>{p.coverage.status} coverage</small>
              {p.proposalStatus && <span className="proposal">{proposal(p)}</span>}
            </a>} />
        </nav>
        {route.q.trim() && <>
          <h3>Search hits</h3>
          <Paged key={route.q} label="Search hits" items={hits} render={hit =>
            <a href={routeUrl({ ...route, process: hit.processIdentity, element: hit.elementIdentity ?? "", lens: "diagram" })}
              onClick={e => internalClick(e, () => navigate({
                process: hit.processIdentity, element: hit.elementIdentity ?? "", lens: "diagram",
              }))}>
              <span>{hit.term}</span><small>{hit.kind}</small>
            </a>} />
        </>}
        <DocumentNavigation snapshot={s} route={route} navigate={navigate} />
      </aside>
      <main id="main-content" className="canvas">
        <p className="eyebrow">Task-first navigator</p>
        <h2>{route.lens === "diagram" ? selected ? title(selected) : "Process diagram" : {
          events: "Trace a cross-process handoff", lifecycle: "Follow an object lifecycle", journey: "Walk the phase journey",
          collaboration: "Inspect a collaboration",
        }[route.lens]}</h2>
        <nav className="lenses" aria-label="Navigator lenses">
          {lenses.map(lens => <button key={lens} aria-pressed={route.lens === lens}
            onClick={() => navigate({ lens })}>{lens === "diagram" ? "Diagram" : lens === "events"
              ? "Events" : lens === "lifecycle" ? "Lifecycle" : lens === "collaboration" ? "Collaboration" : "Journey"}</button>)}
        </nav>
        {route.lens !== "collaboration" && route.process && !selected && <p role="status" className="notice">Linked process is absent from this snapshot.
          The URL is preserved; select another process to continue.</p>}
        {route.lens !== "collaboration" && selected && !processes.includes(selected) && <p role="status" className="notice">
          The selected process is outside the current catalogue filters. Its details remain visible.
        </p>}
        {!s.processes.length && <p role="status" className="notice">Empty snapshot: no processes were supplied.
          Supplemental lenses and provenance may still contain evidence.</p>}
        {route.lens === "diagram" && <Diagram process={selected} route={route} navigate={navigate} snapshot={s} />}
        {route.lens === "events" && <Events snapshot={s} processes={processes} navigate={navigate} route={route} />}
        {route.lens === "lifecycle" && <Lifecycle snapshot={s} processes={processes} navigate={navigate} route={route} />}
        {route.lens === "journey" && <Journey snapshot={s} processes={processes} navigate={navigate} route={route} />}
        {route.lens === "collaboration" && <Collaboration key={document?.path}
          snapshot={s} route={route} navigate={navigate} />}
      </main>
      {route.lens === "collaboration" ? <DocumentInspector snapshot={s} doc={document} elementId={route.documentElement} response={response} />
        : <Inspector key={`${s.snapshotId}/${selected?.processIdentity}/${route.element}`}
          snapshot={s} process={selected} elementId={route.element} response={response} />}
    </div>
  </>;
}

export function App({ load = fetchSnapshot }: { load?: Loader }) {
  const { response, loading, error, loadedAt, refresh } = useSnapshot(load);
  const { route, navigate } = useRoute();
  const snapshot = response?.status === "ok" ? response.snapshot : null;
  const identity = response?.identityVerification?.status;
  const verified = identity === "Reconstructed" || identity === "reconstructed" || identity === 0;
  return <>
    <a className="skip-link" href="#main-content">Skip to navigator</a>
    <header className="app-header"><div><p className="eyebrow">Capability Explorer / process landscape</p>
      <h1>Find the work. Follow the handoff.</h1></div>
      <button onClick={refresh} disabled={loading}>{loading ? "Loading…" : "Refresh snapshot"}</button>
    </header>
    <div className="status-bar" role="status" aria-live="polite">
      {loading ? snapshot ? "Refreshing · showing previously loaded snapshot" : "Loading process landscape…"
        : error ? snapshot ? "Stale view · refresh failed" : "Snapshot unavailable"
        : snapshot ? `${snapshot.contentProfile} profile · ${snapshot.coverage.status} coverage` : `Snapshot status: ${response?.status ?? "unavailable"}`}
      {loadedAt && <span>Loaded {loadedAt} · source freshness not established</span>}
    </div>
    {error && <div role="alert" className="notice error">{error}
      {snapshot && " Previously loaded data is retained as stale; it is not a fresh response."} Use Refresh snapshot to retry.</div>}
    {snapshot && <>
      {!verified && <p className="notice identity">Unverified snapshot identity · {response?.identityVerification?.reason
        || "No independent verification supplied."} Source records are evidence, not proof of approval.</p>}
      {snapshot.contentProfile === "structural" && <p className="profile-note">
        Structural-only view · raw BPMN/XML is not requested. No model content is executed.
      </p>}
      {snapshot.navigator?.verified === false && <p className="notice identity">
        Unverified source navigator / possibly stale · its categories are excluded from navigation and search.
      </p>}
      <Workspace snapshot={snapshot} response={response!} route={route} navigate={navigate} />
    </>}
    {!snapshot && !loading && <main id="main-content" className="unavailable">
      <h2>No landscape available</h2><p>{response?.reason || "No usable snapshot was returned."}</p>
      <p>Denied, unsupported, missing and failed responses do not become an empty successful catalogue.</p>
    </main>}
    <footer>Read-only source views · no ownership approval inferred · lists paged at 40 records</footer>
  </>;
}
