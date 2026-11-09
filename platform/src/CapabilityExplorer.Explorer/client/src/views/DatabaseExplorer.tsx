import { lazy, Suspense, useEffect, useRef, useState } from "react";
import { useApi } from "../state";
import { Card, ErrorBox, H1, Loading, Muted, Pill } from "../components/ui";
import { databaseUrl, useDatabaseInfinitePage, useDatabasePage, useDebouncedText, type DatabaseMeta, type DatabaseHit } from "../database-api";
import { DatabaseInspector } from "./DatabaseInspector";
import { DatabasePager } from "../components/DatabasePager";
import { EmbeddingStatusPanel } from "../components/embedding-status";
import { href, useRoute } from "../state";

const ProcessesDecisions = lazy(() => import("./ProcessesDecisions").then((module) => ({ default: module.ProcessesDecisions })));
const OwnershipAlignment = lazy(() => import("./OwnershipAlignment").then((module) => ({ default: module.OwnershipAlignment })));
type Tab = "explore" | "processes-decisions" | "ownership-alignment";
const button = "rounded border border-zinc-300 px-3 py-1.5 text-sm disabled:cursor-default disabled:opacity-40 dark:border-zinc-700 hover:bg-zinc-100 dark:hover:bg-zinc-800";
const input = "rounded border border-zinc-300 bg-white px-3 py-2 dark:border-zinc-700 dark:bg-zinc-900";

export function DatabaseExplorer({ meta, reload }: { meta: DatabaseMeta; reload: () => void }) {
  const route = useRoute();
  const [capability, setCapability] = useState("*");
  const scoped = useApi<DatabaseMeta>(capability === "*" ? null : `/api/meta?cap=${encodeURIComponent(capability)}`);
  const effective = capability === "*" ? meta : scoped.data?.capability === capability ? scoped.data : undefined;
  const loadLatest = () => { reload(); if (capability !== "*") scoped.reload(); };
  return <>
    <header className="flex flex-wrap items-center gap-4 border-b border-zinc-200 bg-white px-5 py-3 dark:border-zinc-800 dark:bg-zinc-900">
      <span className="text-lg font-bold">Capability Explorer</span><Pill tone="info">PostgreSQL</Pill>
      <label className="flex items-center gap-2">Capability
        <select aria-label="Capability" className={input} value={capability} onChange={(event) => setCapability(event.target.value)}>
          <option value="*">All repositories</option>
          {meta.capabilities.map((cap) => <option key={cap.id} value={cap.id}>{cap.name}</option>)}
        </select>
      </label>
      <button type="button" className={`${button} sm:ml-auto`} onClick={loadLatest}>Load latest snapshot</button>
    </header>
    <main className="w-full min-w-0 space-y-4 p-3 sm:p-5">
      {scoped.error && capability !== "*" && <ErrorBox message={scoped.error} />}
      {!effective ? <Loading what="Loading published snapshot" /> : <DatabaseWorkspace key={effective.snapshotId} meta={effective} route={route} loadLatest={loadLatest} />}
    </main>
  </>;
}

