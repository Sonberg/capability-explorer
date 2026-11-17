export interface Coverage { status: string; supported: string[]; limitations: string[] }
export interface Diagnostic {
  code: string; message: string; disposition: string;
  source?: { path?: string; element?: string; ref?: string };
}
export interface Element {
  identity: string; kind: string; namespace: string; id: string; type: string; order: number;
  attributes: Record<string, string>; source: { path: string; element?: string };
  parent?: string; content?: { text: string; language?: string };
}
export interface Relationship {
  identity: string; kind: string; source: string; target: string; order: number;
  condition?: { text?: string; language?: string };
}
export interface DocumentRelationship extends Relationship { kind: "messageFlow"; elementIdentity: string }
export interface LandscapeDocument {
  path: string; elements: Element[]; relationships: DocumentRelationship[];
}
export interface Process {
  processIdentity: string; path: string; byteLength: number; sha256: string;
  processId?: string; name?: string; proposalStatus?: string;
  contextTags: string[]; phaseTags: string[]; coverage: Coverage; diagnostics: Diagnostic[];
  elements: Element[]; relationships: Relationship[];
}
export interface Endpoint {
  processId: string; resolved: boolean; elementId?: string;
  processIdentity?: string; elementIdentity?: string;
}
export interface Event { eventId: string; source: Endpoint; target: Endpoint; label?: string }
export interface Stage extends Endpoint { stage: string; order: number }
export interface Lifecycle { objectId: string; label?: string; stages: Stage[] }
export interface Step extends Endpoint { order: number }
export interface Phase { phaseId: string; label?: string; order: number; steps: Step[] }
export interface Overlay { tag: string; processIdentities: string[] }
export const categories = [
  "catalogue", "elements", "links", "events", "lifecycle", "journey", "contexts", "procFile", "dmn", "phases",
] as const;
export type Category = typeof categories[number];
export interface Snapshot {
  schema: string; snapshotId: string; manifestVersion: number; repositoryId: string;
  contentProfile: "structural" | "model-content";
  coverage: Coverage; diagnostics: Diagnostic[]; processes: Process[];
  searchIndex: { term: string; kind: string; processIdentity: string; elementIdentity?: string; deepLink: string }[];
  crossProcessEvents: Event[]; objectLifecycle: Lifecycle[]; phaseJourney: Phase[];
  overlays: { context: Overlay[]; phase: Overlay[] };
  sources: { path: string; byteLength: number; sha256: string }[];
  documents?: LandscapeDocument[];
  navigator?: { verified: boolean; bundle: { path: string; byteLength: number; sha256: string }; generated?: unknown }
    & Partial<Record<Category, unknown>>;
}
export interface ApiResponse {
  schema: string; status: string; snapshot?: Snapshot | null; reason?: string | null;
  identityVerification?: { status: string | number; reason: string } | null;
}
