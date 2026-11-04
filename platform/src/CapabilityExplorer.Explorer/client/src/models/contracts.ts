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

const isRecord = (value: unknown): value is Record<string, unknown> =>
  !!value && typeof value === "object" && !Array.isArray(value);
const isString = (value: unknown): value is string => typeof value === "string";
const isInteger = (value: unknown): value is number => Number.isSafeInteger(value);
const isStringMap = (value: unknown): value is Record<string, string> =>
  isRecord(value) && Object.values(value).every(isString);
const isLocator = (value: unknown): boolean =>
  isRecord(value) && isString(value.artifact) && (value.element === undefined || isString(value.element));
const isDiagnostic = (value: unknown): boolean =>
  isRecord(value) && isString(value.code) && isString(value.message) &&
  (value.disposition === undefined || value.disposition === "partial" || value.disposition === "rejected") &&
  (value.source === undefined || isLocator(value.source));
const isCandidateSource = (value: unknown): value is CandidateSource =>
  isRecord(value) && isString(value.unitPath) && isString(value.commitSha) &&
  /^[a-f\d]{64}$/i.test(String(value.sourceSha256)) &&
  [value.start, value.length, value.startLine, value.startColumn, value.endLine, value.endColumn].every(isInteger) &&
  Number(value.start) >= 0 && Number(value.length) >= 0 && Number(value.startLine) >= 1 &&
  Number(value.startColumn) >= 1 && Number(value.endLine) >= 1 && Number(value.endColumn) >= 1;

function isCandidateNode(value: unknown, depth = 0, budget = { remaining: 5_000 }): value is CandidateNode {
  if (!isRecord(value) || depth > 64 || budget.remaining-- <= 0 || !isString(value.kind) || !isCandidateSource(value.source) ||
      !Array.isArray(value.children) || value.children.length > 1_000) return false;
  for (const key of ["operator", "value", "type", "symbol", "rawPredicate", "failure"] as const) {
    if (value[key] !== undefined && !isString(value[key])) return false;
  }
  return value.children.every((child) => isCandidateNode(child, depth + 1, budget));
}

export function isAuthoredModelEvidence(value: unknown): value is AuthoredModelEvidence {
  if (!isRecord(value) || value.schema !== "ce.models.v1" ||
      !["repositoryId", "commitSha", "unitPath"].every((key) => isString(value[key])) ||
      !/^[a-f\d]{64}$/i.test(String(value.sourceSha256)) ||
      (value.contentProfile !== "structural" && value.contentProfile !== "model-content") ||
      !isRecord(value.coverage) || !["full", "partial"].includes(String(value.coverage.status)) ||
      !Array.isArray(value.coverage.supported) || value.coverage.supported.length > 500 || !value.coverage.supported.every(isString) ||
      !Array.isArray(value.coverage.limitations) || value.coverage.limitations.length > 500 || !value.coverage.limitations.every(isString) ||
      !Array.isArray(value.diagnostics) || value.diagnostics.length > 5_000 || !value.diagnostics.every(isDiagnostic) ||
      !Array.isArray(value.elements) || value.elements.length > 500 ||
      !Array.isArray(value.relationships) || value.relationships.length > 1000) return false;
  for (const element of value.elements) {
    if (!isRecord(element) || !isString(element.identity) ||
        !["bpmn", "dmn", "opaqueExtension", "unsupported"].includes(String(element.kind)) ||
        !["namespace", "id", "type"].every((key) => isString(element[key])) || !isInteger(element.order) ||
        (element.parent !== undefined && !isString(element.parent)) || !isStringMap(element.attributes) ||
        !isLocator(element.source)) return false;
    if (element.content !== undefined && (!isRecord(element.content) || !isString(element.content.text) ||
        (element.content.language !== undefined && !isString(element.content.language)))) return false;
    if (element.cells !== undefined && (!Array.isArray(element.cells) || element.cells.length > 500 ||
        !element.cells.every((cell) => isRecord(cell) && (cell.kind === "input" || cell.kind === "output") &&
          isInteger(cell.order) && (cell.text === undefined || isString(cell.text)) &&
          (cell.language === undefined || isString(cell.language))))) return false;
    for (const key of ["hitPolicy", "aggregation"] as const) {
      if (element[key] !== undefined && !isString(element[key])) return false;
    }
  }
  return value.relationships.every((relationship) => isRecord(relationship) &&
    isString(relationship.identity) &&
    ["sequenceFlow", "messageFlow", "association", "reference"].includes(String(relationship.kind)) &&
    isString(relationship.source) && isString(relationship.target) && isInteger(relationship.order) &&
    (relationship.condition === undefined || (isRecord(relationship.condition) &&
      (relationship.condition.text === undefined || isString(relationship.condition.text)) &&
      (relationship.condition.language === undefined || isString(relationship.condition.language)))));
}

