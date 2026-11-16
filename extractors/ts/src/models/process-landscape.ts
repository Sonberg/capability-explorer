import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { Ajv2020, type ValidateFunction } from "ajv/dist/2020.js";
import addFormatsModule from "ajv-formats";
import { MAX_SOURCE_BYTES } from "./source.ts";
import { extractBpmnDiagram, type PendingCalledElement } from "./process-landscape-xml.ts";
import { parseNavigatorBundle } from "./process-landscape-navigator.ts";
import {
  parseCrossProcessEvents, parseObjectLifecycle, parsePhaseJourney, parseProcessMetadata,
  type ProcessMetadataEntry,
} from "./process-landscape-sidecars.ts";
import {
  decodeUtf8Strict, MANIFEST_LIMITS, readManifestFile, sha256Hex, validateManifest,
  type ManifestRole, type ProcessLandscapeManifest,
} from "./process-landscape-manifest.ts";
import {
  ProcessLandscapeError,
  type CrossProcessEvent, type LandscapeDiagnostic, type LandscapeSourceRecord, type ObjectLifecycle, type PhaseJourneyPhase,
  type ProcessEntry, type ProcessLandscapeContentProfile, type ProcessLandscapeOverlays, type ProcessLandscapeSnapshot,
  type ProposalStatus, type SearchEntry,
} from "./process-landscape-types.ts";

const SNAPSHOT_SCHEMA = "ce.process-landscape.v1";
const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as (ajv: Ajv2020) => Ajv2020;
let compiledSchema: ValidateFunction | undefined;

function snapshotValidator(): ValidateFunction {
  if (!compiledSchema) {
    const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false, allowUnionTypes: true });
    addFormats(ajv);
    const schemaUrl = new URL("../../../../schema/models/process-landscape.v1.schema.json", import.meta.url);
    const schema = JSON.parse(readFileSync(schemaUrl, "utf8")) as Record<string, unknown>;
    compiledSchema = ajv.compile(schema);
  }
  return compiledSchema;
}

/** Deterministic, locale-independent ordinal string comparison (UTF-16 code unit order). */
function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

export interface ProcessLandscapeOptions {
  contentProfile?: ProcessLandscapeContentProfile;
}

interface FileRecord { path: string; byteLength: number; sha256: string }

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Builds a deterministic, content-addressed process-landscape snapshot from an explicit manifest of
 * repository-relative paths and roles. The manifest and every referenced file are admitted under strict
 * bounds (paths, symlinks, duplicates, extensions, UTF-8/XML validity, and size) before any output is
 * published. No commitSha is ever read, invented, or serialized; identity is bound to the manifest's
 * repositoryId and the sorted set of admitted paths, byte lengths, and raw-byte SHA-256 values.
 */
