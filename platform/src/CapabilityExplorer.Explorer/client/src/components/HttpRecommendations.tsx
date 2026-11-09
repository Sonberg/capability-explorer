import { useEffect, useState } from "react";
import { FileCheck, RefreshCw } from "lucide-react";
import type { Evidence } from "../api";
import { useApi } from "../state";
import type { PreviewSource } from "../views/BindingPreviewState";
import { EvidenceLinks } from "./source";
import { ErrorBox, Loading } from "./ui";

interface HttpEvidence {
  id: string; repositoryId: string; commitSha: string; path: string;
  span: { startLine: number; startColumn: number; endLine: number; endColumn: number };
}
interface HttpRequestEvidence { source: PreviewSource; edgeId: string; target: string | null; evidence: HttpEvidence[] }
export interface HttpBindingCandidate {
  target: string; score: number; matchedRoutes: number; totalRoutes: number; state: string; previewEligible: boolean;
  features: { name: string; points: number }[];
  matches: { request: HttpRequestEvidence; endpoint: { source: PreviewSource; key: string; evidence: HttpEvidence[] } }[];
  unmatched: HttpRequestEvidence[]; missingEvidence: string[];
}
export interface HttpRecommendationGroup {
  id: string; hint: string | null; label: string; state: string; requests: HttpRequestEvidence[]; candidates: HttpBindingCandidate[];
}
export interface HttpRecommendationSnapshot {
  fingerprint: string; algorithmVersion: string; sourceView: string; targetView: string; groups: HttpRecommendationGroup[];
  owners: { repositoryId: string; capabilityId: string; name: string }[];
}

export function selectionIdentity(scope: string): string {
  const query = new URLSearchParams(scope);
  query.delete("g");
  query.sort();
  return query.toString();
}

function evidenceLinks(records: HttpEvidence[]): Evidence[] {
  return records.map(record => ({ id: record.id, repositoryId: record.repositoryId, commitSha: record.commitSha,
    path: record.path, ...record.span }));
}

export function HttpCandidates({ group, snapshot, onChoose }: {
  group: HttpRecommendationGroup; snapshot: HttpRecommendationSnapshot; scope: string;
  onChoose?: (candidate: HttpBindingCandidate) => void;
}) {
  return <div className="min-w-0 space-y-2">
    <p className="text-xs text-zinc-500">{group.state} / {group.requests.length} observed call{group.requests.length === 1 ? "" : "s"}</p>
    {group.candidates.length === 0 && <p className="text-sm">{group.state === "broken-binding" ? "Existing binding has missing endpoints." : group.state === "insufficient-evidence" ? "No static HTTP target available." : "No matching exposed endpoint in these revisions."}</p>}
    <ol className="divide-y divide-zinc-200 dark:divide-zinc-700">
      {group.candidates.map(candidate => {
        const repositories = new Set(candidate.matches.map(match => match.endpoint.source.repositoryId));
        const owners = snapshot.owners.filter(owner => repositories.has(owner.repositoryId));
        const names = [...new Set(owners.map(owner => owner.name))];
        return <li key={candidate.target} className="min-w-0 space-y-2 py-3">
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <strong className="break-words text-sm">{names.length ? names.join(", ") : repositories.size ? [...repositories].join(", ") : candidate.target}</strong>
            <span className="text-xs tabular-nums" title="Observed method/route coverage, not confidence or proof of host ownership">{candidate.matchedRoutes}/{candidate.totalRoutes} routes / score {candidate.score}</span>
          </div>
          <div className="break-all font-mono text-xs">{candidate.target}</div>
          <details className="text-xs">
            <summary className="cursor-pointer">Evidence and gaps</summary>
            <ul className="my-2 space-y-1">{candidate.features.map(feature => <li key={feature.name}>{feature.name}: {feature.points}</li>)}</ul>
            {candidate.matches.map((match, index) => <div key={`${match.request.edgeId}/${match.endpoint.key}/${index}`} className="my-3 min-w-0 space-y-1">
              <p className="break-all font-mono">{match.request.target?.split("|").at(-1)}</p>
              <p className="break-all">Request: {match.request.source.repositoryId} @ {match.request.source.commitSha.slice(0, 12)}</p>
              <EvidenceLinks evidence={evidenceLinks(match.request.evidence)} />
              <p className="break-all">Endpoint: {match.endpoint.source.repositoryId} @ {match.endpoint.source.commitSha.slice(0, 12)}</p>
              <EvidenceLinks evidence={evidenceLinks(match.endpoint.evidence)} />
            </div>)}
            {candidate.unmatched.length > 0 && <div className="my-2"><strong>Unmatched calls</strong><ul>{candidate.unmatched.map(call => <li key={`${call.source.repositoryId}/${call.source.unitPath}/${call.edgeId}`} className="break-all">{call.target ?? "Dynamic target"}<EvidenceLinks evidence={evidenceLinks(call.evidence)} /></li>)}</ul></div>}
            <p className="break-words text-zinc-500">Unknown: {candidate.missingEvidence.join(", ")}</p>
          </details>
          {group.hint && candidate.previewEligible ? onChoose
            ? <button type="button" className="inline-flex items-center gap-1.5 rounded border border-zinc-300 px-2 py-1 text-sm dark:border-zinc-600" onClick={() => onChoose(candidate)}><FileCheck size={14} />Use {candidate.target}</button>
            : <p className="text-xs text-zinc-500">Candidate evidence only. Binding review is not available in this UI.</p>
            : <p className="text-xs text-amber-700 dark:text-amber-300">{group.hint ? "Some calls cannot be validated by an exact binding." : "No bindable configuration hint."}</p>}
        </li>;
      })}
  </ol>
  </div>;
}

