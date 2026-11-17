import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ApiError, request, type GeneratedModelAuthorization, type GeneratedModelLatestResponse,
  type GeneratedModelRunResponse, type GeneratedModelRunState,
} from "../api";
import { Button, ErrorBox, Mono, Note, Pill } from "./ui";

interface Props {
  capability: { id: string; repositories: { id: string; lockedSha?: string }[] };
  snapshotId: string;
  generation: number;
}

export function GeneratedModelsPanel({ capability, snapshotId, generation }: Props) {
  const [authorization, setAuthorization] = useState<GeneratedModelAuthorization>();
  const [latest, setLatest] = useState<GeneratedModelLatestResponse>();
  const [run, setRun] = useState<GeneratedModelRunState>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [authorizeProvider, setAuthorizeProvider] = useState(false);
  const [includeSourceSnippets, setIncludeSourceSnippets] = useState(false);

  const selection = useMemo(() => Object.fromEntries(
    capability.repositories.map(repository => [repository.id, repository.lockedSha ?? ""]),
  ), [capability.repositories]);
  const hasCompleteSelection = capability.repositories.length > 0
    && Object.values(selection).every(commit => /^[\da-f]{40}$/i.test(commit));

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const currentAuthorization = await request<GeneratedModelAuthorization>("/api/generated-models/settings", { signal });
      setAuthorization(currentAuthorization);
      const currentRun = await request<GeneratedModelRunState | null>("/api/generated-models/status", { signal });
      setRun(currentRun ?? undefined);
      try {
        setLatest(await request<GeneratedModelLatestResponse>("/api/generated-models", { signal }));
      } catch (requestError) {
        if (!(requestError instanceof ApiError) || requestError.status !== 404) throw requestError;
      }
      setError(undefined);
    } catch (requestError) {
      if (!signal?.aborted) setError((requestError as Error).message);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    const timer = window.setInterval(() => {
      if (!document.hidden) void refresh(controller.signal);
    }, 2500);
    return () => { controller.abort(); window.clearInterval(timer); };
  }, [refresh]);

  const start = async () => {
    setBusy(true);
    setError(undefined);
    const runId = window.crypto.randomUUID();
    setRun({
      runId, status: "running", startedAt: new Date().toISOString(),
      provider: authorizeProvider ? authorization?.model : "deterministic-only",
      snapshotId, generation, selection,
    });
    try {
      const response = await request<GeneratedModelRunResponse>("/api/generated-models", {
        method: "POST",
        headers: { "X-CE-Generated-Model-Analysis": "1" },
        body: JSON.stringify({
          runId, capability: capability.id, snapshotId, generation, selection,
          authorizeProvider, includeSourceSnippets,
          authorizeSourceSnippets: includeSourceSnippets,
        }),
      });
      setRun(response.run);
      if (response.snapshot) setLatest({ snapshot: response.snapshot, stale: false, currentSnapshotId: snapshotId, currentGeneration: generation });
      await refresh();
    } catch (requestError) {
      setError((requestError as Error).message);
      await refresh();
    } finally {
      setBusy(false);
    }
  };

  const cancel = async () => {
    if (!run?.runId) return;
    setError(undefined);
    try {
      await request(`/api/generated-models/runs/${encodeURIComponent(run.runId)}`, { method: "DELETE" });
      await refresh();
    } catch (requestError) {
      setError((requestError as Error).message);
    }
  };

  const current = run?.status === "running";
  const shown = latest?.snapshot.capability === capability.id ? latest : undefined;
  const providerAvailable = !!authorization?.providerConfigured && !!authorization.providerAuthorized;
  const sourceAvailable = providerAvailable && !!authorization?.sourceSnippetsAuthorized && !!authorization.sourceExcerptAccessGranted;

  return (
    <section className="mt-3 space-y-2 border-t border-zinc-200 pt-3 dark:border-zinc-700" aria-label="Generated process and decision models">
      <div className="flex flex-wrap items-center gap-2">
        <strong className="text-sm">Generated BPMN / DMN proposals</strong>
        <Pill tone="neutral">unreviewed · never authored</Pill>
        {current && <Pill tone="warn">analysis running</Pill>}
      </div>
      <p className="text-xs text-zinc-500">
        Reads published model evidence only; it does not acquire repositories or rerun extractors. Incomplete or stale input is not published as a fresh result.
      </p>
      {!authorization?.modelEvidenceAccessGranted && <Note>Model-evidence access is denied by the task-query policy. Configure snapshot_resolve and models_list grants, plus access to every selected repository, before running.</Note>}
      <div className="flex flex-wrap items-center gap-3 text-xs">
        <label className="flex items-start gap-1.5">
          <input type="checkbox" checked={authorizeProvider} disabled={!providerAvailable || !authorization?.modelEvidenceAccessGranted || current || busy}
            onChange={event => { setAuthorizeProvider(event.target.checked); if (!event.target.checked) setIncludeSourceSnippets(false); }} />
          <span>For this run, authorize {authorization?.provider ?? "the configured provider"} to receive the listed model evidence.</span>
        </label>
        <label className="flex items-start gap-1.5">
          <input type="checkbox" checked={includeSourceSnippets} disabled={!sourceAvailable || !authorizeProvider || current || busy}
            onChange={event => setIncludeSourceSnippets(event.target.checked)} />
          <span>Also send cited source excerpts (separate Settings consent and source-access grant required).</span>
        </label>
      </div>
      <p className="break-words text-xs text-zinc-500">
        Provider: {authorization?.providerConfigured ? `${authorization.provider} (${authorization.model})` : "not configured"}.
        Data sent: selected revisions, published artifacts, candidate summaries{includeSourceSnippets ? ", and cited source excerpts" : "; no source excerpts"}.
      </p>
      {!hasCompleteSelection && <Note>Lock every repository to an exact revision before generating proposals.</Note>}
      {error && <ErrorBox message={error} />}
      <div className="flex flex-wrap gap-2">
        <Button onClick={start} disabled={busy || current || !hasCompleteSelection || !snapshotId || !authorization?.modelEvidenceAccessGranted}>
          {busy ? "Generating…" : "Generate proposals"}
        </Button>
        {current && <Button variant="danger" onClick={cancel}>Cancel run</Button>}
      </div>
      {run?.status === "partial" && <Note>Run was partial; the previous successful result, if any, remains available. {run.error}</Note>}
      {run?.status === "failed" && <Note>Run failed; the previous successful result, if any, remains available. {run.error}</Note>}
      {run?.status === "cancelled" && <Note>Run cancelled; the previous successful result, if any, remains available.</Note>}
      {shown && (
        <div className="space-y-2 rounded-md border border-zinc-200 p-3 dark:border-zinc-700">
          <div className="flex flex-wrap items-center gap-2">
            <Pill tone={latest?.stale ? "warn" : "ok"}>{latest?.stale ? "stale inputs" : "inputs current"}</Pill>
            <span className="text-xs text-zinc-500">{shown.snapshot.diagrams.length} diagram(s) · {shown.snapshot.proposals.length} relationship proposal(s)</span>
          </div>
          <p className="break-all text-xs text-zinc-500">Run inputs: <Mono>{shown.snapshot.dataScope}</Mono></p>
          {shown.snapshot.diagnostics.map(diagnostic => <Note key={diagnostic}>{diagnostic}</Note>)}
          {shown.snapshot.diagrams.map(diagram => (
            <article key={diagram.id} className="border-t border-zinc-200 pt-2 dark:border-zinc-700">
              <div className="flex flex-wrap items-center gap-2">
                <strong className="text-sm">{diagram.title}</strong>
                <Pill tone="neutral">{diagram.kind.toUpperCase()}</Pill>
                <Pill tone="warn">{diagram.reviewStatus}</Pill>
              </div>
              <p className="break-all text-xs text-zinc-500">
                Evidence: {diagram.repositoryId}@<Mono>{diagram.commitSha.slice(0, 12)}</Mono> · {diagram.unitPath} · {diagram.evidence.map(item => `${item.startLine}-${item.endLine}`).join(", ")}
              </p>
              <ul className="mt-1 list-inside list-disc text-xs">
                {diagram.elements.map(element => <li key={element.id}>{element.label}{element.condition ? ` — source text: ${element.condition}` : ""}</li>)}
              </ul>
              <details className="mt-1 text-xs">
                <summary className="cursor-pointer">View generated {diagram.kind.toUpperCase()} XML</summary>
                <pre className="mt-1 max-h-64 overflow-auto whitespace-pre-wrap break-all rounded bg-zinc-100 p-2 dark:bg-zinc-900">{diagram.bpmnXml ?? diagram.dmnXml}</pre>
              </details>
            </article>
          ))}
          {shown.snapshot.proposals.map(proposal => (
            <p key={`${proposal.fromCandidateId}:${proposal.toCandidateId}:${proposal.relation}`} className="text-xs">
              {proposal.fromCandidateId} —{proposal.relation}→ {proposal.toCandidateId}. {proposal.rationale} <Pill tone="warn">{proposal.evidenceClass} · {proposal.reviewStatus}</Pill>
            </p>
          ))}
        </div>
      )}
    </section>
  );
}
