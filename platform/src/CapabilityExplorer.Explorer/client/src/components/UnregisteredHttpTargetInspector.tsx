import type { ReactNode } from "react";
import type { Evidence } from "../api";
import type { ArchitectureHttpTargetDetail, ArchitectureHttpTargets, EvidenceRecord, Graph } from "../architecture-types";
import { symbolHref } from "../state";
import { EvidenceLinks } from "./source";
import { Button, Pill } from "./ui";
import { UNREGISTERED_HTTP_TARGET_BADGE, UNREGISTERED_HTTP_TARGET_CATEGORY } from "../../../../../../spikes/likec4/src/live-contracts";

export { UNREGISTERED_HTTP_TARGET_CATEGORY };

const outcomeLabels: Record<string, string> = {
  "no-known-provider": "No known provider",
  "known-provider": "Known provider",
  "ambiguous-known-providers": "Ambiguous known providers",
  "broken-binding": "Broken binding",
  "dynamic/insufficient-evidence": "Dynamic or insufficient evidence",
  "partial-provider-coverage": "Partial provider coverage",
};

const plural = (count: number, one: string, many = `${one}s`) => `${count} ${count === 1 ? one : many}`;

const sourceEvidence = (record: EvidenceRecord): Evidence => ({
  id: record.id, repositoryId: record.repositoryId, commitSha: record.commitSha, path: record.path,
  startLine: record.span.startLine, startColumn: record.span.startColumn, endLine: record.span.endLine, endColumn: record.span.endColumn,
});

function Section({ title, children }: { title: string; children: ReactNode }) {
  return <section className="space-y-1 border-t border-zinc-200 pt-2 dark:border-zinc-700">
    <h3 className="text-xs font-semibold text-zinc-600 dark:text-zinc-300">{title}</h3>
    {children}
  </section>;
}

function List({ items }: { items: string[] }) {
  return items.length ? <ul className="list-disc space-y-0.5 break-words pl-5 text-xs">{items.map(item => <li key={item}>{item}</li>)}</ul> : null;
}

/** Selection-wide candidate counts, distinct outcomes and recovery beside the graph. */
export function UnregisteredHttpTargetStatus({ targets, hiddenByKind, onShow }: {
  targets: ArchitectureHttpTargets;
  hiddenByKind: boolean;
  onShow: () => void;
}) {
  const omitted = Math.max(0, targets.matchedCandidates - targets.shownCandidates);
  const unmatched = Math.max(0, targets.totalCandidates - targets.matchedCandidates);
  const coverage = targets.providerCoverage;
  return <div role="status" aria-label="Unregistered target candidates" className="space-y-1 break-words rounded-lg border border-dashed border-zinc-400 px-3 py-2 text-xs dark:border-zinc-600">
    <div className="font-semibold">{`Unregistered target candidates: ${targets.totalCandidates} in selection · ${targets.matchedCandidates} matched · ${targets.shownCandidates} shown`}</div>
    {omitted > 0 && <div>{`${plural(omitted, "matched candidate")} ${omitted === 1 ? "is" : "are"} omitted by graph budgets; search, pin from omitted elements or drill down.`}</div>}
    {/* Totals are selection-wide; matched counts are limited to this diagram scope and its filters. */}
    {unmatched > 0 && <div>
      {`${plural(unmatched, "candidate")} in the selection ${unmatched === 1 ? "is" : "are"} outside this diagram scope or excluded by the current filters. `}
      Open a broader Architecture level to include candidates outside this scope; changing filters recovers only candidates excluded by filters.
    </div>}
    {hiddenByKind && <div className="flex flex-wrap items-center gap-2">
      <span>Hidden by the relationship kind filter.</span>
      <Button variant="secondary" onClick={onShow}>Show unregistered targets</Button>
    </div>}
    {targets.totalCandidates > 0 && <div>{`Candidate details listed: ${targets.details.length} of ${targets.totalCandidates}${targets.detailsTruncated ? " (bounded)" : ""}.`}</div>}
    {coverage.status === "partial" && <div>{`Provider coverage is partial: ${plural(targets.missingRepositoryCount, "registered repository", "registered repositories")} without analysed provider evidence. Candidates may have a provider outside the selection.`}</div>}
    {targets.outcomes.length > 0 && <ul aria-label="HTTP target outcomes" className="flex flex-wrap gap-x-4 gap-y-0.5">
      {targets.outcomes.map(outcome => <li key={outcome.status}>{`${outcomeLabels[outcome.status] ?? outcome.status} (${outcome.status}): ${plural(outcome.groups, "group")} · ${plural(outcome.occurrences, "occurrence")}`}</li>)}
    </ul>}
  </div>;
}

