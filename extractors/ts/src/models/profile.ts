export type ModelContentProfile = "structural" | "model-content";

export interface ModelExtractionOptions {
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  contentProfile?: ModelContentProfile;
}

export interface ModelElement {
  identity: string;
  kind: string;
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
  source: { artifact: string; element: string };
}

export interface ModelRelationship {
  identity: string;
  kind: "sequenceFlow" | "messageFlow" | "association" | "reference";
  source: string;
  target: string;
  order: number;
  condition?: { text?: string; language?: string };
}

export interface ModelDiagnostic {
  code: string;
  message: string;
  disposition: "partial" | "rejected";
  source?: { artifact: string; element?: string };
}

export interface ModelEvidence {
  schema: "ce.models.v1";
  repositoryId: string;
  commitSha: string;
  unitPath: string;
  sourceSha256: string;
  contentProfile: ModelContentProfile;
  coverage: { status: "full" | "partial"; supported: string[]; limitations: string[] };
  diagnostics: ModelDiagnostic[];
  elements: ModelElement[];
  relationships: ModelRelationship[];
}
