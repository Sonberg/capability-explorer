// Types mirror the explorer's JSON (platform/src/CapabilityExplorer.Explorer). Only what the UI reads is typed.

export type Json = string | number | boolean | null | Json[] | { [k: string]: Json };
export type Props = Record<string, Json>;

export interface Revision {
  repositoryId: string;
  commitSha: string;
  tags: string[];
  subject?: string;
  commitTime?: string;
  label: string;
}

export interface CapabilitySummary {
  id: string;
  name: string;
  repositories: string[];
}

export interface Meta {
  /** Increases whenever the server reloaded artifacts or capabilities from disk. */
  generation: number;
  capabilities: CapabilitySummary[];
  githubAuth: { configured: boolean; source: string };
  interpretation: { enabled: boolean; chat: boolean; provider: string; model?: string };
  /** Vector search: needs the database (CE_DATABASE) and an embedding model (make ollama, or Settings). */
  similarity: { database: boolean; embeddings?: string };
  revisions: Revision[];
  defaultView: Record<string, string>;
  artifacts: number;
  problems: string[];
  hasSource: boolean;
  hasEvaluation: boolean;
  nodeKinds: string[];
}

export interface Evidence {
  id: string;
  repositoryId: string;
  commitSha: string;
  path: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
}

export interface NodeDto {
  key: string;
  kind: string;
  name: string;
  signature?: string;
  generated: boolean;
  props?: Props;
  repositoryId: string;
  commitSha: string;
  unit: string;
  evidence: Evidence[];
}

export interface SymbolRef {
  key: string;
  name: string;
  kind?: string;
  declared: boolean;
  repositoryId?: string;
}

export interface Edge {
  relation: string;
  from: SymbolRef;
  to?: SymbolRef;
  derivation: string;
  resolution: "resolved" | "static-target" | "candidate" | "unresolved";
  unresolvedReason?: string;
  rule?: string;
  props?: Props;
  repositoryId: string;
  commitSha: string;
  evidence: Evidence[];
}

export interface Attribute {
  status: "known" | "partial" | "unknown";
  value?: Json;
  detail?: Props;
}

export type Expr = { op: string } & Record<string, Json>;

export interface Calculation {
  key: string;
  name: string;
  repositoryId: string;
  commitSha: string;
  unit: string;
  language: string;
  output: { type: string };
  inputs: { ref: string; type: string; origin?: string }[];
  expr: Expr;
  attributes: Record<string, Attribute>;
  irHash?: string;
  includes: string[];
  evidence: Evidence[];
  attributeEvidence: Record<string, Evidence[]>;
}

export interface HistoryEntry {
  commitSha: string;
  label: string;
  subject?: string;
  state: "present" | "absent" | "notObserved" | "notAnalysed";
}

export interface SymbolDetail {
  node: NodeDto;
  outgoing: Edge[];
  incoming: Edge[];
  calculation?: Calculation;
  history: HistoryEntry[];
}

export interface SearchHit {
  key: string;
  name: string;
  kind: string;
  repositoryId: string;
  unit: string;
  generated: boolean;
  hasCalculation: boolean;
}

export interface SimilarHit {
  key: string;
  name: string;
  kind: string;
  repositoryId: string;
  unit: string;
  hasCalculation: boolean;
  /** Cosine similarity, 1 = identical text. */
  score: number;
}

export interface ModelSettings {
  source: "saved" | "configuration" | "none";
  settingsPath: string;
  provider: string;
  endpoint?: string;
  chatModel?: string;
  embeddingModel?: string;
  /** Where `make ollama` serves, as the explorer reaches it. */
  ollamaEndpoint: string;
  hasApiKey: boolean;
  apiKeySource: string;
  database: boolean;
  problem?: string;
}

export interface Integrations {
  services: { service: NodeDto; endpoints: { endpoint: NodeDto; handlers: SymbolRef[] }[] }[];
  /** group: who it talks to (Fortnox, Klarna, …); base: the host, configuration name or client it goes through. */
  http: { request: Edge; candidates: SymbolRef[]; match: "bound" | "binding" | "broken" | "route" | "none" | "dynamic"; note: string; base: string; group: string }[];
  channels: ResourceGroup[];
  data: ResourceGroup[];
  marks: Mark[];
}

/** key: "group|http|Klarna" for a whole integration, otherwise an endpoint, request, channel or table key. */
export interface Mark {
  key: string;
  status: "deprecated" | "obsolete";
  note: string;
  markedAt: string;
  /** Set when the key is gone from the newest analysed revision: the first analysed commit without it. */
  removal?: { repositoryId: string; lastSeen: string; removedIn: string; subject?: string };
}

export interface ResourceGroup {
  name: string;
  kind: string;
  note?: string;
  keys: { key: string; bound: boolean; edges: Edge[]; declaration?: NodeDto }[];
}

export interface AttributeValue {
  status: string;
  value?: string;
  detail?: Props;
}

export interface Comparison {
  left: { key: string; name: string; repositoryId: string; outputType: string };
  right: { key: string; name: string; repositoryId: string; outputType: string };
  sameExpression: boolean;
  differences: { id: string; attribute: string; kind: "ValueDiffers" | "DetailDiffers" | "KnownOnOneSide"; left?: AttributeValue; right?: AttributeValue }[];
  agreements: string[];
  unknownOnBoth: string[];
}

