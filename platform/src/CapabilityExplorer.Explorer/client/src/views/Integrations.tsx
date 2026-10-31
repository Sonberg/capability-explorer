import type { Integrations as Data, ResourceGroup } from "../api";
import { EvidenceLinks } from "../components/source";
import { ResolutionPill, SymbolLink } from "../components/symbols";
import { Card, ErrorBox, H1, H2, Key, Loading, Mono, Muted, Note, Pill, Table, type Tone } from "../components/ui";
import { revisionLabel, symbolHref, useApi, useSelection } from "../state";

const matchTone: Record<string, Tone> = { bound: "ok", binding: "ok", broken: "bad", route: "warn", none: "bad", dynamic: "neutral" };

export function Integrations() {
  const { meta, scope } = useSelection();
  const { data: d, error } = useApi<Data>(`/api/integrations?${scope}`);
  if (error) return <ErrorBox message={error} />;
  if (!d) return <Loading />;
  return (
    <div>
      <H1>How services connect</H1>
      <Muted>
        Derived from rule-based extraction. A cross-repo match by route or name is a candidate, not a proven connection: base URLs, namespaces and connection strings stay unbound until configuration is
        analysed.
      </Muted>

      <H2 className="mt-6">Services and endpoints</H2>
      <div className="grid gap-3 sm:grid-cols-[repeat(auto-fill,minmax(320px,1fr))]">
        {d.services.map((s) => (
          <Card key={s.service.key}>
            <div className="flex flex-wrap items-baseline gap-2">
              <a href={symbolHref(s.service.key)} className="font-semibold text-blue-700 hover:underline dark:text-blue-300">
                {s.service.name}
              </a>
              <span className="text-xs text-zinc-500">
                {s.service.repositoryId} @ {revisionLabel(meta, s.service.repositoryId, s.service.commitSha)}
              </span>
            </div>
            {s.endpoints.length === 0 && <Muted className="mt-2 text-xs">No endpoints exposed.</Muted>}
            <ul className="mt-2 space-y-1.5">
              {s.endpoints.map((ep) => (
                <li key={ep.endpoint.key}>
                  <span className="mr-1.5 rounded bg-blue-50 px-1.5 py-px font-mono text-[11px] font-bold text-blue-700 dark:bg-blue-950 dark:text-blue-300">{String(ep.endpoint.props?.method ?? "")}</span>
                  <a href={symbolHref(ep.endpoint.key)} className="font-mono text-[12.5px] text-blue-700 hover:underline dark:text-blue-300">
                    {String(ep.endpoint.props?.route ?? ep.endpoint.name)}
                  </a>
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

      <H2 className="mt-6">HTTP calls</H2>
      <Table
        columns={["Client", "Request", "Match", "Candidate endpoint", "Why"]}
        rows={d.http.map((l) => [
          <SymbolLink symbol={l.request.from} />,
          l.request.to ? <Mono className="text-xs">{l.request.to.name}</Mono> : <span className="text-zinc-500">dynamic</span>,
          <Pill tone={matchTone[l.match]}>{l.match}</Pill>,
          l.candidates.length ? l.candidates.map((c) => <div key={c.key}><SymbolLink symbol={c} /></div>) : <span className="text-zinc-400">—</span>,
          <span className="text-xs text-zinc-500">{l.note}</span>,
        ])}
      />

      <H2 className="mt-6">Messaging</H2>
      <div className="space-y-3">{d.channels.map((g) => <Resource key={g.name} group={g} />)}</div>
      <H2 className="mt-6">Data</H2>
      <div className="space-y-3">{d.data.map((g) => <Resource key={g.name} group={g} />)}</div>
    </div>
  );
}

function Resource({ group: g }: { group: ResourceGroup }) {
  const isData = g.kind === "data";
  return (
    <Card>
      <div className="flex items-center gap-2">
        <Mono className="font-semibold">{g.name}</Mono>
        <Pill>
          {g.keys.length} key{g.keys.length === 1 ? "" : "s"}
        </Pill>
      </div>
      {g.note && <Note>{g.note}</Note>}
      {g.keys.map((k) => (
        <div key={k.key} className="mt-3">
          <div className="flex flex-wrap items-center gap-2">
            <Pill tone={k.bound ? "ok" : "warn"}>{k.bound ? "bound" : "unbound"}</Pill>
            <Key value={k.key} />
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
