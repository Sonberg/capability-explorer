import { useEffect, useRef, useState } from 'react';
import { request } from '../api';
import { ErrorBox, H1, Loading, Muted } from '../components/ui';
import { useApi, useSelection } from '../state';
import { canExportPreview, editPreview, previewEntries, reviewPreview, type PreviewCatalog, type PreviewDraft, type PreviewResult } from './BindingPreviewState';

export function BindingPreview() {
  const { scope } = useSelection();
  const catalog = useApi<PreviewCatalog>(`/api/binding-preview?${scope}`);
  const [draft, setDraft] = useState<PreviewDraft>({});
  const [result, setResult] = useState<PreviewResult>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const epoch = useRef(0);
  useEffect(() => { epoch.current++; setDraft({}); setResult(undefined); setError(undefined); setBusy(false); }, [scope, catalog.data?.fingerprint]);
  const invalidate = () => { epoch.current++; setResult(undefined); setError(undefined); setBusy(false); };
  const submit = async (download = false) => {
    if (!catalog.data || catalog.loading) return;
    const current = ++epoch.current;
    const body = JSON.stringify({ expectedFingerprint: catalog.data.fingerprint, entries: previewEntries(draft) });
    const init = { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CE-Binding-Preview': '1' }, body };
    setBusy(true); setError(undefined);
    try {
      if (download) {
        const response = await fetch(`/api/binding-preview/export?${scope}`, init);
        if (!response.ok) { const failure = await response.json(); throw new Error(failure.error ?? `Export failed (${response.status})`); }
        const blob = await response.blob();
        if (current !== epoch.current) return;
        const url = URL.createObjectURL(blob), link = document.createElement('a');
        link.href = url; link.download = 'bindings-reviewed-proposal.json'; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      } else {
        const next = await request<PreviewResult>(`/api/binding-preview?${scope}`, init);
        if (current === epoch.current) setResult(next);
      }
    } catch (e) { if (current === epoch.current) { setResult(undefined); setError(e instanceof Error ? e.message : 'Preview failed'); } }
    finally { if (current === epoch.current) setBusy(false); }
  };
  if (catalog.error) return <ErrorBox message={catalog.error} />;
  if (!catalog.data || catalog.loading) return <Loading what="Loading revision-bound binding hints" />;
  const data = catalog.data;
  return <section>
    <H1>Review integration bindings</H1>
    <Muted>Hints come from the selected source artifacts. Choose destinations explicitly; names and matching routes alone do not prove a connection. Preview and export never adopt mappings or change the database.</Muted>
    <p className="my-3 text-sm">Snapshot <code title={data.fingerprint}>{data.fingerprint.slice(0, 16)}</code> · {data.sources.length} source units. Changing revisions clears this review.</p>
    {error && <ErrorBox message={error} />}
    <datalist id="binding-preview-targets">{data.targets.map(t => <option key={t} value={t} />)}</datalist>
    <Muted>Suggestions list declared HTTP services. Other resources require an explicit manual name and remain human assertions.</Muted>
    <div className="space-y-4">
      {data.hints.map(h => {
        const entry = draft[h.hint];
        const row = result?.rows.find(r => r.hint === h.hint);
        return <article key={h.hint} className="rounded-lg border border-zinc-200 p-4 dark:border-zinc-700">
          <h2 className="break-all font-mono text-sm">{h.hint}</h2>
          {h.existingTarget && <p className="mt-1 text-sm">Existing mapping: <code>{h.existingTarget}</code>. A different destination requires conflict resolution.</p>}
          <details className="my-2 text-sm"><summary>Source provenance ({h.occurrences.length} edges)</summary><ul className="mt-2 space-y-1">{h.occurrences.map(o => <li key={`${o.source.repositoryId}/${o.source.unitPath}/${o.edgeId}`} className="break-all"><code>{o.source.repositoryId} @ {o.source.commitSha} · {o.source.unitPath}</code> · {o.relation} · {o.edgeId}<br /><span className="text-xs">Artifact {o.source.artifactHash}; evidence {o.evidence.join(', ')}</span></li>)}</ul></details>
          <div className="grid gap-3 md:grid-cols-2">
            <label className="text-sm">Destination<input aria-label={`Destination for ${h.hint}`} list="binding-preview-targets" value={entry?.target ?? ''} maxLength={512} onChange={e => { invalidate(); setDraft(d => editPreview(d,h.hint,'target',e.target.value)); }} className="mt-1 w-full rounded border border-zinc-300 bg-transparent px-3 py-2 dark:border-zinc-600" placeholder="repository/Service or named resource" /></label>
            <label className="text-sm">Review note<input aria-label={`Review note for ${h.hint}`} value={entry?.note ?? ''} maxLength={2000} onChange={e => { invalidate(); setDraft(d => editPreview(d,h.hint,'note',e.target.value)); }} className="mt-1 w-full rounded border border-zinc-300 bg-transparent px-3 py-2 dark:border-zinc-600" placeholder="Why this destination is appropriate; do not include secrets" /></label>
          </div>
          <label className="mt-3 flex items-start gap-2 text-sm"><input type="checkbox" checked={entry?.reviewed ?? false} disabled={!entry?.target.trim()} onChange={e => { invalidate(); setDraft(d => reviewPreview(d,h.hint,e.target.checked)); }} />I reviewed this human mapping against the source and destination.</label>
          {row && <p role="status" className="mt-3 text-sm"><strong>{row.state}</strong> · {row.message} ({row.edgeCount} edges)</p>}
        </article>;
      })}
    </div>
    {data.hints.length === 0 && <p className="my-4">No unbound edge hints in this selection.</p>}
    <div className="my-5 flex flex-wrap gap-3">
      <button type="button" disabled={busy || previewEntries(draft).length === 0} onClick={() => void submit()} className="rounded bg-blue-600 px-4 py-2 text-white disabled:opacity-40">Preview mappings</button>
      <button type="button" disabled={busy || !canExportPreview(result,data.fingerprint)} onClick={() => void submit(true)} className="rounded border border-zinc-400 px-4 py-2 disabled:opacity-40">Export reviewed proposal</button>
      <button type="button" onClick={() => { invalidate(); setDraft({}); catalog.reload(); }} className="rounded border border-zinc-400 px-4 py-2">Reload source</button>
    </div>
    <Muted>The export preserves existing mappings and adds your reviewed entries. It includes exact revision and artifact hashes. Adoption remains a separate owner-reviewed action.</Muted>
  </section>;
}
