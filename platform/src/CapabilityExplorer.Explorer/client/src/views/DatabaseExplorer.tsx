import { useState } from "react";
import { useApi } from "../state";
import { Card, ErrorBox, H1, Loading, Muted, Pill } from "../components/ui";
import { databaseUrl, useDatabasePage, useDebouncedText, type DatabaseMeta, type DatabaseHit, type DatabaseArtifact, type DatabaseCalculation } from "../database-api";
import { DatabaseInspector } from "./DatabaseInspector";
import { DatabasePager } from "../components/DatabasePager";

type Tab = "explore" | "calculations" | "artifacts";
const button = "rounded border border-zinc-300 px-3 py-1.5 text-sm disabled:cursor-default disabled:opacity-40 dark:border-zinc-700 hover:bg-zinc-100 dark:hover:bg-zinc-800";
const input = "rounded border border-zinc-300 bg-white px-3 py-2 dark:border-zinc-700 dark:bg-zinc-900";

export function DatabaseExplorer({ meta, reload }: { meta: DatabaseMeta; reload: () => void }) {
  const [capability, setCapability] = useState("*");
  const scoped = useApi<DatabaseMeta>(capability === "*" ? null : `/api/meta?cap=${encodeURIComponent(capability)}`);
  const effective = capability === "*" ? meta : scoped.data?.capability === capability ? scoped.data : undefined;
  return <>
    <header className="flex flex-wrap items-center gap-4 border-b border-zinc-200 bg-white px-5 py-3 dark:border-zinc-800 dark:bg-zinc-900">
      <span className="text-lg font-bold">Capability Explorer</span><Pill tone="info">PostgreSQL</Pill>
      <label className="flex items-center gap-2">Capability
        <select aria-label="Capability" className={input} value={capability} onChange={(event) => setCapability(event.target.value)}>
          <option value="*">All repositories</option>
          {meta.capabilities.map((cap) => <option key={cap.id} value={cap.id}>{cap.name}</option>)}
        </select>
      </label>
      <button type="button" className={`${button} sm:ml-auto`} onClick={() => { reload(); if (capability !== "*") scoped.reload(); }}>Load latest snapshot</button>
    </header>
    <main className="w-full min-w-0 space-y-4 p-3 sm:p-5">
      {scoped.error && capability !== "*" && <ErrorBox message={scoped.error} />}
      {!effective ? <Loading what="Loading published snapshot" /> : <DatabaseWorkspace key={effective.snapshotId} meta={effective} />}
    </main>
  </>;
}

function DatabaseWorkspace({ meta }: { meta: DatabaseMeta }) {
  const [tab, setTab] = useState<Tab>("explore");
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
    <nav aria-label="Database views" className="flex gap-2">{(["explore", "calculations", "artifacts"] as Tab[]).map((id) =>
      <button key={id} type="button" aria-current={tab === id ? "page" : undefined} className={`${button} ${tab === id ? "bg-blue-50 font-semibold text-blue-700 dark:bg-blue-950 dark:text-blue-300" : ""}`} onClick={() => { setTab(id); setKey(null); }}>{id[0].toUpperCase() + id.slice(1)}</button>)}</nav>
    <div className={`grid min-w-0 gap-4 ${key ? "xl:grid-cols-[minmax(0,1fr)_minmax(340px,40%)]" : "grid-cols-1"}`}>
      <section className="min-w-0 space-y-3">
        <H1>{tab === "explore" ? "Explore symbols" : tab === "calculations" ? "Calculations" : "Artifacts"}</H1>
        {tab === "explore" && <div className="flex flex-wrap gap-2">
          <input aria-label="Search symbols" placeholder="Search names or symbol keys" className={`${input} min-w-48 flex-1`} value={query} maxLength={500} onChange={(event) => setQuery(event.target.value)} />
          <input aria-label="Node kind" placeholder="Kind (optional)" className={input} value={kind} onChange={(event) => setKind(event.target.value)} />
          <select aria-label="Repository" className={input} value={repository} onChange={(event) => setRepository(event.target.value)}><option value="">All selected repositories</option>{Object.keys(selection).map((repo) => <option key={repo}>{repo}</option>)}</select>
        </div>}
        {tab === "explore" && <SymbolPage key={`${scope}|${settled}|${kind}|${repository}`} scope={scope} query={settled} kind={kind} repository={repository} select={setKey} />}
        {tab === "calculations" && <CalculationPage key={scope} scope={scope} select={setKey} />}
        {tab === "artifacts" && <ArtifactPage key={scope} scope={scope} />}
      </section>
      {key && <DatabaseInspector key={`${scope}|${key}`} scope={scope} symbolKey={key} close={() => setKey(null)} />}
    </div>
    <Card><h2 className="font-semibold">Migration status</h2>
      <Muted>Available: bounded symbol search, symbol and evidence detail, adjacency, calculations and artifact metadata.</Muted>
      <p className="mt-2 text-sm text-amber-700 dark:text-amber-300">Architecture, integrations, glossary, changes, binding review and model features are not yet available in PostgreSQL mode. They are not silently reconstructed from JSONL.</p>
    </Card>
  </>;
}


