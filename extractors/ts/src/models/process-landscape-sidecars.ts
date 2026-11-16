import { parseStrictJson } from "../collection/strictJson.ts";
import { decodeUtf8Strict } from "./process-landscape-manifest.ts";
import { ProcessLandscapeError, type ProposalStatus } from "./process-landscape-types.ts";

export interface ProcessMetadataEntry {
  title?: string;
  proposalStatus?: ProposalStatus;
  contextTags?: string[];
  phaseTags?: string[];
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseSidecarJson(bytes: Uint8Array, path: string): unknown {
  const text = decodeUtf8Strict(bytes, path);
  try {
    return parseStrictJson(text);
  } catch (error) {
    throw new ProcessLandscapeError("invalid-sidecar-json", `${path}: ${error instanceof Error ? error.message : "invalid JSON"}`);
  }
}

function requireString(value: unknown, field: string, path: string): string {
  if (typeof value !== "string" || !value) throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: ${field} must be a non-empty string`);
  return value;
}

function optionalString(value: unknown, field: string, path: string): string | undefined {
  if (value === undefined) return undefined;
  if (typeof value !== "string" || !value) throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: ${field} must be a non-empty string`);
  return value;
}

function requireOrder(value: unknown, field: string, path: string): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < 0) {
    throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: ${field} must be a non-negative integer`);
  }
  return value;
}

export function parseProcessMetadata(bytes: Uint8Array, path: string): Map<string, ProcessMetadataEntry> {
  const value = parseSidecarJson(bytes, path);
  if (!isPlainObject(value) || value.schema !== "ce.process-landscape-metadata.v1" || !isPlainObject(value.processes)) {
    throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: process-metadata sidecar is malformed`);
  }
  const result = new Map<string, ProcessMetadataEntry>();
  for (const [processId, raw] of Object.entries(value.processes)) {
    if (!isPlainObject(raw)) throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: metadata entry for ${processId} must be an object`);
    const proposalStatus = optionalString(raw.proposalStatus, "proposalStatus", path);
    if (proposalStatus !== undefined && !["draft", "proposed", "approved"].includes(proposalStatus)) {
      throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: proposalStatus must be draft, proposed, or approved`);
    }
    const contextTags = raw.contextTags;
    const phaseTags = raw.phaseTags;
    if (contextTags !== undefined && (!Array.isArray(contextTags) || !contextTags.every((tag) => typeof tag === "string" && tag))) {
      throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: contextTags must be an array of non-empty strings`);
    }
    if (phaseTags !== undefined && (!Array.isArray(phaseTags) || !phaseTags.every((tag) => typeof tag === "string" && tag))) {
      throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: phaseTags must be an array of non-empty strings`);
    }
    result.set(processId, {
      title: optionalString(raw.title, "title", path),
      proposalStatus: proposalStatus as ProposalStatus | undefined,
      contextTags: contextTags as string[] | undefined,
      phaseTags: phaseTags as string[] | undefined,
    });
  }
  return result;
}

export function parseCrossProcessEvents(bytes: Uint8Array, path: string): Array<{
  eventId: string;
  label?: string;
  source: { processId: string; elementId?: string };
  target: { processId: string; elementId?: string };
}> {
  const value = parseSidecarJson(bytes, path);
  if (!isPlainObject(value) || value.schema !== "ce.process-landscape-events.v1" || !Array.isArray(value.events)) {
    throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: cross-process-events sidecar is malformed`);
  }
  return value.events.map((raw) => {
    if (!isPlainObject(raw) || !isPlainObject(raw.source) || !isPlainObject(raw.target)) {
      throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: event entry is malformed`);
    }
    return {
      eventId: requireString(raw.eventId, "eventId", path),
      label: optionalString(raw.label, "label", path),
      source: { processId: requireString(raw.source.processId, "source.processId", path), elementId: optionalString(raw.source.elementId, "source.elementId", path) },
      target: { processId: requireString(raw.target.processId, "target.processId", path), elementId: optionalString(raw.target.elementId, "target.elementId", path) },
    };
  });
}

export function parseObjectLifecycle(bytes: Uint8Array, path: string): Array<{
  objectId: string;
  label?: string;
  stages: Array<{ stage: string; order: number; processId: string; elementId?: string }>;
}> {
  const value = parseSidecarJson(bytes, path);
  if (!isPlainObject(value) || value.schema !== "ce.process-landscape-lifecycle.v1" || !Array.isArray(value.objects)) {
    throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: object-lifecycle sidecar is malformed`);
  }
  return value.objects.map((raw) => {
    if (!isPlainObject(raw) || !Array.isArray(raw.stages)) throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: object entry is malformed`);
    return {
      objectId: requireString(raw.objectId, "objectId", path),
      label: optionalString(raw.label, "label", path),
      stages: raw.stages.map((stage) => {
        if (!isPlainObject(stage)) throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: stage entry is malformed`);
        return {
          stage: requireString(stage.stage, "stage", path),
          order: requireOrder(stage.order, "order", path),
          processId: requireString(stage.processId, "processId", path),
          elementId: optionalString(stage.elementId, "elementId", path),
        };
      }),
    };
  });
}

export function parsePhaseJourney(bytes: Uint8Array, path: string): Array<{
  phaseId: string;
  label?: string;
  order: number;
  steps: Array<{ order: number; processId: string; elementId?: string }>;
}> {
  const value = parseSidecarJson(bytes, path);
  if (!isPlainObject(value) || value.schema !== "ce.process-landscape-journey.v1" || !Array.isArray(value.phases)) {
    throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: phase-journey sidecar is malformed`);
  }
  return value.phases.map((raw) => {
    if (!isPlainObject(raw) || !Array.isArray(raw.steps)) throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: phase entry is malformed`);
    return {
      phaseId: requireString(raw.phaseId, "phaseId", path),
      label: optionalString(raw.label, "label", path),
      order: requireOrder(raw.order, "order", path),
      steps: raw.steps.map((step) => {
        if (!isPlainObject(step)) throw new ProcessLandscapeError("invalid-sidecar-shape", `${path}: step entry is malformed`);
        return {
          order: requireOrder(step.order, "order", path),
          processId: requireString(step.processId, "processId", path),
          elementId: optionalString(step.elementId, "elementId", path),
        };
      }),
    };
  });
}
