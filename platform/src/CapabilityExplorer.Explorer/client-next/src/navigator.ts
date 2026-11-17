import type { Category, Element, Endpoint, Process, Snapshot } from "./contracts";

export type SourceRecord = Record<string, unknown>;
export const isRecord = (value: unknown): value is SourceRecord =>
  value !== null && typeof value === "object" && !Array.isArray(value);
export const text = (value: unknown) => typeof value === "string" ? value : "";
export const texts = (value: unknown): string[] =>
  typeof value === "string" ? [value] : Array.isArray(value) ? value.filter(v => typeof v === "string") : [];
export const records = (value: unknown): SourceRecord[] => Array.isArray(value) ? value.filter(isRecord) : [];
export const entries = (value: unknown): [string, unknown][] => isRecord(value) ? Object.entries(value) : [];
export const categoryData = (s: Snapshot, category: Category): unknown =>
  s.navigator?.verified === true ? s.navigator[category] : undefined;

export function fileMatches(s: Snapshot, p: Process, file: string): boolean {
  if (p.path === file) return true;
  const map = categoryData(s, "procFile");
  return !!p.processId && isRecord(map) && text(map[p.processId]) === file;
}

export function sourceEndpoint(s: Snapshot, file: string, element = ""): Endpoint {
  const candidates = s.processes.filter(p => fileMatches(s, p, file)).flatMap<{ p: Process; e?: Element }>(p => {
    if (!element) return [{ p, e: undefined }];
    return p.elements.filter(e => e.id === element).map(e => ({ p, e }));
  });
  if (candidates.length !== 1) return {
    processId: file || "file not supplied", elementId: element || undefined, resolved: false,
  };
  const { p, e } = candidates[0];
  return {
    processId: p.processId || p.processIdentity, processIdentity: p.processIdentity,
    elementId: e?.id, elementIdentity: e?.identity, resolved: true,
  };
}

export function catalogueRows(s: Snapshot, p: Process): SourceRecord[] {
  if (!p.processId || s.processes.filter(other => other.processId === p.processId).length !== 1) return [];
  return records(categoryData(s, "catalogue")).filter(row => {
    const ids = [...texts(row.proc), ...texts(row.procs)];
    return ids.includes(p.processId!) && (!text(row.file) || fileMatches(s, p, text(row.file)));
  });
}

export function processTags(s: Snapshot, p: Process, kind: "context" | "phase"): string[] {
  return [...new Set([
    ...(kind === "context" ? p.contextTags : p.phaseTags),
    ...s.overlays[kind].filter(o => o.processIdentities.includes(p.processIdentity)).map(o => o.tag),
    ...catalogueRows(s, p).flatMap(row => texts(kind === "context" ? row.ctx : row.phases)),
  ])];
}

export const referenceTuples = (value: unknown): string[][] => Array.isArray(value)
  ? value.filter((row): row is string[] => Array.isArray(row) && row.length >= 2 && row.every(v => typeof v === "string")) : [];
