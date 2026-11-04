export interface ModelScope {
  snapshot: string;
  generation: number;
  selection: Record<string, string>;
}

export interface ModelSummary {
  id: string;
  kind: "authored" | "candidate";
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  modelKind: "bpmn" | "dmn" | "candidate";
  coverage: "full" | "partial";
  diagnosticCount: number;
  source: { status: "available" | "missing" | "denied"; sha256?: string };
}

export interface ModelList {
  schema: "ce.model-views.v1";
  items: ModelSummary[];
  problems: string[];
  mode: "files" | "database";
  snapshot: string;
  generation: number;
  selection: Record<string, string>;
  nextCursor: string | null;
}

interface ModelDiagnostic {
  code: string;
  message: string;
  disposition?: "partial" | "rejected";
  source?: { artifact: string; element?: string };
}

export interface AuthoredModelEvidence {
  schema: "ce.models.v1";
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  sourceSha256: string;
  contentProfile: "structural" | "model-content";
  coverage: { status: "full" | "partial"; supported: string[]; limitations: string[] };
  diagnostics: ModelDiagnostic[];
  elements: Array<{
    identity: string;
    kind: "bpmn" | "dmn" | "opaqueExtension" | "unsupported";
    namespace: string;
    id: string;
    type: string;
    order: number;
    parent?: string;
    attributes: Record<string, string>;
    content?: { text: string; language?: string };
    cells?: Array<{ kind: "input" | "output"; order: number; text?: string; language?: string }>;
    hitPolicy?: string;
    aggregation?: string;
    source: { artifact: string; element?: string };
  }>;
  relationships: Array<{
    identity: string;
    kind: "sequenceFlow" | "messageFlow" | "association" | "reference";
    source: string;
    target: string;
    order: number;
    condition?: { text?: string; language?: string };
  }>;
}

export interface CandidateSource {
  unitPath: string;
  commitSha: string;
  sourceSha256: string;
  start: number;
  length: number;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

export interface CandidateNode {
  kind: string;
  source: CandidateSource;
  children: CandidateNode[];
  operator?: string;
  value?: string;
  type?: string;
  symbol?: string;
  rawPredicate?: string;
  failure?: string;
}

export interface CandidateArtifact {
  schema: "ce.candidates.v1";
  recipe: "roslyn.scalar-guards.v1";
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  sourceSha256: string;
  coverage: { status: "full" | "partial"; scope: string; supportedMethods: number; unsupportedMethods: number };
  diagnostics: Array<{ code: string; message: string; source?: CandidateSource }>;
  candidates: Array<{
    id: string;
    kind: "process" | "decision";
    reviewStatus: "UNREVIEWED";
    executable: false;
    contentProfile: "candidate-content";
    source: CandidateSource;
    parameters: Array<{ name: string; symbol: string; type: string }>;
    inputsRead: string[];
    inputsWritten: [];
    body: CandidateNode;
  }>;
}

export type ModelEvidence = AuthoredModelEvidence | CandidateArtifact;

export interface ModelEvidenceView {
  schema: "ce.model-evidence-view.v1";
  id: string;
  evidence: ModelEvidence;
  snapshot: string;
  generation: number;
  selection: Record<string, string>;
}

export interface ModelSourceView {
  schema: "ce.model-source-view.v1";
  id: string;
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  sha256: string;
  format: "bpmn+xml" | "dmn+xml";
  xml: string;
  snapshot: string;
  generation: number;
  selection: Record<string, string>;
}

export function sameScope(a: ModelScope, b: ModelScope): boolean {
  return a.snapshot === b.snapshot && a.generation === b.generation &&
    JSON.stringify(Object.entries(a.selection).sort()) === JSON.stringify(Object.entries(b.selection).sort());
}

export function isModelScope(value: unknown): value is ModelScope {
  if (!value || typeof value !== "object") return false;
  const scope = value as ModelScope;
  return typeof scope.snapshot === "string" && Number.isSafeInteger(scope.generation) &&
    !!scope.selection && typeof scope.selection === "object" && !Array.isArray(scope.selection) &&
    Object.entries(scope.selection).every(([key, sha]) => typeof key === "string" && typeof sha === "string");
}
