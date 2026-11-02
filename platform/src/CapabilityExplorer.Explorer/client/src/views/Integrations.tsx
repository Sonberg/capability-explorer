import { useState } from "react";
import { request, shortSha, type Integrations as Data, type Mark, type ResourceGroup } from "../api";
import { EvidenceLinks } from "../components/source";
import { ResolutionPill, SymbolLink } from "../components/symbols";
import { Card, ErrorBox, H1, H2, Key, Loading, Mono, Muted, Note, Pill, Table, type Tone } from "../components/ui";
import { revisionLabel, symbolHref, useApi, useSelection } from "../state";

const matchTone: Record<string, Tone> = { bound: "ok", binding: "ok", broken: "bad", route: "warn", none: "bad", dynamic: "neutral" };

type Direction = "exposed" | "consumed";
type Marks = { map: Map<string, Mark>; reload: () => void };

const consumedRelations = ["READS", "CONSUMES", "REQUESTS"];

export function Integrations({ direction }: { direction: Direction }) {
  const { meta, scope } = useSelection();
  const { data: d, error, reload } = useApi<Data>(`/api/integrations?${scope}`);
  const [onlyObsolete, setOnlyObsolete] = useState(false);
  const [repo, setRepo] = useState("");
  if (error) return <ErrorBox message={error} />;
  if (!d) return <Loading />;
  const marks: Marks = { map: new Map(d.marks.map((m) => [m.key, m])), reload };
  const repos = [...new Set([
    ...d.services.map((s) => s.service.repositoryId),
    ...d.http.map((l) => l.request.repositoryId),
    ...[...d.channels, ...d.data].flatMap((g) => g.keys.flatMap((k) => k.edges.map((e) => e.repositoryId))),
  ])].sort();
  const inRepo = (r: string) => !repo || r === repo;
  const keep = (...keys: string[]) => !onlyObsolete || keys.some((k) => marks.map.has(k));
  const exposed = direction === "exposed";
  // Channels and data are one list of keys each; a relation puts a use on one side.
  const side = (groups: ResourceGroup[]) =>
    groups
      .map((g) => ({ ...g, keys: g.keys.map((k) => ({ ...k, edges: k.edges.filter((e) => consumedRelations.includes(e.relation) !== exposed && inRepo(e.repositoryId)) })).filter((k) => k.edges.length > 0) }))
      .filter((g) => g.keys.length > 0)
      .filter((g) => keep(groupKey(g), ...g.keys.map((k) => k.key)));
  const http = d.http.filter((l) => inRepo(l.request.repositoryId) && keep(`group|http|${l.group}`, l.request.to?.key ?? ""));
  const services = d.services
    .map((s) => ({ ...s, endpoints: s.endpoints.filter((ep) => keep(ep.endpoint.key)) }))
    .filter((s) => inRepo(s.service.repositoryId))
    .filter((s) => !onlyObsolete || s.endpoints.length > 0 || marks.map.has(s.service.key));
  const removed = d.marks.filter((m) => m.removal);
  return (
    <div>
      <div className="flex flex-wrap items-center gap-3">
        <H1>{exposed ? "What services expose" : "What services consume"}</H1>
        <select value={repo} onChange={(e) => setRepo(e.target.value)} aria-label="Repository" className="ml-auto rounded border border-zinc-300 bg-transparent px-2 py-1 text-sm dark:border-zinc-700">
          <option value="">All repositories</option>
          {repos.map((r) => <option key={r} value={r}>{r}</option>)}
        </select>
        <label className="flex items-center gap-1.5 text-sm">
          <input type="checkbox" checked={onlyObsolete} onChange={(e) => setOnlyObsolete(e.target.checked)} />
          Only obsolete / deprecated
        </label>
      </div>
      <Muted>
        Derived from rule-based extraction. A cross-repo match by route or name is a candidate, not a proven connection: base URLs, namespaces and connection strings stay unbound until configuration is
        analysed.
      </Muted>

      {removed.length > 0 && (
        <>
          <H2 className="mt-6">Removed from the codebase</H2>
          <Card>
            <ul className="space-y-1.5 text-sm">
              {removed.map((m) => (
                <li key={m.key}>
                  <Mono className="text-xs">{m.key}</Mono> <Pill tone="bad">{m.status}</Pill> gone in{" "}
                  <Mono className="text-xs">{m.removal!.repositoryId}@{shortSha(m.removal!.removedIn)}</Mono>
                  {m.removal!.subject && <span className="text-zinc-500"> “{m.removal!.subject}”</span>}
                  <span className="text-xs text-zinc-500"> (last seen {shortSha(m.removal!.lastSeen)}; analysed commits only)</span>
                  {m.note && <div className="text-xs text-zinc-500">{m.note}</div>}
                </li>
              ))}
            </ul>
          </Card>
        </>
      )}

      {exposed && (
        <>
          <H2 className="mt-6">Services and endpoints</H2>
          <div className="grid gap-3 sm:grid-cols-[repeat(auto-fit,minmax(320px,1fr))]">
            {services.map((s) => (
              <Card key={s.service.key}>
                <div className="flex flex-wrap items-baseline gap-2">
                  <a href={symbolHref(s.service.key)} className="font-semibold text-blue-700 hover:underline dark:text-blue-300">
                    {s.service.name}
                  </a>
                  <span className="text-xs text-zinc-500">
                    {s.service.repositoryId} @ {revisionLabel(meta, s.service.repositoryId, s.service.commitSha)}
                  </span>
                  <MarkControl id={s.service.key} marks={marks} />
                </div>
                {s.endpoints.length === 0 && <Muted className="mt-2 text-xs">No endpoints exposed.</Muted>}
                <ul className="mt-2 space-y-1.5">
                  {s.endpoints.map((ep) => (
                    <li key={ep.endpoint.key}>
                      <span className="mr-1.5 rounded bg-blue-50 px-1.5 py-px font-mono text-[11px] font-bold text-blue-700 dark:bg-blue-950 dark:text-blue-300">{String(ep.endpoint.props?.method ?? "")}</span>
                      <a href={symbolHref(ep.endpoint.key)} className="font-mono text-[12.5px] text-blue-700 hover:underline dark:text-blue-300">
                        {String(ep.endpoint.props?.route ?? ep.endpoint.name)}
                      </a>{" "}
                      <MarkControl id={ep.endpoint.key} marks={marks} />
                      <div className="text-xs text-zinc-500">
                        handled by{" "}
                        {ep.handlers.length
                          ? ep.handlers.map((h, i) => (
                              <span key={h.key}>
                                {i > 0 && ", "}
                                <a href={symbolHref(h.key)} className="text-blue-700 hover:underline dark:text-blue-300">
                                  {h.name}
                                </a>
                              </span>
                            ))
                          : "—"}
                      </div>
                    </li>
                  ))}
                </ul>
              </Card>
            ))}
          </div>
        </>
      )}

      {!exposed && (
        <>
          <H2 className="mt-6">HTTP calls</H2>
          <Muted className="mb-2 text-xs">Grouped by destination, then by the source file where each request occurs.</Muted>
          <div className="space-y-2">
            {[...http.reduce((m, l) => m.set(l.group, [...(m.get(l.group) ?? []), l]), new Map<string, Data["http"]>())].map(([group, links]) => (
              <HttpGroup key={group} base={group} links={links} marks={marks} />
            ))}
          </div>
        </>
      )}

      <H2 className="mt-6">Messaging</H2>
      <div className="space-y-3">{side(d.channels).map((g) => <Resource key={g.name} group={g} marks={marks} />)}</div>
      <H2 className="mt-6">Data</H2>
      <div className="space-y-3">{side(d.data).map((g) => <Resource key={g.name} group={g} marks={marks} />)}</div>
    </div>
  );
}

