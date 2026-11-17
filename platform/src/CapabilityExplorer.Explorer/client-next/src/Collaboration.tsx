import { useId, useState, type KeyboardEvent } from "react";
import type { ApiResponse, LandscapeDocument, Snapshot } from "./contracts";
import type { Navigate } from "./Lenses";
import type { Route } from "./model";
import {
  collaborationPlanes, displayElement, documentDiagnostics, documentElements, documentGeometry, semantic,
} from "./document-model";
import { proposal, title } from "./model";
import { Coverage, Diagnostics, Field, Paged } from "./ui";

export function DocumentNavigation({ snapshot, route, navigate }: {
  snapshot: Snapshot; route: Route; navigate: Navigate;
}) {
  return <section aria-label="Collaboration document navigation">
    <h3>Collaboration documents</h3>
    {!snapshot.documents ? <p className="muted small">Optional document graph not supplied.</p>
      : <Paged label="Collaboration documents" items={snapshot.documents} render={doc =>
        <button className="document-choice" aria-pressed={route.lens === "collaboration"
          && (route.document || snapshot.documents?.[0]?.path) === doc.path}
          onClick={() => navigate({ lens: "collaboration", document: doc.path, diagram: "", documentElement: "" })}>
          {doc.path}
        </button>} />}
  </section>;
}

function inspectOnKey(e: KeyboardEvent<SVGGElement>, action: () => void) {
  if (e.key === "Enter" || e.key === " ") { e.preventDefault(); action(); }
}

