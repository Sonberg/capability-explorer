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

/** Retains one result page and at most 20 continuation tokens, independent of corpus size. */
export function useDatabasePage<T>(base: string) {
  const [position, setPosition] = useState({ base, cursor: "", back: [] as string[], number: 1 });
  const current = position.base === base ? position : { base, cursor: "", back: [], number: 1 };
  const [result, setResult] = useState<{ data?: DatabasePage<T>; loading: boolean; error?: string }>({ loading: true });
  useEffect(() => {
    const controller = new AbortController();
    setResult((previous) => ({ data: previous.data, loading: true }));
    const query = new URL(base, window.location.origin);
    query.searchParams.set("limit", "50");
    if (current.cursor) query.searchParams.set("cursor", current.cursor);
    request<DatabasePage<T>>(query.pathname + query.search, { signal: controller.signal })
      .then((data) => { if (!controller.signal.aborted) setResult({ data, loading: false }); })
      .catch((error: Error) => { if (!controller.signal.aborted) setResult((previous) => ({ data: previous.data, error: error.message, loading: false })); });
    return () => controller.abort();
  }, [base, current.cursor]);
  const next = () => {
    if (!result.data?.nextCursor || result.loading) return;
    setPosition({ base, cursor: result.data.nextCursor, back: [...current.back, current.cursor].slice(-20), number: current.number + 1 });
  };
  const previous = () => {
    if (!current.back.length || result.loading) return;
    setPosition({ base, cursor: current.back.at(-1)!, back: current.back.slice(0, -1), number: current.number - 1 });
  };
  return { ...result, next, previous, number: current.number, hasPrevious: current.back.length > 0 };
}

export function useDebouncedText(value: string) {
  const [settled, setSettled] = useState(value);
  useEffect(() => { const timer = window.setTimeout(() => setSettled(value), 250); return () => window.clearTimeout(timer); }, [value]);
  return settled;
}

export function useDatabaseItem<T>(endpoint: string | null) { return useApi<T>(endpoint); }