export async function buildProcessLandscapeSnapshot(
  baseDir: string,
  manifestInput: unknown,
  options: ProcessLandscapeOptions = {},
): Promise<ProcessLandscapeSnapshot> {
  const manifest: ProcessLandscapeManifest = validateManifest(manifestInput);
  const contentProfile: ProcessLandscapeContentProfile = options.contentProfile ?? "structural";
  if (contentProfile !== "structural" && contentProfile !== "model-content") {
    throw new ProcessLandscapeError("invalid-content-profile", "contentProfile must be structural or model-content");
  }

  const fileRecords: FileRecord[] = [];
  const diagnostics: LandscapeDiagnostic[] = [];
  const processes: ProcessEntry[] = [];
  const sources: LandscapeSourceRecord[] = [];
  const admittedBpmnSha256 = new Set<string>();
  const admittedBpmnNewlineNormalizedSha256 = new Set<string>();
  let navigatorBundle: { path: string; bytes: Buffer } | undefined;
  let metadataByProcessId: Map<string, ProcessMetadataEntry> = new Map();
  let crossProcessEventsRaw: ReturnType<typeof parseCrossProcessEvents> = [];
  let objectLifecycleRaw: ReturnType<typeof parseObjectLifecycle> = [];
  let phaseJourneyRaw: ReturnType<typeof parsePhaseJourney> = [];
  let totalBytes = 0;
  const processEntryByPathAndIndex = new Map<string, ProcessEntry>();
  const pendingCalledElementsAcrossFiles: Array<PendingCalledElement & { path: string }> = [];

  const admit = async (path: string, role: ManifestRole, maxBytes: number): Promise<Buffer> => {
    const remaining = MANIFEST_LIMITS.maxTotalBytes - totalBytes;
    const bytes = await readManifestFile(baseDir, path, Math.min(maxBytes, Math.max(remaining, 0)));
    totalBytes += bytes.byteLength;
    if (totalBytes > MANIFEST_LIMITS.maxTotalBytes) {
      throw new ProcessLandscapeError("manifest-total-size-limit", "Manifest aggregate byte budget exceeded");
    }
    fileRecords.push({ path, byteLength: bytes.byteLength, sha256: sha256Hex(bytes) });
    void role;
    return bytes;
  };

  for (const entry of manifest.entries) {
    if (entry.role !== "bpmn") continue;
    const bytes = await admit(entry.path, entry.role, MANIFEST_LIMITS.maxBpmnBytes);
    const sha256 = sha256Hex(bytes);
    admittedBpmnSha256.add(sha256);
    // Raw bytes are stored once per admitted file (never per derived process entry) in the
    // top-level `sources[]`, and only under the explicit model-content permission; the structural
    // profile never embeds or retains raw/embedded XML anywhere in the snapshot.
    if (contentProfile === "model-content") {
      if (bytes.byteLength > MAX_SOURCE_BYTES) {
        throw new ProcessLandscapeError("model-content-size-limit", `${entry.path}: exceeds the model-content embedding limit`);
      }
      sources.push({ path: entry.path, byteLength: bytes.byteLength, sha256, sourceBytesBase64: bytes.toString("base64") });
    } else {
      sources.push({ path: entry.path, byteLength: bytes.byteLength, sha256 });
    }

    const xml = decodeUtf8Strict(bytes, entry.path);
    admittedBpmnNewlineNormalizedSha256.add(createHash("sha256").update(xml.replace(/\r\n?/g, "\n"), "utf8").digest("hex"));
    const extraction = extractBpmnDiagram(xml, entry.path, manifest.repositoryId);
    diagnostics.push(...extraction.documentDiagnostics);
    for (const pending of extraction.pendingCalledElements) pendingCalledElementsAcrossFiles.push({ ...pending, path: entry.path });
    const singleProcessFile = extraction.processes.length <= 1;
    for (const process of extraction.processes) {
      // Preserve the pre-multi-process identity/hash format exactly for the common single-process
      // (or process-less) case: `hash(repositoryId + path)`. A document declaring two or more
      // processes uses a distinct format that also folds in the stable, position-based
      // discriminator, since a BPMN process `id` may repeat or be absent within the same file.
      const identityPreimage = singleProcessFile
        ? `${manifest.repositoryId}\0${entry.path}`
        : `${manifest.repositoryId}\0${entry.path}\0${process.processIndex}`;
      const processIdentity = `landscape-process:${createHash("sha256").update(identityPreimage, "utf8").digest("hex")}`;
      const processEntry: ProcessEntry = {
        processIdentity,
        path: entry.path,
        byteLength: bytes.byteLength,
        sha256,
        processId: process.processId,
        name: process.name,
        contextTags: [],
        phaseTags: [],
        coverage: process.coverage,
        diagnostics: process.diagnostics,
        elements: process.elements,
        relationships: process.relationships,
      };
      processes.push(processEntry);
      processEntryByPathAndIndex.set(`${entry.path}\0${process.processIndex}`, processEntry);
    }
  }

  for (const entry of manifest.entries) {
    if (entry.role === "navigator-data") {
      const bytes = await admit(entry.path, entry.role, MANIFEST_LIMITS.maxNavigatorDataBytes);
      navigatorBundle = { path: entry.path, bytes };
      continue;
    }
    const bytes = entry.role === "bpmn" ? undefined : await admit(entry.path, entry.role, MANIFEST_LIMITS.maxSidecarBytes);
    if (!bytes) continue;
    if (entry.role === "process-metadata") metadataByProcessId = parseProcessMetadata(bytes, entry.path);
    else if (entry.role === "cross-process-events") crossProcessEventsRaw = parseCrossProcessEvents(bytes, entry.path);
    else if (entry.role === "object-lifecycle") objectLifecycleRaw = parseObjectLifecycle(bytes, entry.path);
    else if (entry.role === "phase-journey") phaseJourneyRaw = parsePhaseJourney(bytes, entry.path);
  }

  let navigator: ProcessLandscapeSnapshot["navigator"];
  if (navigatorBundle) {
    const bundleIdentity = { path: navigatorBundle.path, byteLength: navigatorBundle.bytes.byteLength, sha256: sha256Hex(navigatorBundle.bytes) };
    const parsedNavigator = parseNavigatorBundle(navigatorBundle.bytes, bundleIdentity, {
      raw: admittedBpmnSha256,
      newlineNormalized: admittedBpmnNewlineNormalizedSha256,
    });
    navigator = parsedNavigator.navigator;
    diagnostics.push(...parsedNavigator.diagnostics);
  }

  const AMBIGUOUS_PROCESS = Symbol("ambiguous-process-id");
  const processesByProcessId = new Map<string, ProcessEntry[]>();
  for (const process of processes) {
    if (!process.processId) continue;
    const bucket = processesByProcessId.get(process.processId);
    if (bucket) bucket.push(process);
    else processesByProcessId.set(process.processId, [process]);
  }
  const processByProcessId = new Map<string, ProcessEntry | typeof AMBIGUOUS_PROCESS>();
  for (const [processId, bucket] of processesByProcessId) {
    if (bucket.length > 1) {
      const paths = bucket.map((process) => process.path).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
      diagnostics.push({
        code: "ambiguous-process-id",
        message: `BPMN process id "${processId}" is declared by multiple admitted files: ${paths.join(", ")}`,
        disposition: "unresolved",
        source: { ref: processId },
      });
      processByProcessId.set(processId, AMBIGUOUS_PROCESS);
    } else {
      processByProcessId.set(processId, bucket[0]!);
    }
  }
  const resolveProcessById = (processId: string): ProcessEntry | undefined => {
    const found = processByProcessId.get(processId);
    return found === undefined || found === AMBIGUOUS_PROCESS ? undefined : found;
  };

  // Cross-file callActivity resolution: a calledElement that looked like a local process-id
  // reference but was absent from its own document may still name a process declared in a
  // different admitted file. Resolution is explicit and never silent: an id that matches no
  // admitted process remains `unresolved-called-element`; an id declared by more than one admitted
  // file is only resolved when the navigator bundle's `procFile` map has been cryptographically
  // verified against the admitted BPMN bytes and names exactly one of the candidate files --
  // otherwise it is diagnosed as `ambiguous-called-element` rather than guessed.
  for (const pending of pendingCalledElementsAcrossFiles) {
    const sourceProcess = processEntryByPathAndIndex.get(`${pending.path}\0${pending.processIndex}`)!;
    const bucket = processesByProcessId.get(pending.calledProcessId);
    let target: ProcessEntry | undefined;
    if (!bucket || bucket.length === 0) {
      sourceProcess.diagnostics.push({
        code: "unresolved-called-element",
        message: "Called process is not resolved within this document and is not imported",
        disposition: "partial",
        source: { path: pending.path, element: pending.elementId },
      });
      continue;
    } else if (bucket.length === 1) {
      target = bucket[0];
    } else {
      const procFile = navigator?.verified && isPlainObject(navigator.procFile) ? navigator.procFile : undefined;
      const recordedPath = procFile ? procFile[pending.calledProcessId] : undefined;
      const matches = typeof recordedPath === "string" ? bucket.filter((candidate) => candidate.path === recordedPath) : [];
      if (matches.length === 1) {
        target = matches[0];
      } else {
        const paths = bucket.map((candidate) => candidate.path).sort(compareOrdinal);
        sourceProcess.diagnostics.push({
          code: "ambiguous-called-element",
          message: `callActivity calledElement "${pending.calledProcessId}" matches multiple admitted processes: ${paths.join(", ")}`,
          disposition: "unresolved",
          source: { path: pending.path, element: pending.elementId },
        });
        continue;
      }
    }
    const targetElement = target!.elements.find((element) => element.type === "process" && element.id === pending.calledProcessId);
    if (!targetElement) {
      sourceProcess.diagnostics.push({
        code: "unresolved-called-element",
        message: "Called process is not resolved within this document and is not imported",
        disposition: "partial",
        source: { path: pending.path, element: pending.elementId },
      });
      continue;
    }
    sourceProcess.relationships.push({
      identity: `landscape-relationship:${createHash("sha256").update(`${manifest.repositoryId}\0${pending.path}\0${pending.elementId}\0${pending.calledProcessId}`, "utf8").digest("hex")}`,
      kind: "reference",
      source: pending.elementIdentity,
      target: targetElement.identity,
      order: sourceProcess.relationships.length,
    });
  }

  const matchedProcessIds = new Set<string>();
  for (const [processId, meta] of metadataByProcessId) {
    const resolvedAmbiguously = processByProcessId.get(processId) === AMBIGUOUS_PROCESS;
    const process = resolveProcessById(processId);
    if (!process) {
      diagnostics.push({
        code: resolvedAmbiguously ? "ambiguous-process-metadata" : "unresolved-process-metadata",
        message: resolvedAmbiguously
          ? `Process metadata references an ambiguous process id declared by multiple files: ${processId}`
          : `Process metadata references an unknown process id: ${processId}`,
        disposition: "unresolved",
        source: { ref: processId },
      });
      continue;
    }
    matchedProcessIds.add(processId);
    if (meta.title) process.name = meta.title;
    if (meta.proposalStatus) process.proposalStatus = meta.proposalStatus;
    if (meta.contextTags) process.contextTags = [...meta.contextTags];
    if (meta.phaseTags) process.phaseTags = [...meta.phaseTags];
  }

  const resolveEndpoint = (processId: string, elementId: string | undefined) => {
    const isAmbiguous = processByProcessId.get(processId) === AMBIGUOUS_PROCESS;
    const process = resolveProcessById(processId);
    if (!process) {
      diagnostics.push({
        code: isAmbiguous ? "ambiguous-cross-reference" : "unresolved-cross-reference",
        message: isAmbiguous
          ? `Reference to an ambiguous process id declared by multiple files: ${processId}`
          : `Reference to an unknown process id: ${processId}`,
        disposition: "unresolved",
        source: { ref: processId },
      });
      return { processId, elementId, resolved: false };
    }
    if (!elementId) return { processId, elementId, processIdentity: process.processIdentity, resolved: true };
    const element = process.elements.find((candidate) => candidate.id === elementId);
    if (!element) {
      diagnostics.push({ code: "unresolved-cross-reference", message: `Reference to an unknown element id: ${elementId}`, disposition: "unresolved", source: { ref: elementId, path: process.path } });
      return { processId, elementId, processIdentity: process.processIdentity, resolved: false };
    }
    return { processId, elementId, processIdentity: process.processIdentity, elementIdentity: element.identity, resolved: true };
  };

  const crossProcessEvents: CrossProcessEvent[] = crossProcessEventsRaw.map((event) => ({
    eventId: event.eventId,
    ...(event.label ? { label: event.label } : {}),
    source: resolveEndpoint(event.source.processId, event.source.elementId),
    target: resolveEndpoint(event.target.processId, event.target.elementId),
  }));

  const objectLifecycle: ObjectLifecycle[] = objectLifecycleRaw.map((object) => ({
    objectId: object.objectId,
    ...(object.label ? { label: object.label } : {}),
    stages: object.stages.map((stage) => ({ stage: stage.stage, order: stage.order, ...resolveEndpoint(stage.processId, stage.elementId) })),
  }));

  const phaseJourney: PhaseJourneyPhase[] = phaseJourneyRaw.map((phase) => ({
    phaseId: phase.phaseId,
    ...(phase.label ? { label: phase.label } : {}),
    order: phase.order,
    steps: phase.steps.map((step) => ({ order: step.order, ...resolveEndpoint(step.processId, step.elementId) })),
  }));

  const overlays: ProcessLandscapeOverlays = { context: [], phase: [] };
  const overlayMap = (select: (process: ProcessEntry) => string[]) => {
    const byTag = new Map<string, Set<string>>();
    for (const process of processes) {
      for (const tag of select(process)) {
        if (!byTag.has(tag)) byTag.set(tag, new Set());
        byTag.get(tag)!.add(process.processIdentity);
      }
    }
    return [...byTag.entries()].sort(([a], [b]) => compareOrdinal(a, b))
      .map(([tag, identities]) => ({ tag, processIdentities: [...identities].sort() }));
  };
  overlays.context = overlayMap((process) => process.contextTags);
  overlays.phase = overlayMap((process) => process.phaseTags);

  const searchIndex: SearchEntry[] = [];
  for (const process of processes) {
    searchIndex.push({ term: process.name ?? process.processId ?? process.path, kind: "process", processIdentity: process.processIdentity, deepLink: `process:${process.processIdentity}` });
    for (const element of process.elements) {
      if (element.kind !== "bpmn") continue;
      const term = element.attributes.name ?? element.id;
      searchIndex.push({ term, kind: "element", processIdentity: process.processIdentity, elementIdentity: element.identity, deepLink: `process:${process.processIdentity}#${element.identity}` });
    }
  }

  void matchedProcessIds;
  const allDiagnostics = [...diagnostics, ...processes.flatMap((process) => process.diagnostics)];
  const supported = [...new Set(processes.flatMap((process) => process.coverage.supported))].sort();
  const limitations = [...new Set(allDiagnostics.map((item) => item.code))].sort();

  const sortedRecords = [...fileRecords].sort((a, b) => compareOrdinal(a.path, b.path));
  const identityPreimage = [
    SNAPSHOT_SCHEMA, manifest.repositoryId,
    ...sortedRecords.map((record) => `${record.path}\0${record.byteLength}\0${record.sha256}`),
  ].join("\n");
  const snapshotId = `landscape:${createHash("sha256").update(identityPreimage, "utf8").digest("hex")}`;

  const snapshot: ProcessLandscapeSnapshot = {
    schema: SNAPSHOT_SCHEMA,
    snapshotId,
    manifestVersion: 1,
    repositoryId: manifest.repositoryId,
    contentProfile,
    coverage: { status: allDiagnostics.length === 0 ? "full" : "partial", supported, limitations },
    diagnostics,
    sources,
    processes,
    searchIndex,
    crossProcessEvents,
    objectLifecycle,
    phaseJourney,
    overlays,
    ...(navigator ? { navigator } : {}),
  };

  const validate = snapshotValidator();
  if (!validate(snapshot)) {
    const detail = (validate.errors ?? []).slice(0, 5).map((error) => `${error.instancePath || "/"} ${error.message}`).join("; ");
    throw new ProcessLandscapeError("snapshot-schema-invalid", `Process landscape snapshot failed schema validation: ${detail}`);
  }
  return snapshot;
}
