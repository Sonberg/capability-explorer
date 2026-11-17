import assert from "node:assert/strict";
import test from "node:test";
import { extractBpmnDiagram } from "../../src/models/process-landscape-xml.ts";

const document = (layout: string, extra = "") => `
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
 xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
 xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
 xmlns:di="http://www.omg.org/spec/DD/20100524/DI" targetNamespace="urn:test">
 <process id="p1"><task id="a"/><task id="b"/><sequenceFlow id="f" sourceRef="a" targetRef="b"/></process>
 <process id="p2"><task id="c"/></process>
 ${extra}
 ${layout}
</definitions>`;

test("multi-process geometry retains bounds, labels, waypoints and their local parent chains", () => {
  const xml = document(`
 <bpmndi:BPMNDiagram id="diagram">
  <bpmndi:BPMNPlane id="plane" bpmnElement="p1">
   <bpmndi:BPMNShape id="shape" bpmnElement="a">
    <dc:Bounds x="12" y="23" width="100" height="80"/>
    <bpmndi:BPMNLabel><dc:Bounds x="14" y="110" width="60" height="20"/></bpmndi:BPMNLabel>
   </bpmndi:BPMNShape>
   <bpmndi:BPMNEdge id="edge" bpmnElement="f">
    <di:waypoint x="112" y="63"/><di:waypoint x="200" y="63"/>
   </bpmndi:BPMNEdge>
  </bpmndi:BPMNPlane>
 </bpmndi:BPMNDiagram>`);
  const result = extractBpmnDiagram(xml, "synthetic.bpmn", "test");
  const first = result.processes[0]!;
  const bounds = first.elements.filter(element => element.type === "Bounds");
  assert.equal(bounds.length, 2, "both shape and label geometry must survive multi-process partitioning");
  assert.deepEqual(bounds[0]!.attributes, { x: "12", y: "23", width: "100", height: "80" });
  assert.deepEqual(first.elements.filter(element => element.type === "waypoint").map(element => element.attributes),
    [{ x: "112", y: "63" }, { x: "200", y: "63" }]);
  const identities = new Set(first.elements.map(element => element.identity));
  assert.ok(first.elements.every(element => !element.parent || identities.has(element.parent)));
  assert.equal(result.processes[1]!.elements.some(element => element.type === "Bounds"), false);
  assert.deepEqual(extractBpmnDiagram(xml, "synthetic.bpmn", "test"), result);
});

test("collaboration planes partition process shapes without inventing collaboration relationships", () => {
  const result = extractBpmnDiagram(document(`
 <bpmndi:BPMNDiagram id="diagram">
  <bpmndi:BPMNPlane id="plane" bpmnElement="collab">
   <bpmndi:BPMNShape id="shape-a" bpmnElement="a"><dc:Bounds x="10" y="20" width="30" height="40"/></bpmndi:BPMNShape>
   <bpmndi:BPMNShape id="shape-c" bpmnElement="c"><dc:Bounds x="50" y="60" width="30" height="40"/></bpmndi:BPMNShape>
   <bpmndi:BPMNShape id="pool-shape" bpmnElement="pool1"><dc:Bounds x="0" y="0" width="200" height="100"/></bpmndi:BPMNShape>
   <bpmndi:BPMNEdge id="message-edge" bpmnElement="message"><di:waypoint x="0" y="0"/></bpmndi:BPMNEdge>
  </bpmndi:BPMNPlane>
 </bpmndi:BPMNDiagram>`, `
 <collaboration id="collab">
  <participant id="pool1" processRef="p1"/><participant id="pool2" processRef="p2"/>
  <messageFlow id="message" sourceRef="pool1" targetRef="pool2"/>
 </collaboration>`), "synthetic.bpmn", "test");
  for (const [index, shape] of ["shape-a", "shape-c"].entries()) {
    const process = result.processes[index]!;
    assert.deepEqual(process.elements.filter(element => element.type === "BPMNShape").map(element => element.id), [shape]);
    const identities = new Set(process.elements.map(element => element.identity));
    assert.ok(process.elements.every(element => !element.parent || identities.has(element.parent)));
    assert.ok(process.elements.some(element => element.id === "plane"));
    assert.equal(process.relationships.some(relationship => relationship.kind === "messageFlow"), false);
  }
  assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "cross-process-relationship-unresolved"));
  assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "unowned-bpmn-element"));
  for (const id of ["pool-shape", "message-edge"]) {
    assert.ok(result.documentDiagnostics.some(diagnostic =>
      diagnostic.code === "unresolved-layout-reference" && diagnostic.source?.element === id));
    assert.equal(result.processes.flatMap(process => process.elements).some(element => element.id === id), false);
  }
});

