import { createHash } from "node:crypto";
import modelSupport from "../../../../schema/models/support.json" with { type: "json" };
import { BPMN_NAMESPACE, DEFAULT_XML_LIMITS, ModelXmlError, parseXml, type XmlNode } from "./xml.ts";
import { constructsFor, FLOW_NODE_TYPES, isStandardLayoutNamespace, validateFlowDirectionDeclarations, type SupportConstruct } from "./extract-semantics.ts";
import type { LandscapeCoverage, LandscapeDiagnostic, LandscapeDocument, LandscapeElement, LandscapeRelationship } from "./process-landscape-types.ts";
import { extractDocumentGraph } from "./process-landscape-document.ts";

const SUPPORT_CONSTRUCTS = (modelSupport as { constructs: SupportConstruct[] }).constructs;

const RELATIONSHIP_TYPES = new Map<string, LandscapeRelationship["kind"]>([
  ["sequenceFlow", "sequenceFlow"],
  ["messageFlow", "messageFlow"],
  ["association", "association"],
]);
const RELATIONSHIP_ELEMENT_NAMES = new Set(RELATIONSHIP_TYPES.keys());

export interface BpmnDiagramExtraction {
  processIndex: number;
  processId?: string;
  name?: string;
  coverage: LandscapeCoverage;
  diagnostics: LandscapeDiagnostic[];
  elements: LandscapeElement[];
  relationships: LandscapeRelationship[];
}

/**
 * A `callActivity` whose `calledElement` looks like a local process-id reference (no scheme,
 * path separator, or non-leading fragment marker) but is not declared in this same document. BPMN
 * processes are commonly split across separate admitted files, so this is deferred -- never
 * silently dropped -- for cross-file resolution once every admitted file's process ids are known;
 * only a reference that is unambiguously external (a scheme/path/import-style value) is diagnosed
 * immediately as `external-called-element`.
 */
export interface PendingCalledElement {
  processIndex: number;
  elementIdentity: string;
  elementId: string;
  calledProcessId: string;
}

export interface BpmnFileExtraction {
  /** Document-scoped diagnostics that are not attributable to a single process (e.g. elements
   * declared outside any `<process>`, or a relationship whose endpoints span more than one process). */
  documentDiagnostics: LandscapeDiagnostic[];
  processes: BpmnDiagramExtraction[];
  pendingCalledElements: PendingCalledElement[];
  document?: LandscapeDocument;
  documentSupported?: string[];
}

