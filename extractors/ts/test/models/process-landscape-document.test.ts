import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import test from "node:test";
import { Ajv2020 } from "ajv/dist/2020.js";
import addFormatsModule from "ajv-formats";
import { buildProcessLandscapeSnapshot } from "../../src/models/process-landscape.ts";
import { extractBpmnDiagram } from "../../src/models/process-landscape-xml.ts";
import { ModelXmlError } from "../../src/models/xml.ts";

const xml = (options: { participants?: string; source?: string; target?: string; extra?: string } = {}) => `
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
 xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
 xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
 xmlns:di="http://www.omg.org/spec/DD/20100524/DI" id="defs" targetNamespace="urn:synthetic">
 <process id="p1"><sendTask id="send"/><task id="next"/><sequenceFlow id="local" sourceRef="send" targetRef="next"/></process>
 <process id="p2"><receiveTask id="receive"/></process>
 <collaboration id="collab">
  ${options.participants ?? `<participant id="pool1" processRef="p1"/><participant id="pool2" processRef="p2"/>`}
  <messageFlow id="message" sourceRef="${options.source ?? "send"}" targetRef="${options.target ?? "receive"}"/>
 </collaboration>
 ${options.extra ?? ""}
 <bpmndi:BPMNDiagram id="diagram">
  <bpmndi:BPMNPlane id="plane" bpmnElement="collab">
   <bpmndi:BPMNShape id="pool-shape" bpmnElement="pool1"><dc:Bounds x="0" y="0" width="600" height="180"/></bpmndi:BPMNShape>
   <bpmndi:BPMNShape id="task-shape" bpmnElement="send">
    <dc:Bounds x="30" y="40" width="100" height="80"/>
    <bpmndi:BPMNLabel><dc:Bounds x="30" y="125" width="100" height="20"/></bpmndi:BPMNLabel>
   </bpmndi:BPMNShape>
   <bpmndi:BPMNEdge id="message-edge" bpmnElement="message"><di:waypoint x="130" y="80"/><di:waypoint x="300" y="240"/></bpmndi:BPMNEdge>
  </bpmndi:BPMNPlane>
 </bpmndi:BPMNDiagram>
</definitions>`;
const extract = (source = xml()) => extractBpmnDiagram(source, "synthetic.bpmn", "local:test", true);

test("opt-in documents retain canonical collaboration geometry and explicit authored endpoints", () => {
  const result = extract();
  const document = result.document!;
  assert.ok(document);
  assert.equal(document.path, "synthetic.bpmn");
  assert.equal(document.relationships.length, 1);
  const message = document.relationships[0]!;
  const carrier = document.elements.find(element => element.id === "message")!;
  assert.equal(message.elementIdentity, carrier.identity);
  assert.equal(carrier.parent, document.elements.find(element => element.id === "collab")!.identity);
  assert.equal(message.source, result.processes[0]!.elements.find(element => element.id === "send")!.identity);
  assert.equal(message.target, result.processes[1]!.elements.find(element => element.id === "receive")!.identity);
  assert.equal(document.elements.some(element => element.id === "send"), false, "process semantics must not be copied");
  assert.equal(result.processes.flatMap(process => process.elements).some(element => element.id === "pool1"), false);
  assert.equal(result.processes.flatMap(process => process.relationships).filter(relationship => relationship.kind === "sequenceFlow").length, 1);
  const edge = document.elements.find(element => element.id === "message-edge")!;
  assert.deepEqual(document.elements.filter(element => element.parent === edge.identity).map(element => element.attributes),
    [{ x: "130", y: "80" }, { x: "300", y: "240" }]);
  assert.equal(document.elements.filter(element => element.type === "Bounds").length, 3);
  const canonical = new Map(document.elements.map(element => [element.identity, element]));
  for (const process of result.processes) {
    for (const projected of process.elements) {
      const original = canonical.get(projected.identity);
      if (!original) continue;
      assert.deepEqual({ ...projected, parent: undefined }, { ...original, parent: undefined });
      if (projected.parent) assert.equal(projected.parent, original.parent);
    }
  }
  assert.deepEqual(result.documentDiagnostics, []);
  assert.deepEqual(extract(), result);
});

