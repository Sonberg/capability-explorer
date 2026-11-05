import type { AuthoredModelEvidence } from "./contracts";

export type ModelElement = AuthoredModelEvidence["elements"][number];
export type ModelRelationship = AuthoredModelEvidence["relationships"][number];
export type ModelDiagram = { id: string; name: string; rootId?: string };
export type ModelOccurrence = { diagramId: string; elementId: string; layoutId: string };
export const BPMN = "http://www.omg.org/spec/BPMN/20100524/MODEL";
export const DMN = "https://www.omg.org/spec/DMN/20191111/MODEL/";
const layoutNamespaces = new Set([
  "http://www.omg.org/spec/BPMN/20100524/DI", "http://www.omg.org/spec/DD/20100524/DC",
  "http://www.omg.org/spec/DD/20100524/DI", "https://www.omg.org/spec/DMN/20191111/DMNDI/",
  "http://www.omg.org/spec/DMN/20180521/DC/", "http://www.omg.org/spec/DMN/20180521/DI/",
]);
const supportingTypes = new Set(["definitions", "process", "collaboration", "laneSet", "flowNodeRef",
  "incoming", "outgoing", "conditionExpression", "dataObject", "dataStore"]);
const dmnGlyphs = new Set(["decision", "inputData", "businessKnowledgeModel", "knowledgeSource",
  "informationRequirement", "knowledgeRequirement", "authorityRequirement", "association", "textAnnotation"]);
