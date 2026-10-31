import type { ChangeReport, NodeDto } from "../api";
import { KindPill, StatusPill } from "../components/symbols";
import { Card, ErrorBox, H1, H2, H3, inputClass, Key, Loading, Mono, Muted, Note, Pill, Table } from "../components/ui";
import { href, symbolHref, useApi, useSelection } from "../state";

export function Changes({ params }: { params: URLSearchParams }) {
  const { meta, view } = useSelection();
  const repos = Object.keys(view).sort();
  const revisionsOf = (r: string) => meta.revisions.filter((x) => x.repositoryId === r);
  const repo = params.get("repo") ?? repos.find((r) => revisionsOf(r).length > 1) ?? repos[0];
  const revs = repo ? revisionsOf(repo) : [];
  const toSha = params.get("to") ?? view[repo ?? ""] ?? revs.at(-1)?.commitSha;
  const toIndex = Math.max(0, revs.findIndex((r) => r.commitSha === toSha));
  const fromSha = params.get("from") ?? revs[Math.max(0, toIndex - 1)]?.commitSha;
  const go = (r: string, f?: string, t?: string) => (window.location.hash = href("changes", { repo: r, from: f, to: t }));
  const ready = repo && fromSha && toSha && fromSha !== toSha;
  const { data: d, error } = useApi<ChangeReport>(ready ? `/api/changes?repo=${encodeURIComponent(repo)}&from=${fromSha}&to=${toSha}&g=${meta.generation}` : null);

  if (!repo) return <Muted>No repositories in scope.</Muted>;
  return (
    <div>
      <H1>Changes between revisions</H1>
      <Card className="my-3 flex flex-wrap items-center gap-3">
        <label className="flex items-center gap-2">
          Repository
          <select
            className={`${inputClass} w-auto`}
            value={repo}
            onChange={(e) => {
              const rv = revisionsOf(e.target.value);
              go(e.target.value, rv[Math.max(0, rv.length - 2)]?.commitSha, rv.at(-1)?.commitSha);
            }}
          >
            {repos.map((r) => (
              <option key={r}>{r}</option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2">
          from
          <select className={`${inputClass} w-auto`} value={fromSha} onChange={(e) => go(repo, e.target.value, toSha)}>
            {revs.map((r) => (
              <option key={r.commitSha} value={r.commitSha}>
                {r.label}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2">
          to
          <select className={`${inputClass} w-auto`} value={toSha} onChange={(e) => go(repo, fromSha, e.target.value)}>
            {revs.map((r) => (
              <option key={r.commitSha} value={r.commitSha}>
                {r.label}
              </option>
            ))}
          </select>
        </label>
      </Card>
      {!ready && <Card className="text-zinc-500">{revs.length > 1 ? "Pick two different revisions." : "Only one revision of this repository is loaded."}</Card>}
      {ready && error && <ErrorBox message={error} />}
      {ready && !d && !error && <Loading />}
      {d && (
        <div className="space-y-3">
          <Muted>
            {d.from.label} “{d.from.subject}” → {d.to.label} “{d.to.subject}”. Continuity is a candidate with reasons; symbols missing from a partial analysis are “not observed”, never removed.
          </Muted>
          {d.addedUnits.length > 0 && <Note>New units: {d.addedUnits.join(", ")}</Note>}
          {d.removedUnits.length > 0 && <Note>Units not present in {d.to.label}: {d.removedUnits.join(", ")}</Note>}
          {d.units.map((u) => (
            <UnitCard key={u.unitPath} unit={u} />
          ))}
        </div>
      )}
    </div>
  );
}

function NodeList({ nodes, tone }: { nodes: NodeDto[]; tone: string }) {
  return (
    <div className="space-y-1">
      {nodes.map((n) => (
        <div key={n.key}>
          <KindPill kind={n.kind} /> <span className={tone}>{n.name}</span>
          <Key value={n.key} />
        </div>
      ))}
    </div>
  );
}

function UnitCard({ unit: u }: { unit: ChangeReport["units"][number] }) {
  const nothing = !u.added.length && !u.removed.length && !u.notObserved.length && !u.continuity.length && !u.calculations.length && !u.edges.length;
  return (
    <Card>
      <div className="flex flex-wrap items-center gap-2">
        <H2 className="mb-0">{u.unit}</H2>
        <Mono className="text-xs text-zinc-500">{u.unitPath}</Mono>
        <span className="ml-auto flex items-center gap-1.5">
          {u.inputUnchanged && (
            <Pill tone="ok" title="Same inputHash: the unit's own files and dependency surfaces did not change">
              inputs unchanged
            </Pill>
          )}
          <StatusPill status={u.fromStatus} /> → <StatusPill status={u.toStatus} />
        </span>
      </div>
      {nothing && <Muted className="mt-2">No changes in this unit.</Muted>}
      {u.toStatus === "partial" && <Note>The newer analysis is partial: missing symbols are listed as not observed instead of removed.</Note>}
      {u.continuity.length > 0 && (
        <>
          <H3>Continuity candidates (rename / move)</H3>
          <Table
            columns={["Before", "After", "Reasons"]}
            rows={u.continuity.map((c) => [
              <div>
                <span className="text-red-700 line-through dark:text-red-300">{c.from.name}</span>
                <Key value={c.from.key} />
              </div>,
              <div>
                <a href={symbolHref(c.to.key)} className="text-emerald-700 hover:underline dark:text-emerald-300">
                  {c.to.name}
                </a>
                <Key value={c.to.key} />
              </div>,
              <div className="space-y-0.5 text-xs">
                {c.reasons.map((r) => (
                  <div key={r}>• {r}</div>
                ))}
                {c.ambiguous && <Pill tone="warn">ambiguous</Pill>}
              </div>,
            ])}
          />
        </>
      )}
      {u.calculations.length > 0 && (
        <>
          <H3>Calculations</H3>
          <div className="space-y-2">
            {u.calculations.map((c) => (
              <div key={c.toKey} className="rounded-lg bg-zinc-50 p-3 dark:bg-zinc-800/50">
                <div className="mb-2 flex items-center gap-2">
                  <a href={symbolHref(c.toKey)} className="font-semibold text-blue-700 hover:underline dark:text-blue-300">
                    {c.name}
                  </a>
                  {c.logicChanged ? (
                    <Pill tone="bad" title="Normalised expression differs">
                      logic changed
                    </Pill>
                  ) : (
                    <Pill tone="ok">same logic</Pill>
                  )}
                </div>
                {c.attributes.length ? (
                  <Table
                    columns={["Attribute", "Before", "After"]}
                    rows={c.attributes.map((a) => [a.name, <span className="text-xs text-red-700 line-through dark:text-red-300">{a.from ?? "—"}</span>, <span className="text-xs text-emerald-700 dark:text-emerald-300">{a.to ?? "—"}</span>])}
                  />
                ) : (
                  <Muted className="text-xs">No attribute changed.</Muted>
                )}
                {c.possibleConsumers.length > 0 && (
                  <p className="mt-2 text-xs">
                    <span className="text-zinc-500">Possible consumers (static paths, not runtime proof): </span>
                    {c.possibleConsumers.map((r, i) => (
                      <span key={r.key}>
                        {i > 0 && ", "}
                        {r.declared ? (
                          <a href={symbolHref(r.key)} className="text-blue-700 hover:underline dark:text-blue-300">
                            {r.name}
                          </a>
                        ) : (
                          r.name
                        )}
                      </span>
                    ))}
                  </p>
                )}
              </div>
            ))}
          </div>
        </>
      )}
      {u.added.length > 0 && (
        <>
          <H3>Added ({u.added.length})</H3>
          <NodeList nodes={u.added} tone="text-emerald-700 dark:text-emerald-300" />
        </>
      )}
      {u.removed.length > 0 && (
        <>
          <H3>Removed ({u.removed.length})</H3>
          <NodeList nodes={u.removed} tone="text-red-700 line-through dark:text-red-300" />
        </>
      )}
      {u.notObserved.length > 0 && (
        <>
          <H3>Not observed ({u.notObserved.length})</H3>
          <NodeList nodes={u.notObserved} tone="text-zinc-500" />
        </>
      )}
      {u.edges.length > 0 && (
        <details className="mt-3">
          <summary className="cursor-pointer text-xs">{u.edges.length} edge change(s) after continuity mapping</summary>
          <div className="mt-2">
            <Table
              columns={["Change", "Relation", "From", "To"]}
              rows={u.edges.map((e) => [
                <Pill tone={e.change === "added" ? "ok" : e.change === "removed" ? "bad" : "warn"}>{e.change}</Pill>,
                <Mono className="text-xs">{e.relation}</Mono>,
                <Key value={e.from} />,
                <Key value={e.to ?? "—"} />,
              ])}
            />
          </div>
        </details>
      )}
    </Card>
  );
}
