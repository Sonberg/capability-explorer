import { shortSha, type ArtifactSummary } from "../api";
import { StatusPill } from "../components/symbols";
import { Card, ErrorBox, H1, Key, Loading, Mono, Muted, Pill, Table } from "../components/ui";
import { useApi, useSelection } from "../state";

export function Artifacts() {
  const { meta } = useSelection();
  const { data: list, error } = useApi<ArtifactSummary[]>("/api/artifacts");
  if (error) return <ErrorBox message={error} />;
  if (!list) return <Loading />;
  return (
    <div className="space-y-3">
      <div>
        <H1>Artifacts</H1>
        <Muted>{list.length} validated artifacts. One artifact is one analysed unit at one exact revision.</Muted>
      </div>
      {meta.problems.length > 0 && (
        <Card className="border-red-200 dark:border-red-900">
          <div className="mb-1 font-semibold text-red-700 dark:text-red-300">Rejected files</div>
          {meta.problems.map((p) => (
            <Mono key={p} className="block text-xs">
              {p}
            </Mono>
          ))}
        </Card>
      )}
      <Table
        columns={["Repository", "Revision", "Unit", "Language", "Extractor", "Status", "Nodes / edges / calcs", "Files", "Diagnostics"]}
        rows={list.map((a) => [
          a.repositoryId,
          <span>
            {a.revision}
            <Key value={shortSha(a.commitSha)} />
          </span>,
          <span>
            {a.unit}
            <Key value={a.unitPath} />
          </span>,
          a.language,
          <span className="text-xs">{a.extractor}</span>,
          <StatusPill status={a.status} />,
          <Mono className="text-xs">
            {a.counts.node} / {a.counts.edge} / {a.counts.calculation}
          </Mono>,
          <span className="text-xs">
            {a.coverage.filesAnalyzed}/{a.coverage.filesTotal}
            {a.coverage.incompletePaths?.length ? <Key value={a.coverage.incompletePaths.join(", ")} /> : null}
          </span>,
          a.diagnostics.length ? (
            <div className="space-y-1">
              {a.diagnostics.map((d) => (
                <div key={d.code + d.message} className="text-xs">
                  <Pill tone={d.severity === "error" ? "bad" : "warn"}>{d.severity}</Pill> <Mono>{d.code}</Mono>
                  <div className="text-zinc-500">{d.message}</div>
                </div>
              ))}
            </div>
          ) : (
            <span className="text-zinc-400">—</span>
          ),
        ])}
      />
    </div>
  );
}
