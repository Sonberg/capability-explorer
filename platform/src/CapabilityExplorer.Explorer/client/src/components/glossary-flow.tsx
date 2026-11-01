import { useState } from "react";
import { type GlossaryOccurrence } from "../api";
import { type FlowOptions, type FlowResult } from "../glossary-api";
import { useApi } from "../state";
import { SuppliedArchitectureCanvas } from "./glossary-canvas";
import { EvidenceLinks } from "./source";
import { Button, Card, ErrorBox, H2, inputClass, Loading, Mono, Note, Pill, Table } from "./ui";

const defaults: FlowOptions = { direction: "both", maxHops: 4, maxNodes: 250, maxEdges: 500 };
export function GlossaryFlow({ occurrence, scope, reviewGeneration }: { occurrence: GlossaryOccurrence; scope: string; reviewGeneration: number }) {
  const [options, setOptions] = useState(defaults);
  const [requested, setRequested] = useState(defaults);
  const [reviewed, setReviewed] = useState(false);
  const [selected, setSelected] = useState<{ type: "node" | "edge"; id: string }>();
  const query = new URLSearchParams({ occurrence: occurrence.id, direction: requested.direction, maxHops: String(requested.maxHops), maxNodes: String(requested.maxNodes), maxEdges: String(requested.maxEdges), reviewed: String(reviewed), refresh: String(reviewGeneration) });
  const { data, error, loading, reload } = useApi<FlowResult>(`/api/glossary/flow?${query}&${scope}`);
  // A new request never presents the old graph as current evidence.
  const flow = loading ? undefined : data;
  const edge = selected?.type === "edge" ? flow?.edges.find((item) => item.id === selected.id) : undefined;
  const node = selected?.type === "node" ? flow?.nodes.find((item) => item.id === selected.id) : undefined;
  return <Card>
    <H2>Connections for {occurrence.name}</H2>
    <p className="mb-2 text-xs text-zinc-500">Source and binding relations provide navigation context. They do not prove field or value propagation. Reviewed assertions express intended meaning; they do not change source certainty.</p>
    <div className="mb-2 break-all text-xs">Seed occurrence: <Mono>{occurrence.id}</Mono></div>
    <form className="mb-3 flex flex-wrap items-end gap-3" onSubmit={(event) => { event.preventDefault(); setSelected(undefined); setRequested({ ...options }); reload(); }}>
      <label className="text-xs font-semibold">Direction<select aria-label="Flow direction" className={`${inputClass} mt-1`} value={options.direction} onChange={(event) => setOptions({ ...options, direction: event.target.value as FlowOptions["direction"] })}><option value="both">Both directions</option><option value="forward">Forward arrows</option><option value="backward">Backward arrows</option></select></label>
      {([["maxHops", "Maximum hops", 8], ["maxNodes", "Maximum nodes", 500], ["maxEdges", "Maximum edges", 1000]] as const).map(([key, label, max]) => <label key={key} className="w-32 text-xs font-semibold">{label}<input aria-label={label} type="number" min={1} max={max} required value={options[key]} onChange={(event) => setOptions({ ...options, [key]: Number(event.target.value) })} className={`${inputClass} mt-1`} /></label>)}
      <Button type="submit">Apply flow limits</Button>
      <label className="flex items-center gap-1 text-xs"><input type="checkbox" checked={reviewed} onChange={(event) => { setReviewed(event.target.checked); setSelected(undefined); }} />Include reviewed assertions</label>
    </form>
    {error && <div className="space-y-2"><ErrorBox message={error} /><Button variant="secondary" onClick={reload}>Retry connections</Button></div>}
    {loading && <Loading what="Loading occurrence connections" />}
    {flow && <>
      <div className="mb-2 flex flex-wrap gap-2 text-xs"><Pill tone={flow.coverage.truncated ? "warn" : "info"}>{flow.coverage.truncated ? "truncated navigation" : flow.coverage.complete ? "available navigation complete" : "navigation incomplete"}</Pill><Pill tone="warn">{flow.coverage.valueFlowComplete ? "field/value coverage reported complete" : "field/value flow incomplete"}</Pill><span>{flow.nodes.length} nodes · {flow.edges.length} edges · applied bounds {requested.maxHops} hops / {requested.maxNodes} nodes / {requested.maxEdges} edges</span></div>
      {flow.coverage.reasons.map((reason, index) => <Note key={`${reason.code}:${index}`}><strong>{reason.code}</strong>: {reason.message}</Note>)}
      {flow.edges.length === 0 && <Note>No connection evidence was found within these bounds. Coverage and unsupported boundaries are reported below.</Note>}
      {flow.graph.nodes.length > 0 && <SuppliedArchitectureCanvas graph={flow.graph} selectedId={selected?.id} onNodeSelect={(item) => setSelected({ type: "node", id: item.id })} onEdgeSelect={(item) => setSelected({ type: "edge", id: item.id })} />}
      <div className="my-3 rounded border border-zinc-200 p-3 text-xs dark:border-zinc-800">
        {!edge && !node && <p>Select a node or connection on the canvas to inspect exact provenance and source evidence.</p>}
        {node && <><div className="font-semibold">Selected source node</div><div className="break-all">{node.repositoryId ?? "Human term hub"} · {node.commitSha ?? "No source revision"}</div><div className="break-all">{node.unitPath} · {node.symbolKey}</div><div className="break-all">Occurrence IDs: {node.occurrenceIds.join(", ") || "none (term hub)"}</div><EvidenceLinks evidence={node.evidence} /></>}
        {edge && <><div className="mb-1 flex flex-wrap gap-2"><strong>{edge.relation}</strong><Pill>{edge.category}</Pill><Pill>{edge.resolution}</Pill></div><p>{edge.category === "human-assertion" || edge.assertionId ? "Human assertion of intended meaning; no observed value equality is implied." : "Source/binding context; no field/value mapping is implied."}</p><div className="my-1 break-all">Direction: {edge.from} → {edge.to}</div><div className="break-all">Revision: {edge.repositoryId ?? "none"} · {edge.commitSha ?? "none"} · {edge.unitPath}</div><div className="break-all">Source record: {edge.recordId ?? "none"} · Assertion: {edge.assertionId ?? "none"} · Term: {edge.termId ?? "none"}</div><div>Derivation: {edge.derivation}{edge.rule && ` · Rule: ${edge.rule}`}</div>{edge.unresolvedReason && <Note>{edge.unresolvedReason}</Note>}{edge.bindingHint && <div>Binding hint: {edge.bindingHint}</div>}{edge.bindingTarget && <div>Binding target: {edge.bindingTarget}</div>}{edge.bindingNote && <div>Binding note: {edge.bindingNote}</div>}{edge.reviewer && <div>Reviewer label (self-reported): {edge.reviewer}</div>}{edge.rationale && <p>{edge.rationale}</p>}<EvidenceLinks evidence={edge.evidence} /></>}
      </div>
      <H2>Selection and revision coverage</H2>
      <Table columns={["Repository / exact revision", "Unit", "Analysis coverage"]} rows={flow.selection.map((item) => [<div className="break-all">{item.repositoryId}<div><Mono>{item.commitSha}</Mono></div></div>, <span className="break-all">{item.unit} · {item.unitPath}</span>, <div>{item.analysisStatus} · {item.filesAnalyzed}/{item.filesTotal} files<div>{item.unresolvedEdges} unresolved edges</div></div>])} />
    </>}
  </Card>;
}
