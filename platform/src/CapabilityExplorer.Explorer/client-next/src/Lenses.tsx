import type { Element, Endpoint, Process, Snapshot } from "./contracts";
import { endpointProcess, ordered, title, type Route } from "./model";
import { Paged } from "./ui";
import { SourceEvents, SourceJourney, SourceLifecycle } from "./SourceLenses";
import { processTags } from "./navigator";

export type Navigate = (patch: Partial<Route>, replace?: boolean) => void;

export function EndpointLink({ endpoint, snapshot, navigate }: {
  endpoint: Endpoint; snapshot: Snapshot; navigate: Navigate;
}) {
  const p = endpointProcess(snapshot, endpoint);
  if (!p) return <span className="unresolved">
    Unresolved endpoint · {endpoint.processId}{endpoint.elementId && ` / ${endpoint.elementId}`}
    {endpoint.resolved && " (reference missing from snapshot)"}
  </span>;
  return <button className="text-button" onClick={() => navigate({
    process: p.processIdentity, element: endpoint.elementIdentity ?? "", lens: "diagram", q: "", context: "", phase: "",
  })}>{title(p)}{endpoint.elementId && ` / ${endpoint.elementId}`}</button>;
}

function ElementButton({ element, selected, navigate, p, overlay }: {
  element: Element; selected: string; navigate: Navigate; p: Process; overlay: string;
}) {
  return <button className={`station ${element.identity === selected ? "selected" : ""}`}
    aria-pressed={element.identity === selected}
    onClick={() => navigate({ process: p.processIdentity, element: element.identity })}>
    <span className="station-dot" aria-hidden="true" />
    <span className="eyebrow">{element.type} · source order {element.order}</span>
    <strong>{element.attributes.name || element.id}</strong>
    <span className="muted">{element.id}</span>
    {element.kind !== "bpmn" && <span className="warning">Unsupported/opaque: {element.kind}</span>}
    {overlay && <small className="overlay-tag">{overlay} overlay · process-level membership only</small>}
  </button>;
}

export function Diagram({ process: p, route, navigate, snapshot }: {
  process?: Process; route: Route; navigate: Navigate; snapshot: Snapshot;
}) {
  if (!p) return <p className="empty">Choose a process from the catalogue to inspect its elements.</p>;
  const resolveLocal = (id: string) => {
    const element = p.elements.find(e => e.identity === id || e.id === id);
    return element ? { process: p, element } : undefined;
  };
  const resolveTarget = (id: string, kind: string, source?: Element) => {
    const local = resolveLocal(id);
    if (local || kind !== "reference" || source?.kind !== "bpmn" || source.type !== "callActivity") return local;
    const candidates = snapshot.processes.flatMap(process => process.elements
      .filter(element => element.identity === id && element.kind === "bpmn" && element.type === "process"
        && element.id === process.processId && element.namespace === source.namespace
        && [element.id, `#${element.id}`].includes(source.attributes.calledElement))
      .map(element => ({ process, element })));
    return candidates.length === 1 ? candidates[0] : undefined;
  };
  const endpointName = (id: string, endpoint?: { process: Process; element: Element }) => endpoint
    ? `${endpoint.process === p ? "" : `${title(endpoint.process)} / `}${endpoint.element.attributes.name || endpoint.element.id}`
    : `Unresolved: ${id}`;
  const overlay = route.overlay === "context" || route.overlay === "phase" ? processTags(snapshot, p, route.overlay) : [];
  const overlayText = overlay.slice(0, 12).join(" · ") + (overlay.length > 12 ? " (first 12 tags shown)" : "");
  return <>
    <p className="notice"><strong>Structural projection — not authored diagram geometry.</strong> Stations follow source
      order, not an execution route. No coordinates are supplied by this contract. Relationships below are explicit
      source references, not inferred from station position.</p>
    {route.overlay && <p className="note">{route.overlay} overlay: {overlayText || "No membership supplied"}.
      Overlay tags do not establish sequence or ownership.</p>}
    <div className="projection">
      <Paged key={p.processIdentity} label="Process elements" items={ordered(p.elements)}
        render={element => <ElementButton element={element} selected={route.element} navigate={navigate}
          p={p} overlay={overlayText} />} />
    </div>
    <h3>Explicit relationships <span className="count">{p.relationships.length}</span></h3>
    <p className="muted">Sequence flow, message flow, association and references retain their source kinds.</p>
    <Paged key={`${p.processIdentity}-relationships`} label="Relationships" items={ordered(p.relationships)}
      render={r => {
        const source = resolveLocal(r.source), target = resolveTarget(r.target, r.kind, source?.element);
        const external = target && target.process !== p;
        return <article className="card">
        <strong>{r.kind}</strong><p>{endpointName(r.source, source)} → {endpointName(r.target, target)}</p>
        {external && <p className="note">Cross-process call reference · not an event handoff or sequence-flow route.</p>}
        <div className="inline">
          {([{ label: "source", endpoint: source }, { label: "target", endpoint: target }]).map(({ label, endpoint }) => {
            return endpoint && <button key={label} className="text-button"
              onClick={() => navigate({
                process: endpoint.process.processIdentity, element: endpoint.element.identity,
                ...(endpoint.process !== p ? { q: "", phase: "", context: "" } : {}),
              })}>Inspect {label}</button>;
          })}
        </div>
        {r.condition?.text && <p>Condition (text only, not evaluated): <code>{r.condition.text}</code></p>}
      </article>;
      }} />
  </>;
}

