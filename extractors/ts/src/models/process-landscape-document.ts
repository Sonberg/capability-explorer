import { createHash } from "node:crypto";
import modelSupport from "../../../../schema/models/support.json" with { type: "json" };
import { constructsFor, isStandardLayoutNamespace, type SupportConstruct } from "./extract-semantics.ts";
import { BPMN_NAMESPACE, ModelXmlError, type XmlNode } from "./xml.ts";
import { MANIFEST_LIMITS } from "./process-landscape-manifest.ts";
import { ProcessLandscapeError } from "./process-landscape-types.ts";
import type { LandscapeDiagnostic, LandscapeDocument, LandscapeElement } from "./process-landscape-types.ts";

export const MESSAGE_ENDPOINT_TYPES = new Set([
  "task", "userTask", "serviceTask", "scriptTask", "manualTask", "businessRuleTask", "sendTask",
  "receiveTask", "subProcess", "transaction", "adHocSubProcess", "callActivity", "startEvent",
  "endEvent", "intermediateCatchEvent", "intermediateThrowEvent", "boundaryEvent",
]);
const SUPPORT = (modelSupport as { constructs: SupportConstruct[] }).constructs;
const RELATIONSHIPS = new Set(["sequenceFlow", "messageFlow", "association"]);

interface DocumentContext {
  nodes: XmlNode[];
  idNodes: Map<string, XmlNode>;
  assignedIds: Map<XmlNode, string>;
  identities: Map<XmlNode, string>;
  ownerOf(node: XmlNode): number | undefined;
  path: string;
  identityScope: string;
}

