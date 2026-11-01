import { useEffect, useMemo, useRef, useState } from "react";
import { shortSha, type GlossaryConcept, type GlossaryConceptView, type GlossaryMember } from "../api";
import { SourceView } from "../components/source";
import { Card, ErrorBox, H1, H2, inputClass, Loading, Mono, Muted, Note, Pill, Table } from "../components/ui";
import { href, useApi } from "../state";

const tierTone = { core: "ok", supporting: "info", plumbing: "neutral" } as const;

export function GlossaryConcepts({ scope, selectedKey }: { scope: string; selectedKey: string | null }) {
  const { data: view, error } = useApi<GlossaryConceptView>(`/api/glossary/concepts?${scope}`);
  const [search, setSearch] = useState("");
  const [showPlumbing, setShowPlumbing] = useState(false);
  const detail = useRef<HTMLDivElement>(null);
  const selected = view?.concepts.find((concept) => concept.key === selectedKey);
  const plumbing = view?.concepts.filter((concept) => concept.tier === "plumbing").length ?? 0;
  const rows = useMemo(() => {
    const query = search.trim().toLowerCase();
    return (view?.concepts ?? []).filter((concept) => (showPlumbing || concept.tier !== "plumbing" || concept.key === selectedKey) &&
      (!query || [concept.term, concept.key, ...concept.repositories, ...concept.members.map((member) => member.name),
        ...concept.attributes.map((field) => field.label)].some((value) => value.toLowerCase().includes(query))));
  }, [view, search, showPlumbing, selectedKey]);

  useEffect(() => {
    if (selected) detail.current?.scrollIntoView({ behavior: "smooth", block: "start" });
  }, [selected]);

  return (
    <div className="space-y-3">
      <div>
        <H1>Glossary concepts</H1>
        <Muted>Declarations whose names differ only by a representation suffix (Dto, Row, Response…) or a plural, ranked by facts found in the artifacts. A concept is a navigation aid, not a reviewed domain model; names that group do not hold interchangeable values.</Muted>
      </div>
      {view?.limits.map((limit) => <Note key={limit}>{limit}</Note>)}
      {view && view.partialUnits.length > 0 && <Note>Partial units: {view.partialUnits.join(", ")}</Note>}
      <div className="flex flex-wrap items-end gap-3">
        <label className="block w-full max-w-md text-xs font-semibold" htmlFor="concept-search">
          Search concepts
          <input id="concept-search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Term, member, attribute or repository" className={`${inputClass} mt-1`} />
        </label>
        <label className="flex items-center gap-2 pb-1 text-xs">
          <input type="checkbox" checked={showPlumbing} onChange={(event) => setShowPlumbing(event.target.checked)} />
          Show plumbing ({plumbing} hidden by default)
        </label>
        {view && <span role="status" className="pb-1 text-xs text-zinc-500">{rows.length} of {view.concepts.length} concepts · {view.excludedTestDeclarations} test declarations excluded</span>}
      </div>
      {error ? <ErrorBox message={error} /> : !view ? <Loading what="Loading glossary concepts" /> : (
        <>
          {view.concepts.length === 0 ? <Note>No concept-bearing declarations are available in this capability and revision selection.</Note> : rows.length === 0 ? <Note>No concepts match. Clear the search{showPlumbing ? "" : " or show plumbing"} to see more.</Note> : (
            <Table
              columns={["Concept", "Tier", "Why", "Members", "Attributes"]}
              selected={(index) => rows[index].key === selectedKey}
              rows={rows.map((concept) => [
                <a href={href("glossary", { concept: concept.key })} aria-current={concept.key === selectedKey ? "true" : undefined} className="font-semibold text-blue-700 hover:underline dark:text-blue-300">{concept.term}</a>,
                <div className="space-y-1"><Pill tone={tierTone[concept.tier]}>{concept.tier}</Pill><div className="text-xs text-zinc-500">score {concept.score}</div></div>,
                <Reasons concept={concept} />,
                <div className="text-xs"><div>{concept.members.length} member{concept.members.length === 1 ? "" : "s"}{concept.variants.length > 0 ? ` · ${concept.variants.length} variants` : ""}</div><div className="text-zinc-500">{concept.repositories.join(", ")}</div></div>,
                <Attributes concept={concept} limit={4} />,
              ])}
            />
          )}
          {selectedKey && !selected && <Note>The selected concept “{selectedKey}” is not present in this capability and revision selection.</Note>}
          {selected && <div ref={detail} className="scroll-mt-32 space-y-3"><ConceptDetail key={selected.key} concept={selected} bias={selected.score - selected.why.reduce((sum, reason) => sum + reason.points, 0)} /></div>}
          <Metrics view={view} />
        </>
      )}
    </div>
  );
}

function Reasons({ concept }: { concept: GlossaryConcept }) {
  if (concept.why.length === 0) return <span className="text-xs text-zinc-500">no features</span>;
  return <div className="flex flex-wrap gap-1">
    {concept.why.slice(0, 4).map((reason) => <Pill key={reason.feature} tone={reason.points > 0 ? "ok" : reason.points < 0 ? "warn" : "neutral"} title={reason.detail ?? undefined}>{reason.feature} {reason.points > 0 ? "+" : ""}{reason.points}</Pill>)}
    {concept.why.length > 4 && <span className="text-xs text-zinc-500">+{concept.why.length - 4} more</span>}
  </div>;
}

