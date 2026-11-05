import { useEmbeddingStatus, type DatabaseMeta, type EmbeddingModelIdentity, type EmbeddingState } from "../database-api";
import { Card, Muted, Pill, type Tone } from "./ui";

const states: Record<EmbeddingState, { label: string; tone: Tone; detail: string }> = {
  "not-configured": {
    label: "Embeddings not configured", tone: "neutral",
    detail: "No embedding model is configured. Core snapshot browsing remains available.",
  },
  pending: {
    label: "Embedding projection pending", tone: "info",
    detail: "Complete coverage for the configured model and selected revisions is not yet confirmed. Unknown history is not proof of coverage.",
  },
  "partial-failure": {
    label: "Embedding projection partially failed", tone: "bad",
    detail: "Some selected-view embedding work failed. Successfully completed projection data is retained for retry.",
  },
  stale: {
    label: "Embedding projection stale", tone: "warn",
    detail: "Coverage belongs to a previous model identity, not the currently configured model.",
  },
  ready: {
    label: "Embedding projection ready", tone: "ok",
    detail: "Embedding coverage is complete for this selected view and configured model. This does not enable similarity search.",
  },
};
const button = "rounded border border-zinc-300 px-3 py-1.5 text-sm disabled:cursor-default disabled:opacity-40 hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800";

export function EmbeddingStatusPanel({ scope, meta, selection }: {
  scope: string; meta: DatabaseMeta; selection: Record<string, string>;
}) {
  const status = useEmbeddingStatus(scope, meta, selection);
  const data = status.data;
  const state = data ? states[data.state] : undefined;
  const retryMeaningful = data?.configuredModel && data.state !== "ready" && data.state !== "not-configured"
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(meta.snapshotId);
  return <Card className="text-sm">
    <div role="region" aria-label="Embedding projection status" data-testid="embedding-status" aria-busy={status.loading}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-semibold">Embedding projection</h2>
        <button type="button" className={button} disabled={status.loading} onClick={status.refresh}>
          {status.error ? "Retry status" : "Refresh status"}
        </button>
      </div>
      <div className="mt-2" role="status" aria-live="polite">
        {status.loading && <Muted>Loading embedding status…</Muted>}
        {state && <><Pill tone={state.tone}>{state.label}</Pill><Muted className="mt-1">{state.detail}</Muted></>}
      </div>
      {status.error && <p role="alert" className="mt-2 text-red-700 dark:text-red-300">
        Could not load embedding status{status.errorStatus ? ` (HTTP ${status.errorStatus})` : ""}. {status.error} Core browsing is unaffected.
      </p>}
      <p className="mt-2 font-medium">PostgreSQL similarity search unavailable.</p>
      {data && <div className="mt-2 space-y-2">
        <p>Coverage scope: selected view · {data.selectedArtifactCount} selected artifacts of {data.snapshotArtifactCount} in snapshot
          {" · "}{data.coveredArtifactCount} selected artifacts fully covered.</p>
        <p>{data.countsExact && data.counts
          ? <>Occurrences: {data.counts.ready} ready · {data.counts.pending} pending · {data.counts.failed} failed · {data.counts.total} total.</>
          : "Occurrence counts unknown — complete enumeration of the selected view has not been confirmed."}</p>
        <p>Last successful coverage completion: {data.lastSuccessfulCompletion
          ? <time dateTime={data.lastSuccessfulCompletion}>{data.lastSuccessfulCompletion}</time>
          : "Unknown"}</p>
        <details>
          <summary className="cursor-pointer">Model identity and exact coverage context</summary>
          <div className="mt-2 space-y-2 break-all text-xs">
            <ModelIdentity label="Configured model" model={data.configuredModel} />
            <ModelIdentity label="Coverage model" model={data.coverageModel} />
            <p>Snapshot: <code>{data.snapshotId}</code> · generation {data.generation} · capability {data.capability}</p>
            <p>Snapshot artifact digest: <code>{data.snapshotArtifactsSha256 ?? "Unknown"}</code></p>
            <p>Selected revisions:</p>
            {Object.entries(data.selection).map(([repo, sha]) => <p key={repo}><code>{repo} · {sha}</code></p>)}
            {!Object.keys(data.selection).length && <p>No repositories in this selected view.</p>}
            {data.reasonCode && <p>Reason: {data.reasonCode}</p>}
          </div>
        </details>
        {retryMeaningful && <details>
          <summary className="cursor-pointer">Operator CLI retry</summary>
          <Muted className="mt-1">On the server, an authorized operator can run the standalone CLI for this whole snapshot.
            This page does not execute commands; Refresh status only reads metadata.</Muted>
          <code className="mt-1 block break-all">ce embed --snapshot {meta.snapshotId}</code>
        </details>}
      </div>}
    </div>
  </Card>;
}

function ModelIdentity({ label, model }: { label: string; model: EmbeddingModelIdentity | null }) {
  return <p>{label}: {model
    ? <><code>{model.id}</code> · fingerprint <code>{model.fingerprint}</code> · recipe <code>{model.recipeVersion}</code></>
    : "None confirmed"}</p>;
}
