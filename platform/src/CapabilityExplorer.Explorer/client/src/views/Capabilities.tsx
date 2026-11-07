import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { ApiError, request, shortSha, type AnalysisJob, type CapabilityCreated, type CapabilityStatus, type GitHubAccounts, type RebuildResult } from "../api";
import { Button, Card, ErrorBox, H1, H2, inputClass, Loading, Mono, Muted, Note, Pill, Table } from "../components/ui";
import { useApi, useSelection } from "../state";

interface LineError {
  line: number;
  input: string;
  message: string;
}

export function Capabilities() {
  const { meta, reloadMeta, setCapability, capability } = useSelection();
  const list = useApi<CapabilityStatus[]>(`/api/capabilities?g=${meta.generation}`, 2000);
  const [busy, setBusy] = useState<string | null>(null);
  const [removing, setRemoving] = useState<string>();
  const [actionError, setActionError] = useState<string>();
  const [rebuilt, setRebuilt] = useState<{ id: string; result: RebuildResult }>();
  const intelligenceOff = !meta.similarity.database
    ? "Needs the database: start with make dev (or CE_DATABASE)"
    : !meta.similarity.embeddings
      ? "Needs an embedding model: make ollama or the Settings page"
      : undefined;

  const reloadList = list.reload;
  const refresh = useCallback(() => {
    reloadList();
    reloadMeta();
  }, [reloadList, reloadMeta]);

  // "Analyze code" runs on the host (scripts/analyze-worker, started by make dev): queue it, then follow it.
  const [analysisError, setAnalysisError] = useState<string>();
  const [jobs, setJobs] = useState<Record<string, AnalysisJob>>({});
  const [githubAccounts, setGitHubAccounts] = useState<GitHubAccounts>();
  const [githubAccount, setGitHubAccount] = useState("");
  const [githubAccountsError, setGitHubAccountsError] = useState<string>();
  const analysisRequests = useRef(new Set<string>());
  const analysisVersions = useRef<Record<string, number>>({});
  const observedJobs = useRef<Record<string, AnalysisJob>>({});
  const [pendingAnalysis, setPendingAnalysis] = useState<Record<string, boolean>>({});
  useEffect(() => {
    const controller = new AbortController();
    void request<GitHubAccounts>("/api/github-accounts", { signal: controller.signal })
      .then(setGitHubAccounts)
      .catch((error: Error) => { if (!controller.signal.aborted) setGitHubAccountsError(error.message); });
    return () => controller.abort();
  }, []);

  const ids = list.data?.map((c) => c.id).join("\n") ?? "";
  useEffect(() => {
    if (!ids) return;
    const controller = new AbortController();
    let timer: number;
    const poll = async () => {
      if (!document.hidden) {
        let next: Record<string, AnalysisJob> = {};
        const versions = { ...analysisVersions.current };
        const errors: string[] = [];
        const deadline = new AbortController();
        const timeout = window.setTimeout(() => deadline.abort(new Error("Analysis status timed out; retrying.")), 15000);
        try {
          next = await request<Record<string, AnalysisJob>>("/api/analysis-status", { signal: AbortSignal.any([controller.signal, deadline.signal]) });
        } catch (error) {
          errors.push((error as Error).message);
        }
        window.clearTimeout(timeout);
        if (controller.signal.aborted) return;
        const known = new Set(ids.split("\n"));
        for (const id of Object.keys(next)) {
          if (!known.has(id) || versions[id] !== analysisVersions.current[id] || analysisRequests.current.has(id)) delete next[id];
        }
        setJobs((current) => ({ ...current, ...next }));
        setAnalysisError(errors.length ? errors.join("; ") : undefined);
        const completed = Object.entries(next).some(([id, job]) => isActive(observedJobs.current[id]) && !isActive(job));
        observedJobs.current = { ...observedJobs.current, ...next };
        if (completed) refresh();
      }
      timer = window.setTimeout(() => { void poll(); }, 2000);
    };
    void poll();
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, [ids, refresh]);

  const analysisAction = async (id: string, action: "analyze" | "stop" | "retry") => {
    if (analysisRequests.current.has(id)) return;
    setActionError(undefined);
    const observed = jobs[id];
    const runId = observed?.runId;
    const attemptId = observed?.attemptId;
    if (action === "stop" && !runId) { setActionError("Reload analysis status before stopping this run."); return; }
    if (action === "retry") {
      if (!canRetry(observed)) { setActionError("Reload analysis status before retrying this run."); return; }
      if (!window.confirm(`Retry interrupted run ${runId}, attempt ${attemptId}? Old evidence will be preserved. Execution termination is not verified. By confirming, you authorize a fresh run; this does not confirm termination or success of the old run.`)) return;
    }
    analysisRequests.current.add(id);
    analysisVersions.current[id] = (analysisVersions.current[id] ?? 0) + 1;
    setPendingAnalysis((current) => ({ ...current, [id]: true }));
    try {
      const target = `/api/capabilities/${encodeURIComponent(id)}/${action === "retry" ? "retry" : "analyze"}`;
      const endpoint = action === "stop" ? `${target}?runId=${encodeURIComponent(runId!)}`
        : action === "analyze" && githubAccount ? `${target}?githubAccount=${encodeURIComponent(githubAccount)}` : target;
      const job = await request<AnalysisJob>(endpoint,
        { method: action === "stop" ? "DELETE" : "POST",
          ...(action === "retry" ? { body: JSON.stringify({ runId, attemptId, acknowledgeUnverifiedExecution: true,
            ...(githubAccount ? { githubAccount } : {}) }) } : {}) });
      observedJobs.current[id] = job;
      setJobs((s) => ({ ...s, [id]: job }));
    } catch (e) {
      setActionError((e as Error).message);
    } finally {
      analysisVersions.current[id]++;
      analysisRequests.current.delete(id);
      setPendingAnalysis((current) => ({ ...current, [id]: false }));
    }
  };

  const rebuild = async (id: string) => {
    setBusy(id);
    setActionError(undefined);
    setRebuilt(undefined);
    try {
      setRebuilt({ id, result: await request<RebuildResult>(`/api/capabilities/${encodeURIComponent(id)}/rebuild`, { method: "POST" }) });
      refresh();
    } catch (e) {
      setActionError((e as Error).message);
    } finally {
      setBusy(null);
    }
  };

  const check = async (c: CapabilityStatus, cancel = false) => {
    setBusy(c.id);
    setActionError(undefined);
    try {
      const url = cancel && c.check
        ? `/api/source-checks/${encodeURIComponent(c.check.requestId)}/cancel`
        : `/api/capabilities/${encodeURIComponent(c.id)}/check${githubAccount ? `?githubAccount=${encodeURIComponent(githubAccount)}` : ""}`;
      await request(url, { method: "POST" });
      refresh();
    } catch (e) {
      setActionError((e as Error).message);
    } finally {
      setBusy(null);
    }
  };

  const remove = async (c: CapabilityStatus) => {
    if (busy !== null || analysisRequests.current.has(c.id)) return;
    if (!window.confirm(`Remove capability “${c.name}”? Cloned sources stay in the workspace.`)) return;
    setBusy(c.id);
    setRemoving(c.id);
    setActionError(undefined);
    try {
      await request(`/api/capabilities/${encodeURIComponent(c.id)}`, { method: "DELETE" });
      if (capability === c.id) setCapability("");
      refresh();
    } catch (e) {
      setActionError((e as Error).message);
    } finally {
      setBusy(null);
      setRemoving(undefined);
    }
  };

  return (
    <div className="space-y-3">
      <div>
        <H1>Capabilities</H1>
        <Muted>
          A capability is a named set of repositories that are checked together. Checks are queued for the trusted host worker, which acquires each source read-only and
          locks it to one exact commit. Pick a capability in the bar above to scope every view to it.
        </Muted>
      </div>
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <Pill tone="neutral">Host-managed acquisition</Pill>
        <span className="text-zinc-500">
          Tokens stay on the host. This selection is saved only with the queued run; it does not put credentials in the capability file.
        </span>
        <label className="flex items-center gap-2">
          <span>GitHub account</span>
          <select className={inputClass} value={githubAccount} onChange={(event) => setGitHubAccount(event.target.value)}>
            <option value="">Automatic host credentials</option>
            {githubAccounts?.accounts.map((account) => (
              <option key={account.login} value={account.login}>{account.login}{account.active ? " (active)" : ""}</option>
            ))}
          </select>
        </label>
      </div>
      {githubAccountsError && <Note>Host account names are unavailable: {githubAccountsError}. Automatic host credentials remain available.</Note>}
      {githubAccounts && !githubAccounts.available && <Note>GitHub CLI is not installed on the host. Automatic credentials remain available.</Note>}
      {actionError && <ErrorBox message={actionError} />}
      {analysisError && <ErrorBox message={`Analysis status unavailable: ${analysisError}. Retrying automatically.`} />}
      {list.error && <ErrorBox message={list.error} />}
      {!list.data && !list.error && <Loading />}
      {list.data?.length === 0 && <Card className="text-zinc-500">No capabilities yet. Define one below.</Card>}
      {list.data?.map((c) => (
        <Card key={c.id}>
          <div className="flex flex-wrap items-center gap-2">
            <H2 className="mb-0">{c.name}</H2>
            <Mono className="text-xs text-zinc-500">{c.id}</Mono>
            <Pill tone={c.analysed === c.repositories.length ? "ok" : c.analysed > 0 ? "warn" : "neutral"}>
              {c.analysed}/{c.repositories.length} analysed at locked revision
            </Pill>
            <span className="ml-auto flex flex-wrap gap-2">
              <Button variant="secondary" onClick={() => setCapability(c.id)} disabled={capability === c.id}>
                {capability === c.id ? "Selected" : "Select"}
              </Button>
              <Button variant="secondary" onClick={() => check(c)} disabled={busy !== null || ["queued", "working", "cancelling", "unknown"].includes(c.check?.state ?? "")}>
                Check sources
              </Button>
              {c.check && ["queued", "working"].includes(c.check.state) && (
                <Button variant="danger" onClick={() => check(c, true)} disabled={busy !== null}>Cancel check</Button>
              )}
              {isActive(jobs[c.id]) ? (
                <Button variant="danger" onClick={() => analysisAction(c.id, "stop")} disabled={pendingAnalysis[c.id] || !jobs[c.id]?.runId || jobs[c.id]?.state === "stopping" || jobs[c.id]?.state === "publishing"}>
                  {pendingAnalysis[c.id] || jobs[c.id]?.state === "stopping" ? "Stopping…" : jobs[c.id]?.state === "queued" ? "Queued · Stop" : "Analyzing · Stop"}
                </Button>
              ) : (
                <Button onClick={() => analysisAction(c.id, "analyze")} disabled={pendingAnalysis[c.id] || busy !== null || !!analysisError || !jobs[c.id] || jobs[c.id]?.blocked || Object.values(jobs).some((job) => job.blocked)}>
                  {pendingAnalysis[c.id] ? "Queuing…" : "Analyze code"}
                </Button>
              )}
              {canRetry(jobs[c.id]) && (
                <Button variant="secondary" onClick={() => analysisAction(c.id, "retry")} disabled={pendingAnalysis[c.id] || busy !== null || !!analysisError}>
                  Retry interrupted run
                </Button>
              )}
              <Button variant="secondary" onClick={() => rebuild(c.id)} disabled={busy !== null || intelligenceOff !== undefined} title={intelligenceOff}>
                {busy === c.id && removing !== c.id ? "Rebuilding…" : "Rebuild intelligence"}
              </Button>
              <Button variant="danger" onClick={() => remove(c)} disabled={busy !== null || pendingAnalysis[c.id]}>
                {removing === c.id ? "Removing…" : "Remove"}
              </Button>
            </span>
          </div>
          {c.description && <Muted className="mt-1">{c.description}</Muted>}
          <p className="my-2 text-xs text-zinc-500">{c.checkedAt ? `Last checked ${new Date(c.checkedAt).toLocaleString()}` : "Not checked yet."}</p>
          {c.check && <Note>
            Source check: <strong>{c.check.state}</strong> · <Mono>{c.check.requestId}</Mono>
            {c.checkStale && <span className="block">Stale configuration: this request belongs to an older definition. Its revisions are not current.</span>}
            {c.check.state === "queued" && <span className="block">Waiting for the host worker. Start <Mono>make analyze-worker</Mono> on the host.</span>}
            {c.check.state === "failed" && <span className="block">Check failed; inspect the host worker before retrying.</span>}
            {c.check.state === "unknown" && <span className="block">Execution is unknown. Host reconciliation is required; no success is assumed.</span>}
            {c.check.state === "cancelling" && <span className="block">Cancellation requested; execution termination is not yet confirmed.</span>}
          </Note>}
          <AnalysisProgress job={jobs[c.id]} />
          {rebuilt?.id === c.id && (
            <Note>
              Imported {rebuilt.result.imported} artifact(s) ({rebuilt.result.unchanged} unchanged), embedded {rebuilt.result.embedded} symbol(s) with <Mono>{rebuilt.result.model}</Mono>.
              {rebuilt.result.failed.map((f) => (
                <span key={f} className="block text-red-700 dark:text-red-300">{f}</span>
              ))}
            </Note>
          )}
          {c.analysed < c.repositories.length && <AnalyseHint id={c.id} />}
          <Table
            columns={["Repository", "Source", "Ref", "Locked revision", "Analysis"]}
            rows={c.repositories.map((r) => [
              <span className="font-semibold">{r.id}</span>,
              <span className="inline-flex flex-wrap items-center gap-1.5">
                <Pill tone={r.kind === "github" ? "info" : "neutral"}>{r.kind}</Pill>
                {r.kind === "github" ? (
                  <a href={r.location} target="_blank" rel="noreferrer noopener" className="break-all font-mono text-xs text-blue-700 hover:underline dark:text-blue-300">
                    {r.location}
                  </a>
                ) : (
                  <Mono className="break-all text-xs">{r.location}</Mono>
                )}
              </span>,
              <Mono className="text-xs">{r.ref}</Mono>,
              r.error ? (
                <span className="text-xs text-red-700 dark:text-red-300">
                  {r.error}
                  {c.check && <span className="block text-zinc-500">
                    Check <Mono>{c.check.requestId}</Mono> could not lock this repository. Verify its location, ref and
                    access, then view the host worker logs before retrying.
                  </span>}
                </span>
              ) : r.lockedSha ? (
                <span className="text-xs">
                  <Mono>{shortSha(r.lockedSha)}</Mono> <span className="text-zinc-500">{r.lockedSubject}</span>
                </span>
              ) : (
                <span className="text-xs text-zinc-500">not checked</span>
              ),
              r.analysed ? (
                <Pill tone="ok">analysed</Pill>
              ) : (
                <span className="text-xs">
                  <Pill tone="warn">not analysed</Pill>
                  <span className="block text-zinc-500">{r.analysedRevisions.length ? `other revisions: ${r.analysedRevisions.join(", ")}` : "no artifacts for this repository yet"}</span>
                </span>
              ),
            ])}
          />
        </Card>
      ))}
      <NewCapability onCreated={(id) => { refresh(); setCapability(id); }} />
    </div>
  );
}

const isActive = (job?: AnalysisJob) => job?.state === "queued" || job?.state === "running" || job?.state === "stopping" || job?.state === "publishing";
const canRetry = (job?: AnalysisJob) => !!job?.runId && !!job.attemptId && (job.state === "unknown" || job.state === "interrupted");

function AnalysisProgress({ job }: { job?: AnalysisJob }) {
  if (!job || job.state === "idle") return null;
  const failure = job.log?.split(/\r?\n/).find((line) => line.startsWith("-- failure: "))?.slice("-- failure: ".length);
  return (
    <div className="my-2">
      {job.runId && <p className="text-xs text-zinc-500">Run <Mono>{job.runId}</Mono> · {job.stage ?? job.state} · {job.outcome ?? job.state}</p>}
      {job.blocked && <ErrorBox message={job.blockReason ?? "Analysis requires host worker reconciliation before another run can start."} />}
      {["interrupted", "unknown", "partial"].includes(job.state) && <Note>Analysis {job.state}. Execution or publication may be incomplete; inspect the host worker before starting another run.</Note>}
      {job.state === "queued" && (
        <Note>
          Waiting for the analyze worker on the host: <Mono>make dev</Mono> starts one, or run <Mono className="select-all">make analyze-worker</Mono>.
        </Note>
      )}
      {job.state === "stopped" && <Note>Analysis termination confirmed. Review publication outcomes before relying on artifacts.</Note>}
      {job.state === "failed" && <ErrorBox message={failure ?? "Analysis failed. The last lines of its log are below."} />}
      {job.log && <pre className="mt-2 max-h-64 overflow-auto rounded-md bg-zinc-100 p-2 text-xs dark:bg-zinc-900">{job.log}</pre>}
    </div>
  );
}

/** Extraction runs outside the explorer, in offline sandboxes; the page refreshes by itself when it finishes. */
function AnalyseHint({ id }: { id: string }) {
  const command = `make analyze CAPABILITY=${id}`;
  return (
    <Note>
      <span className="block">
        To analyse the locked revisions, run in the repository root: <Mono className="select-all">{command}</Mono>
        <button type="button" className="ml-2 text-blue-700 hover:underline dark:text-blue-300" onClick={() => void navigator.clipboard?.writeText(command)}>
          copy
        </button>
      </span>
      <span className="block text-zinc-500">
        It checks each repository again (add LOCKED=1 to keep these revisions), extracts C# and TypeScript without building or running anything, and this page picks up the results automatically.
      </span>
    </Note>
  );
}

function NewCapability({ onCreated }: { onCreated: (id: string) => void }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [repositories, setRepositories] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [lineErrors, setLineErrors] = useState<LineError[]>([]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    setLineErrors([]);
    try {
      const created = await request<CapabilityCreated>("/api/capabilities", { method: "POST", body: JSON.stringify({ name, description, repositories }) });
      setName("");
      setDescription("");
      setRepositories("");
      onCreated(created.capability.id);
    } catch (err) {
      setError((err as Error).message);
      if (err instanceof ApiError) {
        const body = err.body as { lines?: LineError[]; created?: boolean; capability?: { id: string } } | undefined;
        setLineErrors(body?.lines ?? []);
        if (body?.created && body.capability) {
          setName("");
          setDescription("");
          setRepositories("");
          onCreated(body.capability.id);
        }
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <H2>New capability</H2>
      <form onSubmit={submit} className="grid gap-3">
        <label className="grid gap-1">
          <span className="text-xs font-semibold">Name</span>
          <input required value={name} onChange={(e) => setName(e.target.value)} placeholder="Order to cash" className={inputClass} />
        </label>
        <label className="grid gap-1">
          <span className="text-xs font-semibold">Description (optional)</span>
          <input value={description} onChange={(e) => setDescription(e.target.value)} className={inputClass} />
        </label>
        <label className="grid gap-1">
          <span className="text-xs font-semibold">Repositories — one per line</span>
          <textarea
            required
            rows={6}
            value={repositories}
            onChange={(e) => setRepositories(e.target.value)}
            placeholder={"https://github.com/acme/orders-api\nhttps://github.com/acme/web/tree/release/2026.10\nbilling=https://github.com/acme/billing-service v4.2.0\n/home/me/code/warehouse-jobs"}
            className={`${inputClass} font-mono`}
          />
          <span className="text-xs text-zinc-500">
            <Mono>[id=]location [ref]</Mono> — location is <Mono>https://github.com/owner/repo</Mono> (optionally <Mono>/tree/branch</Mono>) or a local git repository. Ref defaults to HEAD. Local paths
            outside this checkout must be available to the host worker. Private GitHub repositories need credentials configured on that host; Explorer does not acquire sources.
          </span>
        </label>
        {error && <Note>{error}</Note>}
        {lineErrors.length > 0 && (
          <ul className="list-disc pl-5 text-xs text-red-700 dark:text-red-300">
            {lineErrors.map((l) => (
              <li key={l.line}>
                line {l.line}: <Mono>{l.input}</Mono> — {l.message}
              </li>
            ))}
          </ul>
        )}
        <div>
          <Button type="submit" disabled={busy}>
            {busy ? "Creating and queuing…" : "Create and queue check"}
          </Button>
        </div>
      </form>
    </Card>
  );
}
