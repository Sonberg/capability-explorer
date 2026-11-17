import type { ApiResponse, Element, Process, Snapshot } from "../src/contracts";
import { BPMN, BPMNDI, DC, DI } from "../src/document-model";

const coverage = { status: "partial", supported: ["task", "sequenceFlow"], limitations: ["Synthetic extension not interpreted"] };
const element = (id: string, order: number): Element => ({
  identity: `el:${id}`, kind: "bpmn", namespace: "urn:synthetic:bpmn", id, type: "task", order,
  attributes: { name: `Task ${id}` }, source: { path: "synthetic/intake.bpmn", element: id },
});
const process = (id: string): Process => ({
  processIdentity: `proc:${id}`, processId: id, path: `synthetic/${id}.bpmn`,
  sha256: "a".repeat(64), byteLength: 12, contextTags: [], phaseTags: [],
  coverage: structuredClone(coverage), diagnostics: [], elements: [], relationships: [],
});

export function fixture(): ApiResponse {
  const intake = process("intake"), review = process("review");
  intake.name = "Synthetic intake";
  intake.proposalStatus = "proposed";
  intake.contextTags = ["front-office"];
  intake.phaseTags = ["discovery"];
  intake.elements = [element("later", 20), element("first", 10)];
  intake.elements[1].attributes.name = "<img src=x onerror=alert(1)> Synthetic check";
  intake.elements[0].kind = "opaqueExtension";
  intake.relationships = [{
    identity: "rel:1", kind: "messageFlow", source: "el:first", target: "el:later", order: 0,
    condition: { text: "window.synthetic = true", language: "javascript" },
  }];
  intake.diagnostics = [{ code: "synthetic-unsupported", message: "Synthetic extension retained", disposition: "partial" }];
  review.name = "Synthetic review";
  review.elements = [element("review-task", 1)];
  review.contextTags = ["back-office"];
  review.phaseTags = ["delivery"];
  const resolved = {
    processId: "review", processIdentity: "proc:review", elementId: "review-task", elementIdentity: "el:review-task", resolved: true,
  };
  const unresolved = { processId: "not-admitted", elementId: "missing", resolved: false };
  const snapshot: Snapshot = {
    schema: "ce.process-landscape.v1", snapshotId: "synthetic-snapshot", repositoryId: "synthetic-repository",
    manifestVersion: 1, contentProfile: "structural", coverage, diagnostics: [], processes: [intake, review],
    searchIndex: [{
      term: "Synthetic check", kind: "element", processIdentity: intake.processIdentity,
      elementIdentity: "el:first", deepLink: "process:proc:intake#el:first",
    }],
    sources: [intake, review].map(p => ({ path: p.path, byteLength: p.byteLength, sha256: p.sha256 })),
    crossProcessEvents: [{ eventId: "synthetic-handoff", label: "Review requested", source: unresolved, target: resolved }],
    objectLifecycle: [{
      objectId: "synthetic-object", label: "Synthetic object",
      stages: [{ ...resolved, stage: "Reviewed", order: 2 }, { ...unresolved, stage: "Created", order: 1 }],
    }],
    phaseJourney: [
      { phaseId: "delivery", order: 2, steps: [{ ...resolved, order: 2 }, { ...unresolved, order: 1 }] },
      { phaseId: "discovery", order: 1, steps: [{ ...resolved, order: 1 }] },
    ],
    overlays: { context: [{ tag: "shared-context", processIdentities: ["proc:review"] }], phase: [] },
    navigator: {
      verified: false, bundle: { path: "synthetic/bundle.js", byteLength: 4, sha256: "b".repeat(64) },
      catalogue: [{ name: "DO_NOT_TRUST_UNVERIFIED_CATEGORY", status: "approved" }],
    },
  };
  return {
    schema: "ce.process-landscape.v1", status: "ok", snapshot,
    identityVerification: { status: "Unverifiable", reason: "Synthetic sidecar provenance is not exposed" },
  };
}

export function sourceFixture(): ApiResponse {
  const data = fixture();
  data.snapshot!.navigator = {
    verified: true, bundle: { path: "synthetic/bundle.js", byteLength: 4, sha256: "b".repeat(64) },
    procFile: { intake: "intake.bpmn", review: "review.bpmn" },
    catalogue: [{
      file: "intake.bpmn", proc: "intake", procs: ["intake"], name: "Synthetic source catalogue",
      group: "Synthetic group", purpose: "Synthetic purpose", ctx: ["source-context"], phases: ["source-phase"],
    }],
    events: {
      "Synthetic source event": {
        ctx: "source-context", phase: "source-phase", kind: "signal",
        throw: [["intake.bpmn", "first"]], catch: [["not-admitted.bpmn", "missing"]], ref: [],
      },
    },
    lifecycle: [{
      name: "Synthetic source lifecycle", entity: "Synthetic entity", obj: "synthetic-object", ctx: "source-context",
      states: ["New", "Ready"], tr: [{ f: "New", t: "Ready", guard: "window.synthetic = true" }],
      set: { Ready: [["review.bpmn", "review-task", "synthetic-via"]] },
    }],
    journey: {
      phases: [
        { id: "source-phase", name: "Synthetic first phase", contexts: ["source-context"], files: ["intake.bpmn"] },
        { id: "source-later", name: "Synthetic second phase", contexts: [], files: ["missing.bpmn"] },
      ],
      entries: [{ name: "Synthetic entry point", file: "review.bpmn", text: "Synthetic entry description" }],
    },
  };
  return data;
}

