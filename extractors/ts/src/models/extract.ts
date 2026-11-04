import { createHash } from "node:crypto";
import { Worker } from "node:worker_threads";
import { BpmnModdle } from "bpmn-moddle";
import { DmnModdle } from "dmn-moddle";
import modelSupport from "../../../../schema/models/support.json" with { type: "json" };
import { BPMN_NAMESPACE, DEFAULT_XML_LIMITS, DMN_13_NAMESPACE, ModelXmlError, parseXml, type XmlNode } from "./xml.ts";
import type { ModelDiagnostic, ModelElement, ModelEvidence, ModelExtractionOptions, ModelRelationship } from "./profile.ts";

type ModelKind = "bpmn" | "dmn";
type ModelParser = { fromXML(xml: string, options?: { lax?: boolean }): Promise<{ warnings: Error[] }> };
type ConstructStatus = "semantic-evidence-supported" | "opaque-partial" | "rejected";
interface SupportConstruct {
  id: string;
  family: ModelKind;
  namespace: string;
  elements: string[];
  status: ConstructStatus;
  capturesText?: boolean;
  matchesUnlisted?: boolean;
}

const SUPPORT_CONSTRUCTS = (modelSupport as { constructs: SupportConstruct[] }).constructs;

const RELATIONSHIP_TYPES = new Map<string, ModelRelationship["kind"]>([
  ["sequenceFlow", "sequenceFlow"],
  ["messageFlow", "messageFlow"],
  ["association", "association"],
]);
const FLOW_NODE_TYPES = new Set([
  "task", "userTask", "serviceTask", "scriptTask", "manualTask", "businessRuleTask", "sendTask", "receiveTask",
  "startEvent", "endEvent", "intermediateCatchEvent", "intermediateThrowEvent", "boundaryEvent", "exclusiveGateway",
  "inclusiveGateway", "parallelGateway", "eventBasedGateway", "complexGateway", "subProcess", "transaction",
  "adHocSubProcess", "callActivity",
]);