test("black-box participants and distinct pools reusing a process remain explicit", () => {
  for (const participants of [
    `<participant id="pool1"/><participant id="pool2"/>`,
    `<participant id="pool1" processRef="p1"/><participant id="pool2" processRef="p1"/>`,
  ]) {
    const result = extract(xml({ participants, source: "pool1", target: "pool2" }));
    assert.equal(result.document!.relationships.length, 1);
    assert.deepEqual(result.documentDiagnostics, []);
  }
});

test("ambiguous, unrepresented, same-pool and foreign-collaboration endpoints never get guessed", () => {
  const cases = [
    {
      options: { participants: `<participant id="pool1" processRef="p1"/><participant id="duplicate" processRef="p1"/><participant id="pool2" processRef="p2"/>` },
      code: "ambiguous-message-flow-participant",
    },
    { options: { participants: `<participant id="pool1"/><participant id="pool2" processRef="p2"/>` }, code: "unrepresented-message-flow-endpoint" },
    { options: { target: "next" }, code: "invalid-message-flow-scope" },
    {
      options: { target: "foreign", extra: `<collaboration id="other"><participant id="foreign" processRef="p2"/></collaboration>` },
      code: "invalid-message-flow-scope",
    },
  ];
  for (const { options, code } of cases) {
    const result = extract(xml(options));
    assert.equal(result.document!.relationships.length, 0);
    assert.ok(result.document!.elements.some(element => element.id === "message"), "unresolved authored carrier remains evidence");
    assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === code && diagnostic.source?.element === "message"));
    assert.equal(result.documentDiagnostics.some(diagnostic => diagnostic.code === "cross-process-relationship-unresolved"), false);
  }
});

test("unsupported participant process references stay diagnostic and cannot create process links", () => {
  for (const reference of ["missing", "send", "#p1", "prefix:p1", "other.bpmn/p1", ""]) {
    const result = extract(xml({ participants: `<participant id="pool1" processRef="${reference}"/><participant id="pool2" processRef="p2"/>` }));
    assert.equal(result.document!.relationships.length, 0);
    assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "unresolved-participant-process-reference"));
    assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "unrepresented-message-flow-endpoint"));
  }
  const explicit = extract(xml({
    participants: `<participant id="pool1" processRef="missing"/><participant id="pool2"/>`,
    source: "pool1", target: "pool2",
  }));
  assert.equal(explicit.document!.relationships.length, 1, "an authored pool endpoint does not imply a process link");
  assert.ok(explicit.documentDiagnostics.some(diagnostic => diagnostic.code === "unresolved-participant-process-reference"));
});

test("nested carriers and participants cannot masquerade as direct collaboration children", () => {
  for (const source of [
    xml().replace(`<messageFlow id="message"`, `<extensionElements><messageFlow id="message"`).replace(`targetRef="receive"/>`, `targetRef="receive"/></extensionElements>`),
    xml({ participants: `<extensionElements><participant id="pool1" processRef="p1"/></extensionElements><participant id="pool2" processRef="p2"/>` }),
  ]) {
    const result = extract(source);
    assert.equal(result.document!.relationships.length, 0);
    assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "invalid-message-flow-scope"));
  }
});

test("document extraction still rejects dangling, incomplete, duplicate and wrong-type endpoints", () => {
  const failures = [
    { source: xml({ target: "missing" }), code: "dangling-model-reference" },
    { source: xml({ target: "p2" }), code: "invalid-model-reference-target" },
    { source: xml().replace(` targetRef="receive"`, ""), code: "incomplete-model-reference" },
    { source: xml({ extra: `<task id="send"/>` }), code: "duplicate-model-id" },
    { source: xml({ extra: `<other xmlns="urn:foreign" id="foreign"/>`, target: "foreign" }), code: "invalid-model-reference-target" },
  ];
  for (const { source, code } of failures) {
    assert.throws(() => extract(source), error => error instanceof ModelXmlError && error.code === code);
  }
});