export function Events({ snapshot, processes, navigate, route }: {
  snapshot: Snapshot; processes: Process[]; navigate: Navigate; route: Route;
}) {
  const ids = new Set(processes.map(p => p.processIdentity));
  const events = snapshot.crossProcessEvents.filter(e => [e.source, e.target]
    .some(end => !endpointProcess(snapshot, end) || ids.has(end.processIdentity ?? "")));
  return <>
    <p className="note">Cross-process events are handoffs, not sequence-flow routes. Events touching catalogue
      matches are shown; unresolved endpoints remain visible because their filter membership is unknown.</p>
    <Paged label="Cross-process events" items={events} render={e => <article className="card">
      <h3>{e.label || e.eventId}</h3><small>{e.eventId}</small>
      <dl>
        <div className="field"><dt>Source</dt><dd><EndpointLink endpoint={e.source} snapshot={snapshot} navigate={navigate} /></dd></div>
        <div className="field"><dt>Target</dt><dd><EndpointLink endpoint={e.target} snapshot={snapshot} navigate={navigate} /></dd></div>
      </dl>
    </article>} />
    <SourceEvents snapshot={snapshot} navigate={navigate} route={route} />
  </>;
}

export function Lifecycle({ snapshot, processes, navigate, route }: {
  snapshot: Snapshot; processes: Process[]; navigate: Navigate; route: Route;
}) {
  const ids = new Set(processes.map(p => p.processIdentity));
  const objects = snapshot.objectLifecycle.filter(o => !o.stages.length || o.stages
    .some(e => !endpointProcess(snapshot, e) || ids.has(e.processIdentity ?? "")));
  return <>
    <p className="note">Object stages in declared order. Matching lifecycles retain all stages for context,
      including unresolved endpoints; stages are not inferred process transitions.</p>
    <Paged label="Object lifecycles" items={objects} render={o => <article className="card">
      <h3>{o.label || o.objectId}</h3><small>{o.objectId}</small>
      <Paged label="Lifecycle stages" ordered items={ordered(o.stages)} render={e => <>
        <p><strong>{e.stage}</strong> · source order {e.order}</p>
        <EndpointLink endpoint={e} snapshot={snapshot} navigate={navigate} />
      </>} />
    </article>} />
    <SourceLifecycle snapshot={snapshot} navigate={navigate} route={route} />
  </>;
}

export function Journey({ snapshot, processes, navigate, route }: {
  snapshot: Snapshot; processes: Process[]; navigate: Navigate; route: Route;
}) {
  const ids = new Set(processes.map(p => p.processIdentity));
  const phases = ordered(snapshot.phaseJourney).filter(phase => !phase.steps.length || phase.steps
    .some(e => !endpointProcess(snapshot, e) || ids.has(e.processIdentity ?? "")));
  return <>
    <p className="note">Declared phase and step order, not an executable route. Matching phases retain all
      steps for context. Repeated processes and unresolved steps are preserved.</p>
    <Paged label="Journey phases" ordered items={phases} render={phase => <article className="card">
      <h3>{phase.label || phase.phaseId}</h3><p className="muted">Phase order {phase.order} · {phase.phaseId}</p>
      <Paged label="Journey steps" ordered items={ordered(phase.steps)} render={step => <>
        <span className="step-number">Step order {step.order}</span>
        <EndpointLink endpoint={step} snapshot={snapshot} navigate={navigate} />
      </>} />
    </article>} />
    <SourceJourney snapshot={snapshot} navigate={navigate} route={route} />
  </>;
}