const groupKey = (g: ResourceGroup) => `group|${g.kind}|${g.name}`;

/** Marks a whole integration (a `group|…` key) or one element; shows the mark, its note and the commit that removed it. */
function MarkControl({ id, marks }: { id: string; marks: Marks }) {
  const mark = marks.map.get(id);
  const [editing, setEditing] = useState(false);
  const [status, setStatus] = useState<Mark["status"]>(mark?.status ?? "deprecated");
  const [note, setNote] = useState(mark?.note ?? "");
  const save = () => request("/api/deprecations", { method: "PUT", body: JSON.stringify({ key: id, status, note }) }).then(() => { setEditing(false); marks.reload(); });
  const clear = () => request(`/api/deprecations?key=${encodeURIComponent(id)}`, { method: "DELETE" }).then(() => { setEditing(false); marks.reload(); });
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5 align-middle">
      {mark && (
        <Pill tone={mark.status === "obsolete" ? "bad" : "warn"} title={mark.note}>
          {mark.status}
        </Pill>
      )}
      {mark?.note && !editing && <span className="text-xs text-zinc-500">{mark.note}</span>}
      {!editing && (
        <button type="button" onClick={() => setEditing(true)} className="text-xs text-zinc-500 hover:underline">
          {mark ? "edit" : "mark…"}
        </button>
      )}
      {editing && (
        <span className="inline-flex flex-wrap items-center gap-1.5">
          <select value={status} onChange={(e) => setStatus(e.target.value as Mark["status"])} className="rounded border border-zinc-300 bg-transparent px-1 text-xs dark:border-zinc-700">
            <option value="deprecated">deprecated</option>
            <option value="obsolete">obsolete</option>
          </select>
          <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="note" aria-label="Note" className="rounded border border-zinc-300 bg-transparent px-1.5 text-xs dark:border-zinc-700" />
          <button type="button" onClick={save} className="text-xs font-semibold text-blue-700 dark:text-blue-300">save</button>
          {mark && <button type="button" onClick={clear} className="text-xs text-red-600">unmark</button>}
          <button type="button" onClick={() => setEditing(false)} className="text-xs text-zinc-500">cancel</button>
        </span>
      )}
    </span>
  );
}

