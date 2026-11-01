import { useState, type FormEvent } from "react";
import { type GlossaryOccurrence } from "../api";
import { mutationMessage, reviewMutation, type ObservedOccurrence, type ReviewCollection, type ReviewResult, type ReviewedIssue, type ReviewedLink, type ReviewedTerm } from "../glossary-api";
import { useApi } from "../state";
import { Button, Card, ErrorBox, H2, inputClass, Loading, Mono, Note, Pill, Table } from "./ui";

type Draft = {
  collection: ReviewCollection; id: string; expectedVersion: number; deleting?: boolean;
  name: string; definition: string; group: string; termId: string; occurrenceId: string;
  role: ReviewedLink["role"]; note: string; kind: ReviewedIssue["kind"]; summary: string; status: ReviewedIssue["status"];
};
const fresh = (collection: ReviewCollection, occurrenceId: string, termId = ""): Draft => ({ collection, id: crypto.randomUUID(), expectedVersion: 0, name: "", definition: "", group: "", termId, occurrenceId, role: "same", note: "", kind: "other", summary: "", status: "open" });

export function GlossaryReview({ scope, occurrence, occurrences, onChanged }: {
  scope: string; occurrence: GlossaryOccurrence; occurrences: GlossaryOccurrence[]; onChanged: () => void;
}) {
  const { data, error, loading, reload } = useApi<ReviewResult>(`/api/glossary/review?${scope}&limit=1000`);
  const [reviewer, setReviewer] = useState("");
  const [draft, setDraft] = useState<Draft>();
  const [mutationError, setMutationError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const needsObservation = draft?.collection === "links" && !draft.deleting;
  // This request changes only on explicit occurrence selection or observation reload.
  // Saving/reloading reviewed records never refreshes the source approval snapshot.
  const observation = useApi<ObservedOccurrence>(needsObservation && draft.occurrenceId ? `/api/glossary/observation?occurrence=${encodeURIComponent(draft.occurrenceId)}&${scope}` : null);
  const observed = !observation.loading && !observation.error && observation.data?.occurrenceId === draft?.occurrenceId ? observation.data : undefined;
  const canSave = !needsObservation || !!observed;
  const [notice, setNotice] = useState("");
  const terms = data?.snapshot.terms.filter((item) => !item.deleted) ?? [];
  const links = data?.snapshot.links.filter((item) => !item.deleted) ?? [];
  const issues = data?.snapshot.issues.filter((item) => !item.deleted) ?? [];
  const writable = !!data?.configured && !data.readOnly && !loading && !error;
  const termName = (id: string) => terms.find((item) => item.id === id)?.name ?? id;
  const start = (next: Draft) => { setDraft(next); setMutationError(undefined); setNotice(""); };
  const editTerm = (item: ReviewedTerm, deleting = false) => start({ ...fresh("terms", occurrence.id), ...item, group: item.group ?? "", expectedVersion: item.version, deleting });
  const editLink = (item: ReviewedLink, deleting = false) => {
    const state = data?.linkStates.find((entry) => entry.id === item.id);
    // A stale link cannot silently acquire a new occurrence. Re-review requires an explicit choice.
    start({ ...fresh("links", ""), ...item, note: item.note ?? "", occurrenceId: state?.state === "current" ? state.occurrenceId ?? item.observed.occurrenceId : "", expectedVersion: item.version, deleting });
  };
  const editIssue = (item: ReviewedIssue, deleting = false, resolve = false) => start({ ...fresh("issues", occurrence.id), ...item, note: item.note ?? "", expectedVersion: item.version, deleting, status: resolve ? "resolved" : item.status });
  const save = async (event: FormEvent) => {
    event.preventDefault();
    if (!draft || !writable || busy || !canSave) return;
    setBusy(true); setMutationError(undefined); setNotice("");
    const common = { expectedVersion: draft.expectedVersion, reviewer: reviewer.trim() };
    const nullable = (value: string) => value.trim() || null;
    const body = draft.deleting ? common : draft.collection === "terms"
      ? { ...common, name: draft.name.trim(), definition: draft.definition.trim(), group: nullable(draft.group) }
      : draft.collection === "links"
        ? { ...common, termId: draft.termId, occurrenceId: draft.occurrenceId, observed, role: draft.role, note: nullable(draft.note) }
        : { ...common, termId: draft.termId, kind: draft.kind, summary: draft.summary.trim(), note: nullable(draft.note), status: draft.status };
    try {
      await reviewMutation(scope, draft.collection, draft.id, draft.deleting ? "DELETE" : "PUT", body);
      setDraft(undefined); setNotice("Review change saved."); reload(); onChanged();
    } catch (failure) { setMutationError(mutationMessage(failure)); }
    finally { setBusy(false); }
  };
  const actions = (label: string, edit: () => void, remove: () => void) => <div className="flex flex-wrap gap-2"><Button variant="secondary" disabled={!writable || busy} onClick={edit}>Edit {label}</Button><Button variant="danger" disabled={!writable || busy} onClick={remove}>Delete {label}</Button></div>;
  const patch = (update: Partial<Draft>) => setDraft((current) => current && ({ ...current, ...update }));
  return <Card>
    <H2>Reviewed glossary</H2>
    <p className="mb-2 text-xs text-zinc-500">Human terms, membership assertions and mismatch issues are stored separately from imported facts. Reviewer labels are self-reported, not authenticated identities. “Same” asserts intended equivalence; alias and derived_from retain their distinct meanings and direction.</p>
    {error && <ErrorBox message={`Reviewed glossary unavailable: ${error}`} />}
    {loading && <Loading what="Loading reviewed glossary" />}
    <Button variant="secondary" disabled={busy || loading} onClick={reload}>Reload reviewed data</Button>
    {data && !data.configured && <Note>Read-only: no glossary database is configured. Source connections remain available. Configure CE_DATABASE to persist reviews.</Note>}
    {data?.configured && data.readOnly && <Note>Reviewed data is read-only in this deployment.</Note>}
    {data && (data.snapshot.termsTruncated || data.snapshot.linksTruncated || data.snapshot.issuesTruncated) && <Note>Reviewed data exceeds the {data.snapshot.limit}-record collection limit. Lists and available term choices are incomplete.</Note>}
    <div className="my-3 flex flex-wrap gap-2"><Button disabled={!writable || busy} onClick={() => start(fresh("terms", occurrence.id))}>Create reviewed term</Button><Button disabled={!writable || busy || !terms.length} onClick={() => start(fresh("links", occurrence.id, terms[0]?.id))}>Create reviewed link</Button><Button disabled={!writable || busy || !terms.length} onClick={() => start(fresh("issues", occurrence.id, terms[0]?.id))}>Create mismatch issue</Button></div>
    {notice && <p role="status" className="my-2 text-sm text-emerald-700 dark:text-emerald-300">{notice}</p>}
    {draft && <form onSubmit={save} className="my-3 space-y-3 rounded border border-blue-200 p-3 dark:border-blue-900">
      <H2>{draft.deleting ? "Delete" : draft.expectedVersion ? "Edit" : "Create"} {draft.collection === "terms" ? "reviewed term" : draft.collection === "links" ? "reviewed link" : "mismatch issue"}</H2>
      <p className="break-all text-xs text-zinc-500">Record {draft.id} · expected version {draft.expectedVersion}</p>
      {draft.deleting ? <Note>Delete this reviewed record. Imported source facts remain available. This action records the supplied reviewer label.</Note> : <>
        {draft.collection === "terms" ? <><TextField label="Term name" value={draft.name} onChange={(name) => patch({ name })} maxLength={200} /><TextField label="Definition" value={draft.definition} onChange={(definition) => patch({ definition })} multiline maxLength={10000} /><TextField label="Group (optional)" value={draft.group} onChange={(group) => patch({ group })} required={false} maxLength={200} /></> : <label className="block text-xs font-semibold">Reviewed term<select required value={draft.termId} onChange={(event) => patch({ termId: event.target.value })} className={`${inputClass} mt-1`}><option value="">Select a term</option>{terms.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>}
        {draft.collection === "links" && <><label className="block text-xs font-semibold">Exact source occurrence<select required value={draft.occurrenceId} onChange={(event) => patch({ occurrenceId: event.target.value })} className={`${inputClass} mt-1`}><option value="">Select an occurrence to explicitly review</option>{occurrences.map((item) => <option key={item.id} value={item.id}>{item.name} · {item.role} · {item.repositoryId} · {item.commitSha} · {item.unitPath}</option>)}</select></label><label className="block text-xs font-semibold">Assertion kind<select value={draft.role} onChange={(event) => patch({ role: event.target.value as Draft["role"] })} className={`${inputClass} mt-1`}><option value="same">same — intended equivalence with term</option><option value="alias">alias — alternate naming</option><option value="derived_from">derived_from — term derives from occurrence (occurrence → term)</option></select></label><Note>The exact occurrence, source commit and artifact hash shown below become review provenance. Reanalysis does not carry approval to a changed revision.</Note>{observation.loading && <p className="text-xs">Loading exact observation…</p>}{observation.error && <ErrorBox message={`Observation unavailable: ${observation.error}`} />}{observed && <div className="break-all text-xs">Observed revision: <Mono>{observed.commitSha}</Mono><div>Observed artifact hash: <Mono>{observed.artifactContentSha256}</Mono></div><div>{observed.repositoryId} · {observed.unitPath} · {observed.symbolKey}</div></div>}<Button variant="secondary" disabled={!draft.occurrenceId || busy || observation.loading} onClick={observation.reload}>Reload selected observation for review</Button></>}
        {draft.collection === "issues" && <><label className="block text-xs font-semibold">Mismatch kind<select value={draft.kind} onChange={(event) => patch({ kind: event.target.value as Draft["kind"] })} className={`${inputClass} mt-1`}>{["type", "derivation", "naming", "other"].map((kind) => <option key={kind}>{kind}</option>)}</select></label><TextField label="Issue summary" value={draft.summary} onChange={(summary) => patch({ summary })} maxLength={500} /><label className="block text-xs font-semibold">Issue status<select value={draft.status} onChange={(event) => patch({ status: event.target.value as Draft["status"] })} className={`${inputClass} mt-1`}><option value="open">open</option><option value="resolved">resolved</option></select></label><p className="text-xs text-zinc-500">Resolving an issue records a review decision and does not alter observed source differences.</p></>}
        {draft.collection !== "terms" && <TextField label="Review note (optional)" value={draft.note} onChange={(note) => patch({ note })} multiline required={false} maxLength={10000} />}
      </>}
      <TextField label="Reviewer label (self-reported)" value={reviewer} onChange={setReviewer} maxLength={200} />
      {mutationError && <div role="alert"><ErrorBox message={mutationError} /></div>}
      {!writable && <Note>Writes are unavailable. Your draft is retained.</Note>}
      <div className="flex gap-2"><Button type="submit" variant={draft.deleting ? "danger" : "primary"} disabled={!writable || busy || !canSave}>{busy ? "Saving…" : draft.deleting ? "Confirm deletion" : "Save review"}</Button><Button variant="secondary" disabled={busy} onClick={() => { setDraft(undefined); setMutationError(undefined); }}>Cancel review edit</Button></div>
    </form>}
    {data && <>
    <H2>Terms</H2>
    <Table columns={["Term", "Definition / group", "Attribution", "Actions"]} rows={terms.map((item) => [item.name, <div className="whitespace-pre-wrap break-words">{item.definition}{item.group && <div className="text-xs text-zinc-500">Group: {item.group}</div>}</div>, <Attribution reviewer={item.reviewer} version={item.version} />, actions(item.name, () => editTerm(item), () => editTerm(item, true))])} />
    <H2>Reviewed occurrence links</H2>
    <Table columns={["Term / assertion", "Source and review provenance", "Reference state", "Attribution / actions"]} rows={links.map((item) => {
      const state = data?.linkStates.find((entry) => entry.id === item.id);
      return [<div>{termName(item.termId)}<div><Pill>{item.role}</Pill></div>{item.note && <p className="mt-1 whitespace-pre-wrap text-xs">{item.note}</p>}</div>, <div className="break-all text-xs">{item.repositoryId} · {item.unitPath}<div>{item.symbolKey}</div><div>Reviewed commit: <Mono>{item.observed.commitSha}</Mono></div><div>Artifact: <Mono>{item.observed.artifactContentSha256}</Mono></div><div>Occurrence: <Mono>{item.observed.occurrenceId}</Mono></div></div>, <div><Pill tone={state?.state === "current" ? "ok" : "warn"}>{state?.state ?? "state unavailable"}</Pill><p className="mt-1 text-xs">{state?.reason ?? "Reference state was not returned."}</p></div>, <div className="space-y-2"><Attribution reviewer={item.reviewer} version={item.version} />{actions(`link ${item.id}`, () => editLink(item), () => editLink(item, true))}</div>];
    })} />
    <H2>Mismatch issues</H2>
    <Table columns={["Term / issue", "Status", "Attribution / actions"]} rows={issues.map((item) => [<div>{termName(item.termId)} · {item.kind}<div className="font-semibold">{item.summary}</div>{item.note && <p className="whitespace-pre-wrap text-xs">{item.note}</p>}</div>, <Pill tone={item.status === "resolved" ? "ok" : "warn"}>{item.status}</Pill>, <div className="space-y-2"><Attribution reviewer={item.reviewer} version={item.version} />{actions(`issue ${item.id}`, () => editIssue(item), () => editIssue(item, true))}{item.status === "open" && <Button variant="secondary" disabled={!writable || busy} onClick={() => editIssue(item, false, true)}>Resolve issue {item.id}</Button>}</div>])} />
    </>}
  </Card>;
}
function TextField({ label, value, onChange, multiline = false, required = true, maxLength }: { label: string; value: string; onChange: (value: string) => void; multiline?: boolean; required?: boolean; maxLength: number }) {
  const props = { value, required, maxLength, className: `${inputClass} mt-1`, onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => onChange(event.target.value) };
  return <label className="block text-xs font-semibold">{label}{multiline ? <textarea {...props} rows={3} /> : <input {...props} />}</label>;
}
function Attribution({ reviewer, version }: { reviewer: string; version: number }) {
  return <div className="text-xs">Reviewer label (self-reported): {reviewer}<div className="text-zinc-500">Version {version}</div></div>;
}
