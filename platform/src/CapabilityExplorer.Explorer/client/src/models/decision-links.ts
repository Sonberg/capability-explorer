import type { ModelSummary } from "./contracts";

export const DECISION_LINK_NS = "urn:capability-explorer:model-links:v1";
export type DecisionReference = { elementId: string; label: string; references: string[] };
export type DecisionResolution = {
  status: "available" | "external" | "missing" | "ambiguous" | "denied";
  message: string; target?: ModelSummary; decisionId?: string;
};

export function readDecisionReferences(xml: string): DecisionReference[] {
  if (new TextEncoder().encode(xml).length > 1_048_576 || /<!\s*(DOCTYPE|ENTITY)\b/i.test(xml)) {
    throw new Error("Decision references require bounded source without DTD or entity declarations.");
  }
  const document = new DOMParser().parseFromString(xml, "application/xml");
  if (document.querySelector("parsererror")) throw new Error("Decision references cannot be read from malformed source.");
  const namespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";
  if (document.documentElement.namespaceURI !== namespace || document.documentElement.localName !== "definitions") {
    throw new Error("Decision references require a BPMN definitions document.");
  }
  const elements = [...document.getElementsByTagName("*")];
  if (elements.length > 500 || elements.some(element => element.localName.toLowerCase() === "script")) {
    throw new Error("Decision references require bounded source without executable script elements.");
  }
  return [...document.getElementsByTagNameNS(namespace, "*")].flatMap(element => {
    const raw = [element.getAttributeNS(DECISION_LINK_NS, "decisionRef"), element.getAttributeNS(DECISION_LINK_NS, "decisionRefs")].filter(Boolean).join(" ");
    const elementId = element.getAttribute("id");
    if (!raw || !elementId) return [];
    return [{ elementId, label: element.getAttribute("name") || elementId, references: [...new Set(raw.trim().split(/\s+/).filter(Boolean))] }];
  });
}

export function resolveDecisionReference(reference: string, source: ModelSummary, summaries: ModelSummary[]): DecisionResolution {
  const parts = reference.split("#");
  const [path, decisionId] = parts;
  if (parts.length !== 2 || !path || !decisionId || /[:\\?%]/.test(reference) || path.startsWith("/") ||
      path.split("/").some(part => !part || part === "." || part === "..") || !/^[A-Za-z_][\w.-]*$/.test(decisionId)) {
    return { status: "external", message: "Unsupported or external reference. Only explicit relative DMN paths and decision IDs are resolved; nothing is fetched externally." };
  }
  const directory = source.unitPath.includes("/") ? source.unitPath.slice(0, source.unitPath.lastIndexOf("/") + 1) : "";
  const matches = summaries.filter(item => item.kind === "authored" && item.modelKind === "dmn" &&
    item.repositoryId === source.repositoryId && item.commitSha === source.commitSha && item.unitPath === `${directory}${path}`);
  if (matches.length > 1) return { status: "ambiguous", message: "More than one target matches this exact source revision; no target was chosen." };
  const target = matches[0];
  if (!target) return { status: "missing", message: "The referenced DMN is not in the loaded summaries at this exact repository revision. It may be outside the loaded scope." };
  if (target.source.status !== "available") return {
    status: target.source.status === "denied" ? "denied" : "missing",
    message: `The referenced DMN source is ${target.source.status}; no alternate source is used.`,
  };
  return { status: "available", message: "Same repository and revision. Target evidence is verified before opening.", target, decisionId };
}
