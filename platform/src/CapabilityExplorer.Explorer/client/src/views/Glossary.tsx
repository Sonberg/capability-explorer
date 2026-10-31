import { useEffect, useMemo, useRef, useState } from "react";
import { shortSha, type AttributeValue, type Comparison, type GlossaryCandidate, type GlossaryOccurrence } from "../api";
import { SourceView } from "../components/source";
import { Card, ErrorBox, H1, H2, inputClass, jsonText, Loading, Mono, Muted, Note, Pill, Table } from "../components/ui";
import { href, useApi, useSelection } from "../state";

type Difference = Comparison["differences"][number];

export function Glossary({ params }: { params: URLSearchParams }) {
  const { scope } = useSelection();
  // useApi retains its previous response while fetching. Remount on scope changes
  // so evidence from a previous capability or revision cannot remain visible.
  return <ScopedGlossary key={scope} scope={scope} selectedTerm={params.get("term")} />;
}

function ScopedGlossary({ scope, selectedTerm }: { scope: string; selectedTerm: string | null }) {
  const { data: candidates, error } = useApi<GlossaryCandidate[]>(`/api/glossary?${scope}`);
  const [search, setSearch] = useState("");
  const detail = useRef<HTMLDivElement>(null);
  const selected = candidates?.find((candidate) => candidate.term === selectedTerm);
  const rows = useMemo(() => {
    const query = search.trim().toLowerCase();
    return (candidates ?? []).filter((candidate) => !query || [
      candidate.term, ...candidate.repositories, ...candidate.capabilities,
      ...candidate.occurrences.flatMap((occurrence) => [occurrence.name, occurrence.kind, occurrence.role, occurrence.type ?? "unknown"]),
    ].some((value) => value.toLowerCase().includes(query)));
  }, [candidates, search]);

  useEffect(() => {
    if (selected) detail.current?.scrollIntoView({ behavior: "smooth", block: "start" });
  }, [selected]);

  return (
    <div className="space-y-3">
      <div>
        <H1>Glossary candidates</H1>
        <Muted>Explicit names grouped by normalized label. A candidate is not a reviewed assertion of business equivalence. Qualifiers such as calendar, active and Id remain distinct.</Muted>
      </div>
      <div className="flex flex-wrap items-end gap-3">
        <label className="block w-full max-w-md text-xs font-semibold" htmlFor="glossary-search">
          Search glossary candidates
          <input id="glossary-search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Term, name, repository, capability or type" className={`${inputClass} mt-1`} />
        </label>
        {candidates && <span role="status" className="pb-1 text-xs text-zinc-500">{rows.length} of {candidates.length} candidates in scope</span>}
      </div>
      {error ? <ErrorBox message={error} /> : !candidates ? <Loading what="Loading glossary candidates" /> : (
        <>
          {candidates.length === 0 ? <Note>No explicit named occurrences are available in this capability and revision selection.</Note> : rows.length === 0 ? <Note>No candidates match this search. Change or clear the search to see terms in scope.</Note> : (
            <Table
              columns={["Candidate label", "Occurrences", "Repositories / capabilities", "Types", "Observed comparisons"]}
              selected={(index) => rows[index].term === selectedTerm}
              rows={rows.map((candidate) => [
                <a href={href("glossary", { term: candidate.term })} aria-current={candidate.term === selectedTerm ? "true" : undefined} className="font-semibold text-blue-700 hover:underline dark:text-blue-300">{candidate.term}</a>,
                candidate.occurrences.length,
                <Membership candidate={candidate} />,
                <TypeSummary occurrences={candidate.occurrences} />,
                <CandidateFacts candidate={candidate} />,
              ])}
            />
          )}
          {selectedTerm && !selected && <Note>The selected candidate “{selectedTerm}” is not present in this capability and revision selection. Choose a candidate from the current list.</Note>}
          {selected && <div ref={detail} className="scroll-mt-32 space-y-3"><CandidateDetail key={selected.term} candidate={selected} /></div>}
        </>
      )}
    </div>
  );
}