/** Read-only inspector for one candidate in the current result; it never adopts or binds the target. */
export function UnregisteredHttpTargetInspector({ detail, targets, graph, onOpen }: {
  detail: ArchitectureHttpTargetDetail;
  targets: ArchitectureHttpTargets;
  graph: Graph;
  onOpen: (to: { level: string; focus: string }) => void;
}) {
  const edges = graph.edges.filter(edge => edge.to === detail.id && edge.kind === UNREGISTERED_HTTP_TARGET_CATEGORY);
  const edgeOccurrences = edges.reduce((sum, edge) => sum + edge.count, 0);
  const coverage = targets.providerCoverage;
  const bounded = detail.detailsTruncated || detail.consumers.length < detail.consumerCount || detail.operations.length < detail.operationCount
    || detail.occurrences.length < detail.occurrenceCount || detail.alternatives.length < detail.alternativeCount;
  return <section role="region" aria-label="Unregistered target candidate" className="min-w-0 space-y-2 break-words">
    <div className="flex flex-wrap items-center gap-2">
      <span aria-hidden="true" className="inline-block size-2.5 rounded-sm border border-dashed border-zinc-500" />
      <Pill>{UNREGISTERED_HTTP_TARGET_BADGE}</Pill>
      <span className="text-xs text-zinc-500">{`${outcomeLabels[detail.status] ?? detail.status} (${detail.status})`}</span>
    </div>
    <div className="text-base font-semibold">{detail.label}</div>
    <p className="text-xs">No known provider in the selected catalogue. This is not proof that no runtime service exists.</p>
    <div className="flex flex-wrap gap-2">
      <Button onClick={() => onOpen(detail.recovery)}>Open candidate occurrences</Button>
      <a href={symbolHref(detail.caller)} className="rounded-md border border-zinc-300 px-3 py-1.5 text-[13px] font-semibold hover:bg-zinc-50 dark:border-zinc-700 dark:hover:bg-zinc-800">Open caller in Explore</a>
    </div>
    <Section title="Source">
      <div className="space-y-0.5 font-mono text-xs">
        <div>{`Repository ${detail.repositoryId} · unit ${detail.unitPath}`}</div>
        <div className="break-all">{`Caller ${detail.caller}`}</div>
        {detail.client && <div>{`Client ${detail.client}`}</div>}
        <div className="break-all">{`Destination hint ${detail.destination}`}</div>
      </div>
    </Section>
    <Section title={`Consumers · ${detail.consumers.length} of ${detail.consumerCount} Capability memberships (selection-wide)`}>
      <List items={detail.consumers.map(consumer => `${consumer.capabilityName} · ${consumer.repositoryId}`)} />
    </Section>
    <Section title={`Distinct operations · ${detail.operations.length} of ${detail.operationCount} listed (selection-wide)`}>
      <List items={detail.operations} />
      <p className="text-xs text-zinc-500">{`Graph edges · ${plural(edgeOccurrences, "occurrence")} on ${plural(edges.length, "shown edge")} (edge support, not distinct operations)`}</p>
    </Section>
    <Section title="Grouping reasons"><List items={detail.reasons} /></Section>
    <Section title={`Occurrences · ${detail.occurrences.length} of ${detail.occurrenceCount} listed (selection-wide)`}>
      <ul className="space-y-2">{detail.occurrences.map(({ occurrence, evidenceCount }) => <li key={occurrence.id} className="space-y-0.5 rounded-md border border-zinc-200 p-2 text-xs dark:border-zinc-800">
        {occurrence.operation && <div className="font-mono">{occurrence.operation}</div>}
        <div className="break-all font-mono">{`${occurrence.source.repositoryId} @ ${occurrence.source.commitSha}`}</div>
        <div className="break-all font-mono">{`unit ${occurrence.source.unitPath}`}</div>
        <div className="break-all font-mono">{`artifact ${occurrence.source.artifactHash}`}</div>
        <div className="break-all font-mono">{`edge ${occurrence.edgeId}`}</div>
        <div>{`Evidence · ${occurrence.evidence.length} of ${plural(evidenceCount, "record")} listed`}</div>
        <EvidenceLinks evidence={occurrence.evidence.map(sourceEvidence)} />
        <ul className="space-y-0.5">{occurrence.evidence.map(record => <li key={record.id} className="break-all font-mono text-[11px] text-zinc-500">
          {`${record.path} · lines ${record.span.startLine}:${record.span.startColumn}–${record.span.endLine}:${record.span.endColumn} · snippet sha256 ${record.snippetSha256} · ${record.extractor.name} ${record.extractor.version}`}
        </li>)}</ul>
      </li>)}</ul>
    </Section>
    <Section title={`Alternatives · ${detail.alternatives.length} of ${detail.alternativeCount} listed`}>
      <p className="text-xs text-zinc-500">Alternatives are exact endpoint matches in the selected catalogue, not inferred aliases or service families.</p>
      <List items={detail.alternatives.map(alternative => `${alternative.key} · ${alternative.source.repositoryId} @ ${alternative.source.commitSha}`)} />
    </Section>
    {detail.missingEvidence.length > 0 && <Section title="Missing evidence"><List items={detail.missingEvidence} /></Section>}
    <Section title={`Provider coverage: ${coverage.status}`}>
      <p className="text-xs">{`${plural(targets.providerSourceCount, "provider source")} · ${plural(targets.missingRepositoryCount, "missing repository", "missing repositories")}`}</p>
      <List items={coverage.missingRepositories} />
      <List items={coverage.qualifications} />
    </Section>
    {bounded && <p className="text-xs text-zinc-500">Detail lists are bounded. Open candidate occurrences, then search or pin occurrences to recover the rest.</p>}
    <p className="text-[11px] text-zinc-500">{`Rule ${targets.ruleVersion} · result ${targets.resultId}`}</p>
  </section>;
}