export function HttpRecommendationInspector({ scope, focus, onClose }: { scope: string; focus?: string; onClose: () => void }) {
  const response = useApi<HttpRecommendationSnapshot>(`/api/binding-preview/recommendations?${scope}`);
  const [selected, setSelected] = useState("");
  useEffect(() => {
    if (!response.data || response.loading) return;
    const groups = response.data.groups;
    setSelected(previous => groups.find(group => focus && (group.hint === focus || group.label === focus))?.id
      ?? (groups.some(group => group.id === previous) ? previous : groups[0]?.id ?? ""));
  }, [response.data, response.loading, focus]);
  const group = response.data?.groups.find(item => item.id === selected);
  return <section aria-label="HTTP capability recommendations" className="min-w-0 space-y-3">
    <div className="flex items-center justify-between gap-2"><h2 className="text-sm font-semibold">Capability candidates</h2>
      <button type="button" onClick={response.reload} title="Refresh recommendations" aria-label="Refresh recommendations"><RefreshCw size={15} /></button>
      <button type="button" onClick={onClose} className="text-xs text-zinc-500">Close</button>
    </div>
    {response.error && <ErrorBox message={response.error} />}
    {response.loading ? <Loading what="Loading HTTP candidates" /> : response.data && <>
      <label className="block text-xs">Unresolved HTTP host<select aria-label="Unresolved HTTP host" className="mt-1 w-full min-w-0 rounded border border-zinc-300 bg-transparent p-1.5 dark:border-zinc-600" value={selected} onChange={event => setSelected(event.target.value)}>
        {response.data.groups.map(item => <option key={item.id} value={item.id}>{item.label} ({item.state})</option>)}
      </select></label>
      <p className="text-xs text-zinc-500">{response.data.groups.length} unresolved groups / {response.data.algorithmVersion}</p>
      {group && <HttpCandidates group={group} snapshot={response.data} scope={scope} />}
      {response.data.groups.length === 0 && <p>No unresolved HTTP calls in this selection.</p>}
    </>}
  </section>;
}