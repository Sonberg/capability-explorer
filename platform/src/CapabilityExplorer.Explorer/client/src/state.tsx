import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { request, type Meta } from "./api";

// ---------------------------------------------------------------- hash routing

export interface Route {
  path: string;
  params: URLSearchParams;
}

function parse(): Route {
  const [path, query] = window.location.hash.replace(/^#\/?/, "").split("?");
  return { path: path || "explore", params: new URLSearchParams(query ?? "") };
}

export function useRoute(): Route {
  const [route, setRoute] = useState(parse);
  useEffect(() => {
    const onChange = () => setRoute(parse());
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return route;
}

export function href(path: string, params: Record<string, string | undefined> = {}): string {
  const q = new URLSearchParams(Object.entries(params).filter((e): e is [string, string] => !!e[1]));
  const s = q.toString();
  return `#/${path}${s ? `?${s}` : ""}`;
}

export const symbolHref = (key: string) => href("explore", { key });

// ---------------------------------------------------------------- data fetching

export function useApi<T>(path: string | null) {
  const [state, setState] = useState<{ data?: T; error?: string; loading: boolean }>({ loading: !!path });
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (!path) {
      setState({ loading: false });
      return;
    }
    let live = true;
    setState((s) => ({ data: s.data, loading: true }));
    request<T>(path)
      .then((data) => live && setState({ data, loading: false }))
      .catch((e: Error) => live && setState({ error: e.message, loading: false }));
    return () => {
      live = false;
    };
  }, [path, tick]);
  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { ...state, reload };
}

// ---------------------------------------------------------------- selection: capability + revisions

const read = <T,>(key: string, fallback: T): T => {
  try {
    const v = localStorage.getItem(key);
    return v ? (JSON.parse(v) as T) : fallback;
  } catch {
    return fallback;
  }
};
const write = (key: string, value: unknown) => {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* storage unavailable: selection lasts for this page only */
  }
};

interface Selection {
  meta: Meta;
  reloadMeta: () => void;
  capability: string;
  setCapability: (id: string) => void;
  /** Effective revision per repository, as resolved by the server (capability locks, defaults, overrides). */
  view: Record<string, string>;
  setRevision: (repo: string, sha: string) => void;
  /** Query string fragment for every scoped API call. */
  scope: string;
}

const SelectionContext = createContext<Selection | null>(null);

export function SelectionProvider({ meta, reloadMeta, children }: { meta: Meta; reloadMeta: () => void; children: ReactNode }) {
  const [capability, setCapabilityState] = useState<string>(() => {
    const saved = read("ce.capability", "");
    return meta.capabilities.some((c) => c.id === saved) ? saved : "";
  });
  const [overrides, setOverrides] = useState<Record<string, Record<string, string>>>(() => read("ce.overrides", {}));

  const known = meta.capabilities.some((c) => c.id === capability);
  const effective = known ? capability : "";
  const own = overrides[effective || "*"] ?? {};
  const spec = Object.entries(own).map(([r, s]) => `${r}:${s}`).join(",");
  // The load generation is part of every scoped request, so new artifacts on the server refetch every view.
  const scope = `view=${encodeURIComponent(spec)}${effective ? `&cap=${encodeURIComponent(effective)}` : ""}&g=${meta.generation}`;
  const resolved = useApi<Record<string, string>>(`/api/view?${scope}`);

  const setCapability = useCallback((id: string) => {
    setCapabilityState(id);
    write("ce.capability", id);
  }, []);

  const setRevision = useCallback(
    (repo: string, sha: string) => {
      setOverrides((all) => {
        const next = { ...all, [effective || "*"]: { ...(all[effective || "*"] ?? {}), [repo]: sha } };
        write("ce.overrides", next);
        return next;
      });
    },
    [effective],
  );

  const value = useMemo<Selection>(
    () => ({ meta, reloadMeta, capability: effective, setCapability, view: resolved.data ?? {}, setRevision, scope }),
    [meta, reloadMeta, effective, setCapability, resolved.data, setRevision, scope],
  );
  return <SelectionContext.Provider value={value}>{children}</SelectionContext.Provider>;
}

export function useSelection(): Selection {
  const s = useContext(SelectionContext);
  if (!s) throw new Error("useSelection outside SelectionProvider");
  return s;
}

export function revisionLabel(meta: Meta, repo: string, sha: string): string {
  return meta.revisions.find((r) => r.repositoryId === repo && r.commitSha === sha)?.label ?? sha.slice(0, 12);
}
