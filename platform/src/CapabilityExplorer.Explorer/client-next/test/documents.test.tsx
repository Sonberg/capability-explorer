import assert from "node:assert/strict";
import { test } from "node:test";
import { documentFixture, fixture } from "./fixtures";
import { collaborationPlanes, documentElements, documentGeometry } from "../src/document-model";
import { parseResponse, readRoute, routeUrl } from "../src/model";

test("documents are optional, but null and non-messageFlow document relationships are rejected", () => {
  assert.equal(parseResponse(fixture()).snapshot?.documents, undefined);
  assert.equal(parseResponse(documentFixture()).snapshot!.documents!.length, 1);
  const data = documentFixture();
  (data.snapshot as unknown as { documents: unknown }).documents = null;
  assert.throws(() => parseResponse(data), /does not match/);
  const wrongKind = documentFixture();
  (wrongKind.snapshot!.documents![0].relationships[0] as unknown as { kind: string }).kind = "sequenceFlow";
  assert.throws(() => parseResponse(wrongKind), /does not match/);
  const missingCarrier = documentFixture();
  delete (missingCarrier.snapshot!.documents![0].relationships[0] as Partial<{ elementIdentity: string }>).elementIdentity;
  assert.throws(() => parseResponse(missingCarrier), /does not match/);
});

test("authored pool Bounds and message waypoints are retained exactly in declared waypoint order", () => {
  const s = documentFixture().snapshot!, doc = s.documents![0], plane = collaborationPlanes(s, doc)[0];
  const geometry = documentGeometry(s, doc, plane);
  assert.equal(geometry.shapes.length, 3);
  const pool = geometry.shapes.find(shape => shape.element.id === "pool-a")!;
  assert.deepEqual([pool.x, pool.y, pool.width, pool.height], [10, 20, 500, 180]);
  assert.equal(geometry.edges.length, 1, "an omitted carrier never becomes a path");
  assert.deepEqual(geometry.edges[0].points, [{ x: 120, y: 150 }, { x: 120, y: 240 }]);
  assert.equal(geometry.edges[0].relationship.source, "el:first", "activity endpoint is not replaced with its pool");
  assert.equal(geometry.warnings.length, 0);
});

test("only canonical document DI supplies geometry, never retained process projection copies", () => {
  const s = documentFixture().snapshot!, doc = s.documents![0];
  const projected = structuredClone(doc.elements.find(e => e.id === "pool-a-bounds")!);
  projected.parent = "process-projection-parent";
  projected.attributes.x = "9999";
  s.processes[0].elements.push(projected);
  assert.equal(documentElements(s, doc).find(e => e.identity === projected.identity)!.attributes.x, "10");
  const geometry = documentGeometry(s, doc, collaborationPlanes(s, doc)[0]);
  assert.equal(geometry.shapes.find(shape => shape.element.id === "pool-a")!.x, 10);
  doc.elements = doc.elements.filter(e => e.id !== "pool-a-bounds");
  assert.ok(!documentGeometry(s, doc, collaborationPlanes(s, doc)[0]).shapes.some(shape => shape.element.id === "pool-a"));
});

test("no path is invented when a waypoint is non-finite, absent, or too large", () => {
  for (const value of ["NaN", "Infinity", "", "1e100", "0x20"]) {
    const s = documentFixture().snapshot!, doc = s.documents![0];
    doc.elements.find(e => e.id === "message-point-last")!.attributes.x = value;
    const geometry = documentGeometry(s, doc, collaborationPlanes(s, doc)[0]);
    assert.equal(geometry.edges.length, 0, value);
    assert.match(geometry.warnings.join(" "), /no path inferred/);
  }
});

test("document geometry and semantic lookup remain file/plane scoped and reject guessed # or QName references", () => {
  const s = documentFixture().snapshot!, doc = s.documents![0];
  doc.elements.find(e => e.id === "pool-a-shape")!.attributes.bpmnElement = "#pool-a";
  doc.elements.find(e => e.id === "task-shape")!.attributes.bpmnElement = "prefix:first";
  const geometry = documentGeometry(s, doc, collaborationPlanes(s, doc)[0]);
  assert.deepEqual(geometry.shapes.map(shape => shape.element.id), ["pool-b"]);
  doc.elements.find(e => e.id === "plane")!.attributes.bpmnElement = "#collab";
  assert.equal(collaborationPlanes(s, doc).length, 0);
  assert.equal(documentGeometry(s, doc).bounds, undefined);
});

test("collaboration deep links preserve deployed prefix and existing process selection independently", () => {
  const base = new URL("https://example.test/process-landscape/?foreign=keep&process=proc%3Aintake&element=el%3Afirst");
  const route = {
    ...readRoute(base), lens: "collaboration" as const, document: "synthetic/collaboration.bpmn",
    diagram: "doc:plane", documentElement: "doc:pool-b",
  };
  const path = routeUrl(route, base), decoded = readRoute(new URL(path, base));
  assert.match(path, /^\/process-landscape\/\?/);
  assert.equal(decoded.process, "proc:intake");
  assert.equal(decoded.element, "el:first");
  assert.equal(decoded.documentElement, "doc:pool-b");
  assert.equal(decoded.diagram, "doc:plane");
  assert.equal(decoded.document, "synthetic/collaboration.bpmn");
  assert.equal(new URL(path, base).searchParams.get("foreign"), "keep");
});

test("multiple collaboration diagrams keep their own canonical coordinates and never reuse another plane's path", () => {
  const s = documentFixture(true).snapshot!, doc = s.documents![0], planes = collaborationPlanes(s, doc);
  assert.equal(planes.length, 2);
  const alternate = documentGeometry(s, doc, planes[1]);
  assert.equal(alternate.shapes.length, 1);
  assert.equal(alternate.shapes[0].x, -20);
  assert.equal(alternate.edges.length, 0);
  assert.match(alternate.warnings.join(" "), /no path inferred/);
});

test("message endpoints never resolve from another source file's process projection", () => {
  const s = documentFixture().snapshot!, doc = s.documents![0];
  s.processes[0].path = "synthetic/other-file.bpmn";
  assert.ok(!documentElements(s, doc).some(e => e.identity === "el:first"));
  const geometry = documentGeometry(s, doc, collaborationPlanes(s, doc)[0]);
  assert.equal(geometry.edges.length, 0);
  assert.match(geometry.warnings.join(" "), /unresolved endpoint identity/);
});

test("drawing budgets are explicit and long authored paths are omitted, not truncated into misleading replacements", () => {
  const s = documentFixture().snapshot!, doc = s.documents![0];
  const shape = doc.elements.find(e => e.id === "pool-b-shape")!, bounds = doc.elements.find(e => e.id === "pool-b-bounds")!;
  for (let i = 0; i < 205; i++) doc.elements.push(
    { ...shape, identity: `extra-shape:${i}`, id: `extra-shape-${i}` },
    { ...bounds, identity: `extra-bounds:${i}`, id: `extra-bounds-${i}`, parent: `extra-shape:${i}` },
  );
  const point = doc.elements.find(e => e.id === "message-point-last")!;
  for (let i = 0; i < 1000; i++) doc.elements.push({ ...point, identity: `extra-point:${i}`, id: `extra-point-${i}`, order: i + 2 });
  const geometry = documentGeometry(s, doc, collaborationPlanes(s, doc)[0]);
  assert.equal(geometry.shapes.length, 200);
  assert.equal(geometry.edges.length, 0);
  assert.match(geometry.warnings.join(" "), /Drawing capped at 200 shapes/);
  assert.match(geometry.warnings.join(" "), /1,000-waypoint drawing limit; no replacement path inferred/);
});