export function Collaboration({ snapshot: s, route, navigate }: {
  snapshot: Snapshot; route: Route; navigate: Navigate;
}) {
  const [zoom, setZoom] = useState(100);
  const marker = `message-arrow-${useId().replace(/:/g, "")}`;
  const doc = route.document ? s.documents?.find(d => d.path === route.document) : s.documents?.[0];
  if (!doc) return <p role="status" className="notice">{route.document
    ? "Linked collaboration document is absent. Its URL is preserved; no other document was substituted."
    : s.documents ? "No collaboration documents were supplied." : "Collaboration graph not supplied. Optional documents[] is opt-in; existing process lenses remain available."}</p>;
  const union = documentElements(s, doc), planes = collaborationPlanes(s, doc);
  const selected = route.diagram ? planes.find(p => p.plane.identity === route.diagram) : planes[0];
  const visiblePlanes = (selected ? [selected, ...planes.filter(p => p !== selected)] : planes).slice(0, 200);
  const geometry = documentGeometry(s, doc, selected);
  const inspect = (identity: string) => navigate({ document: doc.path, diagram: selected?.plane.identity ?? "", documentElement: identity });
  const participants = union.filter(e => semantic(e, "participant") && (!selected || e.parent === selected.collaboration.identity));
  const scopedElements = new Set(doc.elements.filter(e => !selected || e.parent === selected.collaboration.identity).map(e => e.identity));
  const carriers = new Set(doc.relationships.map(r => r.elementIdentity));
  const flows = doc.relationships.filter(r => scopedElements.has(r.elementIdentity));
  const omitted = doc.elements.filter(e => semantic(e, "messageFlow")
    && (!selected || e.parent === selected.collaboration.identity)
    && !carriers.has(e.identity));
  const name = (identity: string) => {
    const e = union.find(e => e.identity === identity);
    return e ? displayElement(e) : `Unresolved endpoint: ${identity}`;
  };
  return <section aria-label="Collaboration document">
    <p className="eyebrow">Source file: {doc.path}</p>
    <p className="note">Document-scoped collaboration. Process search, phase/context filters and overlays remain
      process-scoped and do not hide pools or alter authored coordinates here.</p>
    <label>Diagram / collaboration plane
      <select value={selected?.plane.identity ?? ""} onChange={e => navigate({
        document: doc.path, diagram: e.target.value, documentElement: "",
      })}>
        {!selected && <option value="">{route.diagram ? "Linked plane absent" : "No collaboration plane supplied"}</option>}
        {visiblePlanes.map(p => <option key={p.plane.identity} value={p.plane.identity}>
          {p.diagram?.attributes.name || p.diagram?.id || p.plane.id} / {displayElement(p.collaboration)}
        </option>)}
      </select>
      {planes.length > 200 && <small>First 200 planes shown, including the linked selection.</small>}
    </label>
    {route.diagram && !selected && <p role="status" className="notice">
      Linked collaboration plane is absent or not resolvable. No substitute authored diagram was selected.
      {" "}The lists below are file-scoped evidence, not an alternate diagram.
    </p>}
    {geometry.bounds ? <>
      <p className="note"><strong>Authored coordinates · simplified symbols and UI-positioned labels.</strong> Rectangles
        use supplied Bounds; dashed message paths use supplied waypoints only. This is not a full BPMN renderer.
        No sequence flow, participant replacement or missing path is inferred.</p>
      <div className="inline diagram-controls" aria-label="Collaboration zoom">
        <button disabled={zoom <= 50} onClick={() => setZoom(z => Math.max(50, z - 25))}>Zoom out</button>
        <span>{zoom}%</span><button disabled={zoom >= 300} onClick={() => setZoom(z => Math.min(300, z + 25))}>Zoom in</button>
        <button onClick={() => setZoom(100)}>Fit view</button>
      </div>
      <div className="authored-viewport">
        <svg className="authored-diagram" role="group" aria-label="Authored collaboration coordinates"
          style={{ width: `${zoom}%` }}
          viewBox={`${geometry.bounds.x} ${geometry.bounds.y} ${geometry.bounds.width} ${geometry.bounds.height}`}>
          <defs><marker id={marker} viewBox="0 0 12 12" refX="10" refY="6" markerWidth="10" markerHeight="10" orient="auto-start-reverse">
            <path d="M 1 1 L 10 6 L 1 11" fill="white" stroke="#1d4ed8" strokeWidth="1.5" />
          </marker></defs>
          {[...geometry.shapes].sort((a, b) => Number(!semantic(a.element, "participant"))
            - Number(!semantic(b.element, "participant"))).map(shape => {
            const pool = semantic(shape.element, "participant"), label = displayElement(shape.element);
            return <g key={shape.interchange.identity} className={`authored-shape ${pool ? "authored-pool" : ""}`}
              role="button" tabIndex={0} aria-label={`Inspect ${pool ? "pool" : shape.element.type}: ${label}`}
              aria-pressed={route.documentElement === shape.element.identity}
              onClick={() => inspect(shape.element.identity)}
              onKeyDown={e => inspectOnKey(e, () => inspect(shape.element.identity))}>
              <title>{shape.element.type}: {label}</title>
              <rect data-geometry={shape.interchange.identity} x={shape.x} y={shape.y} width={shape.width} height={shape.height} rx={pool ? 0 : 6} />
              <text x={shape.x + 8} y={shape.y + Math.min(20, shape.height / 2)}>
                {label.slice(0, Math.max(1, Math.min(80, Math.floor(shape.width / 7))))}
              </text>
            </g>;
          })}
          {geometry.edges.map(edge => <g key={edge.interchange.identity} className="authored-message"
            role="button" tabIndex={0} aria-label={`Inspect message flow: ${displayElement(edge.carrier)}`}
            aria-pressed={route.documentElement === edge.carrier.identity}
            onClick={() => inspect(edge.carrier.identity)}
            onKeyDown={e => inspectOnKey(e, () => inspect(edge.carrier.identity))}>
            <title>messageFlow: {name(edge.relationship.source)} → {name(edge.relationship.target)}</title>
            <polyline data-geometry={edge.interchange.identity} points={edge.points.map(p => `${p.x},${p.y}`).join(" ")}
              markerEnd={`url(#${marker})`} />
          </g>)}
        </svg>
      </div>
    </> : <p className="notice"><strong>Structural projection — no usable authored coordinates for this selection.</strong>
      {" "}Pools and admitted message relationships are listed below. No positions or paths have been invented.</p>}
    <Paged label="Geometry limitations" items={geometry.warnings} render={warning => <p className="notice">{warning}</p>} />
    <h3>Pools / participants</h3>
    <Paged label="Collaboration pools" items={participants} render={pool => {
      const ref = pool.attributes.processRef, owners = ref ? s.processes.filter(p => p.path === doc.path && p.processId === ref) : [];
      return <article className="card">
        <button className="text-button" onClick={() => inspect(pool.identity)}>{displayElement(pool)}</button>
        <p>{!ref ? "Black-box pool · no processRef; no process invented."
          : owners.length === 1 ? `Source processRef: ${ref}` : `Unresolved / ambiguous processRef: ${ref}`}</p>
        {owners.length === 1 && <button className="text-button" onClick={() => navigate({
          lens: "diagram", process: owners[0].processIdentity, element: "", q: "", phase: "", context: "",
        })}>Open process: {title(owners[0])}</button>}
      </article>;
    }} />
    <h3>Admitted message relationships</h3>
    <Paged label="Document message relationships" items={flows} render={flow => <article className="card">
      <strong>messageFlow</strong><p>{name(flow.source)} → {name(flow.target)}</p>
      <p className="muted">Carrier identity: {flow.elementIdentity}</p>
      <div className="inline">{[["source", flow.source], ["target", flow.target], ["carrier", flow.elementIdentity]].map(([label, identity]) =>
        <button key={label} className="text-button" onClick={() => inspect(identity)}>Inspect {label}</button>)}</div>
    </article>} />
    <h3>Unresolved / omitted message carriers</h3>
    <Paged label="Unresolved message carriers" items={omitted} render={carrier => <p className="notice">
      {displayElement(carrier)} · no admitted relationship. No message path is drawn.
      <button className="text-button" onClick={() => inspect(carrier.identity)}>Inspect unresolved carrier</button>
    </p>} />
    <Diagnostics items={documentDiagnostics(s, doc)} />
  </section>;
}

