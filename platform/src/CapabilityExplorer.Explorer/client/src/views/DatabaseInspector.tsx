import { useState } from "react";
import { Card, ErrorBox, H2, Loading, Muted, Pill } from "../components/ui";
import { databaseUrl, useDatabaseItem, useDatabasePage, type DatabaseCalculation, type DatabaseEdge, type DatabaseEvidence, type DatabaseNode } from "../database-api";
import { DatabasePager } from "../components/DatabasePager";

export function DatabaseInspector({ scope, symbolKey, close }: { scope: string; symbolKey: string; close: () => void }) {
  const node = useDatabaseItem<DatabaseNode>(databaseUrl("symbol", scope, { key: symbolKey }));
  const [calculation, setCalculation] = useState(false);
  return <aside className="min-w-0 space-y-3" aria-label="Symbol detail">
    <Card><button type="button" className="float-right px-2 text-blue-600 dark:text-blue-300" onClick={close}>Close</button>
      {node.error && <ErrorBox message={node.error} />}
      {!node.data ? <Loading what="Loading symbol" /> : <><H2>{node.data.name}</H2><Pill>{node.data.kind}</Pill><p className="mt-2 break-all font-mono text-xs">{node.data.signature ?? node.data.key}</p><Muted>{node.data.repositoryId} · {node.data.unit} · {node.data.commitSha.slice(0, 12)}</Muted></>}
      <p className="mt-3 text-xs text-zinc-500">Evidence and connections are fetched in separate pages of at most 50 rows.</p>
    </Card>
    <EvidencePage scope={scope} symbolKey={symbolKey} />
    <EdgePage scope={scope} symbolKey={symbolKey} direction="outgoing" />
    <EdgePage scope={scope} symbolKey={symbolKey} direction="incoming" />
    <Card><button type="button" className="text-blue-700 dark:text-blue-300" onClick={() => setCalculation((open) => !open)} aria-expanded={calculation}>{calculation ? "Hide" : "Open"} calculation body</button>
      {calculation && <CalculationBody scope={scope} symbolKey={symbolKey} />}
    </Card>
  </aside>;
}
function EvidencePage({ scope, symbolKey }: { scope: string; symbolKey: string }) {
  const [selected, setSelected] = useState<DatabaseEvidence | null>(null);
  const page = useDatabasePage<DatabaseEvidence>(databaseUrl("symbol/evidence", scope, { key: symbolKey }));
  return <Card><H2>Evidence</H2><DatabasePager page={page} /><ul className="mt-2 space-y-2">{page.data?.items.map((row) => <li key={`${row.repositoryId}/${row.commitSha}/${row.path}/${row.id}`} className="break-all text-xs"><button type="button" disabled={page.loading} className="font-mono text-left text-blue-700 dark:text-blue-300" onClick={() => setSelected(row)}>{row.path}:{row.startLine}:{row.startColumn}–{row.endLine}:{row.endColumn}</button><div className="text-zinc-500">{row.repositoryId} · {row.commitSha.slice(0, 12)}</div></li>)}</ul>{selected && <SourceExcerpt key={`${selected.path}/${selected.startLine}`} scope={scope} evidence={selected} />}</Card>;
}
function SourceExcerpt({ scope, evidence }: { scope: string; evidence: DatabaseEvidence }) {
  const source = useDatabaseItem<{ lines: { n: number; text: string }[] }>(databaseUrl("source", scope, { repo: evidence.repositoryId, commit: evidence.commitSha, path: evidence.path, start: String(evidence.startLine), end: String(Math.min(evidence.endLine, evidence.startLine + 80)) }));
  if (source.error) return <ErrorBox message={source.error} />;
  if (!source.data) return <Loading what="Loading cited source" />;
  return <pre className="mt-3 max-h-80 overflow-auto bg-zinc-100 p-2 text-xs dark:bg-zinc-950">{source.data.lines.map((line) => `${line.n}  ${line.text}`).join("\n")}</pre>;
}
function EdgePage({ scope, symbolKey, direction }: { scope: string; symbolKey: string; direction: string }) {
  const page = useDatabasePage<DatabaseEdge>(databaseUrl("symbol/edges", scope, { key: symbolKey, direction }));
  return <Card><H2>{direction === "outgoing" ? "Outgoing" : "Incoming"} connections</H2><DatabasePager page={page} /><ul className="mt-2 space-y-2">{page.data?.items.map((edge) => <li key={`${edge.artifactId}/${edge.localId}`} className="rounded border border-zinc-200 p-2 dark:border-zinc-800"><div className="flex gap-2"><Pill>{edge.relation}</Pill><span className="text-xs text-zinc-500">{edge.resolution} · {edge.derivation}</span></div><p className="mt-1 break-all font-mono text-xs">{direction === "outgoing" ? edge.to ?? "Unknown target" : edge.from}</p></li>)}</ul></Card>;
}
function CalculationBody({ scope, symbolKey }: { scope: string; symbolKey: string }) {
  const detail = useDatabaseItem<{ metadata: DatabaseCalculation; body: unknown }>(databaseUrl("calculation", scope, { key: symbolKey }));
  if (detail.error) return <ErrorBox message={detail.error} />;
  if (!detail.data) return <Loading what="Loading calculation" />;
  return <pre className="mt-3 max-h-[60vh] overflow-auto whitespace-pre-wrap break-all rounded bg-zinc-100 p-3 text-xs dark:bg-zinc-950">{JSON.stringify(detail.data.body, null, 2)}</pre>;
}