function hash(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

function attribute(node: XmlNode, localName: string): string | undefined {
  return node.attributes[localName];
}

function nodeContent(node: XmlNode): string {
  const text = node.children.find((child) => child.namespace === node.namespace && child.localName === "text");
  return text?.text ?? node.text;
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

function validateOptions(options: ModelExtractionOptions): void {
  if (!options.repositoryId.trim() || /[\u0000-\u001f]/.test(options.repositoryId)) {
    throw new ModelXmlError("invalid-repository", "Repository identity must be non-empty and contain no control characters");
  }
  if (!/^[a-f\d]{40}$/i.test(options.commitSha)) {
    throw new ModelXmlError("invalid-commit", "Commit SHA must be exactly 40 hexadecimal characters");
  }
  if (
    !options.unitPath
    || options.unitPath.startsWith("/")
    || /^[a-zA-Z]:[\\/]/.test(options.unitPath)
    || options.unitPath.startsWith("\\\\")
    || options.unitPath.split(/[\\/]/).includes("..")
    || /[\u0000-\u001f]/.test(options.unitPath)
  ) {
    throw new ModelXmlError("invalid-unit-path", "Model unit path must be a non-empty repository-relative path without traversal");
  }
  if (options.contentProfile && options.contentProfile !== "structural" && options.contentProfile !== "model-content") {
    throw new ModelXmlError("invalid-content-profile", "Content profile must be structural or model-content");
  }
}

async function parserWarnings(kind: ModelKind, xml: string): Promise<Error[]> {
  const parser: ModelParser = kind === "bpmn" ? new BpmnModdle() : new DmnModdle();
  try {
    const parsed = await parser.fromXML(xml, { lax: true });
    return parsed.warnings;
  } catch {
    throw new ModelXmlError("model-parse-failed", "Moddle parser rejected the XML document");
  }
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

function constructsFor(kind: ModelKind, namespace: string, element: string): SupportConstruct[] {
  const rows = SUPPORT_CONSTRUCTS.filter(item => item.family === kind && (item.namespace === namespace || item.namespace === "*"));
  const exact = rows.filter(item => item.elements.includes(element));
  return exact.length ? exact : rows.filter(item => item.matchesUnlisted || item.namespace === "*");
}

function addPartialDiagnostic(
  diagnostics: ModelDiagnostic[],
  code: string,
  message: string,
  unitPath: string,
  element?: string,
): void {
  diagnostics.push({
    code,
    message,
    disposition: "partial",
    source: { artifact: unitPath, ...(element ? { element } : {}) },
  });
}

interface ModelWorkerResult {
  ok: boolean;
  evidence?: ModelEvidence;
  code?: string;
  message?: string;
}

function originalBytes(xml: string, supplied?: Uint8Array): Uint8Array {
  if (Buffer.byteLength(xml, "utf8") > DEFAULT_XML_LIMITS.maxBytes ||
      (supplied && supplied.byteLength > DEFAULT_XML_LIMITS.maxBytes)) {
    throw new ModelXmlError("xml-byte-limit", "XML byte limit exceeded");
  }
  const bytes = supplied ?? Buffer.from(xml, "utf8");
  let decoded: string;
  try { decoded = new TextDecoder("utf-8", { fatal: true }).decode(bytes); }
  catch { throw new ModelXmlError("invalid-source-bytes", "Source bytes must be UTF-8"); }
  if (decoded !== xml.replace(/^\uFEFF/, "")) {
    throw new ModelXmlError("source-byte-mismatch", "XML does not match the supplied source bytes");
  }
  return bytes;
}

export async function extractModelXml(
  xml: string,
  options: ModelExtractionOptions,
  sourceBytes?: Uint8Array,
  workerTimeoutMs = DEFAULT_XML_LIMITS.maxParseMilliseconds,
): Promise<ModelEvidence> {
  validateOptions(options);
  const bytes = originalBytes(xml, sourceBytes);
  if (!Number.isFinite(workerTimeoutMs) || workerTimeoutMs <= 0) {
    throw new ModelXmlError("invalid-time-limit", "Worker timeout must be a positive finite number");
  }
  const boundedWorkerTimeoutMs = Math.min(workerTimeoutMs, DEFAULT_XML_LIMITS.maxParseMilliseconds);
  return new Promise((resolve, reject) => {
    let settled = false;
    const worker = new Worker(new URL("./worker.ts", import.meta.url), {
      workerData: { xml, options, sourceBytes: Buffer.from(bytes) },
      execArgv: [],
      resourceLimits: { maxOldGenerationSizeMb: 128, maxYoungGenerationSizeMb: 32, stackSizeMb: 4 },
    });
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      const timeoutError = new ModelXmlError("xml-time-limit", `XML extraction exceeded ${boundedWorkerTimeoutMs}ms`);
      void worker.terminate().then(() => reject(timeoutError), () => reject(timeoutError));
    }, boundedWorkerTimeoutMs);
    worker.once("message", (result: ModelWorkerResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (result.ok && result.evidence) resolve(result.evidence);
      else reject(new ModelXmlError(result.code ?? "model-extraction-failed", result.message ?? "Model extraction failed"));
    });
    worker.once("error", () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(new ModelXmlError("model-worker-failed", "Model parser worker failed"));
    });
    worker.once("exit", (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(new ModelXmlError("model-worker-failed", `Model parser worker exited (${code}) without a result`));
    });
  });
}

