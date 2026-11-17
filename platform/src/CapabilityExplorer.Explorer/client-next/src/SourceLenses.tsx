import type { ReactNode } from "react";
import type { Snapshot } from "./contracts";
import { EndpointLink, type Navigate } from "./Lenses";
import type { Route } from "./model";
import { categoryData, entries, isRecord, records, referenceTuples, sourceEndpoint, text, texts, type SourceRecord } from "./navigator";
import { Field, Paged } from "./ui";

function SourceFrame({ label, children }: { label: string; children: ReactNode }) {
  return <section className="source-lens" aria-label={`Verified source ${label}`}>
    <h3>Verified source navigator · {label}</h3>
    <p className="note">Source records are displayed separately from normalized snapshot records, never merged
      into inferred routes. File references resolve only by exact admitted path or an explicit verified process-file
      mapping. Ambiguous or missing references stay unresolved.</p>
    {children}
  </section>;
}

function SourceReferences({ value, snapshot, navigate, label }: {
  value: unknown; snapshot: Snapshot; navigate: Navigate; label: string;
}) {
  return <Paged label={label} items={referenceTuples(value)} render={ref => <div>
    <EndpointLink endpoint={sourceEndpoint(snapshot, ref[0], ref[1])} snapshot={snapshot} navigate={navigate} />
    <small className="muted"> · {ref[0]} / {ref[1]}{ref[2] && ` · via ${ref[2]}`}</small>
  </div>} />;
}

function matches(record: SourceRecord, route: Route, name: string) {
  return (!route.context || texts(record.ctx).includes(route.context))
    && (!route.phase || [...texts(record.phase), ...texts(record.phases)].includes(route.phase))
    && (!route.q.trim() || [name, ...texts(record.name), ...texts(record.entity), ...texts(record.obj)]
      .some(t => t.toLocaleLowerCase().includes(route.q.trim().toLocaleLowerCase())));
}

export function SourceEvents({ snapshot, navigate, route }: { snapshot: Snapshot; navigate: Navigate; route: Route }) {
  const data = categoryData(snapshot, "events");
  if (data === undefined) return null;
  if (!isRecord(data)) return <SourceFrame label="events"><p className="notice">
    Unsupported source event shape. It has not been interpreted.</p></SourceFrame>;
  const events = entries(data).filter(([name, value]) => isRecord(value) && matches(value, route, name));
  return <SourceFrame label="events">
    <p className="muted">Throw, catch and reference are source-declared event roles, not paired execution routes.</p>
    <Paged label="Source events" items={events} render={([name, value]) => {
      const event = value as SourceRecord;
      return <article className="card"><h3>{name}</h3>
        <dl><Field name="Kind">{text(event.kind)}</Field><Field name="Context">{texts(event.ctx).join(", ")}</Field>
          <Field name="Phase">{texts(event.phase).join(", ")}</Field><Field name="Aggregate">{text(event.agg)}</Field></dl>
        {(["throw", "catch", "ref"] as const).map(role => <details key={role} open>
          <summary>{role === "ref" ? "References" : role === "throw" ? "Throw endpoints" : "Catch endpoints"}</summary>
          <SourceReferences value={event[role]} snapshot={snapshot} navigate={navigate} label={`${role} endpoints`} />
        </details>)}
      </article>;
    }} />
  </SourceFrame>;
}