export function extractDocumentGraph(context: DocumentContext): {
  document: LandscapeDocument;
  retainedNodes: Set<XmlNode>;
  semanticNodes: Set<XmlNode>;
  diagnostics: LandscapeDiagnostic[];
  supported: string[];
} {
  const { nodes, idNodes, assignedIds, identities, ownerOf, path, identityScope } = context;
  let encodedBytes = Buffer.byteLength(JSON.stringify({ path, elements: [], relationships: [] }), "utf8");
  const account = (value: unknown, hasPrevious: boolean) => {
    encodedBytes += Buffer.byteLength(JSON.stringify(value), "utf8") + Number(hasPrevious);
    if (encodedBytes > MANIFEST_LIMITS.maxTotalBytes) {
      throw new ProcessLandscapeError("document-graph-size-limit", "Document graph projection exceeds the byte budget");
    }
  };
  const diagnostics: LandscapeDiagnostic[] = [];
  const diagnose = (code: string, node: XmlNode, message: string, ref?: string) => {
    diagnostics.push({ code, message, disposition: "unresolved", source: { path, element: assignedIds.get(node)!, ...(ref !== undefined ? { ref } : {}) } });
  };
  const collaborationCache = new Map<XmlNode, XmlNode | undefined>();
  const collaborationOf = (node: XmlNode): XmlNode | undefined => {
    if (collaborationCache.has(node)) return collaborationCache.get(node);
    const result = node.namespace === BPMN_NAMESPACE && node.localName === "collaboration"
      ? node : node.parent ? collaborationOf(node.parent) : undefined;
    collaborationCache.set(node, result);
    return result;
  };
  const participantsByCollaboration = new Map<XmlNode, Map<XmlNode, XmlNode[]>>();
  for (const node of nodes) {
    if (node.namespace !== BPMN_NAMESPACE || node.localName !== "participant" || ownerOf(node) !== undefined) continue;
    const collaboration = collaborationOf(node);
    if (!collaboration || node.parent !== collaboration) {
      diagnose("invalid-message-flow-scope", node, "Participant is not directly owned by a collaboration");
      continue;
    }
    const reference = node.attributes.processRef;
    if (reference === undefined) continue; // Black-box participants have no declared process.
    const process = reference && !/[:/#\s]/u.test(reference) ? idNodes.get(reference) : undefined;
    if (!process || process.namespace !== BPMN_NAMESPACE || process.localName !== "process" || !collaboration) {
      diagnose("unresolved-participant-process-reference", node, "Participant process reference is not a supported local process reference", reference);
      continue;
    }
    let processes = participantsByCollaboration.get(collaboration);
    if (!processes) participantsByCollaboration.set(collaboration, processes = new Map());
    const participants = processes.get(process) ?? [];
    participants.push(node);
    processes.set(process, participants);
  }
  const processByOwner = new Map<number, XmlNode>();
  for (const node of nodes) {
    if (node.namespace === BPMN_NAMESPACE && node.localName === "process") processByOwner.set(ownerOf(node)!, node);
  }
  const retainedNodes = new Set(nodes.filter(node =>
    isStandardLayoutNamespace("bpmn", node.namespace)
    || (ownerOf(node) === undefined
      && !(node.namespace === BPMN_NAMESPACE && (node.localName === "text"
        || (RELATIONSHIPS.has(node.localName) && node.localName !== "messageFlow"))))));
  const semanticNodes = new Set(nodes.filter(node => {
    const owner = ownerOf(node);
    if (owner === undefined || isStandardLayoutNamespace("bpmn", node.namespace)) return false;
    if (node.namespace !== BPMN_NAMESPACE || !RELATIONSHIPS.has(node.localName)) return node.localName !== "text";
    const source = idNodes.get(node.attributes.sourceRef ?? "");
    const target = idNodes.get(node.attributes.targetRef ?? "");
    return !!source && !!target && ownerOf(source) === owner && ownerOf(target) === owner;
  }));
  const elements: LandscapeElement[] = [];
  const supported = new Set<string>();
  for (const node of retainedNodes) {
    const rows = constructsFor("bpmn", node.namespace, node.localName, SUPPORT);
    if (rows.some(row => row.status === "rejected")) throw new ModelXmlError("unsupported-rejected-construct", "Encountered a construct excluded from process-landscape extraction");
    for (const row of rows) if (row.status === "semantic-evidence-supported") supported.add(row.id);
    const kind = node.namespace === BPMN_NAMESPACE
      ? rows.some(row => row.status === "semantic-evidence-supported") ? "bpmn" : "unsupported"
      : isStandardLayoutNamespace("bpmn", node.namespace) ? "bpmn" : "opaqueExtension";
    if (kind !== "bpmn" && ownerOf(node) === undefined) {
      diagnostics.push({
        code: kind === "unsupported" ? "unsupported-model-element" : "opaque-extension-content",
        message: kind === "unsupported" ? `Unsupported BPMN element ${node.localName}` : `Opaque extension element ${node.localName}`,
        disposition: "partial", source: { path, element: assignedIds.get(node)! },
      });
    }
    const element: LandscapeElement = {
      identity: identities.get(node)!, kind, namespace: node.namespace, id: assignedIds.get(node)!,
      type: node.localName, attributes: node.attributes,
      order: node.parent ? node.parent.children.filter(child => child.namespace === node.namespace && child.localName === node.localName).indexOf(node) : 0,
      source: { path, element: assignedIds.get(node)! },
    };
    if (node.parent && (retainedNodes.has(node.parent) || semanticNodes.has(node.parent))) element.parent = identities.get(node.parent)!;
    account(element, elements.length > 0);
    elements.push(element);
    if (isStandardLayoutNamespace("bpmn", node.namespace) && node.attributes.bpmnElement !== undefined) {
      const target = idNodes.get(node.attributes.bpmnElement);
      if (!target || target.namespace !== BPMN_NAMESPACE
        || (!retainedNodes.has(target) && !semanticNodes.has(target))) {
        diagnose("unresolved-layout-reference", node, "Layout reference has no retained local BPMN target", node.attributes.bpmnElement);
      }
    }
  }
  const document: LandscapeDocument = { path, elements, relationships: [] };
  const endpoint = (reference: string | undefined): XmlNode => {
    if (!reference) throw new ModelXmlError("incomplete-model-reference", "Flow requires sourceRef and targetRef");
    const target = idNodes.get(reference);
    if (!target) throw new ModelXmlError("dangling-model-reference", "Dangling BPMN reference");
    if (target.namespace !== BPMN_NAMESPACE || (target.localName !== "participant" && !MESSAGE_ENDPOINT_TYPES.has(target.localName))) {
      throw new ModelXmlError("invalid-model-reference-target", "Invalid BPMN reference target type");
    }
    return target;
  };
  const pool = (flow: XmlNode, node: XmlNode, collaboration: XmlNode): XmlNode | undefined => {
    if (node.localName === "participant") {
      if (node.parent === collaboration && ownerOf(node) === undefined) return node;
      diagnose("invalid-message-flow-scope", flow, "Message endpoint participant is outside the owning collaboration");
      return undefined;
    }
    const owner = ownerOf(node);
    const process = owner === undefined ? undefined : processByOwner.get(owner);
    const participants = process ? participantsByCollaboration.get(collaboration)?.get(process) ?? [] : [];
    if (participants.length === 1) return participants[0]!;
    diagnose(participants.length > 1 ? "ambiguous-message-flow-participant" : "unrepresented-message-flow-endpoint",
      flow, "Message endpoint does not identify exactly one participant in this collaboration");
    return undefined;
  };
  for (const node of retainedNodes) {
    if (node.namespace !== BPMN_NAMESPACE || node.localName !== "messageFlow") continue;
    const source = endpoint(node.attributes.sourceRef);
    const target = endpoint(node.attributes.targetRef);
    const collaboration = collaborationOf(node);
    if (!collaboration || node.parent !== collaboration) {
      diagnose("invalid-message-flow-scope", node, "Message flow is not owned by a collaboration");
      continue;
    }
    const sourcePool = pool(node, source, collaboration);
    const targetPool = pool(node, target, collaboration);
    if (!sourcePool || !targetPool) continue;
    if (sourcePool === targetPool) {
      diagnose("invalid-message-flow-scope", node, "Message endpoints belong to the same participant");
      continue;
    }
    const relationship = {
      identity: `landscape-relationship:${createHash("sha256").update(`${identityScope}\0${path}\0${assignedIds.get(node)!}`, "utf8").digest("hex")}`,
      kind: "messageFlow" as const, source: identities.get(source)!, target: identities.get(target)!,
      elementIdentity: identities.get(node)!, order: document.relationships.length,
    };
    account(relationship, document.relationships.length > 0);
    document.relationships.push(relationship);
  }
  return { document, retainedNodes, semanticNodes, diagnostics, supported: [...supported].sort() };
}