function hash(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

function attribute(node: XmlNode, localName: string): string | undefined {
  return node.attributes[localName];
}

function nodePath(node: XmlNode): string {
  const parts: string[] = [];
  for (let current: XmlNode | undefined = node; current; current = current.parent) {
    const siblings = current.parent?.children ?? [current];
    const order = siblings.filter((sibling) => sibling.localName === current!.localName).indexOf(current);
    parts.push(`${current.localName}[${Math.max(order, 0)}]`);
  }
  return parts.reverse().join("/");
}

function collectNodes(root: XmlNode): XmlNode[] {
  const result: XmlNode[] = [];
  const visit = (node: XmlNode) => {
    result.push(node);
    for (const child of node.children) visit(child);
  };
  visit(root);
  return result;
}

function isProcessNode(node: XmlNode): boolean {
  return node.namespace === BPMN_NAMESPACE && node.localName === "process";
}

function requireReferenceTarget(reference: string, idNodes: Map<string, XmlNode>, expectedTypes: string[]): XmlNode {
  const target = idNodes.get(reference);
  if (!target) throw new ModelXmlError("dangling-model-reference", "Dangling BPMN reference");
  if (target.namespace !== BPMN_NAMESPACE || (expectedTypes.length > 0 && !expectedTypes.includes(target.localName))) {
    throw new ModelXmlError("invalid-model-reference-target", "Invalid BPMN reference target type");
  }
  return target;
}

/**
 * Extracts every `<process>` definition declared in a single BPMN document using the shared XML
 * parser and construct-support helpers, mirroring the authored-model extraction logic without a
 * repository commit binding (process landscapes are not commit-bound).
 *
 * Multi-process documents: a BPMN document may declare more than one `<process>` element (for
 * example, several independently authored processes exported into one file). Each is extracted as
 * a separately navigable process identity, discriminated by its 0-based document-order position
 * (`processIndex`) rather than its `id` attribute, because `id` may repeat or be absent and must
 * not be relied on as a discriminator. Elements and relationships are partitioned to the nearest
 * enclosing `<process>` ancestor ("owner"). To keep the single-process identity/hash format
 * unchanged from before multi-process support existed, a document with zero or one `<process>`
 * elements is treated as one implicit whole-document scope (exactly the prior behavior); the
 * strict per-element ownership partition only activates once a document declares two or more.
 *
 * A relationship (sequenceFlow/messageFlow/association) whose own element, source, or target
 * resolve to different owning processes -- or to no process at all -- cannot be represented by
 * the current per-process relationship contract. This includes invalid cross-process sequence
 * flows as well as valid collaboration message flows. It is never misattributed to a process:
 * it is surfaced as an explicit, document-scoped `cross-process-relationship-unresolved`
 * diagnostic. Non-relationship elements that are not owned by any `<process>` (document-level
 * content such as `<collaboration>`/`<participant>`) are likewise never silently included under an
 * arbitrarily chosen process; they are summarized in one explicit `unowned-bpmn-element`
 * diagnostic per document. Layout uses explicit `bpmnElement` references rather than XML ancestry:
 * geometry is retained with its referenced process, and shared layout containers are repeated as
 * context in each relevant process so that every retained parent reference remains local.
 */
export function extractBpmnDiagram(xml: string, path: string, identityScope: string, includeDocumentGraph = false): BpmnFileExtraction {
  const root = parseXml(xml, DEFAULT_XML_LIMITS);
  if (root.namespace !== BPMN_NAMESPACE || root.localName !== "definitions") {
    throw new ModelXmlError("unsupported-model-namespace", `Unsupported BPMN namespace or root: ${root.namespace || "(none)"}/${root.localName}`);
  }

  const nodes = collectNodes(root);
  const processNodes = nodes.filter(isProcessNode);
  const multiProcessDocument = processNodes.length > 1;
  const processIndexByNode = new Map<XmlNode, number>(processNodes.map((node, index) => [node, index]));
  const scopeCount = multiProcessDocument ? processNodes.length : 1;

  const ownerCache = new Map<XmlNode, number | undefined>();
  const ownerOf = (node: XmlNode): number | undefined => {
    if (!multiProcessDocument) return 0;
    if (ownerCache.has(node)) return ownerCache.get(node);
    let result: number | undefined;
    const ownIndex = processIndexByNode.get(node);
    if (ownIndex !== undefined) result = ownIndex;
    else if (node.parent) result = ownerOf(node.parent);
    else result = undefined;
    ownerCache.set(node, result);
    return result;
  };

  const idNodes = new Map<string, XmlNode>();
  const assignedIds = new Map<XmlNode, string>();
  const elementIdentities = new Map<XmlNode, string>();
  const generatedIds = new Set<string>();
  const documentDiagnostics: LandscapeDiagnostic[] = [];
  const perScopeDiagnostics: LandscapeDiagnostic[][] = Array.from({ length: scopeCount }, () => []);

  for (const node of nodes) {
    if (node.namespace === BPMN_NAMESPACE && node.localName === "script") {
      throw new ModelXmlError("xml-script-disabled", "Executable script elements are not supported or evaluated");
    }
    const explicitId = attribute(node, "id");
    if (explicitId) {
      if (idNodes.has(explicitId)) throw new ModelXmlError("duplicate-model-id", "Duplicate explicit BPMN id");
      idNodes.set(explicitId, node);
    }
    const id = explicitId ?? `${node.localName}@${nodePath(node)}`;
    if (generatedIds.has(id)) throw new ModelXmlError("duplicate-model-id", "Duplicate explicit or generated BPMN id");
    generatedIds.add(id);
    assignedIds.set(node, id);
    elementIdentities.set(node, `landscape-element:${hash(`${identityScope}\0${path}\0${node.namespace}\0${id}`)}`);
  }

  validateFlowDirectionDeclarations(nodes, idNodes);

  const documentGraph = includeDocumentGraph && multiProcessDocument ? extractDocumentGraph({
    nodes, idNodes, assignedIds, identities: elementIdentities, ownerOf, path, identityScope,
  }) : undefined;
  if (documentGraph) documentDiagnostics.push(...documentGraph.diagnostics);

  const layoutScopes = new Map<XmlNode, Set<number>>();
  if (multiProcessDocument) {
    const visitLayout = (node: XmlNode, inherited?: number): Set<number> => {
      const scopes = new Set<number>();
      const layout = isStandardLayoutNamespace("bpmn", node.namespace);
      let owner = layout ? inherited ?? ownerOf(node) : undefined;
      if (layout) {
        layoutScopes.set(node, scopes);
        const reference = attribute(node, "bpmnElement");
        if (reference) {
          const target = idNodes.get(reference);
          owner = target?.namespace === BPMN_NAMESPACE ? ownerOf(target) : undefined;
          if (owner !== undefined && target && RELATIONSHIP_ELEMENT_NAMES.has(target.localName)) {
            const source = idNodes.get(attribute(target, "sourceRef") ?? "");
            const destination = idNodes.get(attribute(target, "targetRef") ?? "");
            if (!source || !destination || ownerOf(source) !== owner || ownerOf(destination) !== owner) owner = undefined;
          }
          // A collaboration plane can contain shapes from several processes, but the
          // collaboration itself is not made a process-owned semantic element.
          const collaborationPlane = node.localName === "BPMNPlane"
            && target?.namespace === BPMN_NAMESPACE && target.localName === "collaboration";
          if (owner === undefined && !collaborationPlane) {
            if (!documentGraph?.retainedNodes.has(target!)) documentDiagnostics.push({
              code: "unresolved-layout-reference",
              message: "Layout reference has no representable process-owned BPMN target",
              disposition: "unresolved",
              source: { path, element: assignedIds.get(node)!, ref: reference },
            });
            for (const descendant of collectNodes(node)) {
              if (isStandardLayoutNamespace("bpmn", descendant.namespace)) layoutScopes.set(descendant, new Set());
            }
            return scopes;
          }
        }
        if (owner !== undefined) scopes.add(owner);
      }
      for (const child of node.children) {
        const childScopes = visitLayout(child, owner);
        if (layout) for (const scope of childScopes) scopes.add(scope);
      }
      return scopes;
    };
    visitLayout(root);
  }

  const perScopeElements: LandscapeElement[][] = Array.from({ length: scopeCount }, () => []);
  const perScopeRelationships: LandscapeRelationship[][] = Array.from({ length: scopeCount }, () => []);
  const pendingCalledElements: PendingCalledElement[] = [];
  let unownedElementCount = 0;
  const unownedElementTypes = new Set<string>();

  const scopedNodes = nodes.flatMap(node => {
    const scopes = layoutScopes.get(node);
    const owners = scopes && scopes.size > 0 ? [...scopes] : [scopes ? undefined : ownerOf(node)];
    return owners.map(owner => ({ node, owner }));
  });
  for (const { node, owner } of scopedNodes) {
    if (node.namespace === BPMN_NAMESPACE && node.localName === "text") continue;
    const isRelationshipNode = node.namespace === BPMN_NAMESPACE && RELATIONSHIP_ELEMENT_NAMES.has(node.localName);
    const id = assignedIds.get(node)!;

    if (!isRelationshipNode) {
      if (owner === undefined) {
        if (documentGraph?.retainedNodes.has(node)) continue;
        unownedElementCount++;
        unownedElementTypes.add(node.localName);
        continue;
      }
    } else {
      // Resolve the relationship's endpoints before deciding ownership: a relationship diagnosed as
      // cross-process must still validate that its references exist and have an allowed target type
      // (hard parse failures), it just must never be silently attributed to a single process.
      const sourceRef = attribute(node, "sourceRef");
      const targetRef = attribute(node, "targetRef");
      if (!sourceRef || !targetRef) throw new ModelXmlError("incomplete-model-reference", "Flow requires sourceRef and targetRef");
      const expectedTypes = node.localName === "sequenceFlow"
        ? [...FLOW_NODE_TYPES]
        : node.localName === "messageFlow" ? [...FLOW_NODE_TYPES, "participant"] : [];
      const sourceNode = requireReferenceTarget(sourceRef, idNodes, expectedTypes);
      const targetNode = requireReferenceTarget(targetRef, idNodes, expectedTypes);
      const sourceOwner = ownerOf(sourceNode);
      const targetOwner = ownerOf(targetNode);
      const sameScope = owner !== undefined && owner === sourceOwner && owner === targetOwner;
      if (!sameScope) {
        if (documentGraph?.retainedNodes.has(node)) continue;
        documentDiagnostics.push({
          code: "cross-process-relationship-unresolved",
          message: `${node.localName} spans more than one process (or an unowned document element) and is not attributed to a single process`,
          disposition: "unresolved",
          source: { path, element: id },
        });
        continue; // never silently attached to an arbitrarily chosen process
      }
    }

    const constructRows = constructsFor("bpmn", node.namespace, node.localName, SUPPORT_CONSTRUCTS);
    const status = constructRows.some(item => item.status === "rejected")
      ? "rejected"
      : constructRows.some(item => item.status === "semantic-evidence-supported")
        ? "semantic-evidence-supported" : "opaque-partial";
    if (status === "rejected") {
      throw new ModelXmlError("unsupported-rejected-construct", "Encountered a construct excluded from process-landscape extraction");
    }
    // A standard BPMN DI/DC/DI (diagram interchange) namespace is known, inherently layout-only
    // content: it is never a "business" BPMN element and must never be counted toward
    // `unsupported-model-element`, even when the specific local name is not individually
    // enumerated in support.json (e.g. BPMNLabel/BPMNLabelStyle/Font alongside the enumerated
    // BPMNDiagram/BPMNPlane/BPMNShape/BPMNEdge/Bounds/waypoint). Any node outside the BPMN model
    // namespace and outside the known layout namespaces is vendor/foreign extension content
    // (`opaqueExtension`), which already has its own, separate `opaque-extension-content`
    // diagnostic and is never conflated with unsupported BPMN business elements either.
    const knownLayoutNamespace = isStandardLayoutNamespace("bpmn", node.namespace);
    const nodeKind: LandscapeElement["kind"] = node.namespace === BPMN_NAMESPACE
      ? status === "semantic-evidence-supported" ? "bpmn" : "unsupported"
      : knownLayoutNamespace ? "bpmn" : "opaqueExtension";
    const relationKind = node.namespace === BPMN_NAMESPACE ? RELATIONSHIP_TYPES.get(node.localName) : undefined;
    const sourceRef = attribute(node, "sourceRef");
    const targetRef = attribute(node, "targetRef");
    const relationshipTargets = relationKind
      ? { sourceNode: idNodes.get(sourceRef!)!, targetNode: idNodes.get(targetRef!)! }
      : undefined;

    if (nodeKind === "unsupported" || nodeKind === "opaqueExtension") {
      perScopeDiagnostics[owner!]!.push({
        code: nodeKind === "unsupported" ? "unsupported-model-element" : "opaque-extension-content",
        message: nodeKind === "unsupported"
          ? `Unsupported BPMN element ${node.localName}`
          : `Opaque extension element ${node.localName} in namespace ${node.namespace || "(none)"}`,
        disposition: "partial",
        source: { path, element: id },
      });
    }

    const element: LandscapeElement = {
      identity: elementIdentities.get(node)!,
      kind: nodeKind,
      namespace: node.namespace,
      id,
      type: node.localName,
      order: node.parent
        ? node.parent.children.filter((child) => child.namespace === node.namespace && child.localName === node.localName).indexOf(node)
        : 0,
      attributes: node.attributes,
      source: { path, element: id },
    };
    if (node.parent && (layoutScopes.get(node.parent)?.has(owner!) ?? ownerOf(node.parent) === owner)
      && (!documentGraph || documentGraph.retainedNodes.has(node.parent) || documentGraph.semanticNodes.has(node.parent))) {
      element.parent = elementIdentities.get(node.parent);
    }
    perScopeElements[owner!]!.push(element);

    if (relationKind && relationshipTargets) {
      perScopeRelationships[owner!]!.push({
        identity: `landscape-relationship:${hash(`${identityScope}\0${path}\0${id}`)}`,
        kind: relationKind,
        source: elementIdentities.get(relationshipTargets.sourceNode)!,
        target: elementIdentities.get(relationshipTargets.targetNode)!,
        order: perScopeRelationships[owner!]!.length,
      });
    }

    if (node.namespace === BPMN_NAMESPACE && node.localName === "callActivity") {
      const calledElement = attribute(node, "calledElement");
      if (calledElement) {
        const targetId = calledElement.startsWith("#") ? calledElement.slice(1) : calledElement;
        const target = !calledElement.includes(":") && !calledElement.includes("/") ? idNodes.get(targetId) : undefined;
        if (target && isProcessNode(target)) {
          // A callActivity referencing another process by id is an intentional, explicitly supported
          // cross-process reference (not a sequenceFlow/messageFlow that BPMN confines to one process).
          perScopeRelationships[owner!]!.push({
            identity: `landscape-relationship:${hash(`${identityScope}\0${path}\0${id}\0${targetId}`)}`,
            kind: "reference",
            source: elementIdentities.get(node)!,
            target: elementIdentities.get(target)!,
            order: perScopeRelationships[owner!]!.length,
          });
        } else if (calledElement.includes(":") || calledElement.includes("/") || (calledElement.includes("#") && !calledElement.startsWith("#"))) {
          perScopeDiagnostics[owner]!.push({
            code: "external-called-element",
            message: "Called process is not resolved within this document and is not imported",
            disposition: "partial",
            source: { path, element: id },
          });
        } else {
          // Looks like a local process-id reference, but no process with that id is declared in
          // this document. BPMN processes are frequently split across files admitted together, so
          // resolution is deferred to a cross-file pass instead of being diagnosed as unresolved here.
          pendingCalledElements.push({ processIndex: owner!, elementIdentity: elementIdentities.get(node)!, elementId: id, calledProcessId: targetId });
        }
      }
    }
  }

  if (unownedElementCount > 0) {
    documentDiagnostics.push({
      code: "unowned-bpmn-element",
      message: `Document declares ${unownedElementCount} element(s) outside any <process> scope (types: ${[...unownedElementTypes].sort().join(", ")})`,
      disposition: "partial",
      source: { path },
    });
  }

  const processes: BpmnDiagramExtraction[] = [];
  for (let index = 0; index < scopeCount; index++) {
    const processNode = multiProcessDocument ? processNodes[index] : processNodes[0];
    const diagnostics = perScopeDiagnostics[index]!;
    const limitations = [...new Set(diagnostics.map(({ code }) => code))].sort();
    const scopeNodes = multiProcessDocument
      ? nodes.filter((node) => layoutScopes.get(node)?.has(index) ?? ownerOf(node) === index)
      : nodes;
    const supported = [...new Set(SUPPORT_CONSTRUCTS.filter(item =>
      item.family === "bpmn"
      && item.status === "semantic-evidence-supported"
      && scopeNodes.some(node => node.namespace === item.namespace && item.elements.includes(node.localName)))
      .map(item => item.id))].sort();

    processes.push({
      processIndex: index,
      processId: processNode ? attribute(processNode, "id") : undefined,
      name: processNode ? attribute(processNode, "name") : undefined,
      coverage: { status: diagnostics.length === 0 ? "full" : "partial", supported, limitations },
      diagnostics,
      elements: perScopeElements[index]!,
      relationships: perScopeRelationships[index]!,
    });
  }

  return {
    documentDiagnostics, processes, pendingCalledElements,
    ...(documentGraph ? { document: documentGraph.document, documentSupported: documentGraph.supported } : {}),
  };
}