export async function extractModelXmlInProcess(
  xml: string,
  options: ModelExtractionOptions,
  sourceBytes?: Uint8Array,
): Promise<ModelEvidence> {
  const started = performance.now();
  validateOptions(options);
  const bytes = originalBytes(xml, sourceBytes);
  const root = parseXml(xml);
  const kind: ModelKind | undefined = root.namespace === BPMN_NAMESPACE
    ? "bpmn"
    : root.namespace === DMN_13_NAMESPACE ? "dmn" : undefined;
  if (!kind || root.localName !== "definitions") {
    throw new ModelXmlError("unsupported-model-namespace", `Unsupported authored model namespace or root: ${root.namespace || "(none)"}/${root.localName}`);
  }

  const warnings = await parserWarnings(kind, xml);
  if (performance.now() - started > DEFAULT_XML_LIMITS.maxParseMilliseconds) {
    throw new ModelXmlError("xml-time-limit", `XML extraction budget exceeded (${DEFAULT_XML_LIMITS.maxParseMilliseconds}ms)`);
  }
  const expectedNamespace = kind === "bpmn" ? BPMN_NAMESPACE : DMN_13_NAMESPACE;
  const contentProfile = options.contentProfile ?? "structural";
  const nodes = collectNodes(root);
  const idNodes = new Map<string, XmlNode>();
  const assignedIds = new Map<XmlNode, string>();
  const diagnostics: ModelDiagnostic[] = [];
  const elementIdentities = new Map<XmlNode, string>();
  const generatedIds = new Set<string>();

  for (const node of nodes) {
    if (node.namespace === BPMN_NAMESPACE && node.localName === "script") {
      throw new ModelXmlError("xml-script-disabled", "Executable script elements are not supported or evaluated");
    }
    const explicitId = attribute(node, "id");
    if (explicitId) {
      if (idNodes.has(explicitId)) throw new ModelXmlError("duplicate-model-id", "Duplicate explicit model id");
      idNodes.set(explicitId, node);
    }
    const id = explicitId ?? `${node.localName}@${nodePath(node)}`;
    if (generatedIds.has(id)) throw new ModelXmlError("duplicate-model-id", "Duplicate explicit or generated model id");
    generatedIds.add(id);
    assignedIds.set(node, id);
    const identity = `model:${hash(`${options.repositoryId}\0${options.unitPath}\0${node.namespace}\0${id}`)}`;
    elementIdentities.set(node, identity);
  }

  for (const warning of warnings) {
    diagnostics.push({
      code: "moddle-parser-warning",
      message: "Moddle parser reported an unsupported or invalid construct; raw parser detail is omitted",
      disposition: "partial",
      source: { artifact: options.unitPath },
    });
  }

  const elements: ModelElement[] = [];
  const relationships: ModelRelationship[] = [];
  for (const node of nodes) {
    if (node.namespace === expectedNamespace && node.localName === "text") continue;
    const constructRows = constructsFor(kind, node.namespace, node.localName);
    const status = constructRows.some(item => item.status === "rejected")
      ? "rejected"
      : constructRows.some(item => item.status === "semantic-evidence-supported")
        ? "semantic-evidence-supported" : "opaque-partial";
    if (status === "rejected") {
      throw new ModelXmlError("unsupported-rejected-construct", "Encountered a construct excluded from authored-model extraction");
    }
    const nodeKind = node.namespace !== expectedNamespace
      ? "opaqueExtension"
      : status === "semantic-evidence-supported" ? kind : "unsupported";
    if (nodeKind === "unsupported" || nodeKind === "opaqueExtension") {
      diagnostics.push({
        code: nodeKind === "unsupported" ? "unsupported-model-element" : "opaque-extension-content",
        message: nodeKind === "unsupported"
          ? `Unsupported ${kind.toUpperCase()} element ${node.localName}`
          : `Opaque extension element ${node.localName} in namespace ${node.namespace || "(none)"}`,
        disposition: "partial",
        source: { artifact: options.unitPath, element: assignedIds.get(node) },
      });
    }
    const id = assignedIds.get(node)!;
    const element: ModelElement = {
      identity: elementIdentities.get(node)!,
      kind: nodeKind === "opaqueExtension" ? "opaqueExtension" : nodeKind === "unsupported" ? "unsupported" : kind,
      namespace: node.namespace,
      id,
      type: node.localName,
      order: node.parent
        ? node.parent.children.filter((child) => child.namespace === node.namespace && child.localName === node.localName).indexOf(node)
        : 0,
      attributes: node.attributes,
      source: { artifact: options.unitPath, element: id },
    };
    if (node.parent) element.parent = elementIdentities.get(node.parent);

    if (node.namespace === expectedNamespace && kind === "dmn" && node.localName === "decisionTable") {
      const hitPolicy = attribute(node, "hitPolicy");
      const aggregation = attribute(node, "aggregation");
      if (hitPolicy) element.hitPolicy = hitPolicy;
      if (aggregation) element.aggregation = aggregation;
    }
    if (node.namespace === expectedNamespace && kind === "dmn" && node.localName === "rule") {
      element.cells = node.children
        .filter((child) => child.namespace === expectedNamespace && (child.localName === "inputEntry" || child.localName === "outputEntry"))
        .map((cell, index) => {
          const text = nodeContent(cell);
          const language = attribute(cell, "expressionLanguage");
          return {
            kind: cell.localName === "inputEntry" ? "input" : "output",
            order: index,
            ...(contentProfile === "model-content" ? { text } : {}),
            ...(language ? { language } : {}),
          };
        });
    }
    const textContent = nodeContent(node);
    const capturesText = node.namespace !== expectedNamespace || constructRows.some(item => item.capturesText);
    if (capturesText && textContent.trim()) {
      if (contentProfile === "model-content") {
        const language = attribute(node, "expressionLanguage") ?? attribute(node, "language");
        element.content = { text: textContent, ...(language ? { language } : {}) };
      } else {
        diagnostics.push({
          code: "content-redacted",
          message: "Model text omitted by the structural content profile",
          disposition: "partial",
          source: { artifact: options.unitPath, element: id },
        });
      }
    }
    elements.push(element);

    const relationKind = node.namespace === BPMN_NAMESPACE ? RELATIONSHIP_TYPES.get(node.localName) : undefined;
    const sourceRef = attribute(node, "sourceRef");
    const targetRef = attribute(node, "targetRef");
    if (relationKind) {
      if (!sourceRef || !targetRef) {
        throw new ModelXmlError("incomplete-model-reference", "Flow requires sourceRef and targetRef");
      }
      const expectedTypes = node.localName === "sequenceFlow"
        ? [...FLOW_NODE_TYPES]
        : node.localName === "messageFlow" ? [...FLOW_NODE_TYPES, "participant"] : [];
      const sourceNode = requireReferenceTarget(sourceRef, idNodes, BPMN_NAMESPACE, expectedTypes, node.localName);
      const targetNode = requireReferenceTarget(targetRef, idNodes, BPMN_NAMESPACE, expectedTypes, node.localName);
      const condition = node.children.find((child) => child.namespace === BPMN_NAMESPACE && child.localName === "conditionExpression");
      relationships.push({
        identity: `relationship:${hash(`${options.repositoryId}\0${options.unitPath}\0${id}`)}`,
        kind: relationKind,
        source: elementIdentities.get(sourceNode)!,
        target: elementIdentities.get(targetNode)!,
        order: relationships.length,
        ...(condition ? {
          condition: {
            ...(contentProfile === "model-content" ? { text: nodeContent(condition) } : {}),
            ...(attribute(condition, "language") ? { language: attribute(condition, "language") } : {}),
          },
        } : {}),
      });
    }

    const processRef = node.namespace === BPMN_NAMESPACE && node.localName === "participant" ? attribute(node, "processRef") : undefined;
    if (processRef) {
      addReference(node, processRef, id, idNodes, elementIdentities, relationships, options, BPMN_NAMESPACE, ["process"]);
    }
    if (node.namespace === BPMN_NAMESPACE && node.localName === "dataObjectReference") {
      const reference = attribute(node, "dataObjectRef");
      if (reference) addReference(node, reference, id, idNodes, elementIdentities, relationships, options, BPMN_NAMESPACE, ["dataObject"]);
    }
    if (node.namespace === BPMN_NAMESPACE && node.localName === "dataStoreReference") {
      const reference = attribute(node, "dataStoreRef");
      if (reference) addReference(node, reference, id, idNodes, elementIdentities, relationships, options, BPMN_NAMESPACE, ["dataStore"]);
    }
    if (kind === "bpmn" && node.namespace === BPMN_NAMESPACE && node.localName === "lane") {
      for (const reference of node.children.filter((child) => child.namespace === BPMN_NAMESPACE && child.localName === "flowNodeRef")) {
        addReference(node, reference.text.trim(), `${id}:${reference.text.trim()}`, idNodes, elementIdentities, relationships, options, BPMN_NAMESPACE, [...FLOW_NODE_TYPES]);
      }
    }
    if (node.namespace === BPMN_NAMESPACE && node.localName === "callActivity") {
      const calledElement = attribute(node, "calledElement");
      if (calledElement) {
        const targetId = calledElement.startsWith("#") ? calledElement.slice(1) : calledElement;
        const target = !calledElement.includes(":") && !calledElement.includes("/") ? idNodes.get(targetId) : undefined;
        if (target?.namespace === BPMN_NAMESPACE && target.localName === "process") {
          addReference(node, targetId, id, idNodes, elementIdentities, relationships, options, BPMN_NAMESPACE, ["process"]);
        } else {
          const external = calledElement.includes(":") || calledElement.includes("/")
            || (calledElement.includes("#") && !calledElement.startsWith("#"));
          addPartialDiagnostic(
            diagnostics,
            external ? "external-called-element" : "unresolved-called-element",
            "Called process is not resolved within this document and is not imported",
            options.unitPath,
            id,
          );
        }
      }
    }
    if (kind === "dmn" && node.namespace === DMN_13_NAMESPACE
      && ["requiredInput", "requiredDecision", "requiredKnowledge", "requiredAuthority"].includes(node.localName)) {
      const href = attribute(node, "href");
      if (!href) throw new ModelXmlError("incomplete-model-reference", "Missing model reference href");
      if (href.startsWith("#")) {
        const targetTypes: Record<string, string[]> = {
          requiredInput: ["inputData"],
          requiredDecision: ["decision"],
          requiredKnowledge: ["businessKnowledgeModel"],
          requiredAuthority: ["knowledgeSource"],
        };
        let owner = node.parent ?? node;
        while (owner.parent && owner.localName !== "decision" && owner.localName !== "businessKnowledgeModel") {
          owner = owner.parent;
        }
        addReference(owner, href.slice(1), id, idNodes, elementIdentities, relationships, options, DMN_13_NAMESPACE, targetTypes[node.localName] ?? []);
      } else {
        addPartialDiagnostic(
          diagnostics, "external-model-reference",
          "External model reference retained but not resolved or retrieved",
          options.unitPath, id,
        );
      }
    }
  }

  const limitations = [...new Set(diagnostics.map(({ code }) => code))].sort();
  const supported = [...new Set(SUPPORT_CONSTRUCTS.filter(item =>
    item.family === kind
    && item.status === "semantic-evidence-supported"
    && nodes.some(node => node.namespace === item.namespace && item.elements.includes(node.localName)))
    .map(item => item.id))].sort();
  return {
    schema: "ce.models.v1",
    repositoryId: options.repositoryId,
    commitSha: options.commitSha.toLowerCase(),
    unitPath: options.unitPath,
    sourceSha256: createHash("sha256").update(bytes).digest("hex"),
    contentProfile,
    coverage: {
      status: diagnostics.length === 0 ? "full" : "partial",
      supported,
      limitations,
    },
    diagnostics,
    elements,
    relationships,
  };
}