function HttpGroup({ base, links, marks }: { base: string; links: Data["http"]; marks: Marks }) {
  const matched = links.filter((l) => l.candidates.length > 0).length;
  const repos = [...new Set(links.map((l) => l.request.repositoryId))];
  const sources = [...links.reduce((m, l) => {
    const key = `${l.request.repositoryId}\0${l.sourcePath ?? ""}`;
    return m.set(key, [...(m.get(key) ?? []), l]);
  }, new Map<string, Data["http"]>())];
  return (
    <details open className="group rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-900">
      <summary className="flex cursor-pointer list-none flex-wrap items-center gap-2 px-4 py-2.5">
        <span className="text-zinc-400 transition-transform group-open:rotate-90" aria-hidden="true">›</span>
        <span className="font-semibold">{base}</span>
        <Pill>
          {links.length} call{links.length === 1 ? "" : "s"}
        </Pill>
        {matched > 0 && <Pill tone="ok">{matched} matched</Pill>}
        <span className="text-xs text-zinc-500">from {repos.join(", ")}</span>
        <span onClick={(e) => e.preventDefault()}>
          <MarkControl id={`group|http|${base}`} marks={marks} />
        </span>
      </summary>
      <div className="border-t border-zinc-200 dark:border-zinc-800">
        {sources.map(([key, sourceLinks]) => {
          const [repositoryId, sourcePath] = key.split("\0");
          return (
            <section key={key}>
            {sourcePath && <h3 className="border-b border-zinc-100 px-4 py-2 font-mono text-xs text-zinc-600 dark:border-zinc-800 dark:text-zinc-400">{repositoryId} · {sourcePath}</h3>}
            <Table
              columns={["Client", "Via", "Request", "Match", "Candidate endpoint", "Why", "Status"]}
              rows={sourceLinks.map((l) => [
                <SymbolLink symbol={l.request.from} />,
                <Mono className="text-xs text-zinc-500">{l.base}</Mono>,
                l.request.to ? <Mono className="text-xs">{l.request.to.name}</Mono> : <span className="text-zinc-500">dynamic</span>,
                <Pill tone={matchTone[l.match]}>{l.match}</Pill>,
                l.candidates.length ? l.candidates.map((c) => <div key={c.key}><SymbolLink symbol={c} /></div>) : <span className="text-zinc-400">—</span>,
                <span className="text-xs text-zinc-500">{l.note}</span>,
                l.request.to ? <MarkControl id={l.request.to.key} marks={marks} /> : null,
              ])}
            />
            </section>
          );
        })}
      </div>
    </details>
  );
}

function Resource({ group: g, marks }: { group: ResourceGroup; marks: Marks }) {
  const isData = g.kind === "data";
  return (
    <Card>
      <div className="flex items-center gap-2">
        <Mono className="font-semibold">{g.name}</Mono>
        <Pill>
          {g.keys.length} key{g.keys.length === 1 ? "" : "s"}
        </Pill>
        <MarkControl id={groupKey(g)} marks={marks} />
      </div>
      {g.note && <Note>{g.note}</Note>}
      {g.keys.map((k) => (
        <div key={k.key} className="mt-3">
          <div className="flex flex-wrap items-center gap-2">
            <Pill tone={k.bound ? "ok" : "warn"}>{k.bound ? "bound" : "unbound"}</Pill>
            <Key value={k.key} />
            <MarkControl id={k.key} marks={marks} />
          </div>
          {k.declaration && (
            <div className="text-xs">
              declared by{" "}
              <a href={symbolHref(k.declaration.key)} className="text-blue-700 hover:underline dark:text-blue-300">
                {k.declaration.repositoryId} {k.declaration.unit}
              </a>
            </div>
          )}
          {k.edges.length ? (
            <div className="mt-1.5">
              <Table
                columns={["Relation", "By", ...(isData ? ["Column"] : []), "Resolution", "Evidence"]}
                rows={k.edges.map((e) => [
                  <Mono className="text-xs">{e.relation}</Mono>,
                  <SymbolLink symbol={e.from} />,
                  ...(isData ? [e.to?.key.includes("#") ? <Mono className="text-xs">{e.to.key.split("#").at(-1)}</Mono> : <span className="text-xs text-zinc-500">(whole object)</span>] : []),
                  <ResolutionPill resolution={e.resolution} reason={e.unresolvedReason} />,
                  <EvidenceLinks evidence={e.evidence} />,
                ])}
              />
            </div>
          ) : (
            <Muted className="mt-1 text-xs">No readers or writers in the selected revisions.</Muted>
          )}
        </div>
      ))}
    </Card>
  );
}
