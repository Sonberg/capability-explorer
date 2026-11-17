import type { ApiResponse, Endpoint, Process, Snapshot } from "./contracts";
import { catalogueRows, processTags, text } from "./navigator";

export const PAGE_SIZE = 40;
export const lenses = ["diagram", "events", "lifecycle", "journey", "collaboration"] as const;
export type Lens = typeof lenses[number];
export interface Route {
  process: string; element: string; document: string; diagram: string; documentElement: string;
  lens: Lens; q: string; phase: string; context: string; overlay: string;
}

export function readRoute(url = new URL(window.location.href)): Route {
  const p = url.searchParams;
  let process = p.get("process") ?? "", element = p.get("element") ?? "";
  if (!process && url.hash.startsWith("#process:")) {
    const [pid, eid = ""] = url.hash.slice(9).split("#");
    try { process = decodeURIComponent(pid); element = decodeURIComponent(eid); } catch { /* Leave invalid links unmatched. */ }
  }
  const lens = p.get("lens");
  return {
    process, element, lens: lenses.includes(lens as Lens) ? lens as Lens : "diagram",
    q: p.get("q") ?? "", phase: p.get("phase") ?? "", context: p.get("context") ?? "",
    overlay: p.get("overlay") ?? "", document: p.get("document") ?? "", diagram: p.get("diagram") ?? "",
    documentElement: p.get("documentElement") ?? "",
  };
}

export function routeUrl(route: Route, base = new URL(window.location.href)): string {
  const url = new URL(base);
  for (const [key, value] of Object.entries(route)) {
    if (value) url.searchParams.set(key, value);
    else url.searchParams.delete(key);
  }
  if (url.hash.startsWith("#process:")) url.hash = "";
  return `${url.pathname}${url.search}${url.hash}`;
}

export const title = (p: Process) => p.name || p.processId || p.path;
export const ordered = <T extends { order: number }>(items: readonly T[]) =>
  [...items].sort((a, b) => a.order - b.order);

export function proposal(p: Process): string {
  const status = p.proposalStatus?.toLowerCase();
  if (status === "proposed" || status?.includes("proposal"))
    return "Committee-proposal · not owner-approved";
  if (status === "approved") return "Source status: approved (not independently verified)";
  return `Source status: ${status || "not supplied"} · owner approval not established`;
}

export function filterProcesses(s: Snapshot, route: Route): Process[] {
  const query = route.q.trim().toLocaleLowerCase();
  const indexed = new Set(s.searchIndex.filter(e => e.term.toLocaleLowerCase().includes(query)).map(e => e.processIdentity));
  const tagged = (p: Process, kind: "context" | "phase", tag: string) => !tag || processTags(s, p, kind).includes(tag);
  return s.processes.filter(p => tagged(p, "context", route.context) && tagged(p, "phase", route.phase)
    && (!query || indexed.has(p.processIdentity)
      || [title(p), p.path, p.processIdentity, ...p.elements.flatMap(e => [e.id, e.type, e.attributes.name ?? ""]),
        ...catalogueRows(s, p).flatMap(row => [text(row.name), text(row.group), text(row.purpose)])]
        .some(t => t.toLocaleLowerCase().includes(query))));
}

export function endpointProcess(s: Snapshot, e: Endpoint): Process | undefined {
  if (!e.resolved || !e.processIdentity) return undefined;
  const p = s.processes.find(p => p.processIdentity === e.processIdentity);
  if (!p || (e.elementId && !e.elementIdentity)) return undefined;
  if (e.elementIdentity && !p.elements.some(el => el.identity === e.elementIdentity)) return undefined;
  return p;
}

