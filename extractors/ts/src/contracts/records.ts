// Hand-written view of schema/v1/record.schema.json. The JSON Schema is the source of truth;
// these types only cover what the TypeScript side reads.
export const SCHEMA_ID = "ce.v1";

export type RecordKind = "header" | "node" | "edge" | "evidence" | "calculation" | "diagnostic" | "complete";
export type CountedKind = Exclude<RecordKind, "header" | "complete">;
export const COUNTED_KINDS: readonly CountedKind[] = ["node", "edge", "evidence", "calculation", "diagnostic"];
export const ID_PREFIX: Record<CountedKind, string> = { node: "n:", edge: "e:", evidence: "ev:", calculation: "c:", diagnostic: "d:" };

export interface ToolRef { name: string; version: string }

export interface HeaderRecord {
  schema: typeof SCHEMA_ID; kind: "header";
  repositoryId: string; commitSha: string;
  unit: { path: string; name: string };
  language: string; extractor: ToolRef; rules?: ToolRef[];
  configHash: string; inputHash: string;
}

export interface Span { startLine: number; startColumn: number; endLine: number; endColumn: number }

export interface EvidenceRecord {
  schema: typeof SCHEMA_ID; kind: "evidence"; id: string;
  repositoryId: string; commitSha: string; path: string; span: Span; snippetSha256: string; extractor: ToolRef;
}

export interface NodeRecord { schema: typeof SCHEMA_ID; kind: "node"; id: string; symbolKey: string; nodeKind: string; name: string; evidence: string[] }
export interface EdgeRecord {
  schema: typeof SCHEMA_ID; kind: "edge"; id: string; from: string; relation: string; to?: string;
  derivation: "compiler" | "rule" | "human"; resolution: "resolved" | "static-target" | "candidate" | "unresolved"; evidence: string[];
}
export interface Attribute { status: "known" | "unknown" | "partial"; evidence?: string[] }
export interface CalculationRecord {
  schema: typeof SCHEMA_ID; kind: "calculation"; id: string; symbolKey: string;
  attributes: Record<string, Attribute>; output: { unit?: Attribute }; inputs: { ref: string; unit?: Attribute }[];
  evidence: string[];
}
export interface DiagnosticRecord { schema: typeof SCHEMA_ID; kind: "diagnostic"; id: string; severity: string; affectsCoverage?: boolean; evidence?: string[] }
export interface CompleteRecord {
  schema: typeof SCHEMA_ID; kind: "complete"; status: "full" | "partial";
  counts: Record<CountedKind, number>;
  coverage: { filesTotal: number; filesAnalyzed: number; incompletePaths?: string[]; unresolvedEdges?: number };
}

export type AnyRecord = HeaderRecord | NodeRecord | EdgeRecord | EvidenceRecord | CalculationRecord | DiagnosticRecord | CompleteRecord;