function DatabaseWorkspace({ meta, route, loadLatest }: { meta: DatabaseMeta; route: { path: string; params: URLSearchParams }; loadLatest: () => void }) {
  const validTabs: Tab[] = ["explore", "processes-decisions", "ownership-alignment"];
  const tab: Tab = validTabs.includes(route.path as Tab) ? route.path as Tab : "explore";
  const [query, setQuery] = useState("");
  const settled = useDebouncedText(query);
  const [kind, setKind] = useState("");
  const [repository, setRepository] = useState("");
  const [key, setKey] = useState<string | null>(null);
  const [overrides, setOverrides] = useState<Record<string, string>>({});
  const selection = { ...meta.selection, ...overrides };
  const scope = new URLSearchParams({ cap: meta.capability, snapshot: meta.snapshotId,
    view: Object.entries(overrides).map(([repo, sha]) => `${repo}:${sha}`).join(",") }).toString();
  const resetSelection = (next: Record<string, string>) => { setOverrides(Object.fromEntries(Object.entries(next).filter(([repo, sha]) => meta.selection[repo] !== sha))); setKey(null); };
  return <>
    <div className="flex flex-wrap items-center gap-3 text-xs text-zinc-500 dark:text-zinc-400">
      <span>Snapshot <span className="font-mono">{meta.snapshotId.slice(0, 12)}</span> · generation {meta.generation}</span>
      <span>Pages stay on this immutable snapshot until you load the latest.</span>
      <details className="w-full"><summary className="cursor-pointer">Selected revisions ({Object.keys(selection).length})</summary>
        <div className="mt-2 flex flex-wrap gap-2">{Object.entries(selection).map(([repo, sha]) => <span key={repo} className="rounded border border-zinc-200 px-2 py-1 dark:border-zinc-700">{repo} · {sha.slice(0, 12)}</span>)}</div>
        <RevisionChooser scope={scope} selection={selection} change={resetSelection} />
      </details>
    </div>
    <EmbeddingStatusPanel scope={scope} meta={meta} selection={selection} />
    <Card><h2 className="font-semibold">Migration status</h2>
      <Muted>Available: bounded symbol search, symbol and evidence detail, adjacency, calculations and artifact metadata.</Muted>
      <p className="mt-2 text-sm text-amber-700 dark:text-amber-300">Architecture, integrations, glossary, changes and binding review are not silently reconstructed from JSONL.</p>
    </Card>
    <nav aria-label="Database views" className="flex flex-wrap gap-2">{validTabs.map((id) =>
      <a key={id} href={href(id, Object.fromEntries(route.params))} aria-current={tab === id ? "page" : undefined} className={`${button} ${tab === id ? "bg-blue-50 font-semibold text-blue-700 dark:bg-blue-950 dark:text-blue-300" : ""}`}>{id === "processes-decisions" ? "Processes & decisions" : id === "ownership-alignment" ? "Ownership alignment" : "Explore"}</a>)}</nav>
    {tab === "processes-decisions" && <Suspense fallback={<Loading what="model view" />}><ProcessesDecisions key={scope} scope={`${scope}&g=${meta.generation}`} params={route.params} mode="database" /></Suspense>}
    {tab === "ownership-alignment" && <Suspense fallback={<Loading what="ownership report" />}><OwnershipAlignment /></Suspense>}
    {tab === "explore" && <div className={`grid min-w-0 gap-4 ${key ? "xl:grid-cols-[minmax(0,1fr)_minmax(340px,40%)]" : "grid-cols-1"}`}>
      <section className="min-w-0 space-y-3">
        <H1>Explore symbols</H1>
        {tab === "explore" && <div className="flex flex-wrap gap-2">
          <input aria-label="Search symbols" placeholder="Search names or symbol keys" className={`${input} min-w-48 flex-1`} value={query} maxLength={500} onChange={(event) => setQuery(event.target.value)} />
          <input aria-label="Node kind" placeholder="Kind (optional)" className={input} value={kind} onChange={(event) => setKind(event.target.value)} />
          <select aria-label="Repository" className={input} value={repository} onChange={(event) => setRepository(event.target.value)}><option value="">All selected repositories</option>{Object.keys(selection).map((repo) => <option key={repo}>{repo}</option>)}</select>
        </div>}
        {tab === "explore" && <SymbolPage key={`${scope}|${meta.generation}|${settled}|${kind}|${repository}`} scope={scope} meta={meta} loadLatest={loadLatest} query={settled} kind={kind} repository={repository} select={setKey} />}
      </section>
      {key && <DatabaseInspector key={`${scope}|${key}`} scope={scope} symbolKey={key} close={() => setKey(null)} />}
    </div>}
  </>;
}