export function DocumentInspector({ snapshot: s, doc, elementId, response }: {
  snapshot: Snapshot; doc?: LandscapeDocument; elementId: string; response: ApiResponse;
}) {
  const element = doc && documentElements(s, doc).find(e => e.identity === elementId);
  const source = s.sources.find(source => source.path === doc?.path);
  return <aside className="inspector" aria-label="Collaboration inspector">
    <p className="eyebrow">Document evidence / provenance</p><h2>{element ? displayElement(element) : "Collaboration document"}</h2>
    {elementId && !element && <p role="status" className="notice">Linked document element is absent.
      Its URL remains unchanged; no substitute was selected.</p>}
    {element && <>
      <dl><Field name="Element identity">{element.identity}</Field><Field name="Element ID">{element.id}</Field>
        <Field name="Type">{element.type}</Field><Field name="Namespace">{element.namespace}</Field>
        <Field name="Parent identity">{element.parent}</Field><Field name="Source path">{element.source.path}</Field>
        <Field name="Source element">{element.source.element}</Field></dl>
      <Paged label="Document element attributes" items={Object.entries(element.attributes)}
        render={([key, value]) => <dl><Field name={key}>{value}</Field></dl>} />
    </>}
    {doc && <dl><Field name="Document path">{doc.path}</Field><Field name="SHA-256">{source?.sha256}</Field>
      <Field name="Bytes">{source?.byteLength}</Field></dl>}
    <dl><Field name="Snapshot ID">{s.snapshotId}</Field><Field name="Repository">{s.repositoryId}</Field>
      <Field name="Content profile">{s.contentProfile}</Field>
      <Field name="Identity verification">{String(response.identityVerification?.status ?? "not supplied")}</Field>
      <Field name="Identity reason">{response.identityVerification?.reason}</Field></dl>
    {doc && !source && <p className="notice">Source metadata is missing for this document path.</p>}
    <p className="note">Document nodes and process copies are joined by exact identity within this source file only.
      Source content is text; no XML, script or expression is fetched or executed. Geometry is not approval.</p>
    <Coverage value={s.coverage} />
    <Diagnostics items={s.diagnostics} />
    <Paged label="Document process status" items={s.processes.filter(p => p.path === doc?.path)}
      render={p => <p>{title(p)} · {proposal(p)}</p>} />
  </aside>;
}