function SymbolPage({ scope, query, kind, repository, select }: { scope: string; query: string; kind: string; repository: string; select: (key: string) => void }) {
  const page = useDatabasePage<DatabaseHit>(databaseUrl("search", scope, { q: query, kind, repo: repository }));
  return <><DatabasePager page={page} /><div className="space-y-2" aria-busy={page.loading}>
    {page.data?.items.map((hit) => <button type="button" disabled={page.loading} key={hit.key} onClick={() => select(hit.key)} className="block w-full rounded border border-zinc-200 bg-white p-3 text-left hover:border-blue-400 dark:border-zinc-800 dark:bg-zinc-900">
      <div className="flex flex-wrap items-center gap-2"><span className="font-semibold">{hit.name}</span><Pill>{hit.kind}</Pill>{hit.hasCalculation && <Pill tone="info">Calculation</Pill>}</div>
      <div className="mt-1 text-xs text-zinc-500">{hit.repositoryId} · {hit.unit}</div><div className="mt-1 break-all font-mono text-xs text-zinc-500">{hit.key}</div>
    </button>)}
    {!page.loading && !page.data?.items.length && <Muted>No matching symbols in this selection.</Muted>}
  </div></>;
}
function CalculationPage({ scope, select }: { scope: string; select: (key: string) => void }) {
  const page = useDatabasePage<DatabaseCalculation>(databaseUrl("calculations", scope));
  return <><DatabasePager page={page} /><div className="space-y-2">{page.data?.items.map((row) => <button type="button" key={`${row.artifactId}/${row.localId}`} disabled={page.loading} className="block w-full rounded border border-zinc-200 p-3 text-left dark:border-zinc-800" onClick={() => select(row.key)}><span className="font-semibold">{row.name}</span><div className="text-xs text-zinc-500">{row.repositoryId} · {row.unit} · {row.language}</div></button>)}</div></>;
}
function ArtifactPage({ scope }: { scope: string }) {
  const page = useDatabasePage<DatabaseArtifact>(databaseUrl("artifacts", scope));
  return <><DatabasePager page={page} /><div className="space-y-2">{page.data?.items.map((row) => <Card key={row.id}><div className="flex flex-wrap gap-2"><span className="font-semibold">{row.repositoryId} · {row.unit}</span><Pill tone={row.status === "full" ? "ok" : "warn"}>{row.status}</Pill></div><div className="break-all text-xs text-zinc-500">{row.unitPath} · {row.language} · {row.commitSha}</div></Card>)}</div></>;
}
function RevisionChooser({ scope, selection, change }: { scope: string; selection: Record<string, string>; change: (selection: Record<string, string>) => void }) {
  const page = useDatabasePage<{ repositoryId: string; commitSha: string }>(databaseUrl("revisions", scope));
  return <div className="mt-3 space-y-2"><p>Available revisions in this snapshot:</p><DatabasePager page={page} /><div className="flex flex-wrap gap-2">{page.data?.items.map((revision) => <button type="button" className={button} key={`${revision.repositoryId}/${revision.commitSha}`} disabled={page.loading || selection[revision.repositoryId] === revision.commitSha} onClick={() => change({ ...selection, [revision.repositoryId]: revision.commitSha })}>{revision.repositoryId} · {revision.commitSha.slice(0, 12)}</button>)}</div></div>;
}