export function SourceLifecycle({ snapshot, navigate, route }: { snapshot: Snapshot; navigate: Navigate; route: Route }) {
  const data = categoryData(snapshot, "lifecycle");
  if (data === undefined) return null;
  if (!Array.isArray(data)) return <SourceFrame label="lifecycle"><p className="notice">
    Unsupported source lifecycle shape. It has not been interpreted.</p></SourceFrame>;
  const lifecycles = records(data).filter(row => matches(row, { ...route, phase: "" }, text(row.name)));
  return <SourceFrame label="lifecycle">
    {route.phase && <p className="notice">Source lifecycles do not declare phase membership. Phase filter is not
      applied to these records; context and text filters still apply.</p>}
    <Paged label="Source lifecycles" items={lifecycles} render={row => <article className="card">
      <h3>{text(row.name) || text(row.entity) || "Unnamed source lifecycle"}</h3>
      <dl><Field name="Entity">{text(row.entity)}</Field><Field name="Object">{text(row.obj)}</Field>
        <Field name="Context">{texts(row.ctx).join(", ")}</Field></dl>
      <h3>Declared states</h3><p className="muted">Array order is not a transition sequence.</p>
      <Paged label="Source states" items={texts(row.states)} render={state => <p>{state}</p>} />
      <h3>Explicit transitions</h3>
      <Paged label="Source transitions" items={records(row.tr)} render={transition => <dl>
        <Field name="From → To">{text(transition.f)} → {text(transition.t)}</Field>
        <Field name="Command">{text(transition.cmd)}</Field><Field name="Event">{text(transition.ev)}</Field>
        <Field name="Guard (text only, not evaluated)">{text(transition.guard)}</Field>
        <Field name="DMN reference (not evaluated)">{text(transition.dmn)}</Field>
      </dl>} />
      <h3>State references</h3>
      <Paged label="Source state references" items={entries(row.set)} render={([state, refs]) => <div>
        <strong>{state}</strong><SourceReferences value={refs} snapshot={snapshot} navigate={navigate} label="State endpoints" />
      </div>} />
    </article>} />
  </SourceFrame>;
}

export function SourceJourney({ snapshot, navigate, route }: { snapshot: Snapshot; navigate: Navigate; route: Route }) {
  const data = categoryData(snapshot, "journey");
  if (data === undefined) return null;
  if (!isRecord(data)) return <SourceFrame label="journey"><p className="notice">
    Unsupported source journey shape. It has not been interpreted.</p></SourceFrame>;
  const journey = data;
  const phases = records(journey.phases).filter(phase =>
    (!route.phase || text(phase.id) === route.phase)
    && (!route.context || texts(phase.contexts).includes(route.context))
    && (!route.q.trim() || [text(phase.name), text(phase.id), text(phase.purpose)]
      .some(t => t.toLocaleLowerCase().includes(route.q.trim().toLocaleLowerCase()))));
  return <SourceFrame label="journey">
    <p className="muted">Phases retain declared source array order. Phase files are membership references,
      not an inferred execution order within the phase.</p>
    <Paged label="Source journey phases" ordered items={phases} render={phase => <article className="card">
      <h3>{text(phase.name) || text(phase.id) || "Unnamed source phase"}</h3>
      <dl><Field name="Phase ID">{text(phase.id)}</Field><Field name="Purpose">{text(phase.purpose)}</Field>
        <Field name="Gate (text only)">{text(phase.gate)}</Field>
        <Field name="Contexts">{texts(phase.contexts).join(", ")}</Field></dl>
      <Paged label="Phase files" items={texts(phase.files)} render={file =>
        <EndpointLink endpoint={sourceEndpoint(snapshot, file)} snapshot={snapshot} navigate={navigate} />} />
    </article>} />
    <details><summary>Journey entry points (not ordered steps)</summary>
      {(route.phase || route.context) && <p className="notice">Entry points do not declare phase/context
        membership. Those filters are not applied here.</p>}
      <Paged label="Source journey entries" items={records(journey.entries).filter(row =>
        !route.q.trim() || `${text(row.name)} ${text(row.text)}`.toLocaleLowerCase().includes(route.q.trim().toLocaleLowerCase()))}
        render={entry => <article className="card"><h3>{text(entry.name) || "Unnamed entry"}</h3>
          <p>{text(entry.text)}</p><EndpointLink endpoint={sourceEndpoint(snapshot, text(entry.file))}
            snapshot={snapshot} navigate={navigate} />
        </article>} />
    </details>
  </SourceFrame>;
}
