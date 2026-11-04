import {
  type ModelEvidence, type ModelEvidenceView, type ModelList, type ModelScope, type ModelSourceView,
  type ModelSummary, isModelEvidence, isModelEvidenceView, isModelScope, isModelSourceView, sameScope,
} from "../models/contracts";

export async function getJson<T>(url: string, signal: AbortSignal, maxBytes: number): Promise<T> {
  const response = await fetch(url, { signal, headers: { accept: "application/json" } });
  if (!response.body) throw new Error("The server response cannot be read safely.");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.byteLength;
    if (length > maxBytes) {
      await reader.cancel();
      throw new Error("413 Bounded model response exceeds its serialized byte limit.");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  let body: unknown;
  try { body = length ? JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) : undefined; } catch { body = undefined; }
  if (!response.ok) {
    const problem = body && typeof body === "object" ? body as Record<string, unknown> : {};
    throw new Error(String(problem.detail ?? problem.error ?? problem.title ?? `${response.status} ${response.statusText}`));
  }
  if (body === undefined) throw new Error("The server returned an empty or invalid JSON response.");
  return body as T;
}

function validSummary(value: unknown): value is ModelSummary {
  if (!value || typeof value !== "object") return false;
  const item = value as ModelSummary;
  return typeof item.id === "string" && item.id.length > 0 &&
    (item.kind === "authored" || item.kind === "candidate") &&
    typeof item.repositoryId === "string" && typeof item.commitSha === "string" &&
    typeof item.unitPath === "string" &&
    (item.modelKind === "bpmn" || item.modelKind === "dmn" || item.modelKind === "candidate") &&
    (item.coverage === "full" || item.coverage === "partial") &&
    Number.isSafeInteger(item.diagnosticCount) && item.diagnosticCount >= 0 &&
    !!item.source && ["available", "missing", "denied"].includes(item.source.status) &&
    (item.source.sha256 === undefined || /^[a-f0-9]{64}$/i.test(item.source.sha256)) &&
    (item.kind === "candidate") === (item.modelKind === "candidate");
}

export function validModelList(value: unknown): value is ModelList {
  if (!value || typeof value !== "object") return false;
  const list = value as ModelList;
  return list.schema === "ce.model-views.v1" && isModelScope(list) &&
    (list.mode === "files" || list.mode === "database") &&
    Array.isArray(list.items) && list.items.length <= 50 && list.items.every(validSummary) &&
    Array.isArray(list.problems) && list.problems.every((problem) => typeof problem === "string") &&
    (list.nextCursor === null || typeof list.nextCursor === "string");
}

export function scopeFrom(value: { snapshot: string; generation: number; selection: Record<string, string> }): ModelScope {
  return { snapshot: value.snapshot, generation: value.generation, selection: value.selection };
}

export function scopedRequest(scope: string, snapshot: ModelScope, cursor?: string) {
  const query = new URLSearchParams(scope);
  query.set("snapshot", snapshot.snapshot);
  query.set("generation", String(snapshot.generation));
  if (cursor) query.set("cursor", cursor);
  return query.toString();
}

export function failureDescription(error: string) {
  if (/403|denied|forbidden/i.test(error)) return "Access to this source is denied. No alternate source was requested.";
  if (/404|missing|not found/i.test(error)) return "The selected source is missing from this immutable snapshot.";
  if (/409|stale|snapshot|generation/i.test(error)) return "This model belongs to a stale snapshot. Reload the current selection.";
  if (/413|limit|too large/i.test(error)) return "The bounded response limit was reached; the server did not return truncated evidence.";
  return error;
}

export function validateEvidence(summary: ModelSummary, view: ModelEvidenceView, list: ModelScope): ModelEvidence {
  if (!isModelEvidenceView(view) || view.id !== summary.id) throw new Error("Selected evidence does not match the bounded evidence schema or model identity.");
  const returned = scopeFrom(view);
  if (!sameScope(returned, list)) throw new Error("Evidence response scope does not match the selected snapshot.");
  const evidence = view.evidence;
  if (!isModelEvidence(evidence)) throw new Error("Selected evidence does not match a supported bounded evidence schema.");
  if (evidence.repositoryId !== summary.repositoryId || evidence.commitSha !== summary.commitSha || evidence.unitPath !== summary.unitPath) {
    throw new Error("Evidence provenance does not match the selected model.");
  }
  if (evidence.schema === "ce.models.v1" && summary.kind !== "authored") throw new Error("Unexpected authored evidence for a candidate model.");
  if (evidence.schema === "ce.candidates.v1" && summary.kind !== "candidate") throw new Error("Unexpected candidate evidence for an authored model.");
  if (evidence.sourceSha256 !== summary.source.sha256 && summary.source.sha256) throw new Error("Evidence source digest does not match the model summary.");
  return evidence;
}

export async function validateSource(summary: ModelSummary, evidence: ModelEvidence, source: ModelSourceView, list: ModelScope) {
  if (!isModelSourceView(source) || source.id !== summary.id) throw new Error("Source response does not match the bounded source schema or selected model identity.");
  if (!sameScope(scopeFrom(source), list)) throw new Error("Source response scope does not match the selected snapshot.");
  if (source.repositoryId !== summary.repositoryId || source.commitSha !== summary.commitSha || source.unitPath !== summary.unitPath) {
    throw new Error("Source provenance does not match the selected model.");
  }
  const evidenceHash = evidence.sourceSha256;
  if (source.sha256 !== summary.source.sha256 || source.sha256 !== evidenceHash) throw new Error("Source digest does not match the summary and selected evidence.");
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(source.xml)));
  const actual = [...digest].map((byte) => byte.toString(16).padStart(2, "0")).join("");
  if (actual !== source.sha256) throw new Error("Source bytes failed SHA-256 verification; unverified content was not rendered.");
  if (summary.modelKind === "bpmn" && source.format !== "bpmn+xml" || summary.modelKind === "dmn" && source.format !== "dmn+xml") {
    throw new Error("Source format does not match the declared model kind.");
  }
}
