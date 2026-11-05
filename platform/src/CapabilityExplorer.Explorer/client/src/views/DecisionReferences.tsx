import { useMemo, useState } from "react";
import type { ModelScope, ModelSourceView, ModelSummary } from "../models/contracts";
import { readDecisionReferences, resolveDecisionReference, type DecisionResolution } from "../models/decision-links";
import type { ModelDecisionPreview, ModelSandbox } from "../models/viewer-sandbox";
import type { DecisionCaller } from "./useDecisionNavigation";
import { verifyDecisionTarget } from "./verified-decision";
import { failureDescription } from "./model-view-data";

export function useDecisionReferences({ scope, snapshot, source, summary, summaries, getSandbox, diagramId, insightsOpen, onOpen, select }: {
  scope: string; snapshot?: ModelScope;
  source?: ModelSourceView; summary: ModelSummary; summaries: ModelSummary[];
  getSandbox(): ModelSandbox | undefined; diagramId?: string; insightsOpen: boolean;
  onOpen(resolution: DecisionResolution, caller: DecisionCaller): Promise<void>;
  select(id: string): void;
}) {
  const parsed = useMemo(() => {
    try { return { items: source?.format === "bpmn+xml" ? readDecisionReferences(source.xml) : [], error: "" }; }
    catch (error) { return { items: [], error: (error as Error).message }; }
  }, [source?.sha256]);
  const [error, setError] = useState("");
  const [opening, setOpening] = useState(false);
  const entries = parsed.items.map(item => ({ ...item, targets: item.references.map(reference =>
    ({ reference, resolution: resolveDecisionReference(reference, summary, summaries) })) }));
  const open = async (elementId: string, resolution: DecisionResolution) => {
    const item = entries.find(entry => entry.elementId === elementId);
    if (opening) return;
    setError("");
    setOpening(true);
    try {
      const sandbox = getSandbox();
      if (!item || !source || !sandbox) throw new Error("The verified source viewer is not ready to preserve the caller view.");
      if (resolution.status !== "available") throw new Error(resolution.message);
      const viewport = await sandbox.captureView();
      await onOpen(resolution, { modelId: summary.id, sourceSha256: source.sha256, elementId,
        label: item.label, diagramId, viewport, insightsOpen });
    } catch (failure) {
      select(elementId);
      setError((failure as Error).message);
    } finally { setOpening(false); }
  };
  return {
    entries, open, error: parsed.error || error, opening,
    async preview(id: string, signal: AbortSignal): Promise<ModelDecisionPreview> {
      try {
        const entry = entries.find(item => item.elementId === id);
        if (!source || !snapshot || !entry) throw new Error("The verified source reference is unavailable.");
        if (entry.targets.length !== 1) throw new Error("This task references multiple decisions. Open its references to choose the exact decision.");
        const verified = await verifyDecisionTarget(entry.targets[0].resolution, scope, snapshot, signal);
        return { source: verified.source.xml, decisionId: verified.decisionId, diagramId: verified.diagramId,
          provenance: `${verified.target.repositoryId} @ ${verified.target.commitSha.slice(0, 12)} · ${verified.target.unitPath}` };
      } catch (failure) {
        return { error: failureDescription((failure as Error).message) };
      }
    },
    overlays: entries.map(entry => ({ elementId: entry.elementId, label: entry.label, references: entry.references,
      preview: entry.targets.map(target => target.resolution.message).join(" "),
      available: entry.targets.some(target => target.resolution.status === "available") })),
    activate(id: string) {
      const entry = entries.find(item => item.elementId === id);
      if (entry?.targets.length === 1) void open(id, entry.targets[0].resolution);
      else select(id);
    },
  };
}
export type DecisionReferencesState = ReturnType<typeof useDecisionReferences>;

export function DecisionReferences({ state, selected }: { state: DecisionReferencesState; selected: string }) {
  const entry = state.entries.find(item => item.elementId === selected);
  return <>
    {state.error && <div role="alert" className="rounded border border-amber-400 bg-amber-50 p-3 pr-20 text-sm text-amber-900">
      <p>{state.error}</p>
      {/stale snapshot/i.test(state.error) && <button type="button" className="mt-2 underline"
        onClick={() => window.location.reload()}>Reload current selection</button>}
    </div>}
    {entry && <section className="rounded border border-blue-200 bg-white p-3 pr-16 text-sm dark:border-blue-900 dark:bg-zinc-900" aria-label="Authored decision reference">
      <h3 className="font-semibold">Authored decision reference</h3>
      <p className="text-xs">Explorer annotation from the verified BPMN source, not a standard BPMN marker or runtime binding. No rules are evaluated.</p>
      {entry.targets.map(({ reference, resolution }) => <div key={reference} className="mt-2 space-y-1">
        <p className="break-all font-mono text-xs">{reference}</p>
        <p className="text-xs">{resolution.message}</p>
        {resolution.target && <p className="break-all text-xs">{resolution.target.repositoryId} @ {resolution.target.commitSha.slice(0, 12)} · {resolution.target.unitPath}</p>}
        {resolution.status === "available" && <button className="rounded border border-blue-400 px-2 py-1 text-blue-700 disabled:opacity-50 dark:text-blue-300"
          disabled={state.opening} onClick={() => void state.open(entry.elementId, resolution)}>Open decision {resolution.decisionId}</button>}
      </div>)}
      {state.opening && <p role="status">Verifying the referenced decision and exact source revision…</p>}
    </section>}
  </>;
}