export function documentFixture(secondPlane = false): ApiResponse {
  const data = fixture(), s = data.snapshot!, path = "synthetic/collaboration.bpmn";
  for (const p of s.processes) {
    p.path = path;
    for (const e of p.elements) { e.source.path = path; e.namespace = BPMN; }
  }
  s.sources = [{ path, byteLength: 12, sha256: "a".repeat(64) }];
  const node = (id: string, type: string, namespace: string, parent?: string, attributes: Record<string, string> = {}, order = 0): Element => ({
    identity: `doc:${id}`, kind: "bpmn", namespace, id, type, order, parent,
    attributes, source: { path, element: id },
  });
  const shapes = [
    node("pool-a-shape", "BPMNShape", BPMNDI, "doc:plane", { bpmnElement: "pool-a" }),
    node("pool-a-bounds", "Bounds", DC, "doc:pool-a-shape", { x: "10", y: "20", width: "500", height: "180" }),
    node("pool-b-shape", "BPMNShape", BPMNDI, "doc:plane", { bpmnElement: "pool-b" }),
    node("pool-b-bounds", "Bounds", DC, "doc:pool-b-shape", { x: "10", y: "240", width: "500", height: "180" }),
    node("task-shape", "BPMNShape", BPMNDI, "doc:plane", { bpmnElement: "first" }),
    node("task-bounds", "Bounds", DC, "doc:task-shape", { x: "50", y: "80", width: "140", height: "70" }),
  ];
  s.documents = [{
    path, elements: [
      node("definitions", "definitions", BPMN),
      node("collab", "collaboration", BPMN, "doc:definitions", { name: "Synthetic collaboration" }),
      node("pool-a", "participant", BPMN, "doc:collab", { name: "Synthetic sender pool", processRef: "intake" }),
      node("pool-b", "participant", BPMN, "doc:collab", { name: "Synthetic black-box pool" }),
      node("message", "messageFlow", BPMN, "doc:collab", { name: "Synthetic authored message", sourceRef: "first", targetRef: "pool-b" }),
      node("unresolved", "messageFlow", BPMN, "doc:collab", { name: "Synthetic unresolved message", sourceRef: "review-task", targetRef: "pool-a" }),
      node("diagram", "BPMNDiagram", BPMNDI, "doc:definitions", { name: "Synthetic authored diagram" }),
      node("plane", "BPMNPlane", BPMNDI, "doc:diagram", { bpmnElement: "collab" }),
      ...shapes,
      node("message-edge", "BPMNEdge", BPMNDI, "doc:plane", { bpmnElement: "message" }),
      node("message-point-last", "waypoint", DI, "doc:message-edge", { x: "120", y: "240" }, 1),
      node("message-point-first", "waypoint", DI, "doc:message-edge", { x: "120", y: "150" }, 0),
      node("omitted-edge", "BPMNEdge", BPMNDI, "doc:plane", { bpmnElement: "unresolved" }),
      node("omitted-first", "waypoint", DI, "doc:omitted-edge", { x: "200", y: "250" }, 0),
      node("omitted-last", "waypoint", DI, "doc:omitted-edge", { x: "200", y: "200" }, 1),
    ],
    relationships: [{
      identity: "doc-relationship:message", kind: "messageFlow", elementIdentity: "doc:message",
      source: "el:first", target: "doc:pool-b", order: 0,
    }],
  }];
  s.diagnostics.push({
    code: "unrepresented-message-flow-endpoint", message: "Synthetic endpoint has no unambiguous participant",
    disposition: "unresolved", source: { path, element: "unresolved", ref: "review-task" },
  });
  if (secondPlane) s.documents[0].elements.push(
    node("second-diagram", "BPMNDiagram", BPMNDI, "doc:definitions", { name: "Synthetic alternate diagram" }),
    node("second-plane", "BPMNPlane", BPMNDI, "doc:second-diagram", { bpmnElement: "collab" }),
    node("second-pool-shape", "BPMNShape", BPMNDI, "doc:second-plane", { bpmnElement: "pool-b" }),
    node("second-pool-bounds", "Bounds", DC, "doc:second-pool-shape", { x: "-20", y: "40", width: "300", height: "100" }),
  );
  return data;
}