function SymbolPage({ scope, meta, loadLatest, query, kind, repository, select }: { scope: string; meta: DatabaseMeta; loadLatest: () => void; query: string; kind: string; repository: string; select: (key: string) => void }) {
  const page = useDatabaseInfinitePage<DatabaseHit>(databaseUrl("search", scope, { q: query, kind, repo: repository }), meta, loadLatest);
  const listRef = useRef<HTMLDivElement | null>(null);
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const root = listRef.current;
    const target = sentinelRef.current;
    if (!root || !target || !page.hasMore || page.error) return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) page.loadMore();
    }, { root, rootMargin: "0px 0px 120px 0px", threshold: 0 });
    observer.observe(target);
    return () => observer.disconnect();
  }, [page.error, page.hasMore, page.loadMore]);
  return <div ref={listRef} role="region" aria-label="Symbol results" tabIndex={0} data-testid="symbol-results" className="h-[calc(100dvh-300px)] min-h-0 min-w-0 flex-shrink overflow-y-auto rounded-lg border border-zinc-200 dark:border-zinc-800">
    <div role="list" aria-busy={page.loading || page.loadingMore}>
      {page.data?.items.map((hit) => <button type="button" role="listitem" data-symbol-key={hit.key} disabled={page.loading} key={hit.key} onClick={() => select(hit.key)} className="block w-full border-b border-zinc-200 bg-white p-3 text-left last:border-b-0 hover:border-blue-400 dark:border-zinc-800 dark:bg-zinc-900">
        <div className="flex flex-wrap items-center gap-2"><span className="font-semibold">{hit.name}</span><Pill>{hit.kind}</Pill>{hit.hasCalculation && <Pill tone="info">Calculation</Pill>}</div>
        <div className="mt-1 text-xs text-zinc-500">{hit.repositoryId} · {hit.unit}</div><div className="mt-1 break-all font-mono text-xs text-zinc-500">{hit.key}</div>
      </button>)}
    </div>
    {page.loading && !page.data && <div role="status" className="p-3 text-xs text-zinc-500">Loading symbols…</div>}
    {page.error && <div className="flex items-center justify-between gap-2 p-3 text-xs text-red-600" role="alert">
      <span><strong>{page.data?.items.length ? "Could not load more symbols." : "Could not load symbols."}</strong> {page.error}</span><button type="button" className={button} onClick={page.retry}>Retry</button>
    </div>}
    {!page.loading && !page.error && !page.data?.items.length && <Muted className="p-6 text-center">No matching symbols in this selection.</Muted>}
    {page.loadingMore && <div role="status" className="p-3 text-center text-xs text-zinc-500">Loading more symbols…</div>}
    {page.hasMore && !page.loadingMore && !page.error && <div className="p-2 text-center"><button type="button" className={button} onClick={page.loadMore}>Load more symbols</button></div>}
    {!page.hasMore && !page.loading && !page.error && page.data?.items.length ? <div role="status" aria-live="polite" className="p-3 text-xs text-zinc-500">Loaded {page.data.items.length} symbols.</div> : null}
    <div ref={sentinelRef} aria-hidden="true" className="h-px" />
  </div>;
}
function RevisionChooser({ scope, selection, change }: { scope: string; selection: Record<string, string>; change: (selection: Record<string, string>) => void }) {
  const page = useDatabasePage<{ repositoryId: string; commitSha: string }>(databaseUrl("revisions", scope));
  return <div className="mt-3 space-y-2"><p>Available revisions in this snapshot:</p><DatabasePager page={page} /><div className="flex flex-wrap gap-2">{page.data?.items.map((revision) => <button type="button" className={button} key={`${revision.repositoryId}/${revision.commitSha}`} disabled={page.loading || selection[revision.repositoryId] === revision.commitSha} onClick={() => change({ ...selection, [revision.repositoryId]: revision.commitSha })}>{revision.repositoryId} · {revision.commitSha.slice(0, 12)}</button>)}</div></div>;
}
