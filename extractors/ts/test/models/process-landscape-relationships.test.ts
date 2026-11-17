import assert from "node:assert/strict";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { join, resolve } from "node:path";
import test from "node:test";
import { buildProcessLandscapeSnapshot } from "../../src/models/process-landscape.ts";

const bpmn = (processId: string, taskId = "review") => `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-${processId}" targetNamespace="urn:landscape">
  <process id="${processId}" name="Sample ${processId}">
    <startEvent id="start-${processId}"/>
    <task id="${taskId}"/>
    <endEvent id="end-${processId}"/>
    <sequenceFlow id="flow-1-${processId}" sourceRef="start-${processId}" targetRef="${taskId}"/>
    <sequenceFlow id="flow-2-${processId}" sourceRef="${taskId}" targetRef="end-${processId}"/>
  </process>
</definitions>
`;

async function makeWorkspace(): Promise<string> {
  const root = resolve(".sandbox-cache", "process-landscape", randomUUID());
  await mkdir(root, { recursive: true });
  return root;
}

const manifest = (entries: Array<{ path: string; role: string }>, repositoryId = "local:order-intake") => ({
  schema: "ce.process-landscape-manifest.v1",
  manifestVersion: 1,
  repositoryId,
  entries,
});


test("Given BPMN diagram-interchange (DI/DC) content whose specific local name is not individually enumerated, it is not counted as an unsupported BPMN business element, while a genuinely unknown semantic BPMN element remains visible in coverage", async () => {
  const root = await makeWorkspace();
  try {
    const xml = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
             xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
             xmlns:omgdc="http://www.omg.org/spec/DD/20100524/DC"
             xmlns:omgdi="http://www.omg.org/spec/DD/20100524/DI"
             id="defs-diag" targetNamespace="urn:landscape">
  <process id="diag">
    <startEvent id="start-diag"/>
    <task id="review"/>
    <endEvent id="end-diag"/>
    <sequenceFlow id="flow-1-diag" sourceRef="start-diag" targetRef="review"/>
    <sequenceFlow id="flow-2-diag" sourceRef="review" targetRef="end-diag"/>
    <textAnnotation id="note-1"/>
  </process>
  <bpmndi:BPMNDiagram id="BPMNDiagram_1">
    <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="diag">
      <bpmndi:BPMNShape id="shape-start" bpmnElement="start-diag">
        <omgdc:Bounds x="0" y="0" width="10" height="10"/>
        <bpmndi:BPMNLabel>
          <omgdc:Bounds x="0" y="12" width="10" height="10"/>
        </bpmndi:BPMNLabel>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id="edge-1" bpmnElement="flow-1-diag">
        <omgdi:waypoint x="0" y="0"/>
        <omgdi:waypoint x="10" y="10"/>
      </bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</definitions>
`;
    await writeFile(join(root, "diag.bpmn"), xml);
    const input = manifest([{ path: "diag.bpmn", role: "bpmn" }]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    const process = snapshot.processes[0]!;
    const unsupported = process.diagnostics.filter((d) => d.code === "unsupported-model-element");
    assert.equal(unsupported.length, 1, "only the genuinely unknown semantic BPMN element (textAnnotation) must count as unsupported, not DI/DC layout content");
    assert.match(unsupported[0]!.message, /textAnnotation/, "the unknown semantic BPMN element must remain visible in coverage/diagnostics");
    const bpmnLabelElement = process.elements.find((element) => element.type === "BPMNLabel");
    assert.ok(bpmnLabelElement, "an unenumerated DI element must still be retained as an element");
    assert.equal(bpmnLabelElement?.kind, "bpmn", "an unenumerated element inside a known DI/DC layout namespace must be retained, not flagged unsupported");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a callActivity whose calledElement names a process declared in a different admitted BPMN file, the cross-file reference is resolved as an explicit relationship", async () => {
  const root = await makeWorkspace();
  try {
    const caller = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-caller" targetNamespace="urn:landscape">
  <process id="caller">
    <startEvent id="start-caller"/>
    <callActivity id="call-1" calledElement="callee"/>
    <endEvent id="end-caller"/>
    <sequenceFlow id="flow-1" sourceRef="start-caller" targetRef="call-1"/>
    <sequenceFlow id="flow-2" sourceRef="call-1" targetRef="end-caller"/>
  </process>
</definitions>
`;
    await writeFile(join(root, "caller.bpmn"), caller);
    await writeFile(join(root, "callee.bpmn"), bpmn("callee"));
    const input = manifest([
      { path: "caller.bpmn", role: "bpmn" },
      { path: "callee.bpmn", role: "bpmn" },
    ]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    const caller_ = snapshot.processes.find((process) => process.processId === "caller")!;
    const callee = snapshot.processes.find((process) => process.processId === "callee")!;
    assert.ok(!caller_.diagnostics.some((d) => d.code === "unresolved-called-element"), "a calledElement resolvable in another admitted file must not be reported unresolved");
    const reference = caller_.relationships.find((relationship) => relationship.kind === "reference");
    assert.ok(reference, "a resolved cross-file calledElement must produce an explicit reference relationship");
    const calleeProcessElement = callee.elements.find((element) => element.type === "process" && element.id === "callee");
    assert.equal(reference?.target, calleeProcessElement?.identity, "the reference must target the called process's own element identity");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a callActivity whose calledElement matches no admitted process, it remains an explicit unresolved-called-element diagnostic", async () => {
  const root = await makeWorkspace();
  try {
    const caller = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-caller2" targetNamespace="urn:landscape">
  <process id="caller2">
    <startEvent id="start-caller2"/>
    <callActivity id="call-2" calledElement="nowhere"/>
    <endEvent id="end-caller2"/>
    <sequenceFlow id="flow-1" sourceRef="start-caller2" targetRef="call-2"/>
    <sequenceFlow id="flow-2" sourceRef="call-2" targetRef="end-caller2"/>
  </process>
</definitions>
`;
    await writeFile(join(root, "caller2.bpmn"), caller);
    const input = manifest([{ path: "caller2.bpmn", role: "bpmn" }]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    const process = snapshot.processes[0]!;
    assert.ok(process.diagnostics.some((d) => d.code === "unresolved-called-element"), "a calledElement matching no admitted process must remain an explicit diagnostic");
    assert.ok(!process.relationships.some((relationship) => relationship.kind === "reference"), "an unresolved calledElement must never produce a relationship");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a callActivity calledElement ambiguous across two admitted files, it is only resolved via a cryptographically verified navigator procFile mapping, and is otherwise an explicit ambiguous-called-element diagnostic", async () => {
  const root = await makeWorkspace();
  try {
    const caller = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-caller3" targetNamespace="urn:landscape">
  <process id="caller3">
    <startEvent id="start-caller3"/>
    <callActivity id="call-3" calledElement="dup"/>
    <endEvent id="end-caller3"/>
    <sequenceFlow id="flow-1" sourceRef="start-caller3" targetRef="call-3"/>
    <sequenceFlow id="flow-2" sourceRef="call-3" targetRef="end-caller3"/>
  </process>
</definitions>
`;
    await writeFile(join(root, "caller.bpmn"), caller);
    await writeFile(join(root, "dup-a.bpmn"), bpmn("dup", "review-a"));
    await writeFile(join(root, "dup-b.bpmn"), bpmn("dup", "review-b"));
    const input = manifest([
      { path: "caller.bpmn", role: "bpmn" },
      { path: "dup-a.bpmn", role: "bpmn" },
      { path: "dup-b.bpmn", role: "bpmn" },
    ]);

    // Without a navigator bundle, an ambiguous calledElement id must not be guessed.
    const unresolved = await buildProcessLandscapeSnapshot(root, input);
    const callerProcess = unresolved.processes.find((process) => process.processId === "caller3")!;
    assert.ok(callerProcess.diagnostics.some((d) => d.code === "ambiguous-called-element"), "an ambiguous calledElement must be diagnosed, not silently resolved to an arbitrary candidate");
    assert.ok(!callerProcess.relationships.some((relationship) => relationship.kind === "reference"));

    // With a verified navigator bundle whose procFile unambiguously names one candidate file, it resolves.
    const xmlByFile = { "caller.bpmn": caller, "dup-a.bpmn": bpmn("dup", "review-a"), "dup-b.bpmn": bpmn("dup", "review-b") };
    const bundle = JSON.stringify({ xml: xmlByFile, procFile: { dup: "dup-b.bpmn" } });
    await writeFile(join(root, "navigator.js"), `window.NAV_DATA = ${bundle};\n`);
    const inputWithNavigator = manifest([
      { path: "caller.bpmn", role: "bpmn" },
      { path: "dup-a.bpmn", role: "bpmn" },
      { path: "dup-b.bpmn", role: "bpmn" },
      { path: "navigator.js", role: "navigator-data" },
    ]);
    const resolved = await buildProcessLandscapeSnapshot(root, inputWithNavigator);
    assert.equal(resolved.navigator?.verified, true);
    const resolvedCaller = resolved.processes.find((process) => process.processId === "caller3")!;
    const dupB = resolved.processes.find((process) => process.path === "dup-b.bpmn")!;
    assert.ok(!resolvedCaller.diagnostics.some((d) => d.code === "ambiguous-called-element"), "a verified procFile mapping naming exactly one candidate must resolve the ambiguity");
    const reference = resolvedCaller.relationships.find((relationship) => relationship.kind === "reference");
    const dupBProcessElement = dupB.elements.find((element) => element.type === "process" && element.id === "dup");
    assert.equal(reference?.target, dupBProcessElement?.identity, "the verified procFile mapping must select the exact file it names");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