function addReference(
  sourceNode: XmlNode,
  reference: string,
  identityPart: string,
  idNodes: Map<string, XmlNode>,
  elementIdentities: Map<XmlNode, string>,
  relationships: ModelRelationship[],
  options: ModelExtractionOptions,
  expectedNamespace: string,
  expectedTypes: string[],
): void {
  const targetNode = requireReferenceTarget(reference, idNodes, expectedNamespace, expectedTypes);
  relationships.push({
    identity: `relationship:${hash(`${options.repositoryId}\0${options.unitPath}\0${identityPart}\0${reference}`)}`,
    kind: "reference",
    source: elementIdentities.get(sourceNode)!,
    target: elementIdentities.get(targetNode)!,
    order: relationships.length,
  });
}

function requireReferenceTarget(
  reference: string,
  idNodes: Map<string, XmlNode>,
  expectedNamespace: string,
  expectedTypes: string[],
  relationKind = "model",
): XmlNode {
  const target = idNodes.get(reference);
  if (!target) throw new ModelXmlError("dangling-model-reference", `Dangling ${relationKind} reference`);
  if (target.namespace !== expectedNamespace || (expectedTypes.length > 0 && !expectedTypes.includes(target.localName))) {
    throw new ModelXmlError("invalid-model-reference-target", `Invalid ${relationKind} reference target type`);
  }
  return target;
}
