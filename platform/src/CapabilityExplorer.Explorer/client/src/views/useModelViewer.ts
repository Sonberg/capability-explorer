import { useEffect, useRef, useState } from "react";
import type { AuthoredModelEvidence, ModelScope, ModelSourceView, ModelSummary } from "../models/contracts";
import { createModelSandbox, loadTrustedViewerAssets, type ModelDecisionPreview, type ModelSandbox } from "../models/viewer-sandbox";
import type { ModelIndex } from "../models/model-index";
import type { DecisionCaller } from "./useDecisionNavigation";

export function useModelViewer({ summary, source, snapshot, authored, index, diagramId, selectedElement, setSelectedElement,
  onViewerState, onDiagramRoot, onNotice, decisionLinks, onDecisionReference, onDecisionPreview, restore, onRestored }: {
  summary: ModelSummary; source?: ModelSourceView; snapshot?: ModelScope; authored?: AuthoredModelEvidence; index?: ModelIndex;
  diagramId?: string; selectedElement: string; setSelectedElement(id: string): void;
  onViewerState(status: string): void; onDiagramRoot(rootId: string): void; onNotice(message: string): void;
  decisionLinks: Array<{ elementId: string; label: string; available: boolean; references?: string[]; preview?: string }>;
  onDecisionReference(id: string): void; restore?: DecisionCaller; onRestored(): void;
  onDecisionPreview(id: string, signal: AbortSignal): Promise<ModelDecisionPreview>;
}) {
  const frameHost = useRef<HTMLDivElement>(null);
  const sandbox = useRef<ModelSandbox | undefined>(undefined);
  const [frameStatus, setFrameStatus] = useState("idle");
  const [frameError, setFrameError] = useState("");
  const [renderedIds, setRenderedIds] = useState<string[]>([]);
  const [measured, setMeasured] = useState(false);
  const latest = useRef({ selectedElement, setSelectedElement, onViewerState, onDiagramRoot, onNotice, onDecisionReference, onDecisionPreview, restore, onRestored });
  latest.current = { selectedElement, setSelectedElement, onViewerState, onDiagramRoot, onNotice, onDecisionReference, onDecisionPreview, restore, onRestored };
  useEffect(() => {
    if (!frameHost.current || summary.kind !== "authored" || !source || !snapshot) return;
    const controller = new AbortController();
    let live = true;
    let instance: ModelSandbox | undefined;
    let unsubscribe: (() => void) | undefined;
    const previews = new Map<string, AbortController>();
    setFrameError("");
    setRenderedIds([]);
    setMeasured(false);
    setFrameStatus("Loading trusted read-only viewer assets…");
    const report = (ids: string[], rootId?: string) => {
      const authoredIds = ids.filter(id => index?.byId.has(id));
      setRenderedIds(authoredIds);
      setMeasured(true);
      const status = `Rendered ${authoredIds.length} source elements in the active viewer scope`;
      setFrameStatus(status);
      latest.current.onViewerState(status);
      if (rootId) latest.current.onDiagramRoot(rootId);
    };
    void loadTrustedViewerAssets(controller.signal).then(assets => {
      if (!live || !frameHost.current) return;
      instance = createModelSandbox(frameHost.current, assets);
      sandbox.current = instance;
      unsubscribe = instance.subscribe(event => {
        if (!event.acceptedByParent || event.sourceId !== summary.id || event.generation !== snapshot.generation || event.scope !== snapshot.snapshot) return;
        if (event.status === "rendered" || event.status === "navigation") {
          setFrameError("");
          report(event.renderedIds || [], event.rootId);
          if (event.status === "rendered" && latest.current.selectedElement) {
            const id = latest.current.selectedElement;
            const relationship = authored?.relationships.find(item => item.identity === id);
            const sourceId = relationship && authored?.elements.find(item => item.identity === relationship.source)?.id;
            const targetId = relationship && authored?.elements.find(item => item.identity === relationship.target)?.id;
            if (sourceId && targetId) instance?.selectRelationship(sourceId, targetId);
            else instance?.selectElement(id);
          } else if (event.elementId) latest.current.setSelectedElement(event.elementId);
          if (event.status === "rendered" && latest.current.restore?.modelId === summary.id) {
            if (latest.current.restore.sourceSha256 !== source.sha256) {
              setFrameError("The caller source changed; its saved viewport was not restored.");
              latest.current.onRestored();
            }
            else instance?.restoreView(latest.current.restore.viewport);
          }
        } else if (event.status === "view-restored" || event.status === "view-restore-error") {
          if (event.status === "view-restore-error") setFrameError(event.message || "The saved caller view could not be restored.");
          latest.current.onRestored();
        } else if (event.status === "decision-reference" && event.elementId) {
          latest.current.onDecisionReference(event.elementId);
        } else if (event.status === "decision-preview-request" && event.elementId && event.requestId) {
          const { elementId, requestId } = event;
          for (const pending of previews.values()) pending.abort();
          previews.clear();
          const pending = new AbortController();
          previews.set(requestId, pending);
          const signal = AbortSignal.any([controller.signal, pending.signal]);
          void latest.current.onDecisionPreview(elementId, signal).then(preview => {
            if (live && !signal.aborted) instance?.decisionPreview(requestId, elementId, preview);
          }).catch((failure: Error) => {
            if (live && !signal.aborted) instance?.decisionPreview(requestId, elementId, { error: failure.message });
          }).finally(() => { if (previews.get(requestId) === pending) previews.delete(requestId); });
        } else if (event.status === "decision-preview-cancel" && event.requestId) {
          previews.get(event.requestId)?.abort();
          previews.delete(event.requestId);
        } else if (event.status === "navigation-notice") {
          latest.current.onNotice(event.message || "This model target cannot be opened.");
          if (event.elementId) latest.current.setSelectedElement(event.elementId);
        } else if (event.status === "selection-cleared") {
          latest.current.setSelectedElement("");
        } else if (event.status === "selection" && event.elementId) {
          const matches = authored?.relationships.filter(relationship =>
            index?.byIdentity.get(relationship.source)?.id === event.relationshipSourceId &&
            index?.byIdentity.get(relationship.target)?.id === event.relationshipTargetId) || [];
          latest.current.setSelectedElement(matches.length === 1 ? matches[0].identity : event.elementId);
        } else if (event.status === "selection-result") {
          setFrameError(event.relationshipMatchCount === 1 ? "" :
            "Text relationship selection could not be matched to exactly one viewer connection; textual evidence remains available.");
        } else if (!["accepted", "import-started", "view-state"].includes(event.status)) {
          setMeasured(false);
          setFrameError(`${event.status}: ${event.message || "The read-only renderer could not display this source."}`);
          setFrameStatus("Viewer unavailable");
          latest.current.onViewerState("Viewer unavailable");
        }
      });
      instance.render({ kind: summary.modelKind === "dmn" ? "dmn" : "bpmn", source: source.xml,
        scope: snapshot.snapshot, generation: snapshot.generation, sourceId: summary.id, diagramId, decisionLinks });
    }).catch((error: Error) => {
      if (live && error.name !== "AbortError") {
        setFrameStatus("Viewer unavailable");
        setFrameError(`Trusted viewer assets could not be loaded: ${error.message}`);
        latest.current.onViewerState("Viewer unavailable");
      }
    });
    return () => {
      live = false;
      controller.abort();
      for (const pending of previews.values()) pending.abort();
      previews.clear();
      unsubscribe?.();
      instance?.dispose();
      if (sandbox.current === instance) sandbox.current = undefined;
    };
  }, [summary.id, summary.modelKind, source?.sha256, snapshot?.snapshot, snapshot?.generation, diagramId]);
  return { frameHost, sandbox, frameStatus, frameError, renderedIds, measured };
}
