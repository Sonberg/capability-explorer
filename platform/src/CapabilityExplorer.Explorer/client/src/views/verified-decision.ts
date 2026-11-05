import type { ModelEvidenceView, ModelScope, ModelSourceView } from "../models/contracts";
import type { DecisionResolution } from "../models/decision-links";
import { buildModelIndex } from "../models/model-index";
import { getJson, scopedRequest, validateEvidence, validateSource } from "./model-view-data";

export async function verifyDecisionTarget(resolution: DecisionResolution, scope: string, snapshot: ModelScope, signal: AbortSignal) {
  if (resolution.status !== "available" || !resolution.target || !resolution.decisionId) throw new Error(resolution.message);
  const { target, decisionId } = resolution;
  const query = `id=${encodeURIComponent(target.id)}&${scopedRequest(scope, snapshot)}`;
  const view = await getJson<ModelEvidenceView>(`/api/models/evidence?${query}`, signal, 16_777_216);
  const evidence = validateEvidence(target, view, snapshot);
  if (evidence.schema !== "ce.models.v1" || !evidence.elements.some(element =>
    element.id === decisionId && element.namespace === "https://www.omg.org/spec/DMN/20191111/MODEL/" &&
    ["decision", "decisionService"].includes(element.type))) {
    throw new Error("The referenced decision or decision service is absent from the target DMN evidence.");
  }
  const source = await getJson<ModelSourceView>(`/api/models/source?${query}`, signal, 8_388_608);
  await validateSource(target, evidence, source, snapshot);
  if (source.format !== "dmn+xml") throw new Error("The referenced source is not a DMN document.");
  if (signal.aborted) throw new Error("Decision verification was cancelled because the source selection changed.");
  const occurrence = buildModelIndex(evidence).occurrences.get(decisionId)?.[0];
  return { target, decisionId, source, diagramId: occurrence?.diagramId };
}