type Guard = (v: unknown) => boolean;
const record = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);
const str: Guard = v => typeof v === "string";
const num: Guard = v => typeof v === "number" && Number.isFinite(v);
const bool: Guard = v => typeof v === "boolean";
const optional = (guard: Guard): Guard => v => v == null || guard(v);
const array = (guard: Guard): Guard => v => Array.isArray(v) && v.every(guard);
const shape = (fields: Record<string, Guard>): Guard => v =>
  record(v) && Object.entries(fields).every(([key, guard]) => guard(v[key]));
const strings = array(str);
const coverage = shape({ status: str, supported: strings, limitations: strings });
const diagnostic = shape({
  code: str, message: str, disposition: str,
  source: optional(shape({ path: optional(str), element: optional(str), ref: optional(str) })),
});
const source = shape({ path: str, byteLength: num, sha256: str });
const element = shape({
  identity: str, kind: str, namespace: str, id: str, type: str, order: num,
  attributes: v => record(v) && Object.values(v).every(str),
  source: shape({ path: str, element: optional(str) }), parent: optional(str),
  content: optional(shape({ text: str, language: optional(str) })),
});
const relationship = shape({
  identity: str, kind: str, source: str, target: str, order: num,
  condition: optional(shape({ text: optional(str), language: optional(str) })),
});
const process = shape({
  processIdentity: str, path: str, byteLength: num, sha256: str,
  processId: optional(str), name: optional(str), proposalStatus: optional(str),
  contextTags: strings, phaseTags: strings, coverage, diagnostics: array(diagnostic),
  elements: array(element), relationships: array(relationship),
});
const endpointFields = {
  processId: str, resolved: bool, processIdentity: optional(str), elementId: optional(str), elementIdentity: optional(str),
};
const overlay = shape({ tag: str, processIdentities: strings });
const snapshot = shape({
  schema: v => v === "ce.process-landscape.v1", snapshotId: str, repositoryId: str, manifestVersion: v => v === 1,
  contentProfile: v => v === "structural" || v === "model-content",
  coverage, diagnostics: array(diagnostic), sources: array(source), processes: array(process),
  searchIndex: array(shape({
    term: str, kind: str, processIdentity: str, elementIdentity: optional(str), deepLink: str,
  })),
  crossProcessEvents: array(shape({
    eventId: str, label: optional(str), source: shape(endpointFields), target: shape(endpointFields),
  })),
  objectLifecycle: array(shape({
    objectId: str, label: optional(str), stages: array(shape({ ...endpointFields, stage: str, order: num })),
  })),
  phaseJourney: array(shape({
    phaseId: str, label: optional(str), order: num, steps: array(shape({ ...endpointFields, order: num })),
  })),
  overlays: shape({ context: array(overlay), phase: array(overlay) }),
  navigator: optional(shape({ verified: bool, bundle: source })),
  documents: v => v === undefined || array(shape({
    path: str, elements: array(element), relationships: array(shape({
      identity: str, kind: kind => kind === "messageFlow", source: str, target: str, order: num, elementIdentity: str,
      condition: optional(shape({ text: optional(str), language: optional(str) })),
    })),
  }))(v),
});

export function parseResponse(value: unknown): ApiResponse {
  if (!record(value) || value.schema !== "ce.process-landscape.v1" || !str(value.status)
      || !optional(str)(value.reason)) throw new Error("Unsupported or malformed API response.");
  if (value.status === "ok" && !snapshot(value.snapshot))
    throw new Error("Snapshot is missing or does not match ce.process-landscape.v1.");
  if (!optional(shape({ status: v => str(v) || num(v), reason: str }))(value.identityVerification))
    throw new Error("Malformed identity verification.");
  return value as unknown as ApiResponse;
}

export async function fetchSnapshot(signal: AbortSignal): Promise<ApiResponse> {
  const response = await fetch("/api/process-landscape/v1/snapshot", {
    signal, headers: { Accept: "application/json" }, cache: "no-store",
  });
  const body = parseResponse(await response.json());
  if (!response.ok && body.status === "ok") throw new Error(`Snapshot request failed (HTTP ${response.status}).`);
  return body;
}
