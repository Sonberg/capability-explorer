import { useState } from "react";
import { categories, type ApiResponse, type Process, type Snapshot } from "./contracts";
import { proposal, title } from "./model";
import { Coverage, Diagnostics, Field, Paged } from "./ui";

function Supplemental({ snapshot: s }: { snapshot: Snapshot }) {
  const [category, setCategory] = useState<string>("catalogue");
  if (!s.navigator) return <p>Source navigator: not supplied. Core snapshot views are available above.</p>;
  if (!s.navigator.verified) return <p className="notice">Source navigator: unverified / possibly stale.
    Its categories are not displayed, searched, or used to resolve endpoints.</p>;
  const value = s.navigator[category as typeof categories[number]];
  const items = Array.isArray(value) ? value : value && typeof value === "object" ? Object.entries(value) : [];
  return <details><summary>Verified source navigator · supplemental data</summary>
    <p className="muted">Category schemas are source-specific. These are source records, not inferred routes.
      XML, scripts and expressions are never fetched or evaluated. Text is limited to 8,000 characters per record.</p>
    <label>Source category<select value={category} onChange={e => setCategory(e.target.value)}>
      {categories.map(c => <option key={c}>{c}</option>)}
    </select></label>
    {value == null ? <p>Category not supplied.</p> : typeof value !== "object"
      ? <p className="notice">Unsupported category shape (not interpreted).</p> : <Paged key={category} items={items} label="Source records"
      render={item => {
        const text = JSON.stringify(item, null, 2);
        return <pre>{text.slice(0, 8000)}{text.length > 8000 ? "\n[truncated]" : ""}</pre>;
      }} />}
    <dl><Field name="Bundle path">{s.navigator.bundle.path}</Field>
      <Field name="Bundle SHA-256">{s.navigator.bundle.sha256}</Field>
      <Field name="Generation marker (source supplied, not a freshness guarantee)">
        {typeof s.navigator.generated === "string" || typeof s.navigator.generated === "number"
          ? String(s.navigator.generated).slice(0, 1000) : "Not supplied as a scalar"}
      </Field>
    </dl>
  </details>;
}

export function Inspector({ snapshot: s, process: p, elementId, response }: {
  snapshot: Snapshot; process?: Process; elementId: string; response: ApiResponse;
}) {
  const element = p?.elements.find(e => e.identity === elementId);
  return <aside className="inspector" aria-label="Inspector">
    <p className="eyebrow">Inspect / provenance</p>
    <h2>{element ? element.attributes.name || element.id : p ? title(p) : "Snapshot"}</h2>
    {elementId && !element && <p role="status" className="notice">Linked element is absent from the selected
      process. The link has been preserved; no substitute element was selected.</p>}
    {element && <>
      <dl><Field name="Element identity">{element.identity}</Field><Field name="Element ID">{element.id}</Field>
        <Field name="Type">{element.type}</Field><Field name="Kind">{element.kind}</Field>
        <Field name="Namespace">{element.namespace}</Field><Field name="Parent reference">{element.parent}</Field>
        <Field name="Source path">{element.source.path}</Field><Field name="Source element">{element.source.element}</Field>
      </dl>
      {element.kind !== "bpmn" && <p className="notice">Unsupported/opaque element retained as evidence, not interpreted.</p>}
      <details open><summary>Element attributes (text only)</summary>
        <Paged label="Element attributes" items={Object.entries(element.attributes)}
          render={([key, value]) => <dl><Field name={key}>{value}</Field></dl>} />
      </details>
      {element.content && <details><summary>Element content (not evaluated)</summary>
        <p>Language: {element.content.language || "not supplied"}</p><pre>{element.content.text.slice(0, 12000)}
          {element.content.text.length > 12000 && "\n[truncated at 12,000 characters]"}</pre>
      </details>}
    </>}
    {p && <>
      <p className={p.proposalStatus === "approved" ? "note" : "notice"}>{proposal(p)}</p>
      <dl><Field name="Process identity">{p.processIdentity}</Field><Field name="Source path">{p.path}</Field>
        <Field name="SHA-256">{p.sha256}</Field><Field name="Bytes">{p.byteLength}</Field>
        <Field name="Context tags">{p.contextTags.slice(0, 40).join(", ") + (p.contextTags.length > 40 ? " (first 40 shown)" : "")}</Field>
        <Field name="Phase tags">{p.phaseTags.slice(0, 40).join(", ") + (p.phaseTags.length > 40 ? " (first 40 shown)" : "")}</Field>
      </dl>
      <Coverage value={p.coverage} /><Diagnostics items={p.diagnostics} />
    </>}
    <details className="snapshot-provenance"><summary>Snapshot provenance / source coverage</summary>
      <dl><Field name="Snapshot ID">{s.snapshotId}</Field><Field name="Repository">{s.repositoryId}</Field>
        <Field name="Manifest version">{s.manifestVersion}</Field><Field name="Content profile">{s.contentProfile}</Field>
        <Field name="Identity verification">{String(response.identityVerification?.status ?? "not supplied")}</Field>
        <Field name="Identity reason">{response.identityVerification?.reason}</Field>
      </dl>
      <Coverage value={s.coverage} /><Diagnostics items={s.diagnostics} />
      <Paged label="Source files" items={s.sources} render={source => <dl>
        <Field name="Path">{source.path}</Field><Field name="SHA-256">{source.sha256}</Field>
        <Field name="Bytes">{source.byteLength}</Field>
      </dl>} />
    </details>
    <Supplemental snapshot={s} />
  </aside>;
}