export const localReference = (value: string | undefined) => {
  const id = value?.replace(/^#/, "");
  return id && !/[:/#]/.test(id) ? id : undefined;
};
export const elementName = (element: ModelElement | undefined) => element?.attributes.name || element?.id || "Not captured";
export const isLayout = (element: ModelElement) => layoutNamespaces.has(element.namespace);

export function buildModelIndex(evidence: AuthoredModelEvidence) {
  const byId = new Map(evidence.elements.map(element => [element.id, element]));
  const byIdentity = new Map(evidence.elements.map(element => [element.identity, element]));
  const ancestors = (element: ModelElement) => {
    const result: ModelElement[] = [];
    const seen = new Set([element.identity]);
    let owner = element.parent && byIdentity.get(element.parent);
    while (owner && !seen.has(owner.identity)) {
      result.push(owner);
      seen.add(owner.identity);
      owner = owner.parent && byIdentity.get(owner.parent);
    }
    return result;
  };
  const layout = evidence.elements.filter(isLayout);
  const semantic = evidence.elements.filter(element => !isLayout(element));
  const title = semantic.find(element => element.type === "definitions")?.attributes.name ||
    semantic.filter(element => element.type === "process").map(element => element.attributes.name || element.id).join(" / ") ||
    evidence.unitPath.split("/").at(-1) || evidence.unitPath;
  const diagrams: ModelDiagram[] = layout.filter(element => ["BPMNDiagram", "DMNDiagram"].includes(element.type))
    .map(element => {
      const plane = layout.find(candidate => candidate.type === "BPMNPlane" && candidate.parent === element.identity);
      const rootId = localReference(plane?.attributes.bpmnElement);
      return { id: element.id, rootId, name: element.attributes.name || byId.get(rootId || "")?.attributes.name ||
        (byId.get(rootId || "")?.type === "collaboration" || element.type === "DMNDiagram" ? title : element.id) };
    });
  const occurrences = new Map<string, ModelOccurrence[]>();
  for (const element of layout) {
    if (!["BPMNShape", "BPMNEdge", "DMNShape", "DMNEdge"].includes(element.type)) continue;
    const elementId = localReference(element.attributes.bpmnElement || element.attributes.dmnElementRef);
    const diagram = ancestors(element).find(owner => ["BPMNDiagram", "DMNDiagram"].includes(owner.type));
    if (!elementId || !diagram || !byId.has(elementId)) continue;
    const items = occurrences.get(elementId) || [];
    items.push({ diagramId: diagram.id, elementId, layoutId: element.id });
    occurrences.set(elementId, items);
  }
  return { evidence, byId, byIdentity, ancestors, layout, semantic, diagrams, occurrences, title };
}
export type ModelIndex = ReturnType<typeof buildModelIndex>;
export type ElementLocation = {
  kind: "here" | "elsewhere" | "logic" | "supporting" | "unplaced" | "unknown";
  text: string;
  diagrams: ModelDiagram[];
  logicId?: string;
};

export function elementLocation(index: ModelIndex, element: ModelElement, diagramId?: string): ElementLocation {
  const diagrams = index.diagrams.filter(diagram =>
    index.occurrences.get(element.id)?.some(occurrence => occurrence.diagramId === diagram.id));
  if (diagrams.length) return {
    kind: diagrams.some(diagram => diagram.id === diagramId) ? "here" : "elsewhere",
    text: diagrams.some(diagram => diagram.id === diagramId) ? "Placed in this diagram." : "Placed in another diagram in this source.",
    diagrams,
  };
  const owners = [element, ...index.ancestors(element)];
  const logic = element.namespace === DMN && owners.find(owner => owner.namespace === DMN &&
    ["decisionTable", "literalExpression"].includes(owner.type));
  if (logic) return { kind: "logic", text: "Decision logic; available in Decision table / expressions.", diagrams, logicId: logic.id };
  if (element.namespace === BPMN && supportingTypes.has(element.type) ||
      element.namespace === DMN && !dmnGlyphs.has(element.type)) {
    return { kind: "supporting", text: "Supporting model content; inspect its details and relationships.", diagrams };
  }
  if ((element.namespace !== BPMN && element.namespace !== DMN) || element.kind === "unsupported") {
    return { kind: "unknown", text: "Diagram placement not determined for this construct.", diagrams };
  }
  return { kind: "unplaced", text: "No diagram placement captured in this source evidence; not necessarily an error.", diagrams };
}

export function lanesFor(index: ModelIndex, element: ModelElement) {
  return index.evidence.relationships.filter(relationship => relationship.kind === "reference" && relationship.target === element.identity)
    .map(relationship => index.byIdentity.get(relationship.source))
    .filter((owner): owner is ModelElement => owner?.namespace === BPMN && owner.type === "lane");
}

export function relationshipName(index: ModelIndex, relationship: ModelRelationship) {
  if (relationship.kind !== "reference") return relationship.kind;
  const source = index.byIdentity.get(relationship.source);
  const target = index.byIdentity.get(relationship.target);
  if (source?.namespace === BPMN) {
    return ({ lane: "Lane membership", participant: "Process reference", callActivity: "Called process",
      dataObjectReference: "Data object reference", dataStoreReference: "Data store reference" })[source.type] || "Reference";
  }
  const owning = index.evidence.elements.find(element => element.namespace === DMN && /Requirement$/.test(element.type) &&
    element.parent === source?.identity && index.evidence.elements.some(child =>
      child.parent === element.identity && localReference(child.attributes.href) === target?.id));
  return owning?.type || "Reference";
}

export function defaultFlow(index: ModelIndex, element: ModelElement) {
  return element.type === "sequenceFlow" && index.semantic.some(owner => owner.attributes.default === element.id);
}

export function calledProcessLocations(index: ModelIndex, element: ModelElement) {
  const id = localReference(element.attributes.calledElement);
  const target = id && index.byId.get(id);
  if (!target || target.namespace !== BPMN || target.type !== "process") return [];
  const participants = index.semantic.filter(item => item.type === "participant" &&
    item.namespace === BPMN && localReference(item.attributes.processRef) === target.id);
  return index.diagrams.flatMap(diagram => {
    const placed = participants.filter(item =>
      index.occurrences.get(item.id)?.some(occurrence => occurrence.diagramId === diagram.id));
    if (placed.length) return placed.map(item => ({ diagram, targetId: item.id, name: elementName(target) }));
    return diagram.rootId === target.id ? [{ diagram, targetId: target.id, name: elementName(target) }] : [];
  });
}
