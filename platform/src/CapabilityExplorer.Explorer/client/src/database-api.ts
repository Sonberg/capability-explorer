import { useEffect, useState } from "react";
import { request } from "./api";
import { useApi } from "./state";

export interface DatabaseMeta {
  source: "postgres"; snapshotId: string; generation: number; capability: string;
  selection: Record<string, string>; capabilities: { id: string; name: string; repositories: string[] }[];
  supported: string[]; status: string;
}
export function isDatabaseMeta(value: unknown): value is DatabaseMeta {
  return !!value && typeof value === "object" && "source" in value && value.source === "postgres";
}
export interface DatabasePage<T> { items: T[]; nextCursor: string | null; snapshotId: string }
export interface DatabaseNode {
  key: string; kind: string; name: string; signature: string | null; generated: boolean;
  repositoryId: string; commitSha: string; unit: string; artifactId: number;
}
export interface DatabaseHit { key: string; name: string; kind: string; repositoryId: string; unit: string; hasCalculation: boolean }
export interface DatabaseArtifact { id: number; repositoryId: string; commitSha: string; unit: string; unitPath: string; language: string; status: string }
export interface DatabaseCalculation { artifactId: number; localId: string; key: string; name: string; repositoryId: string; commitSha: string; unit: string; language: string; irHash: string | null }
export interface DatabaseEdge { artifactId: number; localId: string; from: string; to: string | null; relation: string; resolution: string; derivation: string }
export interface DatabaseEvidence { id: string; repositoryId: string; commitSha: string; path: string; startLine: number; startColumn: number; endLine: number; endColumn: number }
export function databaseUrl(endpoint: string, scope: string, values: Record<string, string> = {}) {
  const query = new URLSearchParams(scope);
  Object.entries(values).forEach(([key, value]) => query.set(key, value));
  return `/api/${endpoint}?${query}`;
}

/** Retains one successful page and at most 20 continuation tokens, independent of corpus size.
 * Failed requests keep their exact cursor for Retry without relabeling the successful rows. */
export function useDatabasePage<T>(base: string) {
  type Position = { cursor: string; back: string[]; number: number };
  const first: Position = { cursor: "", back: [], number: 1 };
  const [requested, setRequested] = useState({ base, ...first, attempt: 0 });
  const current = requested.base === base ? requested : { base, ...first, attempt: 0 };
  const [result, setResult] = useState<{ base: string; data?: DatabasePage<T>; position: Position; loading: boolean; error?: string }>({ base, position: first, loading: true });
  const shown = result.base === base ? result : { base, position: first, loading: true };
  useEffect(() => {
    const controller = new AbortController();
    const position: Position = { cursor: current.cursor, back: current.back, number: current.number };
    setResult((previous) => ({ base, data: previous.base === base ? previous.data : undefined,
      position: previous.base === base ? previous.position : first, loading: true }));
    const query = new URL(base, window.location.origin);
    query.searchParams.set("limit", "50");
    if (current.cursor) query.searchParams.set("cursor", current.cursor);
    request<DatabasePage<T>>(query.pathname + query.search, { signal: controller.signal })
      .then((data) => { if (!controller.signal.aborted) setResult({ base, data, position, loading: false }); })
      .catch((error: Error) => { if (!controller.signal.aborted) setResult((previous) => ({ ...previous, error: error.message, loading: false })); });
    return () => controller.abort();
  }, [base, current.cursor, current.attempt]);
  const next = () => {
    if (!shown.data?.nextCursor || shown.loading || shown.error) return;
    setRequested({ base, cursor: shown.data.nextCursor, back: [...shown.position.back, shown.position.cursor].slice(-20),
      number: shown.position.number + 1, attempt: current.attempt + 1 });
  };
  const previous = () => {
    if (!shown.position.back.length || shown.loading) return;
    setRequested({ base, cursor: shown.position.back.at(-1)!, back: shown.position.back.slice(0, -1),
      number: shown.position.number - 1, attempt: current.attempt + 1 });
  };
  const retry = () => {
    if (!shown.error || shown.loading) return;
    setRequested({ ...current, attempt: current.attempt + 1 });
  };
  return { ...shown, next, previous, retry, number: shown.position.number, hasPrevious: shown.position.back.length > 0 };
}

export function useDebouncedText(value: string) {
  const [settled, setSettled] = useState(value);
  useEffect(() => { const timer = window.setTimeout(() => setSettled(value), 250); return () => window.clearTimeout(timer); }, [value]);
  return settled;
}

export function useDatabaseItem<T>(endpoint: string | null) { return useApi<T>(endpoint); }
