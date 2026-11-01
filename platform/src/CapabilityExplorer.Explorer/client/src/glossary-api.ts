import { ApiError, request, type Evidence } from "./api";
import type { Graph } from "./views/Architecture";

export interface FlowOptions { direction: "both" | "forward" | "backward"; maxHops: number; maxNodes: number; maxEdges: number }
export interface FlowNode {
  id: string; repositoryId: string | null; commitSha: string | null; unit: string | null;
  unitPath: string | null; symbolKey: string | null; occurrenceIds: string[]; evidence: Evidence[];
}
export interface FlowEdge {
  id: string; from: string; to: string; category: string; relation: string;
  repositoryId: string | null; commitSha: string | null; unitPath: string | null; recordId: string | null;
  resolution: string; derivation: string; rule: string | null; unresolvedReason: string | null;
  evidence: Evidence[]; bindingHint?: string; bindingTarget?: string; bindingNote?: string;
  assertionId?: string; termId?: string; reviewer?: string; rationale?: string;
}
export interface FlowResult {
  graph: Graph;
  selection: { repositoryId: string; commitSha: string; unit: string; unitPath: string; analysisStatus: string; filesTotal: number; filesAnalyzed: number; unresolvedEdges: number }[];
  seeds: { occurrenceId: string; nodeId: string }[];
  nodes: FlowNode[]; edges: FlowEdge[];
  coverage: { complete: boolean; truncated: boolean; valueFlowComplete: boolean; reasons: { code: string; message: string; nodeId?: string; edgeId?: string }[] };
}
export interface ReviewedTerm { id: string; name: string; definition: string; group: string | null; reviewer: string; version: number; deleted: boolean }
export interface ObservedOccurrence { repositoryId: string; commitSha: string; unitPath: string; symbolKey: string; artifactContentSha256: string; occurrenceId: string }
export interface ReviewedLink {
  id: string; termId: string; repositoryId: string; unitPath: string; symbolKey: string;
  role: "same" | "alias" | "derived_from"; note: string | null; reviewer: string; version: number; deleted: boolean;
  observed: ObservedOccurrence;
}
export interface ReviewedIssue { id: string; termId: string; kind: "type" | "derivation" | "naming" | "other"; summary: string; note: string | null; status: "open" | "resolved"; reviewer: string; version: number; deleted: boolean }
export interface ReviewResult {
  configured: boolean; readOnly: boolean;
  snapshot: { terms: ReviewedTerm[]; links: ReviewedLink[]; issues: ReviewedIssue[]; limit: number; termsTruncated: boolean; linksTruncated: boolean; issuesTruncated: boolean };
  linkStates: { id: string; state: "current" | "stale" | "orphaned" | "ambiguous" | "out-of-scope"; occurrenceId?: string; reason: string }[];
}
export type ReviewCollection = "terms" | "links" | "issues";
export function reviewMutation(scope: string, collection: ReviewCollection, id: string, method: "PUT" | "DELETE", body: Record<string, unknown>) {
  return request(`/api/glossary/${collection}/${encodeURIComponent(id)}?${scope}`, {
    method, headers: { "X-CE-Glossary-Write": "1" }, body: JSON.stringify(body),
  });
}
export function mutationMessage(error: unknown) {
  return error instanceof ApiError && error.status === 409
    ? "This record changed since you loaded it. Your edits are kept. Reload reviewed data, then reopen the current record to reconcile the changes."
    : error instanceof Error ? error.message : "The request failed.";
}
