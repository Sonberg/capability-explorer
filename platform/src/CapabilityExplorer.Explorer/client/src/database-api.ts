import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, request } from "./api";
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

type InfiniteDatabaseState<T> = {
  base: string;
  data?: DatabasePage<T>;
  loading: boolean;
  loadingMore: boolean;
  error?: string;
  errorStatus?: number;
  failedCursor?: string | null;
};

/** Appends cursor pages on one immutable database snapshot without limiting the result count. */
export function useDatabaseInfinitePage<T>(base: string) {
  const [state, setState] = useState<InfiniteDatabaseState<T>>({ base: "", loading: true, loadingMore: false });
  const generation = useRef(0);
  const inFlight = useRef<{ controller: AbortController; token: number; cursor: string | null } | null>(null);
  const active = state.base === base ? state : { base, loading: true, loadingMore: false } as InfiniteDatabaseState<T>;

  const fetchPage = useCallback(async (cursor: string | null, token: number) => {
    if (token !== generation.current || inFlight.current) return;
    const controller = new AbortController();
    inFlight.current = { controller, token, cursor };
    setState((previous) => ({
      ...previous,
      base,
      loading: cursor === null,
      loadingMore: cursor !== null,
      error: undefined,
      errorStatus: undefined,
    }));
    const query = new URL(base, window.location.origin);
    query.searchParams.set("limit", "50");
    if (cursor) query.searchParams.set("cursor", cursor);
    try {
      const data = await request<DatabasePage<T>>(query.pathname + query.search, { signal: controller.signal });
      if (token !== generation.current) return;
      const expectedSnapshot = new URLSearchParams(query.search).get("snapshot");
      if (expectedSnapshot && data.snapshotId !== expectedSnapshot) {
        throw new ApiError("The database snapshot changed. Restart the search to continue.", 409, data);
      }
      setState((previous) => {
        const current = previous.base === base ? previous.data : undefined;
        const nextData = cursor === null || !current
          ? data
          : { ...data, items: [...current.items, ...data.items] };
        return { base, data: nextData, loading: false, loadingMore: false };
      });
    } catch (error) {
      if (controller.signal.aborted || token !== generation.current) return;
      const failure = error as Error;
      setState((previous) => ({
        ...(previous.base === base ? previous : { base, loading: false, loadingMore: false }),
        loading: false,
        loadingMore: false,
        error: failure.message,
        errorStatus: failure instanceof ApiError ? failure.status : undefined,
        failedCursor: cursor,
      }));
    } finally {
      if (inFlight.current?.controller === controller) inFlight.current = null;
    }
  }, [base]);

  useEffect(() => {
    const token = ++generation.current;
    inFlight.current?.controller.abort();
    inFlight.current = null;
    setState({ base, loading: true, loadingMore: false });
    void fetchPage(null, token);
    return () => {
      if (generation.current === token) {
        generation.current++;
        if (inFlight.current?.token === token) {
          inFlight.current.controller.abort();
          inFlight.current = null;
        }
      }
    };
  }, [base, fetchPage]);

  const loadMore = useCallback(() => {
    if (!active.data?.nextCursor || active.loading || active.loadingMore || active.error || inFlight.current) return;
    void fetchPage(active.data.nextCursor, generation.current);
  }, [active, fetchPage]);

  const retry = useCallback(() => {
    if (!active.error || active.loading || active.loadingMore || inFlight.current) return;
    if (active.errorStatus === 400 || active.errorStatus === 409) {
      const token = ++generation.current;
      setState({ base, loading: true, loadingMore: false });
      void fetchPage(null, token);
      return;
    }
    void fetchPage(active.failedCursor ?? null, generation.current);
  }, [active, base, fetchPage]);

  return {
    ...active,
    loadMore,
    retry,
    hasMore: active.data?.nextCursor !== null && active.data?.nextCursor !== undefined,
  };
}

export function useDebouncedText(value: string) {
  const [settled, setSettled] = useState(value);
  useEffect(() => { const timer = window.setTimeout(() => setSettled(value), 250); return () => window.clearTimeout(timer); }, [value]);
  return settled;
}

export function useDatabaseItem<T>(endpoint: string | null) { return useApi<T>(endpoint); }