function Membership({ candidate }: { candidate: GlossaryCandidate }) {
  return <div className="space-y-1 text-xs">
    <div>{candidate.repositories.length} repositories: {candidate.repositories.join(", ") || "none"}</div>
    <div className="text-zinc-500">{candidate.capabilities.length} capabilities: {candidate.capabilities.join(", ") || "none recorded"}</div>
  </div>;
}

function TypeSummary({ occurrences }: { occurrences: GlossaryOccurrence[] }) {
  const known = [...new Set(occurrences.flatMap((occurrence) => occurrence.type ? [occurrence.type] : []))];
  const unknown = occurrences.filter((occurrence) => !occurrence.type).length;
  return <div className="text-xs">
    {known.length > 0 && <div>Known: {known.join(", ")}</div>}
    {unknown > 0 && <div className="italic text-zinc-500">Unknown: {unknown} occurrences</div>}
    {known.length === 0 && unknown === 0 && <span className="text-zinc-500">No type evidence</span>}
  </div>;
}

function CandidateFacts({ candidate }: { candidate: GlossaryCandidate }) {
  const gaps = candidate.comparisons.reduce((count, comparison) => count + comparison.differences.filter((difference) => difference.kind === "KnownOnOneSide").length, 0);
  return <div className="flex flex-wrap gap-1">
    {candidate.typeDiffers && <Pill tone="warn">known types differ</Pill>}
    {candidate.derivationDiffers && <Pill tone="warn">known attributes differ</Pill>}
    {candidate.expressionDiffers && <Pill>expression hashes differ</Pill>}
    {gaps > 0 && <Pill>{gaps} evidence gaps</Pill>}
    {!candidate.comparisonsComplete && <Pill tone="warn">comparisons incomplete</Pill>}
    {!candidate.typeDiffers && !candidate.derivationDiffers && !candidate.expressionDiffers && gaps === 0 && <span className="text-xs text-zinc-500">No observed differences</span>}
  </div>;
}

function CandidateDetail({ candidate }: { candidate: GlossaryCandidate }) {
  return <>
    <Card>
      <H2>{candidate.term}</H2>
      <Muted className="mb-3 text-xs">Candidate group · {candidate.occurrences.length} occurrences. Equal labels do not establish equal meaning or interchangeable values.</Muted>
      <Membership candidate={candidate} />
      <div className="my-3"><TypeSummary occurrences={candidate.occurrences} /></div>
      <CandidateFacts candidate={candidate} />
      <p className="mt-3 text-xs text-zinc-500">Unknown types are missing evidence and do not count as type mismatches.</p>
    </Card>
    <Card>
      <H2>Calculation comparisons</H2>
      <Muted className="text-xs">{candidate.comparisons.length} of {candidate.possibleComparisons} possible calculation pairs compared. Only calculation occurrences participate.</Muted>
      {!candidate.comparisonsComplete && <Note>Comparison coverage is incomplete: the deterministic pair limit was reached. Uncompared pairs may contain additional differences or evidence gaps.</Note>}
      <p className="my-2 text-xs text-zinc-500">Different values or details are observed attribute differences. “Known on one side” is an evidence gap, not a business divergence. Expression-hash inequality is not proof of mathematical inequivalence.</p>
      {candidate.comparisons.length === 0 ? <Note>No calculation pairs are available for comparison in this candidate.</Note> : candidate.comparisons.map((comparison) => {
        const left = candidate.occurrences.find((occurrence) => occurrence.id === comparison.leftId);
        const right = candidate.occurrences.find((occurrence) => occurrence.id === comparison.rightId);
        const differences = comparison.differences.filter((difference) => difference.kind !== "KnownOnOneSide");
        const gaps = comparison.differences.filter((difference) => difference.kind === "KnownOnOneSide");
        return <div key={`${comparison.leftId}:${comparison.rightId}`} className="mt-4 border-t border-zinc-200 pt-3 dark:border-zinc-800">
          <div className="mb-2 flex flex-wrap items-center gap-2">
            <span className="font-semibold">{left?.name ?? "Unknown occurrence"} / {right?.name ?? "Unknown occurrence"}</span>
            <Pill>{comparison.sameExpression === null ? "expression hash unavailable" : comparison.sameExpression ? "same expression hash" : "different expression hashes"}</Pill>
          </div>
          <div className="mb-2 text-xs text-zinc-500"><OccurrenceLabel occurrence={left} /> vs <OccurrenceLabel occurrence={right} /></div>
          {differences.length > 0 ? <DifferenceTable differences={differences} /> : <Muted className="text-xs">No known attribute values or details differ in this pair.</Muted>}
          {gaps.length > 0 && <div className="mt-3"><div className="mb-1 text-xs font-semibold">Evidence gaps</div><DifferenceTable differences={gaps} /></div>}
        </div>;
      })}
    </Card>
    <H2>Source occurrences</H2>
    {candidate.occurrences.map((occurrence) => <OccurrenceCard key={occurrence.id} occurrence={occurrence} />)}
  </>;
}