function Attributes({ concept, limit }: { concept: GlossaryConcept; limit: number }) {
  const fields = concept.attributes.filter((field) => !field.generic);
  if (fields.length === 0) return <span className="text-xs text-zinc-500">{concept.attributes.length === 0 ? "none recorded" : "only generic"}</span>;
  return <div className="text-xs">{fields.slice(0, limit).map((field) => field.label).join(", ")}{fields.length > limit && <span className="text-zinc-500"> +{fields.length - limit}</span>}</div>;
}

function ConceptDetail({ concept, bias }: { concept: GlossaryConcept; bias: number }) {
  return <>
    <Card>
      <div className="flex flex-wrap items-center gap-2"><H2>{concept.term}</H2><Pill tone={tierTone[concept.tier]}>{concept.tier}</Pill></div>
      <Muted className="mb-3 text-xs">Concept · {concept.members.length} members · {concept.repositories.join(", ")} · {concept.capabilities.join(", ") || "no capability recorded"}. Related names, not proof of equal values.</Muted>
      <H2>Why this tier</H2>
      <Table columns={["Feature", "Detail", "Points"]} rows={[
        ...concept.why.map((reason) => [reason.feature, reason.detail ?? "", <span className={reason.points < 0 ? "text-amber-700 dark:text-amber-300" : ""}>{reason.points > 0 ? "+" : ""}{reason.points}</span>]),
        [<span className="text-zinc-500">baseline</span>, "", bias],
        [<strong>score</strong>, "", <strong>{concept.score}</strong>],
      ]} />
      <p className="mt-2 text-xs text-zinc-500">Points are checked-in integer weights fitted from labelled examples or stated as priors (glossary-weights.json). A feature with zero points is a fact that did not change the score.</p>
    </Card>
    <Card>
      <H2>Attributes</H2>
      {concept.attributes.length === 0 ? <Note>No attributes are recorded. Properties, fields and columns appear here when the extractor emits them for this language.</Note> : (
        <Table columns={["Attribute", "Declared in", "Note"]} rows={concept.attributes.map((field) => [
          field.label, `${field.declarations} declaration${field.declarations === 1 ? "" : "s"}`,
          field.generic ? <span className="text-xs italic text-zinc-500">generic: common to many concepts</span> : "",
        ])} />
      )}
    </Card>
    <H2>Members</H2>
    {concept.members.map((member) => <MemberCard key={member.id} member={member} />)}
    {concept.variants.length > 0 && <><H2>Variants</H2>{concept.variants.map((member) => <MemberCard key={member.id} member={member} />)}</>}
  </>;
}

function MemberCard({ member }: { member: GlossaryMember }) {
  const [showSource, setShowSource] = useState(false);
  return <Card>
    <div className="mb-1 flex flex-wrap items-center gap-2"><span className="font-semibold">{member.name}</span><Pill>{member.kind}</Pill><Pill tone={member.fold === "declaration" ? "kind" : "neutral"}>{member.fold}</Pill></div>
    <div className="text-xs">{member.repositoryId} · {shortSha(member.commitSha)} · {member.unit}</div>
    <div className="mt-1 break-all text-xs text-zinc-500">Symbol: <Mono>{member.key}</Mono></div>
    {member.evidence.length === 0 ? <Note>No source evidence was recorded for this member.</Note> : <div className="mt-3">
      <button type="button" aria-expanded={showSource} onClick={() => setShowSource((shown) => !shown)} className="text-xs font-semibold text-blue-700 hover:underline dark:text-blue-300">{showSource ? "Hide" : "Show"} source evidence ({member.evidence.length}) for {member.name}</button>
      {showSource && member.evidence.map((evidence) => <div key={evidence.id} className="mt-2"><div className="break-all text-xs text-zinc-500">{evidence.repositoryId} · <Mono>{evidence.commitSha}</Mono></div><SourceView evidence={evidence} /></div>)}
    </div>}
  </Card>;
}

function Metrics({ view }: { view: GlossaryConceptView }) {
  if (view.metrics.length === 0) return null;
  return <div className="space-y-2">
    <H2>Metrics</H2>
    <Muted className="text-xs">Calculations are listed separately from concepts. Open a metric to compare its derivations in the candidate view.</Muted>
    <Table columns={["Metric", "Calculations", "Observed"]} rows={view.metrics.map((metric) => [
      <a href={href("glossary", { mode: "candidates", term: metric.term })} className="font-semibold text-blue-700 hover:underline dark:text-blue-300">{metric.term}</a>,
      metric.occurrences.filter((occurrence) => occurrence.role === "calculation").length,
      <div className="flex flex-wrap gap-1">{metric.derivationDiffers && <Pill tone="warn">known attributes differ</Pill>}{metric.typeDiffers && <Pill tone="warn">known types differ</Pill>}{!metric.comparisonsComplete && <Pill tone="warn">comparisons incomplete</Pill>}</div>,
    ])} />
  </div>;
}