test("document graphs cannot rehabilitate cross-process or unowned sequence flows", () => {
  for (const target of ["receive", "next"]) {
    const result = extract(xml({ extra: `<sequenceFlow id="invalid" sourceRef="send" targetRef="${target}"/>` }));
    assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "cross-process-relationship-unresolved"));
    assert.equal(result.document!.elements.some(element => element.id === "invalid"), false);
    assert.ok(result.document!.relationships.every(relationship => relationship.kind === "messageFlow"));
  }
  const malformed = xml().replace(`<sequenceFlow id="local" sourceRef="send" targetRef="next"/>`,
    `<sequenceFlow id="local" sourceRef="send" targetRef="receive">
      <conditionExpression id="orphan-condition"/>
      <bpmndi:BPMNEdge id="invalid-edge" bpmnElement="local"><di:waypoint x="1" y="2"/></bpmndi:BPMNEdge>
    </sequenceFlow>`);
  const result = extract(malformed);
  const identities = new Set([...result.document!.elements, ...result.processes.flatMap(process => process.elements)].map(element => element.identity));
  assert.ok([...result.document!.elements, ...result.processes.flatMap(process => process.elements)].every(element => !element.parent || identities.has(element.parent)));
  assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "unresolved-layout-reference"));
  assert.equal(result.processes.flatMap(process => process.relationships).length, 0);
});

test("snapshot documents are explicit opt-in, source-bound, schema-validated and safe across repeated file ids", async () => {
  const root = resolve(".sandbox-cache", "process-landscape-document", randomUUID());
  await mkdir(root, { recursive: true });
  try {
    await writeFile(join(root, "first.bpmn"), xml());
    await writeFile(join(root, "second.bpmn"), xml());
    const manifest = {
      schema: "ce.process-landscape-manifest.v1", manifestVersion: 1, repositoryId: "local:test",
      entries: [{ path: "first.bpmn", role: "bpmn" }, { path: "second.bpmn", role: "bpmn" }],
    };
    const legacy = await buildProcessLandscapeSnapshot(root, manifest);
    assert.equal(Object.hasOwn(legacy, "documents"), false);
    assert.deepEqual(await buildProcessLandscapeSnapshot(root, manifest, { includeDocumentGraph: false }), legacy);
    const graph = await buildProcessLandscapeSnapshot(root, manifest, { includeDocumentGraph: true });
    assert.equal(graph.documents!.length, 2);
    assert.equal(graph.sources.length, 2);
    assert.equal(graph.snapshotId, legacy.snapshotId);
    assert.deepEqual(graph.sources, legacy.sources);
    assert.deepEqual(graph.processes, legacy.processes);
    assert.equal(graph.coverage.status, "partial", "repeated process IDs retain the pre-existing ambiguity diagnostic");
    assert.ok(graph.diagnostics.every(diagnostic => diagnostic.code === "ambiguous-process-id"));
    assert.notEqual(graph.documents![0]!.relationships[0]!.source, graph.documents![1]!.relationships[0]!.source);
    for (const document of graph.documents!) {
      const fileElements = new Set(graph.processes.filter(process => process.path === document.path).flatMap(process => process.elements.map(element => element.identity)));
      for (const element of document.elements) fileElements.add(element.identity);
      assert.ok(document.elements.every(element => !element.parent || fileElements.has(element.parent)));
      assert.ok(document.relationships.every(relationship => fileElements.has(relationship.source) && fileElements.has(relationship.target)));
    }
    const content = await buildProcessLandscapeSnapshot(root, manifest, { includeDocumentGraph: true, contentProfile: "model-content" });
    assert.deepEqual(content.documents, graph.documents);
    assert.ok(content.sources.every(source => typeof source.sourceBytesBase64 === "string"));
    assert.ok(graph.sources.every(source => source.sourceBytesBase64 === undefined));
    const schema = JSON.parse(readFileSync(new URL("../../../../schema/models/process-landscape.v1.schema.json", import.meta.url), "utf8"));
    const ajv = new Ajv2020({ strict: true, strictRequired: false });
    const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as (ajv: Ajv2020) => Ajv2020;
    addFormats(ajv);
    const validate = ajv.compile(schema);
    assert.equal(validate(legacy), true, "new schema must continue accepting legacy omission");
    for (const documents of [
      null,
      [{ ...graph.documents![0]!, extra: true }],
      [{ ...graph.documents![0]!, relationships: [{ ...graph.documents![0]!.relationships[0]!, kind: "sequenceFlow" }] }],
      [{ ...graph.documents![0]!, relationships: [{ ...graph.documents![0]!.relationships[0]!, elementIdentity: undefined }] }],
    ]) {
      assert.equal(validate({ ...graph, documents }), false, "new scope must reject malformed or process-sequence relationship shapes");
    }
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest, { includeDocumentGraph: "true" as unknown as boolean }), /must be boolean/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
