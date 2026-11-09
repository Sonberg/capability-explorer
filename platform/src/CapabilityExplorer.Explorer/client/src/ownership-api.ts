import { ApiError, request } from "./api";

export type OwnershipAudience = "engineering" | "architecture";
export type OwnershipSystem = "github" | "leanix" | "servicenow" | "backstage";
export interface OwnershipParty { kind: string; id: string }
export interface OwnershipRole { system: OwnershipSystem; role: string; party?: OwnershipParty | null; unruled?: boolean }
export interface OwnershipEvidence {
  system: OwnershipSystem;
  sourceRecordId: string;
  link?: string;
  snapshotId: string;
  digest: string;
  capturedAt: string;
  freshness: string;
  coverage: string;
}
export interface OwnershipFinding {
  findingId: string;
  repositoryId: string;
  checkKey: { type: string; system?: OwnershipSystem; ruleId?: string };
  kind: string;
  status: "observed" | "aligned" | "action-needed" | "excepted" | "not-applicable" | "not-evaluable";
  evidence: OwnershipEvidence[];
  observedRoles: OwnershipRole[];
  expectedRoles: OwnershipRole[];
  fingerprint: string;
  reason: string;
  nextAction: string;
  verification: string;
  resolutionOwner: { partyRef: string } | null;
  triage: "assigned" | "needs-triage";
  exception?: { exceptionId: string; expiresAt: string };
}
export interface OwnershipReport {
  schema: "ce.ownership-alignment.v1";
  status: string;
  alignment: "aligned" | "action-needed" | "not-evaluable";
  audience: OwnershipAudience;
  inputVector: string;
  generatedAt: string;
  coverage: { systems: { system: OwnershipSystem; state: string; coverage?: string; snapshotId?: string; capturedAt?: string }[] };
  findings: OwnershipFinding[];
  groups: { key: string; findingIds: string[] }[];
  diagnostics: { code: string; message: string }[];
  reason?: string;
  nextAction?: string;
}

const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);
const strings = (value: Record<string, unknown>, keys: string[]) => keys.every(key => typeof value[key] === "string");
const system = (value: unknown): value is OwnershipSystem => ["github", "leanix", "servicenow", "backstage"].some(item => item === value);
const role = (value: unknown): value is OwnershipRole =>
  object(value) && system(value.system) && typeof value.role === "string"
  && (value.party == null || object(value.party) && strings(value.party, ["kind", "id"]))
  && (value.unruled === undefined || typeof value.unruled === "boolean");
const evidence = (value: unknown): value is OwnershipEvidence =>
  object(value) && system(value.system)
  && strings(value, ["sourceRecordId", "snapshotId", "digest", "capturedAt", "freshness", "coverage"])
  && (value.link === undefined || typeof value.link === "string");
const finding = (value: unknown): value is OwnershipFinding =>
  object(value) && strings(value, ["findingId", "repositoryId", "kind", "fingerprint", "reason", "nextAction", "verification"])
  && ["observed", "aligned", "action-needed", "excepted", "not-applicable", "not-evaluable"].some(status => status === value.status)
  && object(value.checkKey) && typeof value.checkKey.type === "string"
  && Array.isArray(value.evidence) && value.evidence.every(evidence)
  && Array.isArray(value.observedRoles) && value.observedRoles.every(role)
  && Array.isArray(value.expectedRoles) && value.expectedRoles.every(role)
  && (value.resolutionOwner === null || object(value.resolutionOwner) && typeof value.resolutionOwner.partyRef === "string")
  && (value.triage === "assigned" || value.triage === "needs-triage")
  && (value.exception === undefined || object(value.exception) && strings(value.exception, ["exceptionId", "expiresAt"]));

export function isOwnershipReport(value: unknown): value is OwnershipReport {
  return object(value) && value.schema === "ce.ownership-alignment.v1"
    && (value.audience === "engineering" || value.audience === "architecture")
    && ["aligned", "action-needed", "not-evaluable"].some(alignment => alignment === value.alignment)
    && strings(value, ["status", "inputVector", "generatedAt"])
    && object(value.coverage) && Array.isArray(value.coverage.systems)
    && value.coverage.systems.every(item => object(item) && system(item.system) && typeof item.state === "string"
      && (item.coverage === undefined || typeof item.coverage === "string")
      && (item.snapshotId === undefined || typeof item.snapshotId === "string")
      && (item.capturedAt === undefined || typeof item.capturedAt === "string"))
    && Array.isArray(value.findings) && value.findings.every(finding)
    && Array.isArray(value.groups) && value.groups.every(item => object(item) && typeof item.key === "string"
      && Array.isArray(item.findingIds) && item.findingIds.every(id => typeof id === "string"))
    && Array.isArray(value.diagnostics) && value.diagnostics.every(item => object(item) && strings(item, ["code", "message"]))
    && (value.reason === undefined || typeof value.reason === "string")
    && (value.nextAction === undefined || typeof value.nextAction === "string");
}

export const NO_ALIGNMENT = "No alignment is established.";

function unavailable(error: unknown): Error {
  if (error instanceof SyntaxError) return new Error(`The ownership report response could not be read. ${NO_ALIGNMENT}`);
  if (error instanceof ApiError) return new Error(`The ownership report request failed (HTTP ${error.status}): ${error.message.slice(0, 500).replace(/\.$/, "")}. ${NO_ALIGNMENT}`);
  return new Error(`The ownership report could not be loaded. ${NO_ALIGNMENT}`);
}

export async function ownershipReport(audience: OwnershipAudience, signal: AbortSignal, repository = ""): Promise<OwnershipReport> {
  const query = new URLSearchParams({ audience });
  if (repository) query.set("repository", repository);
  let value: unknown;
  try { value = await request<unknown>(`/api/ownership-alignment?${query}`, { signal }); }
  catch (error) {
    if (signal.aborted) throw error;
    if (!(error instanceof ApiError) || !isOwnershipReport(error.body)) throw unavailable(error);
    value = error.body;
  }
  if (!isOwnershipReport(value) || value.audience !== audience) throw new Error(`The ownership report response is invalid. ${NO_ALIGNMENT}`);
  return value;
}

export function ownershipEvidenceLink(link?: string): string | undefined {
  if (!link) return undefined;
  try {
    const url = new URL(link);
    if (["http:", "https:"].includes(url.protocol) && !url.username && !url.password) return url.href;
  } catch (error) {
    if (!(error instanceof TypeError)) throw error;
  }
  return undefined;
}