export interface Proposal {
  status: "proposed" | "rejected";
  derivation: string;
  reviewStatus: string;
  model: string;
  promptVersion: string;
  inputHash: string;
  summary?: string;
  points: { differenceId: string; text: string }[];
  problems: string[];
  cached: boolean;
}

export interface ChangeReport {
  repositoryId: string;
  from: Revision;
  to: Revision;
  addedUnits: string[];
  removedUnits: string[];
  units: {
    unit: string;
    unitPath: string;
    fromStatus: string;
    toStatus: string;
    added: NodeDto[];
    removed: NodeDto[];
    notObserved: NodeDto[];
    continuity: { from: NodeDto; to: NodeDto; reasons: string[]; ambiguous: boolean }[];
    calculations: { fromKey: string; toKey: string; name: string; logicChanged: boolean; attributes: { name: string; from?: string; to?: string }[]; possibleConsumers: SymbolRef[] }[];
    edges: { relation: string; from: string; to?: string; resolution: string; change: string }[];
    inputUnchanged: boolean;
  }[];
}

export interface ArtifactSummary {
  id: number;
  label: string;
  repositoryId: string;
  commitSha: string;
  revision: string;
  unit: string;
  unitPath: string;
  language: string;
  extractor: string;
  status: "full" | "partial";
  counts: { node: number; edge: number; calculation: number };
  coverage: { filesTotal: number; filesAnalyzed: number; incompletePaths?: string[] };
  diagnostics: { severity: string; code: string; message: string }[];
}

export interface RepositoryStatus {
  id: string;
  location: string;
  kind: "github" | "local";
  ref: string;
  available: boolean;
  lockedSha?: string;
  lockedSubject?: string;
  lockedAt?: string;
  error?: string;
  analysed: boolean;
  analysedRevisions: string[];
}

/** An "Analyze code" run, executed on the host by scripts/analyze-worker. */
export interface AnalysisJob {
  state: "idle" | "queued" | "running" | "stopping" | "done" | "failed" | "stopped";
  since?: string;
  log?: string;
}

export interface RebuildResult {
  imported: number;
  unchanged: number;
  failed: string[];
  embedded: number;
  model: string;
}

export interface CapabilityStatus {
  id: string;
  name: string;
  description?: string;
  checkedAt?: string;
  repositories: RepositoryStatus[];
  analysed: number;
}

export interface Ratio {
  hit: number;
  total: number;
  value: number;
}

export interface EvaluationReport {
  passed: boolean;
  thresholdFailures: string[];
  totals: Record<string, Ratio>;
  artifacts: {
    label: string;
    status: string;
    actualExtractor?: string;
    metrics: Record<string, Ratio>;
    findings: { kind: string; subject: string; detail: string }[];
    problems: string[];
  }[];
}

export class ApiError extends Error {
  constructor(message: string, readonly status: number, readonly body: unknown) {
    super(message);
  }
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  if (!headers.has('accept')) headers.set('accept', 'application/json');
  if (init?.body && !headers.has('content-type')) headers.set('content-type', 'application/json');
  const res = await fetch(path, { ...init, headers });
  const text = await res.text();
  const body: unknown = text ? JSON.parse(text) : undefined;
  if (!res.ok) {
    const message = (body as { error?: string } | undefined)?.error ?? `${res.status} ${res.statusText}`;
    throw new ApiError(message, res.status, body);
  }
  return body as T;
}

export const shortSha = (sha?: string) => (sha ? sha.slice(0, 12) : "");

/** A source occurrence in an exact-name candidate, not a reviewed concept link. */
export interface GlossaryOccurrence {
  id: string;
  key: string;
  name: string;
  kind: string;
  role: string;
  repositoryId: string;
  commitSha: string;
  unit: string;
  unitPath: string;
  type: string | null;
  capabilities: string[];
  evidence: Evidence[];
}

export interface GlossaryComparison {
  leftId: string;
  rightId: string;
  sameExpression: boolean | null;
  differences: Comparison["differences"];
}

export interface GlossaryCandidate {
  term: string;
  occurrences: GlossaryOccurrence[];
  repositories: string[];
  capabilities: string[];
  typeDiffers: boolean;
  derivationDiffers: boolean;
  expressionDiffers: boolean;
  comparisonsComplete: boolean;
  possibleComparisons: number;
  comparisons: GlossaryComparison[];
}

/** One feature of a concept's score (MADR-0005). Zero points is still a reported fact. */
export interface GlossaryReason {
  feature: string;
  points: number;
  detail: string | null;
}

export interface GlossaryMember {
  id: string;
  key: string;
  name: string;
  kind: string;
  fold: "declaration" | "representation" | "variant";
  repositoryId: string;
  commitSha: string;
  unit: string;
  unitPath: string;
  evidence: Evidence[];
}

/** A property, field or column nested under the concept that declares it. */
export interface GlossaryField {
  label: string;
  name: string;
  generic: boolean;
  declarations: number;
  evidence: Evidence[];
}

/** Declarations with an equal stem, ranked by an explainable score. Not a reviewed domain model. */
export interface GlossaryConcept {
  key: string;
  term: string;
  tier: "core" | "supporting" | "plumbing";
  score: number;
  why: GlossaryReason[];
  members: GlossaryMember[];
  variants: GlossaryMember[];
  attributes: GlossaryField[];
  repositories: string[];
  capabilities: string[];
}

export interface GlossaryConceptView {
  weightsVersion: number;
  concepts: GlossaryConcept[];
  metrics: GlossaryCandidate[];
  excludedTestDeclarations: number;
  partialUnits: string[];
  limits: string[];
}
