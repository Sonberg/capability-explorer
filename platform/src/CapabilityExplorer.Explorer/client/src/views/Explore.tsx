import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, request, shortSha, type Calculation, type Edge, type SearchHit, type SimilarHit, type SymbolDetail } from "../api";
import { Formula, shortTarget } from "../components/formula";
import { EvidenceLinks, SourceView } from "../components/source";
import { KindPill, ResolutionPill, SymbolLink } from "../components/symbols";
import { Card, H1, H2, H3, inputClass, Key, Kv, Loading, Mono, Muted, Pill, PropsList, Table, jsonText } from "../components/ui";
import { usePublishFocus } from "../focus";
import { revisionLabel, symbolHref, useApi, useSelection } from "../state";

export const ATTRIBUTES = ["aggregation", "timeBucket", "period", "denominatorSource", "filters", "rounding", "numericType", "nullHandling", "zeroDenominator", "currency", "overflow"];

let lastSearch = { q: "", kind: "", repo: "" };

export function Explore({ params }: { params: URLSearchParams }) {
  const { meta, scope, view, reloadMeta } = useSelection();
  const key = params.get("key");
  const [q, setQ] = useState(lastSearch.q);
  const [kind, setKind] = useState(lastSearch.kind);
  const [repo, setRepo] = useState(lastSearch.repo);
  const [debounced, setDebounced] = useState(q);

  useEffect(() => {
    const t = setTimeout(() => setDebounced(q), 150);
    return () => clearTimeout(t);
  }, [q]);
  useEffect(() => {
    lastSearch = { q, kind, repo };
  }, [q, kind, repo]);

  const hits = useSearchHits(debounced, kind, repo, scope, meta.snapshotId, meta.generation, reloadMeta);
  const repos = Object.keys(view).sort();
  const listRef = useRef<HTMLDivElement | null>(null);
  const sentinelRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const root = listRef.current;
    const target = sentinelRef.current;
    if (!root || !target || !hits.hasMore) return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) hits.loadMore();
    }, { root, rootMargin: "0px 0px 120px 0px", threshold: 0 });
    observer.observe(target);
    return () => observer.disconnect();
  }, [hits.hasMore, hits.loadMore]);

  return (
    <div className="grid items-start gap-4 lg:grid-cols-[340px_minmax(0,1fr)]">
      <aside className="flex min-h-0 flex-col gap-2 lg:sticky lg:top-[120px] lg:h-[calc(100dvh-140px)]">
        <input type="search" aria-label="Search symbols" placeholder="Search symbols, e.g. average, Forecast, dbo.Sales" value={q} onChange={(e) => setQ(e.target.value)} className={inputClass} autoFocus={!key} />
        <div className="flex gap-2">
          <select aria-label="Kind" value={kind} onChange={(e) => setKind(e.target.value)} className={inputClass}>
            <option value="">All kinds</option>
            {meta.nodeKinds.map((k) => (
              <option key={k}>{k}</option>
            ))}
          </select>
          <select aria-label="Repository" value={repo} onChange={(e) => setRepo(e.target.value)} className={inputClass}>
            <option value="">All repositories</option>
            {repos.map((r) => (
              <option key={r}>{r}</option>
            ))}
          </select>
        </div>
        <div ref={listRef} role="region" aria-label="Symbol results" tabIndex={0} data-testid="symbol-results" className="h-[360px] min-h-0 min-w-0 flex-shrink-0 overflow-y-auto rounded-lg border border-zinc-200 bg-white lg:h-auto lg:min-h-[240px] lg:flex-1 lg:flex-shrink dark:border-zinc-800 dark:bg-zinc-900">
          <div role="list" aria-busy={hits.loading || hits.loadingMore}>
            {hits.items.map((h) => (
              <a
                key={h.key}
                role="listitem"
                data-symbol-key={h.key}
                href={symbolHref(h.key)}
                className={`block border-b border-zinc-100 px-3 py-2 last:border-b-0 hover:bg-zinc-50 dark:border-zinc-800 dark:hover:bg-zinc-800/60 ${h.key === key ? "bg-blue-50 dark:bg-blue-950/50" : ""}`}
              >
                <div className="font-semibold">{h.name}</div>
                <div className="mt-0.5 flex flex-wrap items-center gap-1.5 text-xs text-zinc-500">
                  <KindPill kind={h.kind} />
                  {h.repositoryId}
                  {h.hasCalculation && <Pill tone="info">calculation</Pill>}
                  {h.generated && <Pill tone="warn">generated</Pill>}
                </div>
              </a>
            ))}
          </div>
          {hits.loading && hits.items.length === 0 && <div role="status" className="p-3 text-xs text-zinc-500">Loading symbols…</div>}
          {hits.error && (
            <div role="alert" className="flex items-center justify-between gap-2 p-3 text-xs text-red-600">
              <span><strong>{hits.items.length === 0 ? "Could not load symbols." : "Could not load more symbols."}</strong> {hits.error}</span>
              <button type="button" className="rounded border border-red-300 px-2 py-1 text-[11px] font-semibold hover:bg-red-50 dark:border-red-700 dark:hover:bg-red-950/40" onClick={hits.retry}>Retry</button>
            </div>
          )}
          {!hits.loading && !hits.error && hits.items.length === 0 && <div className="p-6 text-center text-zinc-500">No symbols match.</div>}
          {hits.loadingMore && <div role="status" className="p-3 text-xs text-zinc-500">Loading more…</div>}
          {hits.hasMore && !hits.loadingMore && !hits.error && <div className="p-2 text-center"><button type="button" className="rounded border border-zinc-300 px-3 py-1.5 text-xs hover:bg-zinc-50 dark:border-zinc-700 dark:hover:bg-zinc-800" onClick={hits.loadMore}>Load more symbols</button></div>}
          {!hits.hasMore && !hits.loading && !hits.error && hits.items.length > 0 && <div className="p-3 text-xs text-zinc-500">{typeof hits.total === "number" ? `Loaded ${hits.items.length} of ${hits.total} symbols.` : `Loaded ${hits.items.length} symbols.`}</div>}
          <div ref={sentinelRef} aria-hidden="true" className="h-px" />
        </div>
      </aside>
      <div className="min-w-0">{key ? <Symbol symbolKey={key} /> : <Welcome onPick={(next) => { setQ(next.q); setKind(next.kind); }} />}</div>
    </div>
  );
}

