import type { Diagnostic, DocumentRelationship, Element, LandscapeDocument, Snapshot } from "./contracts";
import { ordered } from "./model";

export const BPMN = "http://www.omg.org/spec/BPMN/20100524/MODEL";
export const BPMNDI = "http://www.omg.org/spec/BPMN/20100524/DI";
export const DC = "http://www.omg.org/spec/DD/20100524/DC";
export const DI = "http://www.omg.org/spec/DD/20100524/DI";
export const DRAW_LIMIT = 200;

export function documentElements(s: Snapshot, doc: LandscapeDocument): Element[] {
  const elements = new Map(doc.elements.map(e => [e.identity, e]));
  for (const p of s.processes.filter(p => p.path === doc.path))
    for (const e of p.elements) if (!elements.has(e.identity)) elements.set(e.identity, e);
  return [...elements.values()];
}

export const semantic = (e: Element, type?: string) =>
  e.kind === "bpmn" && e.namespace === BPMN && (!type || e.type === type);

function uniqueId(elements: Element[], id: string): Element | undefined {
  if (!id) return undefined;
  const matches = elements.filter(e => semantic(e) && e.id === id);
  return matches.length === 1 ? matches[0] : undefined;
}

export interface CollaborationPlane { plane: Element; collaboration: Element; diagram?: Element }
export function collaborationPlanes(s: Snapshot, doc: LandscapeDocument): CollaborationPlane[] {
  const union = documentElements(s, doc);
  return doc.elements.filter(e => e.kind === "bpmn" && e.namespace === BPMNDI && e.type === "BPMNPlane")
    .flatMap(plane => {
      const collaboration = uniqueId(union, plane.attributes.bpmnElement);
      if (!collaboration || collaboration.type !== "collaboration") return [];
      const diagram = doc.elements.find(e => e.identity === plane.parent && e.namespace === BPMNDI && e.type === "BPMNDiagram");
      return [{ plane, collaboration, diagram }];
    });
}

function coordinate(value: string | undefined): number | undefined {
  const normalized = value?.trim();
  if (!normalized || !/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(normalized)) return undefined;
  const n = Number(normalized);
  return Number.isFinite(n) && Math.abs(n) <= 10_000_000 ? n : undefined;
}

export interface AuthoredShape {
  interchange: Element; element: Element; x: number; y: number; width: number; height: number;
}
export interface AuthoredEdge {
  interchange: Element; carrier: Element; relationship: DocumentRelationship; points: { x: number; y: number }[];
}
export interface DocumentGeometry {
  shapes: AuthoredShape[]; edges: AuthoredEdge[]; warnings: string[]; bounds?: { x: number; y: number; width: number; height: number };
}

function underPlane(element: Element, plane: Element, canonical: Map<string, Element>) {
  const seen = new Set<string>();
  let parent = element.parent;
  while (parent && !seen.has(parent)) {
    if (parent === plane.identity) return true;
    seen.add(parent);
    parent = canonical.get(parent)?.parent;
  }
  return false;
}