export function isCandidateArtifact(value: unknown): value is CandidateArtifact {
  if (!isRecord(value) || value.schema !== "ce.candidates.v1" || value.recipe !== "roslyn.scalar-guards.v1" ||
      !["repositoryId", "commitSha", "unitPath"].every((key) => isString(value[key])) ||
      !/^[a-f\d]{64}$/i.test(String(value.sourceSha256)) ||
      !isRecord(value.coverage) || !["full", "partial"].includes(String(value.coverage.status)) ||
      !isString(value.coverage.scope) || !isInteger(value.coverage.supportedMethods) ||
      !isInteger(value.coverage.unsupportedMethods) ||
      !Array.isArray(value.diagnostics) || !value.diagnostics.every((diagnostic) =>
        isRecord(diagnostic) && isString(diagnostic.code) && isString(diagnostic.message) &&
        (diagnostic.source === undefined || isCandidateSource(diagnostic.source))) ||
      !Array.isArray(value.candidates) || value.candidates.length > 500) return false;
  return value.candidates.every((candidate) => isRecord(candidate) && isString(candidate.id) &&
    (candidate.kind === "process" || candidate.kind === "decision") && candidate.reviewStatus === "UNREVIEWED" &&
    candidate.executable === false && candidate.contentProfile === "candidate-content" &&
    isCandidateSource(candidate.source) && Array.isArray(candidate.parameters) &&
    candidate.parameters.every((parameter) => isRecord(parameter) &&
      ["name", "symbol", "type"].every((key) => isString(parameter[key]))) &&
    Array.isArray(candidate.inputsRead) && candidate.inputsRead.every(isString) &&
    Array.isArray(candidate.inputsWritten) && candidate.inputsWritten.length === 0 &&
    isCandidateNode(candidate.body));
}

export function isModelEvidence(value: unknown): value is ModelEvidence {
  return isAuthoredModelEvidence(value) || isCandidateArtifact(value);
}

export function isModelEvidenceView(value: unknown): value is ModelEvidenceView {
  return isRecord(value) && value.schema === "ce.model-evidence-view.v1" && isString(value.id) &&
    isModelScope(value) && isModelEvidence(value.evidence);
}

export function isModelSourceView(value: unknown): value is ModelSourceView {
  return isRecord(value) && value.schema === "ce.model-source-view.v1" &&
    ["id", "repositoryId", "commitSha", "unitPath", "xml"].every((key) => isString(value[key])) &&
    /^[a-f\d]{64}$/i.test(String(value.sha256)) &&
    (value.format === "bpmn+xml" || value.format === "dmn+xml") && isModelScope(value);
}

export function sameScope(a: ModelScope, b: ModelScope): boolean {
  return a.snapshot === b.snapshot && a.generation === b.generation &&
    JSON.stringify(Object.entries(a.selection).sort()) === JSON.stringify(Object.entries(b.selection).sort());
}

export function isModelScope(value: unknown): value is ModelScope {
  if (!value || typeof value !== "object") return false;
  const scope = value as ModelScope;
  return typeof scope.snapshot === "string" && scope.snapshot.length > 0 && scope.snapshot.length <= 64 &&
    Number.isSafeInteger(scope.generation) && scope.generation >= 0 &&
    !!scope.selection && typeof scope.selection === "object" && !Array.isArray(scope.selection) &&
    Object.keys(scope.selection).length <= 100 &&
    Object.entries(scope.selection).every(([key, sha]) => key.length > 0 && key.length <= 200 &&
      typeof sha === "string" && /^[a-f\d]{40}$/i.test(sha));
}