type SearchPageResponse = SearchHit[] | { items: SearchHit[]; nextCursor?: string | null; total?: number; snapshotId?: string; generation?: number };
type SearchState = {
  identity: string;
  items: SearchHit[];
  nextCursor: string | null;
  total?: number;
  loading: boolean;
  loadingMore: boolean;
  error?: string;
  errorStatus?: number;
  failedCursor?: string | null;
};

function useSearchHits(query: string, kind: string, repo: string, scope: string, snapshotId: string, generation: number, reloadMeta: () => void) {
  const identity = JSON.stringify([query, kind, repo, scope, snapshotId, generation]);
  const [state, setState] = useState<SearchState>({ identity: "", items: [], nextCursor: null, loading: true, loadingMore: false });
  const generationRef = useRef(0);
  const inFlight = useRef<{ controller: AbortController; token: number; cursor: string | null } | null>(null);
  const snapshotRef = useRef(snapshotId);
  const active = state.identity === identity ? state : { identity, items: [], nextCursor: null, loading: true, loadingMore: false };

  const fetchPage = useCallback(async (cursor: string | null, token: number) => {
    if (token !== generationRef.current || inFlight.current) return;
    const controller = new AbortController();
    inFlight.current = { controller, token, cursor };
    setState((previous) => ({
      ...previous,
      identity,
      loading: cursor === null,
      loadingMore: cursor !== null,
      error: undefined,
      errorStatus: undefined,
    }));
    const params = new URLSearchParams(scope);
    params.set("q", query);
    params.set("kind", kind);
    params.set("repo", repo);
    params.set("limit", "50");
    params.set("snapshot", cursor === null ? snapshotId : snapshotRef.current);
    if (cursor) params.set("cursor", cursor);
    try {
      const response = await request<SearchPageResponse>(`/api/search?${params.toString()}`, { signal: controller.signal });
      if (token !== generationRef.current) return;
      const page = Array.isArray(response)
        ? { items: response, nextCursor: null as string | null, total: response.length, snapshotId }
        : { items: response.items, nextCursor: response.nextCursor ?? null, total: response.total, snapshotId: response.snapshotId ?? snapshotRef.current };
      if (!Array.isArray(response) && response.snapshotId && response.snapshotId !== snapshotRef.current) {
        throw new ApiError("The search snapshot changed. Restart the search to continue.", 409, response);
      }
      if (!Array.isArray(response) && response.generation !== undefined && response.generation !== generation) {
        throw new ApiError("The search generation changed. Restart the search to continue.", 409, response);
      }
      snapshotRef.current = page.snapshotId;
      setState((previous) => {
        const current = previous.identity === identity ? previous : { identity, items: [], nextCursor: null, loading: true, loadingMore: false };
        return {
          ...current,
          items: cursor === null ? page.items : [...current.items, ...page.items],
          nextCursor: page.nextCursor,
          total: page.total,
          loading: false,
          loadingMore: false,
          error: undefined,
          errorStatus: undefined,
          failedCursor: undefined,
        };
      });
    } catch (error) {
      if (controller.signal.aborted || token !== generationRef.current) return;
      const failure = error as Error;
      setState((previous) => ({
        ...(previous.identity === identity ? previous : { identity, items: [], nextCursor: null, loading: false, loadingMore: false }),
        loading: false,
        loadingMore: false,
        error: failure.message,
        errorStatus: failure instanceof ApiError ? failure.status : undefined,
        failedCursor: cursor,
      }));
    } finally {
      if (inFlight.current?.controller === controller) inFlight.current = null;
    }
  }, [generation, identity, kind, query, repo, scope, snapshotId]);

  useEffect(() => {
    const token = ++generationRef.current;
    inFlight.current?.controller.abort();
    inFlight.current = null;
    snapshotRef.current = snapshotId;
    setState({ identity, items: [], nextCursor: null, loading: true, loadingMore: false });
    void fetchPage(null, token);
    return () => {
      if (generationRef.current === token) {
        generationRef.current++;
        if (inFlight.current?.token === token) {
          inFlight.current.controller.abort();
          inFlight.current = null;
        }
      }
    };
  }, [fetchPage, identity, snapshotId]);

  const loadMore = useCallback(() => {
    if (!active.nextCursor || active.loading || active.loadingMore || active.error || inFlight.current) return;
    void fetchPage(active.nextCursor, generationRef.current);
  }, [active, fetchPage]);

  const retry = useCallback(() => {
    if (!active.error || active.loading || active.loadingMore || inFlight.current) return;
    if (active.errorStatus === 400 || active.errorStatus === 409) {
      reloadMeta();
      const token = ++generationRef.current;
      snapshotRef.current = snapshotId;
      setState({ identity, items: [], nextCursor: null, loading: true, loadingMore: false });
      void fetchPage(null, token);
      return;
    }
    void fetchPage(active.failedCursor ?? null, generationRef.current);
  }, [active, fetchPage, identity, reloadMeta, snapshotId]);

  return { ...active, hasMore: active.nextCursor !== null, loadMore, retry };
}