test("unresolvable layout references remain explicit rather than inheriting an arbitrary process", () => {
  for (const reference of ["missing", "diagram"]) {
    const result = extractBpmnDiagram(document(`
 <bpmndi:BPMNDiagram id="diagram">
  <bpmndi:BPMNPlane id="plane" bpmnElement="p1">
   <bpmndi:BPMNShape id="broken" bpmnElement="${reference}"><dc:Bounds x="1" y="2"/></bpmndi:BPMNShape>
  </bpmndi:BPMNPlane>
 </bpmndi:BPMNDiagram>`), "synthetic.bpmn", "test");
    assert.equal(result.processes.flatMap(process => process.elements).some(element => element.id === "broken"), false);
    assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "unresolved-layout-reference"));
  }
  const nested = document("").replace(`<task id="a"/>`, `<task id="a">
   <bpmndi:BPMNShape id="nested-broken" bpmnElement="missing"><dc:Bounds id="orphan" x="1" y="2"/></bpmndi:BPMNShape>
  </task>`);
  const result = extractBpmnDiagram(nested, "synthetic.bpmn", "test");
  assert.equal(result.processes.flatMap(process => process.elements).some(element => element.id === "orphan"), false);
});

test("layout must not rehabilitate cross-process or unowned sequence flows", () => {
  const result = extractBpmnDiagram(document(`
 <bpmndi:BPMNDiagram><bpmndi:BPMNPlane bpmnElement="p1">
  <bpmndi:BPMNEdge id="cross-edge" bpmnElement="cross"><di:waypoint x="1" y="2"/></bpmndi:BPMNEdge>
 </bpmndi:BPMNPlane></bpmndi:BPMNDiagram>`,
  `<sequenceFlow id="cross" sourceRef="a" targetRef="c"/>`), "synthetic.bpmn", "test");
  assert.ok(result.documentDiagnostics.some(diagnostic => diagnostic.code === "cross-process-relationship-unresolved"));
  assert.equal(result.processes.flatMap(process => process.relationships).some(relationship =>
    relationship.target === result.processes[1]!.elements.find(element => element.id === "c")!.identity), false);
  assert.equal(result.processes.flatMap(process => process.elements).some(element => element.id === "cross-edge"), false);
});

test("unowned same-process flows stay unresolved and malformed collaboration references still reject", () => {
  const result = extractBpmnDiagram(document("", `<sequenceFlow id="outside" sourceRef="a" targetRef="b"/>`),
    "synthetic.bpmn", "test");
  assert.ok(result.documentDiagnostics.some(diagnostic =>
    diagnostic.code === "cross-process-relationship-unresolved" && diagnostic.source?.element === "outside"));
  assert.equal(result.processes.flatMap(process => process.elements).some(element => element.id === "outside"), false);
  for (const flow of [
    `<messageFlow id="bad" sourceRef="a"/>`,
    `<messageFlow id="bad" sourceRef="a" targetRef="missing"/>`,
    `<messageFlow id="bad" sourceRef="a" targetRef="p2"/>`,
  ]) {
    assert.throws(() => extractBpmnDiagram(document("", `<collaboration>${flow}</collaboration>`),
      "synthetic.bpmn", "test"), /reference|Flow requires/i);
  }
  assert.throws(() => extractBpmnDiagram("<definitions>", "synthetic.bpmn", "test"), /malformed/i);
});

test("single-process whole-document extraction keeps existing collaboration and geometry behavior", () => {
  const xml = document(`
 <bpmndi:BPMNDiagram><bpmndi:BPMNPlane bpmnElement="p1">
  <bpmndi:BPMNShape bpmnElement="a"><dc:Bounds x="1" y="2" width="3" height="4"/></bpmndi:BPMNShape>
 </bpmndi:BPMNPlane></bpmndi:BPMNDiagram>`,
  `<collaboration><participant id="pool"/><messageFlow id="message" sourceRef="a" targetRef="pool"/></collaboration>`)
    .replace(`<process id="p2"><task id="c"/></process>`, "");
  const result = extractBpmnDiagram(xml, "synthetic.bpmn", "test");
  assert.equal(result.processes.length, 1);
  assert.equal(result.processes[0]!.relationships.filter(relationship => relationship.kind === "messageFlow").length, 1);
  assert.equal(result.processes[0]!.elements.find(element => element.type === "Bounds")?.attributes.x, "1");
  assert.deepEqual(result.documentDiagnostics, []);
});
