export type ProcessLandscapeContentProfile = "structural" | "model-content";
export type ProposalStatus = "draft" | "proposed" | "approved";

export interface LandscapeSource {
  path: string;
  element?: string;
}

export interface LandscapeElement {
  identity: string;
  kind: "bpmn" | "opaqueExtension" | "unsupported";
  namespace: string;
  id: string;
  type: string;
  order: number;
  parent?: string;
  attributes: Record<string, string>;
  content?: { text: string; language?: string };
  source: LandscapeSource;
}

export interface LandscapeRelationship {
  identity: string;
  kind: "sequenceFlow" | "messageFlow" | "association" | "reference";
  source: string;
  target: string;
  order: number;
  condition?: { text?: string; language?: string };
}

export interface LandscapeDocumentRelationship extends LandscapeRelationship {
  kind: "messageFlow";
  elementIdentity: string;
}

export interface LandscapeDocument {
  path: string;
  elements: LandscapeElement[];
  relationships: LandscapeDocumentRelationship[];
}

export interface LandscapeDiagnostic {
  code: string;
  message: string;
  disposition: "partial" | "unresolved";
  source?: { path?: string; element?: string; ref?: string };
}

export interface LandscapeCoverage {
  status: "full" | "partial";
  supported: string[];
  limitations: string[];
}

export interface ProcessEntry {
  processIdentity: string;
  path: string;
  byteLength: number;
  sha256: string;
  processId?: string;
  name?: string;
  proposalStatus?: ProposalStatus;
  contextTags: string[];
  phaseTags: string[];
  coverage: LandscapeCoverage;
  diagnostics: LandscapeDiagnostic[];
  elements: LandscapeElement[];
  relationships: LandscapeRelationship[];
}

/**
 * Raw admitted-file bytes, stored at most once per unique admitted path/sha256 pair (even when a
 * single BPMN file declares multiple processes and therefore backs multiple ProcessEntry records).
 * `sourceBytesBase64` is populated only under the "model-content" contentProfile.
 */
export interface LandscapeSourceRecord {
  path: string;
  byteLength: number;
  sha256: string;
  sourceBytesBase64?: string;
}

export type NavigatorCategoryName =
  | "catalogue" | "elements" | "links" | "events" | "lifecycle" | "journey" | "contexts"
  | "procFile" | "dmn" | "phases";

export interface NavigatorBundleIdentity {
  path: string;
  byteLength: number;
  sha256: string;
}

export interface ProcessLandscapeNavigator {
  bundle: NavigatorBundleIdentity;
  verified: boolean;
  catalogue?: unknown;
  elements?: unknown;
  links?: unknown;
  events?: unknown;
  lifecycle?: unknown;
  journey?: unknown;
  contexts?: unknown;
  /** Process-id to originating-file mapping as published by the bundle; only trustworthy for
   * resolution purposes once `verified` is true. */
  procFile?: unknown;
  dmn?: unknown;
  phases?: unknown;
  /** Bundle provenance/generation marker (e.g. a build timestamp); not a data collection, so it is
   * copied through verbatim rather than shape-validated as a container. */
  generated?: unknown;
}

export interface SearchEntry {
  term: string;
  kind: "process" | "element";
  processIdentity: string;
  elementIdentity?: string;
  deepLink: string;
}

export interface CrossProcessEventEndpoint {
  processId: string;
  elementId?: string;
  processIdentity?: string;
  elementIdentity?: string;
  resolved: boolean;
}

export interface CrossProcessEvent {
  eventId: string;
  label?: string;
  source: CrossProcessEventEndpoint;
  target: CrossProcessEventEndpoint;
}

export interface ObjectLifecycleStage {
  stage: string;
  order: number;
  processId: string;
  elementId?: string;
  processIdentity?: string;
  elementIdentity?: string;
  resolved: boolean;
}

export interface ObjectLifecycle {
  objectId: string;
  label?: string;
  stages: ObjectLifecycleStage[];
}

export interface PhaseJourneyStep {
  order: number;
  processId: string;
  elementId?: string;
  processIdentity?: string;
  elementIdentity?: string;
  resolved: boolean;
}

export interface PhaseJourneyPhase {
  phaseId: string;
  label?: string;
  order: number;
  steps: PhaseJourneyStep[];
}

export interface ProcessLandscapeOverlays {
  context: Array<{ tag: string; processIdentities: string[] }>;
  phase: Array<{ tag: string; processIdentities: string[] }>;
}

export interface ProcessLandscapeSnapshot {
  schema: "ce.process-landscape.v1";
  snapshotId: string;
  manifestVersion: 1;
  repositoryId: string;
  contentProfile: ProcessLandscapeContentProfile;
  coverage: LandscapeCoverage;
  diagnostics: LandscapeDiagnostic[];
  processes: ProcessEntry[];
  sources: LandscapeSourceRecord[];
  searchIndex: SearchEntry[];
  crossProcessEvents: CrossProcessEvent[];
  objectLifecycle: ObjectLifecycle[];
  phaseJourney: PhaseJourneyPhase[];
  overlays: ProcessLandscapeOverlays;
  navigator?: ProcessLandscapeNavigator;
  documents?: LandscapeDocument[];
}

export class ProcessLandscapeError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "ProcessLandscapeError";
    this.code = code;
  }
}
