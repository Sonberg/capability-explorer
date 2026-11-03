import { useState } from "react";
import { request, shortSha, type GlossaryCandidate } from "../api";
import { reviewId, type ReviewedLink, type ReviewResult } from "../glossary-api";
import { useApi } from "../state";
import { Button, Card, ErrorBox, H2, inputClass, Mono, Note, Pill } from "./ui";

interface Citation { occurrenceId: string; alias: string; evidenceIds: string[] }
interface Proposal {
  status: "proposed" | "rejected"; derivation: "llm"; reviewStatus: "unreviewed";
  inputHash: string; model: string; promptVersion: string; cached: boolean; problems: string[];
  draft: {
    canonicalTerm: string; definition: string; group: string | null; aliasesToAvoid: string[];
    occurrences: Citation[];
    relationships: { fromOccurrenceId: string; toOccurrenceId: string; relationship: string; cardinality: string; evidenceIds: string[] }[];
    ambiguities: { text: string; occurrenceIds: string[]; evidenceIds: string[] }[];
  } | null;
}

export function GlossaryProposal({ candidate, scope, onChanged }: { candidate: GlossaryCandidate; scope: string; onChanged: () => void }) {
  const [proposal, setProposal] = useState<Proposal>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState("");
  const [reviewer, setReviewer] = useState("");
  const [reviewed, setReviewed] = useState(false);
  const [links, setLinks] = useState<Record<string, ReviewedLink["role"]>>({});
  const [id, setId] = useState(reviewId);
  const [decided, setDecided] = useState(false);
  const reviews = useApi<ReviewResult>(`/api/glossary/review?${scope}&limit=1`);
  const writable = !!reviews.data?.configured && !reviews.loading && !reviews.error;
  const post = <T,>(path: string, body: unknown) => request<T>(`/api/glossary/${path}?${scope}`, {
    method: "POST", headers: { "X-CE-Glossary-Write": "1" }, body: JSON.stringify(body),
  });
  const propose = async () => {
    setBusy(true); setError(undefined); setNotice(""); setProposal(undefined);
    setReviewed(false); setLinks({}); setDecided(false); setId(reviewId());
    try { setProposal(await post<Proposal>("propose", { candidate: candidate.term })); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Proposal request failed."); }
    finally { setBusy(false); }
  };
  const decide = async (decision: "accepted" | "rejected") => {
    if (!proposal || !reviewed || !reviewer.trim() || !writable || busy || decided) return;
    setBusy(true); setError(undefined);
    try {
      await post("proposals/review", {
        id, candidate: candidate.term, inputHash: proposal.inputHash, decision, reviewed, reviewer: reviewer.trim(),
        links: decision === "accepted" ? Object.entries(links).map(([occurrenceId, role]) => ({ occurrenceId, role })) : [],
      });
      setDecided(true);
      setNotice(decision === "accepted" ? "Accepted term and explicitly selected links saved atomically." : "Rejection saved. No term or links were created.");
      onChanged();
    } catch (failure) { setError(failure instanceof Error ? failure.message : "Proposal review failed."); }
    finally { setBusy(false); }
  };
  const label = (id: string) => {
    const source = candidate.occurrences.find(o => o.id === id);
    return source ? `${source.name} · ${source.role} · ${source.repositoryId} · ${shortSha(source.commitSha)} · ${source.unitPath}` : id;
  };
  const citation = (ids: string[]) => ids.length ? ids.join(", ") : "No extracted evidence IDs";
  const draft = proposal?.draft;
  return <Card>
    <H2>Optional glossary proposal</H2>
    <p className="mb-2 text-xs text-zinc-500">Explicitly send this candidate’s names, declaration properties/documentation, calculation attributes and evidence IDs to the configured model. Nothing is sent automatically. Without a model, deterministic candidates remain available without generated definitions.</p>
    <Button disabled={busy} onClick={propose}>{busy ? "Working…" : "Propose terminology"}</Button>
    {error && <ErrorBox message={error} />}
    {proposal && <div className="mt-3 space-y-3">
      <div className="flex flex-wrap gap-2"><Pill>{proposal.derivation}</Pill><Pill tone="warn">{proposal.reviewStatus}</Pill><Pill>{proposal.status}</Pill><Mono>{proposal.model} · {proposal.promptVersion}</Mono>{proposal.cached && <Pill>cached</Pill>}</div>
      <div className="break-all text-xs">Input hash: <Mono>{proposal.inputHash}</Mono></div>
      {proposal.problems.length > 0 && <ErrorBox message={`Rejected model output: ${proposal.problems.join(" ")}`} />}
      {draft && <>
        <div><strong>{draft.canonicalTerm}</strong><p>{draft.definition}</p><p className="text-xs">Group/subdomain: {draft.group ?? "not proposed"}</p></div>
        <p className="text-xs">Existing aliases to avoid: {draft.aliasesToAvoid.join(", ") || "none proposed"}</p>
        <div className="text-xs">Tentative relationships/cardinality — not value flow or equivalence:
          {draft.relationships.length === 0 ? <p>None proposed.</p> : draft.relationships.map((r, i) => <p key={i}>{label(r.fromOccurrenceId)} → {label(r.toOccurrenceId)}: {r.relationship} ({r.cardinality}); evidence: {citation(r.evidenceIds)}</p>)}
        </div>
        <div className="text-xs">Ambiguities:
          {draft.ambiguities.length === 0 ? <p>None proposed; this does not establish certainty.</p> : draft.ambiguities.map((a, i) => <p key={i}>{a.text} · {a.occurrenceIds.map(label).join(" / ")} · evidence: {citation(a.evidenceIds)}</p>)}
        </div>
        <Note>Review the source occurrences below before saving. Only the canonical term, definition, group and individually selected membership roles become human assertions. Relationships and ambiguities are retained with the review, not promoted to source facts. Reviewer names are self-reported, not authenticated.</Note>
        <fieldset disabled={busy || decided || !writable} className="space-y-2">
          <legend className="text-sm font-semibold">Explicitly select reviewed links</legend>
          {draft.occurrences.map(o => <div key={o.occurrenceId} className="text-xs">
            <label><input type="checkbox" checked={o.occurrenceId in links} onChange={event => {
              const checked = event.target.checked;
              setLinks(current => { const next = { ...current }; if (checked) next[o.occurrenceId] = "alias"; else delete next[o.occurrenceId]; return next; });
            }} /> {label(o.occurrenceId)}</label>
            {o.occurrenceId in links && <select aria-label={`Reviewed membership role for ${label(o.occurrenceId)}`} className={inputClass} value={links[o.occurrenceId]} onChange={event => setLinks(current => ({ ...current, [o.occurrenceId]: event.target.value as ReviewedLink["role"] }))}>
              <option value="alias">Alias</option><option value="same">Same — intended equivalence</option><option value="derived_from">Derived from</option>
            </select>}
            <p className="break-all">Evidence: {citation(o.evidenceIds)}</p>
          </div>)}
          <label className="block text-xs">Reviewer<input className={inputClass} maxLength={200} value={reviewer} onChange={e => setReviewer(e.target.value)} /></label>
          <label className="block text-xs"><input type="checkbox" checked={reviewed} onChange={e => setReviewed(e.target.checked)} /> I explicitly reviewed this proposal and the selected source provenance.</label>
        </fieldset>
        {!writable && <Note>{reviews.error ? `Review storage unavailable: ${reviews.error}` : "Saving acceptance/rejection requires the configured review database."}</Note>}
        <div className="flex gap-2">
          <Button disabled={busy || decided || !writable || !reviewed || !reviewer.trim() || Object.keys(links).length === 0} onClick={() => decide("accepted")}>Accept selected links</Button>
          <Button variant="danger" disabled={busy || decided || !writable || !reviewed || !reviewer.trim()} onClick={() => decide("rejected")}>Reject proposal</Button>
        </div>
      </>}
    </div>}
    {notice && <p role="status" className="mt-2 text-sm">{notice}</p>}
  </Card>;
}
