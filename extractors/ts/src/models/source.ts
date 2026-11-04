import { createHash } from "node:crypto";
import { BPMN_NAMESPACE, DEFAULT_XML_LIMITS, DMN_13_NAMESPACE, parseXml } from "./xml.ts";

export interface ModelSource {
  schema: "ce.model-source.v1";
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  modelArtifactSha256: string;
  sourceSha256: string;
  sourceFormat: "bpmn+xml" | "dmn+xml";
  sourceBytesBase64: string;
}

export const MAX_SOURCE_BYTES = 1_048_576;
const hash = (bytes: Uint8Array) => createHash("sha256").update(bytes).digest("hex");

export function sourceBytes(source: ModelSource): Buffer {
  const bytes = Buffer.from(source.sourceBytesBase64, "base64");
  if (!bytes.length || bytes.length > MAX_SOURCE_BYTES ||
      bytes.toString("base64") !== source.sourceBytesBase64) throw new Error("invalid canonical source base64 or byte limit");
  if (hash(bytes) !== source.sourceSha256) throw new Error("source digest mismatch");
  const xml = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  const root = parseXml(xml, { ...DEFAULT_XML_LIMITS, maxBytes: MAX_SOURCE_BYTES });
  const pending = [root];
  while (pending.length) {
    const node = pending.pop()!;
    if (node.localName === "script" && node.namespace === BPMN_NAMESPACE) throw new Error("source scripts are prohibited");
    pending.push(...node.children);
  }
  if (root.localName !== "definitions" ||
      root.namespace !== (source.sourceFormat === "bpmn+xml" ? BPMN_NAMESPACE : DMN_13_NAMESPACE)) {
    throw new Error("source format and XML root disagree");
  }
  return bytes;
}

export function createModelSource(bytes: Buffer, modelBytes: Buffer): ModelSource {
  const model = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(modelBytes));
  if (model.schema !== "ce.models.v1" || model.contentProfile !== "model-content") {
    throw new Error("source attachment requires authored model-content evidence");
  }
  const root = parseXml(new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes));
  const source: ModelSource = {
    schema: "ce.model-source.v1", repositoryId: model.repositoryId, commitSha: model.commitSha,
    unitPath: model.unitPath, modelArtifactSha256: hash(modelBytes), sourceSha256: hash(bytes),
    sourceFormat: root.namespace === BPMN_NAMESPACE ? "bpmn+xml" : "dmn+xml",
    sourceBytesBase64: bytes.toString("base64"),
  };
  sourceBytes(source);
  if (model.sourceSha256 !== source.sourceSha256) throw new Error("source and companion digest disagree");
  return source;
}
