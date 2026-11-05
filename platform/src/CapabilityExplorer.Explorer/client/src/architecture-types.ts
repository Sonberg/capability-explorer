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
}

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
}
