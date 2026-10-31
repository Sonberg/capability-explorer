import { useState } from "react";
import type { EvaluationReport } from "../api";
import { Card, ErrorBox, H1, H2, Key, Loading, Muted, Pill, RatioBar, Table } from "../components/ui";
import { useApi, useSelection } from "../state";

const metrics: [string, string][] = [
  ["nodeRecall", "Node recall"],
  ["edgeRecall", "Edge recall"],
  ["edgeAccuracy", "Edge accuracy"],
  ["evidenceLocated", "Evidence located"],
  ["calculationRecall", "Calculation recall"],
  ["attributeAccuracy", "Attribute accuracy"],
  ["irHashMatch", "IR hash match"],
  ["precision", "Precision (exhaustive)"],
  ["coverage", "Coverage"],
];

export function Evaluation() {
  const { meta } = useSelection();
  const { data: r, error } = useApi<EvaluationReport>(meta.hasEvaluation ? "/api/evaluation" : null);
  const [kind, setKind] = useState("");

  if (!meta.hasEvaluation)
    return (
      <Card>
        <H1>No evaluation report loaded</H1>
        <Muted>Score extractor output and open the explorer on it:</Muted>
        <pre className="mt-2 overflow-x-auto rounded-md bg-zinc-100 p-3 font-mono text-xs dark:bg-zinc-800">{"make evaluate ACTUAL=out/\nmake explore ARTIFACTS=out/ EVALUATION=out/evaluation.json"}</pre>
      </Card>
    );
  if (error) return <ErrorBox message={error} />;
  if (!r) return <Loading />;

  const kinds = [...new Set(r.artifacts.flatMap((a) => a.findings.map((f) => f.kind)))].sort();
  const findings = r.artifacts.flatMap((a) => a.findings.map((f) => ({ a, f }))).filter(({ f }) => !kind || f.kind === kind);
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <H1>Evaluation against golden artifacts</H1>
        <Pill tone={r.passed ? "ok" : "bad"}>thresholds {r.passed ? "passed" : "failed"}</Pill>
      </div>
      {r.thresholdFailures.length > 0 && (
        <Card className="border-red-200 text-red-700 dark:border-red-900 dark:text-red-300">
          {r.thresholdFailures.map((f) => (
            <div key={f} className="text-xs">
              • {f}
            </div>
          ))}
        </Card>
      )}
      <Card>
        {metrics.map(([k, label]) => (r.totals[k] ? <RatioBar key={k} label={label} ratio={r.totals[k]} /> : null))}
      </Card>
      <H2>Artifacts</H2>
      <Table
        columns={["Golden artifact", "Status", "Extractor", "Nodes", "Edges", "Attributes", "Findings"]}
        rows={r.artifacts.map((a) => [
          a.label,
          <Pill tone={a.status === "Evaluated" ? (a.findings.length ? "warn" : "ok") : "bad"}>{a.status}</Pill>,
          a.actualExtractor ?? "—",
          `${a.metrics.nodeRecall?.hit}/${a.metrics.nodeRecall?.total}`,
          `${a.metrics.edgeRecall?.hit}/${a.metrics.edgeRecall?.total}`,
          `${a.metrics.attributeAccuracy?.hit}/${a.metrics.attributeAccuracy?.total}`,
          a.findings.length || (a.problems[0] ?? "—"),
        ])}
      />
      <H2>Findings</H2>
      <div className="flex flex-wrap gap-1.5">
        {["", ...kinds].map((k) => (
          <button
            key={k || "all"}
            type="button"
            onClick={() => setKind(k)}
            className={`rounded-full border px-3 py-0.5 text-xs ${kind === k ? "border-blue-500 bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300" : "border-zinc-300 bg-white dark:border-zinc-700 dark:bg-zinc-900"}`}
          >
            {k || "All findings"}
          </button>
        ))}
      </div>
      <Table
        columns={["Artifact", "Kind", "Subject", "Detail"]}
        rows={findings.map(({ a, f }) => [<span className="text-xs">{a.label}</span>, <Pill tone={f.kind.startsWith("Missing") ? "bad" : "warn"}>{f.kind}</Pill>, <Key value={f.subject} />, <span className="text-xs">{f.detail}</span>])}
      />
    </div>
  );
}
