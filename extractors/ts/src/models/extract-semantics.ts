import { BPMN_NAMESPACE, ModelXmlError, type XmlNode } from "./xml.ts";

export type ModelKind = "bpmn" | "dmn";
type ConstructStatus = "semantic-evidence-supported" | "opaque-partial" | "rejected";

export interface SupportConstruct {
  id: string;
  family: ModelKind;
  namespace: string;
  elements: string[];
  status: ConstructStatus;
  capturesText?: boolean;
  matchesUnlisted?: boolean;
  disposition?: "structural-retained-layout";
}

const STANDARD_LAYOUT_NAMESPACES: Record<ModelKind, Set<string>> = {
  bpmn: new Set([
    "http://www.omg.org/spec/BPMN/20100524/DI",
    "http://www.omg.org/spec/DD/20100524/DC",
    "http://www.omg.org/spec/DD/20100524/DI",
  ]),
  dmn: new Set([
    "https://www.omg.org/spec/DMN/20191111/DMNDI/",
    "http://www.omg.org/spec/DMN/20180521/DC/",
    "http://www.omg.org/spec/DMN/20180521/DI/",
  ]),
};

export function isStandardLayoutNamespace(kind: ModelKind, namespace: string): boolean {
  return STANDARD_LAYOUT_NAMESPACES[kind].has(namespace);
}

export function constructsFor(
  kind: ModelKind,
  namespace: string,
  element: string,
  support: SupportConstruct[],
): SupportConstruct[] {
  const rows = support.filter(item => item.family === kind && (item.namespace === namespace || item.namespace === "*"));
  const exact = rows.filter(item => item.namespace === namespace && item.elements.includes(element));
  if (exact.length) return exact;
  if (isStandardLayoutNamespace(kind, namespace)) return [];
  return rows.filter(item => item.matchesUnlisted || item.namespace === "*");
}

export const FLOW_NODE_TYPES = new Set([
  "task", "userTask", "serviceTask", "scriptTask", "manualTask", "businessRuleTask", "sendTask", "receiveTask",
  "startEvent", "endEvent", "intermediateCatchEvent", "intermediateThrowEvent", "boundaryEvent", "exclusiveGateway",
  "inclusiveGateway", "parallelGateway", "eventBasedGateway", "complexGateway", "subProcess", "transaction",
  "adHocSubProcess", "callActivity",
]);

export function validateFlowDirectionDeclarations(nodes: XmlNode[], idNodes: Map<string, XmlNode>): void {
  for (const node of nodes) {
    if (node.namespace !== BPMN_NAMESPACE || (node.localName !== "incoming" && node.localName !== "outgoing")) continue;
    const owner = node.parent;
    if (!owner || owner.namespace !== BPMN_NAMESPACE || !FLOW_NODE_TYPES.has(owner.localName)) {
      throw new ModelXmlError("invalid-flow-direction-owner", "Incoming/outgoing declaration must belong to a BPMN flow node");
    }
    const reference = node.text.trim();
    if (!reference) throw new ModelXmlError("incomplete-flow-direction-reference", "Incoming/outgoing declaration requires a sequenceFlow id");
    const flow = idNodes.get(reference);
    if (!flow) throw new ModelXmlError("dangling-flow-direction-reference", "Incoming/outgoing declaration references a missing sequenceFlow");
    if (flow.namespace !== BPMN_NAMESPACE || flow.localName !== "sequenceFlow") throw new ModelXmlError("invalid-flow-direction-reference", "Incoming/outgoing declaration must reference a BPMN sequenceFlow");
    const endpoint = flow.attributes[node.localName === "incoming" ? "targetRef" : "sourceRef"];
    if (endpoint !== owner.attributes.id) throw new ModelXmlError("inconsistent-flow-direction-reference", "Incoming/outgoing declaration disagrees with its sequenceFlow endpoint");
  }
}
