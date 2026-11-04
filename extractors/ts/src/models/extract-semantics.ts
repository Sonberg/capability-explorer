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

const NCNAME = "[\\p{L}_][\\p{L}\\p{N}\\p{M}\\p{Pc}.\\-\\u00B7]*";
const QNAME_PATTERN = new RegExp(`^(?:(${NCNAME}):)?(${NCNAME})$`, "u");

function resolveFlowDirectionReference(node: XmlNode, targetNamespace: string): string {
  const value = node.text.trim();
  const match = QNAME_PATTERN.exec(value);
  if (!match) throw new ModelXmlError("invalid-flow-direction-qname", "Incoming/outgoing declaration must contain a valid QName");
  const prefix = match[1];
  if (!prefix) return match[2]!;
  const namespace = node.namespaceBindings[prefix];
  if (namespace === undefined) {
    throw new ModelXmlError("undeclared-flow-direction-prefix", "Incoming/outgoing QName uses an undeclared prefix");
  }
  if (!targetNamespace || namespace !== targetNamespace) {
    throw new ModelXmlError("foreign-flow-direction-prefix", "Incoming/outgoing QName prefix must resolve to the model targetNamespace");
  }
  return match[2]!;
}

export function validateFlowDirectionDeclarations(nodes: XmlNode[], idNodes: Map<string, XmlNode>): Set<string> {
  const targetNamespace = nodes[0]?.attributes.targetNamespace ?? "";
  const validatedPrefixedReferences = new Set<string>();
  for (const node of nodes) {
    if (node.namespace !== BPMN_NAMESPACE || (node.localName !== "incoming" && node.localName !== "outgoing")) continue;
    const owner = node.parent;
    if (!owner || owner.namespace !== BPMN_NAMESPACE || !FLOW_NODE_TYPES.has(owner.localName)) {
      throw new ModelXmlError("invalid-flow-direction-owner", "Incoming/outgoing declaration must belong to a BPMN flow node");
    }
    const lexicalReference = node.text.trim();
    const reference = resolveFlowDirectionReference(node, targetNamespace);
    if (lexicalReference.includes(":")) validatedPrefixedReferences.add(lexicalReference);
    if (!reference) throw new ModelXmlError("incomplete-flow-direction-reference", "Incoming/outgoing declaration requires a sequenceFlow id");
    const flow = idNodes.get(reference);
    if (!flow) throw new ModelXmlError("dangling-flow-direction-reference", "Incoming/outgoing declaration references a missing sequenceFlow");
    if (flow.namespace !== BPMN_NAMESPACE || flow.localName !== "sequenceFlow") throw new ModelXmlError("invalid-flow-direction-reference", "Incoming/outgoing declaration must reference a BPMN sequenceFlow");
    const endpoint = flow.attributes[node.localName === "incoming" ? "targetRef" : "sourceRef"];
    if (endpoint !== owner.attributes.id) throw new ModelXmlError("inconsistent-flow-direction-reference", "Incoming/outgoing declaration disagrees with its sequenceFlow endpoint");
  }
  return validatedPrefixedReferences;
}