export function documentGeometry(s: Snapshot, doc: LandscapeDocument, selection?: CollaborationPlane): DocumentGeometry {
  const warnings: string[] = [], shapes: AuthoredShape[] = [], edges: AuthoredEdge[] = [];
  if (!selection) return { shapes, edges, warnings: ["No selected collaboration plane. No authored geometry has been assumed."] };
  const union = documentElements(s, doc), canonical = new Map(doc.elements.map(e => [e.identity, e]));
  const children = new Map<string, Element[]>(), semantics = new Map<string, Element[]>();
  for (const e of doc.elements) if (e.parent) {
    const siblings = children.get(e.parent) ?? [];
    siblings.push(e); children.set(e.parent, siblings);
  }
  for (const e of union.filter(e => semantic(e))) {
    const matches = semantics.get(e.id) ?? [];
    matches.push(e); semantics.set(e.id, matches);
  }
  const identities = new Set(union.map(e => e.identity));
  const planeElements = doc.elements.filter(e => e.kind === "bpmn" && e.namespace === BPMNDI
    && underPlane(e, selection.plane, canonical));
  const edgeReferences = new Map<string, Element[]>();
  for (const e of planeElements.filter(e => e.type === "BPMNEdge")) {
    const references = edgeReferences.get(e.attributes.bpmnElement) ?? [];
    references.push(e); edgeReferences.set(e.attributes.bpmnElement, references);
  }
  for (const shape of planeElements.filter(e => e.type === "BPMNShape")) {
    const targets = semantics.get(shape.attributes.bpmnElement) ?? [];
    const target = targets.length === 1 ? targets[0] : undefined;
    const boxes = (children.get(shape.identity) ?? []).filter(e => e.kind === "bpmn" && e.namespace === DC && e.type === "Bounds");
    const values = boxes.length === 1 ? ["x", "y", "width", "height"].map(key => coordinate(boxes[0].attributes[key])) : [];
    if (!target || values.length !== 4 || values.some(v => v === undefined) || values[2]! <= 0 || values[3]! <= 0) {
      warnings.push(`Shape ${shape.id}: unresolved semantic reference or missing/invalid authored Bounds; not drawn.`);
      continue;
    }
    const [x, y, width, height] = values as number[];
    shapes.push({ interchange: shape, element: target, x, y, width, height });
  }
  for (const relationship of ordered(doc.relationships)) {
    const carrier = canonical.get(relationship.elementIdentity);
    if (!carrier || !semantic(carrier, "messageFlow") || carrier.parent !== selection.collaboration.identity) continue;
    if (![relationship.source, relationship.target].every(id => identities.has(id))) {
      warnings.push(`Message flow ${carrier.id}: unresolved endpoint identity; not drawn.`);
      continue;
    }
    const candidates = edgeReferences.get(carrier.id) ?? [];
    const edge = candidates.length === 1 ? candidates[0] : undefined;
    const waypoints = edge ? ordered((children.get(edge.identity) ?? []).filter(e => e.kind === "bpmn" && e.namespace === DI
      && e.type === "waypoint")) : [];
    if (waypoints.length > 1000) {
      warnings.push(`Message flow ${carrier.id}: exceeds the 1,000-waypoint drawing limit; no replacement path inferred.`);
      continue;
    }
    const points = waypoints.map(e => ({ x: coordinate(e.attributes.x), y: coordinate(e.attributes.y) }));
    if (!edge || points.length < 2 || points.some(point => point.x === undefined || point.y === undefined)) {
      warnings.push(`Message flow ${carrier.id}: missing, ambiguous or invalid authored waypoints; no path inferred.`);
      continue;
    }
    edges.push({ interchange: edge, carrier, relationship, points: points as { x: number; y: number }[] });
  }
  if (shapes.length > DRAW_LIMIT || edges.length > DRAW_LIMIT)
    warnings.push(`Drawing capped at ${DRAW_LIMIT} shapes and ${DRAW_LIMIT} message paths. Paged evidence lists remain available.`);
  const drawnShapes = shapes.slice(0, DRAW_LIMIT), drawnEdges = edges.slice(0, DRAW_LIMIT);
  const xs = drawnShapes.flatMap(e => [e.x, e.x + e.width]).concat(drawnEdges.flatMap(e => e.points.map(p => p.x)));
  const ys = drawnShapes.flatMap(e => [e.y, e.y + e.height]).concat(drawnEdges.flatMap(e => e.points.map(p => p.y)));
  const min = (items: number[]) => items.reduce((a, b) => Math.min(a, b), Infinity);
  const max = (items: number[]) => items.reduce((a, b) => Math.max(a, b), -Infinity);
  const x = min(xs) - 30, y = min(ys) - 30;
  const bounds = xs.length ? { x, y, width: max(xs) - x + 30, height: max(ys) - y + 30 } : undefined;
  return { shapes: drawnShapes, edges: drawnEdges, warnings, bounds };
}

export function documentDiagnostics(s: Snapshot, doc: LandscapeDocument): Diagnostic[] {
  const all = [...s.diagnostics, ...s.processes.filter(p => p.path === doc.path).flatMap(p => p.diagnostics)]
    .filter(d => d.source?.path === doc.path);
  return [...new Map(all.map(d => [JSON.stringify(d), d])).values()];
}

export const displayElement = (e: Element) => e.attributes.name || e.id;