function OccurrenceLabel({ occurrence }: { occurrence?: GlossaryOccurrence }) {
  return occurrence ? <span>{occurrence.repositoryId} · {shortSha(occurrence.commitSha)} · {occurrence.unit}</span> : <span>Source occurrence unavailable</span>;
}

function DifferenceTable({ differences }: { differences: Difference[] }) {
  const labels: Record<Difference["kind"], string> = {
    ValueDiffers: "Different known values",
    DetailDiffers: "Different details",
    KnownOnOneSide: "Known on one side — evidence gap",
  };
  return <Table columns={["Attribute", "Left", "Right", "Observation"]} rows={differences.map((difference) => [
    difference.attribute,
    <AttributeText value={difference.left} />,
    <AttributeText value={difference.right} />,
    <span className="text-xs">{labels[difference.kind]}</span>,
  ])} />;
}

function AttributeText({ value }: { value?: AttributeValue }) {
  if (!value || value.status === "unknown") return <span className="italic text-zinc-500">unknown</span>;
  return <div>
    <span>{value.value ?? "value unavailable"}</span>
    {value.status !== "known" && <span className="ml-1"><Pill>{value.status}</Pill></span>}
    {value.detail && <div className="mt-1 break-words text-xs text-zinc-500">{jsonText(value.detail)}</div>}
  </div>;
}

function OccurrenceCard({ occurrence }: { occurrence: GlossaryOccurrence }) {
  const [showSource, setShowSource] = useState(false);
  return <Card>
    <div className="mb-1 flex flex-wrap items-center gap-2"><span className="font-semibold">{occurrence.name}</span><Pill>{occurrence.kind}</Pill><Pill>{occurrence.role}</Pill></div>
    <div className="text-xs"><OccurrenceLabel occurrence={occurrence} /></div>
    <div className="mt-1 break-all text-xs text-zinc-500">Revision: <Mono>{occurrence.commitSha}</Mono></div>
    <div className="mt-1 break-all text-xs text-zinc-500">Unit path: {occurrence.unitPath}</div>
    <div className="mt-1 text-xs">Type: {occurrence.type ? <Mono>{occurrence.type}</Mono> : <span className="italic text-zinc-500">unknown</span>}</div>
    <div className="mt-1 text-xs text-zinc-500">Capabilities: {occurrence.capabilities.join(", ") || "none recorded"}</div>
    {occurrence.evidence.length === 0 ? <Note>No source evidence was recorded for this occurrence.</Note> : <div className="mt-3">
      <button type="button" aria-expanded={showSource} onClick={() => setShowSource((shown) => !shown)} className="text-xs font-semibold text-blue-700 hover:underline dark:text-blue-300">{showSource ? "Hide" : "Show"} source evidence ({occurrence.evidence.length}) for {occurrence.name}</button>
      {showSource && occurrence.evidence.map((evidence) => <div key={evidence.id} className="mt-2"><div className="break-all text-xs text-zinc-500">{evidence.repositoryId} · <Mono>{evidence.commitSha}</Mono></div><SourceView evidence={evidence} /></div>)}
    </div>}
  </Card>;
}
