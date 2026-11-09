import { useEffect, useRef, useState } from "react";
import { ErrorBox, Loading } from "../components/ui";
import { ownershipEvidenceLink, ownershipReport, type OwnershipAudience, type OwnershipFinding, type OwnershipReport } from "../ownership-api";

const PAGE = 50;
const needsAction = (finding: OwnershipFinding) => finding.status === "action-needed" || finding.status === "not-evaluable";
const plural = (count: number, one: string, many = `${one}s`) => `${count} ${count === 1 ? one : many}`;
const STATUS: Record<OwnershipFinding["status"], { label: string; tone: string }> = {
  "action-needed": { label: "Action needed", tone: "border-l-red-600 dark:border-l-red-400" },
  "not-evaluable": { label: "Not evaluable", tone: "border-l-amber-500 dark:border-l-amber-400" },
  excepted: { label: "Declared exception", tone: "border-l-violet-600 dark:border-l-violet-400" },
  observed: { label: "Observed", tone: "border-l-blue-600 dark:border-l-blue-400" },
  aligned: { label: "Aligned", tone: "border-l-emerald-600 dark:border-l-emerald-400" },
  "not-applicable": { label: "Not applicable", tone: "border-l-zinc-400 dark:border-l-zinc-500" },
};
const BADGE = "inline-block rounded border border-current px-1.5 text-xs font-semibold uppercase tracking-wide";
const FIELD = "rounded border border-zinc-500 bg-white p-2 dark:border-zinc-400 dark:bg-zinc-900";
const BUTTON = "rounded border px-3 py-2 disabled:opacity-40";
// Keeps keyboard focus clear of the sticky application header.
const CLEAR_HEADER = "[&_:is(a,button,input,select,summary,[tabindex])]:scroll-mt-20 [&_:is(a,button,input,select,summary,[tabindex])]:scroll-mb-4";
const fullyCovered = (source: { state: string; coverage?: string }) => source.state === "current-full" || source.state === "current" && source.coverage === "full";

