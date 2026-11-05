import { useState } from "react";
import { calledProcessLocations, defaultFlow, elementLocation, elementName, lanesFor, relationshipName,
  type ModelDiagram, type ModelElement, type ModelIndex } from "../models/model-index";

const button = "rounded border border-zinc-300 px-2 py-1 text-sm hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 dark:border-zinc-700 dark:hover:bg-zinc-800";
const link = "text-blue-700 underline dark:text-blue-300";
const groups = {
  here: "In this diagram", elsewhere: "Other diagrams in this source", logic: "Decision logic",
  supporting: "Supporting model content", unplaced: "Without captured diagram placement", unknown: "Placement not determined",
};

export function ModelElements({ index, diagramId, selected, renderedIds, measured, onSelect, onOpen, onLogic }: {
  index: ModelIndex; diagramId?: string; selected: string; renderedIds: string[]; measured: boolean;
  onSelect(id: string): void; onOpen(diagram: ModelDiagram, id: string): void; onLogic(id: string): void;
}) {
  const [scope, setScope] = useState<"diagram" | "all">("diagram");
  const diagram = index.diagrams.find(item => item.id === diagramId);
  const all = scope === "all" || !diagram;
  const locations = index.semantic.map(element => ({ element, location: elementLocation(index, element, diagramId) }));
  const shown = locations.filter(item => all || item.location.kind === "here");
  const shownIdentities = new Set(shown.map(item => item.element.identity));
  const relationships = index.evidence.relationships.filter(relationship =>
    all || shownIdentities.has(relationship.source) && shownIdentities.has(relationship.target));
  const details = (element: ModelElement) => {
    const parents = index.ancestors(element);
    const lanes = lanesFor(index, element);
    return <>
      <button type="button" className="text-left" aria-pressed={selected === element.id} onClick={() => onSelect(element.id)}>
        <span className="font-medium">{elementName(element)}</span> <span className="text-xs text-zinc-500">{element.type}</span>
        <span className="block break-all font-mono text-xs">{element.id}</span>
      </button>
      <p className="text-xs text-zinc-500">Context: {parents.filter(owner => ["process", "subProcess", "decision", "businessKnowledgeModel"].includes(owner.type))
        .reverse().map(elementName).join(" / ") || "Source-level content"}</p>
      {element.namespace.includes("BPMN") && <p className="text-xs">Lane (as authored): {lanes.map(elementName).join(", ") || "No lane membership captured"}; not an accountability assignment.</p>}
      {defaultFlow(index, element) && <p className="text-sm font-medium">Default gateway flow</p>}
      {element.content?.text && <p className="whitespace-pre-wrap break-words text-xs">Captured content ({element.content.language || "language not specified"}): {element.content.text}</p>}
      {!!Object.keys(element.attributes).length && <details className="text-xs"><summary className="cursor-pointer">Authored attributes</summary>
        <dl>{Object.entries(element.attributes).map(([name, value]) => <div key={name}><dt className="font-semibold">{name}</dt><dd className="break-all">{value}</dd></div>)}</dl>
      </details>}
      {parents.length > 0 && <nav aria-label={`Breadcrumb for ${element.id}`} className="text-xs">
        {parents.reverse().map(parent => <button key={parent.identity} className={`${link} mr-2`} onClick={() => onSelect(parent.id)}>{elementName(parent)}</button>)}
      </nav>}
    </>;
  };
  return <section className="max-h-[70vh] space-y-3 overflow-auto p-3" aria-label="Captured model elements">
    <h2 className="font-semibold">Text alternative and captured source evidence</h2>
    <div role="group" aria-label="Element scope" className="flex flex-wrap gap-2">
      <button className={button} aria-pressed={!all} disabled={!diagram} onClick={() => setScope("diagram")}>This diagram</button>
      <button className={button} aria-pressed={all} onClick={() => setScope("all")}>All captured source evidence</button>
    </div>
    <p className="text-sm">{all ? "All captured model content in this source file." : `Diagram: ${diagram?.name}. Source-backed placements, independent of zoom and viewer loading.`}
      {" "}{shown.length} of {index.semantic.length} model-content records. {index.layout.length} technical layout records kept separately.</p>
    {!all && <button className={link} onClick={() => setScope("all")}>Explore {index.semantic.length - shown.length} records outside this diagram</button>}
    <p className="text-xs text-zinc-500">Source order is not execution order. Diagram placement, extraction coverage and business completeness are different facts. Unsupported relationships may not be captured.</p>
    {Object.entries(groups).map(([kind, heading]) => {
      const entries = shown.filter(item => item.location.kind === kind);
      if (!entries.length) return null;
      return <section key={kind} aria-label={heading}>
        <h3 className="font-semibold">{heading} ({entries.length})</h3>
        <ol className="mt-2 space-y-2">{entries.map(({ element, location }) => <li key={element.identity} data-model-element={element.id}
          className={`space-y-1 rounded border p-2 ${selected === element.id ? "border-blue-500" : "border-zinc-200 dark:border-zinc-800"}`}>
          {details(element)}
          <p className="text-xs">{location.text}</p>
          {location.kind === "here" && <p className="text-xs text-zinc-500">{!measured ? "Viewer state not measured." :
            renderedIds.includes(element.id) ? "Included in the active viewer scope; may be off-screen." : "Outside the active viewer scope; source placement is unchanged."}</p>}
          <div className="flex flex-wrap gap-2">{location.diagrams.map(target => <button key={target.id} className={`${button} text-xs`}
            onClick={() => onOpen(target, element.id)}>Show in {target.name}</button>)}
            {location.logicId && <button className={`${button} text-xs`} onClick={() => onLogic(location.logicId!)}>Open decision logic</button>}
          </div>
          {element.attributes.calledElement && <p className="text-xs">Called process reference: {element.attributes.calledElement}. Resolve using the diagram’s + button; external targets are not fetched.</p>}
          {calledProcessLocations(index, element).map(({ diagram, targetId, name }) => <button key={`${diagram.id}:${targetId}`} className={`${button} text-xs`}
            onClick={() => onOpen(diagram, targetId)}>Open called process {name} in {diagram.name}</button>)}
        </li>)}</ol>
      </section>;
    })}
    {!shown.length && <p>No model-content placements captured for this diagram. All source evidence remains available.</p>}
    <section aria-label="Captured relationships" className="space-y-2">
      <h3 className="font-semibold">Captured relationships ({relationships.length})</h3>
      <p className="text-xs text-zinc-500">Only captured relationship kinds are listed; absence is not proof of no business dependency.</p>
      {relationships.map(relationship => <div key={relationship.identity} className="rounded border border-zinc-200 p-2 text-sm dark:border-zinc-800">
        <button className={link} aria-pressed={selected === relationship.identity} onClick={() => onSelect(relationship.identity)}>{relationshipName(index, relationship)}: {elementName(index.byIdentity.get(relationship.source))} → {elementName(index.byIdentity.get(relationship.target))}</button>
        {relationship.condition && <p className="whitespace-pre-wrap">Condition ({relationship.condition.language || "language not specified"}): {relationship.condition.text ?? "Text not captured by this profile"}</p>}
      </div>)}
    </section>
    <details className="rounded border border-zinc-200 p-2 dark:border-zinc-800">
      <summary className="cursor-pointer font-semibold">Diagram layout (technical) · {index.layout.length} records</summary>
      <p className="text-xs">Drawing instructions, not additional business elements. Retained unchanged with the source evidence.</p>
      <ol className="space-y-2">{index.layout.map(element => <li key={element.identity} data-layout-record={element.id} className="rounded border p-2">{details(element)}</li>)}</ol>
    </details>
  </section>;
}
