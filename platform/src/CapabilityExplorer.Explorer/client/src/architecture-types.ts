import type { Evidence, SymbolRef } from "./api";


export interface ArchMember {
  label: string;
  detail?: string;
  status: string;
  symbolKey?: string;
  connections: { service: string; label: string; paths: string[]; bound: string[] }[];
}

export interface ArchNode {
  id: string;
  category: string;
  label: string;
  sublabel?: string;
  group?: string;
  symbolKey?: string;
  repositoryId?: string;
  drill?: { level: string; focus: string };
  badges: string[];
  members?: ArchMember[];
}

export interface ArchFact {
  relation: string;
  from: SymbolRef;
  to?: SymbolRef;
  resolution: "resolved" | "static-target" | "candidate" | "unresolved";
  unresolvedReason?: string;
  derivation: string;
  rule?: string;
  evidence: Evidence[];
}

export interface ArchEdge {
  id: string;
  from: string;
  to: string;
  kind: string;
  label: string;
  count: number;
  certainty: "human" | "proven" | "static" | "bound" | "broken" | "unbound" | "candidate" | "unresolved";
  facts: ArchFact[];
  manualConnectionId?: string | null;
  note?: string | null;
}

export interface ManualConnection {
  id: string;
  fromCapabilityId: string;
  toCapabilityId: string;
  kind: string;
  note?: string | null;
  sourceRepositoryId?: string | null;
  sourceKey?: string | null;
  targetRepositoryId?: string | null;
  targetKey?: string | null;
}

export interface ManualConnectionSnapshot {
  version: number;
  connections: ManualConnection[];
}

export type ConnectionDraft = Omit<ManualConnection, "id"> & { id?: string };

export interface Graph {
  level: "landscape" | "system" | "repository" | "component" | "requests" | "glossary-flow";
  focus?: string;
  title: string;
  breadcrumbs: { label: string; level: string; focus?: string }[];
  nodes: ArchNode[];
  edges: ArchEdge[];
  notes: string[];
  budget?: {
    nodeBudget: number;
    edgeBudget: number;
    totalNodes: number;
    matchedNodes: number;
    shownNodes: number;
    totalEdges: number;
    matchedEdges: number;
    shownEdges: number;
    truncated: boolean;
    nodesTruncated: boolean;
    edgesTruncated: boolean;
    pinned: string[];
    missingPins: string[];
  };
  filters?: {
    hideKinds: string[];
    query: string | null;
    include: string[];
  };
  facets?: {
    kinds: { value: string; total: number; matched: number }[];
  };
  omitted?: {
    nodes: { id: string; label: string; category: string; degree: number }[];
    nodeCount: number;
    listed: number;
  };
  /** Issue #102 metadata; optional so legacy graphs and fixtures remain valid. */
  unregisteredHttpTargets?: ArchitectureHttpTargets;
}

export interface BindingPreviewSource {
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  artifactHash: string;
}

export interface EvidenceRecord {
  id: string;
  repositoryId: string;
  commitSha: string;
  path: string;
  snippetSha256: string;
  span: { startLine: number; startColumn: number; endLine: number; endColumn: number };
  extractor: { name: string; version: string };
}

export interface HttpEndpointEvidence {
  source: BindingPreviewSource;
  key: string;
  evidence: EvidenceRecord[];
}

export interface ArchitectureHttpOccurrence {
  occurrence: {
    id: string;
    source: BindingPreviewSource;
    edgeId: string;
    caller: string;
    client?: string | null;
    target?: string | null;
    operation?: string | null;
    evidence: EvidenceRecord[];
  };
  /** Full count before the bounded evidence list. */
  evidenceCount: number;
}

export interface ArchitectureHttpTargetDetail {
  id: string;
  label: string;
  status: string;
  repositoryId: string;
  unitPath: string;
  caller: string;
  client?: string | null;
  destination: string;
  consumers: { capabilityId: string; capabilityName: string; repositoryId: string }[];
  /** Selection-wide Capability membership rows. */
  consumerCount: number;
  operations: string[];
  /** Selection-wide distinct operations; graph edge counts are occurrence support. */
  operationCount: number;
  reasons: string[];
  alternatives: HttpEndpointEvidence[];
  alternativeCount: number;
  missingEvidence: string[];
  occurrences: ArchitectureHttpOccurrence[];
  occurrenceCount: number;
  detailsTruncated: boolean;
  recovery: { level: string; focus: string };
}

export interface ArchitectureHttpTargets {
  /** Changes whenever the selected source scope or rules change. */
  resultId: string;
  ruleVersion: string;
  providerCoverage: {
    status: "full" | "partial";
    sources: BindingPreviewSource[];
    missingRepositories: string[];
    qualifications: string[];
  };
  providerSourceCount: number;
  missingRepositoryCount: number;
  totalCandidates: number;
  matchedCandidates: number;
  shownCandidates: number;
  details: ArchitectureHttpTargetDetail[];
  detailsTruncated: boolean;
  outcomes: { status: string; groups: number; occurrences: number }[];
}