function Welcome({ onPick }: { onPick: (s: { q: string; kind: string }) => void }) {
  const starts = [
    { title: "Weekly averages", q: "average", kind: "", text: "Implementations of “sales per week” that differ in denominator, rounding and null handling." },
    { title: "Endpoints", q: "", kind: "Endpoint", text: "Every HTTP endpoint exposed in the selected revisions." },
    { title: "Channels", q: "", kind: "Channel", text: "Service Bus topics and queues, bound and unbound." },
  ];
  return (
    <Card>
      <H1>Explore the extracted model</H1>
      <Muted>Search for a symbol on the left. Every fact links to the exact source span at the revision it was observed in.</Muted>
      <div className="mt-4 grid gap-3 sm:grid-cols-3">
        {starts.map((s) => (
          <button key={s.title} type="button" onClick={() => onPick(s)} className="rounded-lg border border-zinc-200 p-3 text-left hover:border-blue-400 hover:bg-blue-50/40 dark:border-zinc-700 dark:hover:bg-blue-950/30">
            <div className="font-semibold">{s.title}</div>
            <div className="text-xs text-zinc-500">{s.text}</div>
          </button>
        ))}
      </div>
    </Card>
  );
}

const stateLabel: Record<string, string> = { present: "present", absent: "absent", notObserved: "not observed (partial analysis)", notAnalysed: "unit not analysed" };
const stateDot: Record<string, string> = { present: "bg-emerald-500", absent: "bg-red-500", notObserved: "bg-amber-500", notAnalysed: "bg-zinc-400" };

