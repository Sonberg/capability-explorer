import { useEffect, useRef, useState } from "react";
import type { ModelScope, ModelSummary } from "../models/contracts";
import type { ModelViewport } from "../models/viewer-sandbox";
import type { DecisionResolution } from "../models/decision-links";
import { failureDescription } from "./model-view-data";
import { verifyDecisionTarget } from "./verified-decision";

export type DecisionCaller = {
  modelId: string; sourceSha256: string; elementId: string; label: string;
  diagramId?: string; viewport: ModelViewport; insightsOpen: boolean;
};

export function useDecisionNavigation({ scope, snapshot, selected, updateRoute }: {
  scope: string; snapshot?: ModelScope; selected?: ModelSummary;
  updateRoute(updates: Record<string, string | undefined>): void;
}) {
  const [caller, setCaller] = useState<DecisionCaller>();
  const [restore, setRestore] = useState<DecisionCaller>();
  const [destination, setDestination] = useState<string>();
  const request = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => request.current?.abort(), [scope, snapshot?.snapshot, snapshot?.generation, selected?.id]);
  useEffect(() => { setCaller(undefined); setRestore(undefined); setDestination(undefined); }, [scope, snapshot?.snapshot, snapshot?.generation]);
  const open = async (resolution: DecisionResolution, context: DecisionCaller) => {
    if (!snapshot || resolution.status !== "available" || !resolution.target || !resolution.decisionId) throw new Error(resolution.message);
    if (context.modelId !== selected?.id || context.sourceSha256 !== selected.source.sha256) throw new Error("The BPMN source changed; reload before following its decision reference.");
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    try {
      const { target, decisionId, diagramId } = await verifyDecisionTarget(resolution, scope, snapshot, controller.signal);
      setRestore(undefined);
      setCaller(context);
      setDestination(target.id);
      updateRoute({ id: target.id, element: decisionId, tab: diagramId ? "requirements" : "elements", diagram: diagramId });
    } catch (error) {
      throw new Error(failureDescription((error as Error).message));
    }
  };
  const back = () => {
    if (!caller) return;
    setRestore(caller);
    setCaller(undefined);
    setDestination(undefined);
    updateRoute({ id: caller.modelId, element: caller.elementId, tab: "diagram", diagram: caller.diagramId });
  };
  return { open, back, caller, restore, destination, restored: () => setRestore(undefined) };
}