export function OwnershipAlignment() {
  const [audience, setAudience] = useState<OwnershipAudience>("engineering");
  const [repositoryInput, setRepositoryInput] = useState("");
  const [repository, setRepository] = useState("");
  const [retry, setRetry] = useState(0);
  const [state, setState] = useState<{ audience: OwnershipAudience; repository: string; retry: number; report?: OwnershipReport; error?: string }>();
  const [page, setPage] = useState(0);
  const findingsHeading = useRef<HTMLHeadingElement>(null);
  const pageMoved = useRef(false);
  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    setState(undefined);
    ownershipReport(audience, controller.signal, repository).then(report => {
      if (active) setState({ audience, repository, retry, report });
    }).catch((error: unknown) => {
      if (active) setState({ audience, repository, retry, error: error instanceof Error ? error.message : "The ownership report could not be loaded. No alignment is established." });
    });
    return () => { active = false; controller.abort(); };
  }, [audience, repository, retry]);
  useEffect(() => {
    if (!pageMoved.current) return;
    pageMoved.current = false;
    findingsHeading.current?.focus({ preventScroll: true });
    findingsHeading.current?.scrollIntoView({ block: "start" });
  }, [page]);
  const current = state?.audience === audience && state.repository === repository && state.retry === retry ? state : undefined;
  const report = current?.report;
  const findings = report?.findings ?? [];
  const ordered = [...findings.filter(needsAction), ...findings.filter(item => !needsAction(item))];
  const pages = Math.max(1, Math.ceil(ordered.length / PAGE));
  const shown = ordered.slice(page * PAGE, (page + 1) * PAGE);
  const range = `${shown.length ? page * PAGE + 1 : 0}–${page * PAGE + shown.length} of ${findings.length}`;
  const count = (status: OwnershipFinding["status"]) => findings.filter(item => item.status === status).length;
  const movePage = (to: number) => { pageMoved.current = true; setPage(to); };
  return <section className={`min-w-0 space-y-4 ${CLEAR_HEADER}`}>
    <h1 className="text-xl font-semibold">Ownership alignment</h1>
    <div className="flex flex-wrap items-end gap-3">
      <label className="flex flex-wrap items-center gap-2">Report audience
        <select aria-label="Report audience" value={audience}
          onChange={event => { setAudience(event.target.value === "architecture" ? "architecture" : "engineering"); setPage(0); }}
          className={FIELD}>
          <option value="engineering">Engineering</option><option value="architecture">Architecture</option>
        </select>
      </label>
      <button type="button" onClick={() => { setPage(0); setRetry(value => value + 1); }} className={BUTTON}>Refresh ownership evidence</button>
    </div>
    <form className="flex flex-wrap items-end gap-2" onSubmit={event => {
      event.preventDefault(); setRepository(repositoryInput); setPage(0); setRetry(value => value + 1);
    }}>
      <label className="min-w-0">Granted repository (blank for all granted)
        <input aria-label="Granted repository" aria-describedby="ownership-repository-hint" value={repositoryInput} maxLength={100} pattern="[a-z0-9][a-z0-9._\-]{0,99}"
          onChange={event => setRepositoryInput(event.target.value)} placeholder="Exact repository ID"
          className={`mt-1 block w-full ${FIELD}`} />
      </label>
      <button type="submit" className={BUTTON}>Apply repository scope</button>
      <p id="ownership-repository-hint" className="w-full text-sm text-zinc-600 dark:text-zinc-400">Lowercase letters, digits, dots, underscores or hyphens; up to 100 characters.</p>
    </form>
    <p>This read-only report uses configured ownership snapshots and repository grants, not the code-revision selector. Both audiences use the same findings. Recommendations do not change ownership.</p>
    {current?.error ? <div role="alert" className="min-w-0 [overflow-wrap:anywhere]"><ErrorBox message={current.error} /></div> : !report ? <Loading what="ownership report" /> : <>
      <div role="status" className={`space-y-1 rounded border border-l-4 border-zinc-300 p-3 dark:border-zinc-700 ${report.alignment === "aligned" ? "border-l-emerald-600 dark:border-l-emerald-400" : report.alignment === "action-needed" ? "border-l-red-600 dark:border-l-red-400" : "border-l-amber-500 dark:border-l-amber-400"}`}>
        <p className="text-lg font-semibold">Ownership verdict: {report.alignment}.</p>
        <p>{report.status === "ok" ? "Current evidence evaluated against configured rules." : "Alignment is not established for the whole granted scope."}</p>
        <p>{plural(findings.length, "finding")}: {findings.filter(needsAction).length} need action; {plural(count("aligned"), "aligned check")}; {plural(count("excepted"), "declared exception")}; {count("not-applicable")} not applicable.</p>
        {report.reason && <p className="break-words">{report.reason}</p>}
        {report.nextAction && <p className="break-words">Next action: {report.nextAction}</p>}
      </div>
      <section aria-label="Ownership source coverage" className="space-y-2">
        <h2 className="font-semibold">Source coverage</h2>
        <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
          {report.coverage.systems.map(source => <div key={source.system} className={`min-w-0 rounded border border-l-4 p-3 ${fullyCovered(source) ? "border-zinc-300 border-l-emerald-600 dark:border-zinc-700 dark:border-l-emerald-400" : "border-amber-500 border-l-amber-500 dark:border-amber-400 dark:border-l-amber-400"}`}>
            <h3 className="font-semibold">{source.system}</h3>
            <p>{source.state}{!fullyCovered(source) && <strong className="ml-2 text-sm">Limits alignment</strong>}</p>
            {source.coverage && <p>Observed coverage: {source.coverage}</p>}
            {source.capturedAt && <p className="break-all">Captured: {source.capturedAt}</p>}
            {source.snapshotId && <p className="break-all">Snapshot: {source.snapshotId}</p>}
          </div>)}
        </div>
      </section>
      {report.diagnostics.map((diagnostic, index) => <p key={`${diagnostic.code}:${index}`} className="break-words rounded border border-amber-500 p-3 text-sm dark:border-amber-400">
        <strong>{diagnostic.code}:</strong> {diagnostic.message}
      </p>)}
      <section aria-label="Ownership findings" className="min-w-0 space-y-3">
        <h2 ref={findingsHeading} tabIndex={-1} className="text-lg font-semibold">Findings {range}{" "}<span className="block text-sm font-normal">Findings requiring action are listed first. Page {page + 1} of {pages}.</span></h2>
        <p aria-live="polite" className="text-sm">Showing {range} findings across the granted scope. Groups change by audience, not evidence.</p>
        {report.groups.length > 0 && <details className="text-sm"><summary className="cursor-pointer py-1">{plural(report.groups.length, "group")} for the {audience} audience</summary>
          <ul className="mt-1 flex flex-wrap gap-2">{report.groups.map(group => <li key={group.key} className="min-w-0 break-all rounded border border-zinc-300 px-2 py-0.5 dark:border-zinc-700">{group.key}: {group.findingIds.length}</li>)}</ul>
        </details>}
        {shown.map(item => <article key={item.findingId} data-finding-id={item.findingId} className={`min-w-0 space-y-2 break-words rounded border border-l-4 border-zinc-300 p-3 dark:border-zinc-700 ${STATUS[item.status].tone}`}>
          <h3 className="break-all font-semibold"><span className={`${BADGE} mr-2`}>{STATUS[item.status].label}</span>{item.repositoryId}: {item.kind} ({item.status})</h3>
          <p>{item.reason}</p>
          <p><strong>Next action:</strong> {item.nextAction}</p>
          <p><strong>Verify:</strong> {item.verification}</p>
          <p><strong>Resolution:</strong> {item.resolutionOwner ? item.resolutionOwner.partyRef : "Needs triage: assign a reviewed resolution owner."}</p>
          {item.exception && <p className="break-all">Declared exception: {item.exception.exceptionId}; expires {item.exception.expiresAt}. Review provenance is not authenticated.</p>}
          <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-2">
            <div className="min-w-0"><dt className="font-semibold">Observed ownership</dt>
              <dd>{item.observedRoles.length ? item.observedRoles.map((role, index) => <p key={index} className="break-all">{role.system} / {role.role}: {role.party ? `${role.party.kind} ${role.party.id}` : "No party recorded"}{role.unruled && " (unruled observation)"}</p>) : "Not established"}</dd></div>
            <div className="min-w-0"><dt className="font-semibold">Expected roles</dt>
              <dd>{item.expectedRoles.length ? item.expectedRoles.map((role, index) => <p key={index} className="break-all">{role.system} / {role.role}</p>) : "See the verification action"}</dd></div>
          </dl>
          <ul className="space-y-1 text-sm">{item.evidence.map((evidence, index) => {
            const link = ownershipEvidenceLink(evidence.link);
            return <li key={index} className="break-all">{evidence.system}: {link ? <a href={link} rel="noreferrer" className="underline">{evidence.sourceRecordId}</a> : evidence.sourceRecordId}
              {evidence.link && !link && <span> Invalid source link; verify the configured evidence.</span>}
              {" "}{evidence.capturedAt}; {evidence.freshness}; {evidence.coverage}; snapshot {evidence.snapshotId}; digest {evidence.digest}
            </li>;
          })}</ul>
          <p className="break-all text-xs">Finding {item.findingId}; fingerprint {item.fingerprint}</p>
        </article>)}
        <nav aria-label="Findings pages" className="flex flex-wrap items-center gap-3">
          <button type="button" disabled={page === 0} onClick={() => movePage(page - 1)} className={BUTTON}>Previous findings</button>
          <button type="button" disabled={page + 1 >= pages} onClick={() => movePage(page + 1)} className={BUTTON}>Next findings</button>
          <span className="text-sm">Page {page + 1} of {pages}</span>
        </nav>
      </section>
      <details className="break-all"><summary className="cursor-pointer py-1">Report identity</summary><p>{report.inputVector}</p><p>Evaluated: {report.generatedAt}</p></details>
    </>}
  </section>;
}