function Symbol({ symbolKey }: { symbolKey: string }) {
  const { meta, scope } = useSelection();
  const { data: d, error } = useApi<SymbolDetail>(`/api/symbol?key=${encodeURIComponent(symbolKey)}&${scope}`);
  usePublishFocus(d ? { kind: d.node.kind, label: d.node.name, key: symbolKey } : null);
  if (error)
    return (
      <Card>
        <p>{error}</p>
        <Key value={symbolKey} />
        <Muted className="mt-2 text-xs">It may exist in another revision or outside the selected capability: change the selection above.</Muted>
      </Card>
    );
  if (!d) return <Loading />;
  const n = d.node;
  return (
    <div className="space-y-3">
      <Card>
        <div className="flex flex-wrap items-baseline gap-2">
          <H1>{n.name}</H1>
          <KindPill kind={n.kind} />
          {n.generated && <Pill tone="warn" title="Generated code: not counted as an independent implementation">generated</Pill>}
        </div>
        <Key value={n.key} />
        <div className="mt-3">
          <Kv
            pairs={[
              ["Repository", `${n.repositoryId} @ ${revisionLabel(meta, n.repositoryId, n.commitSha)} (${shortSha(n.commitSha)})`],
              ["Unit", n.unit],
              ["Signature", n.signature ? <Mono>{n.signature}</Mono> : null],
            ]}
          />
        </div>
        {n.props && (
          <>
            <H3>Properties</H3>
            <PropsList props={n.props} />
          </>
        )}
        <H3>Across revisions</H3>
        <div className="flex flex-wrap gap-1.5">
          {d.history.map((h) => (
            <span key={h.commitSha} title={`${h.subject ?? ""} — ${h.state}`} className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs ${h.commitSha === n.commitSha ? "border-blue-500" : "border-zinc-200 dark:border-zinc-700"}`}>
              <i className={`inline-block size-2 rounded-full ${stateDot[h.state]}`} />
              {h.label}
              <span className="text-zinc-500">{stateLabel[h.state]}</span>
            </span>
          ))}
        </div>
        <H3>Evidence</H3>
        {n.evidence.map((e) => (
          <SourceView key={e.id} evidence={e} />
        ))}
      </Card>
      {d.calculation && <CalculationCard calculation={d.calculation} />}
      {meta.similarity.database && meta.similarity.embeddings && <Similar symbolKey={symbolKey} />}
      <EdgeTable title="Outgoing" edges={d.outgoing} side="to" />
      <EdgeTable title="Incoming" edges={d.incoming} side="from" />
    </div>
  );
}

/** Nearest symbols by embedding (pgvector), within the selected revisions. */
function Similar({ symbolKey }: { symbolKey: string }) {
  const { scope } = useSelection();
  const { data, error } = useApi<SimilarHit[]>(`/api/similar?key=${encodeURIComponent(symbolKey)}&k=8&${scope}`);
  return (
    <Card>
      <H2>Similar functionality</H2>
      {error && <Muted className="text-xs">{error}</Muted>}
      {!data && !error && <Muted>Loading…</Muted>}
      {data?.length === 0 && <Muted>Not embedded yet, or nothing similar in the selected revisions.</Muted>}
      {data && data.length > 0 && (
        <Table
          columns={["Symbol", "Repository", "Similarity"]}
          rows={data.map((h) => [
            <span className="inline-flex flex-wrap items-center gap-1.5">
              <a href={symbolHref(h.key)} className="font-medium">{h.name}</a>
              <KindPill kind={h.kind} />
              {h.hasCalculation && <Pill tone="info">calculation</Pill>}
            </span>,
            h.repositoryId,
            <Mono className="text-xs">{h.score.toFixed(2)}</Mono>,
          ])}
        />
      )}
    </Card>
  );
}

function EdgeTable({ title, edges, side }: { title: string; edges: Edge[]; side: "to" | "from" }) {
  const sorted = [...edges].sort((a, b) => a.relation.localeCompare(b.relation));
  return (
    <Card>
      <H2>
        {title} ({edges.length})
      </H2>
      <Table
        columns={["Relation", side === "to" ? "Target" : "Source", "Resolution", "Derived by", "", "Evidence"]}
        rows={sorted.map((e) => [
          <Mono className="text-xs">{e.relation}</Mono>,
          side === "to" ? (e.to ? <SymbolLink symbol={e.to} /> : <span className="text-zinc-500">unknown target</span>) : <SymbolLink symbol={e.from} />,
          <ResolutionPill resolution={e.resolution} reason={e.unresolvedReason} />,
          <span className="text-xs">
            {e.derivation}
            {e.rule && <span className="text-zinc-500"> · {e.rule}</span>}
          </span>,
          e.props ? (
            <details>
              <summary className="cursor-pointer text-xs text-zinc-500">props</summary>
              <PropsList props={e.props} />
            </details>
          ) : null,
          <EvidenceLinks evidence={e.evidence} />,
        ])}
      />
    </Card>
  );
}

export function AttributeValue({ attribute }: { attribute?: Calculation["attributes"][string] }) {
  if (!attribute) return <span className="text-zinc-400">—</span>;
  if (attribute.status === "unknown")
    return (
      <span className="italic text-zinc-500" title="No evidence in the code; not inferred from names">
        unknown
      </span>
    );
  return (
    <span title={attribute.detail ? JSON.stringify(attribute.detail) : attribute.status} className={attribute.status === "partial" ? "rounded border border-dashed border-zinc-300 px-1 dark:border-zinc-600" : ""}>
      {jsonText(attribute.value ?? attribute.status)}
    </span>
  );
}

export function CalculationCard({ calculation: c }: { calculation: Calculation }) {
  return (
    <Card>
      <div className="flex items-center gap-2">
        <H2 className="mb-0">Calculation</H2>
        {c.irHash && (
          <Mono className="ml-auto text-xs text-zinc-500" >
            <span title="sha256 of the normalised expression">irHash {c.irHash.slice(0, 12)}</span>
          </Mono>
        )}
      </div>
      <H3>Normalised expression</H3>
      <Formula expr={c.expr} inputs={c.inputs} />
      {c.includes.length > 0 && <Muted className="mt-1 text-xs">Includes helpers: {c.includes.map(shortTarget).join(", ")}</Muted>}
      <H3>Attributes</H3>
      <Table
        columns={["Attribute", "Value", "Detail", "Evidence"]}
        rows={ATTRIBUTES.filter((a) => c.attributes[a]).map((a) => {
          const attr = c.attributes[a];
          return [
            a,
            <span className="inline-flex items-center gap-1.5">
              <AttributeValue attribute={attr} />
              {attr.status === "partial" && <Pill tone="warn">partial</Pill>}
            </span>,
            attr.detail ? (
              <span className="text-xs text-zinc-500">
                {Object.entries(attr.detail)
                  .map(([k, v]) => `${k}: ${typeof v === "string" && v.includes("|") ? shortTarget(v) : jsonText(v)}`)
                  .join(" · ")}
              </span>
            ) : null,
            <EvidenceLinks evidence={c.attributeEvidence[a]} />,
          ];
        })}
      />
    </Card>
  );
}
