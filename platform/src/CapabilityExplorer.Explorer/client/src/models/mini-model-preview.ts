import NavigatedViewer from 'bpmn-js/lib/NavigatedViewer.js';
// @ts-expect-error dmn-js 18.0.1 ships its viewer JavaScript without TypeScript declarations.
import DmnViewer from 'dmn-js/lib/Viewer.js';
import type Canvas from 'diagram-js/lib/core/Canvas';
import type ElementRegistry from 'diagram-js/lib/core/ElementRegistry';
import type Overlays from 'diagram-js/lib/features/overlays/Overlays';
import type { ElementLike } from 'diagram-js/lib/model/Types';
import type { ModelDecisionPreview } from './viewer-sandbox';
import type { PopupContext } from './hover-popup';
import { popupButton } from './hover-popup';
import { renderDecisionLogic } from './dmn-decision-logic';

const DMN_NS = 'https://www.omg.org/spec/DMN/20191111/MODEL/';
const DMNDI_NS = 'https://www.omg.org/spec/DMN/20191111/DMNDI/';

/** The frame's single source preflight (namespace, size, DTD, script and external-reference limits). */
export type ParseSource = (kind: 'bpmn' | 'dmn', source: string) =>
  { document: Document; elements: Element[]; hasDiagram: boolean };

type PreviewContext = Pick<PopupContext, 'signal' | 'cleanup' | 'reposition'>;
type Services = { get<T = unknown>(name: string): T };

export function previewNote(text: string, error = false) {
  const element = document.createElement('p');
  element.className = error ? 'model-preview-error' : 'model-preview-note';
  if (error) element.setAttribute('role', 'alert');
  element.textContent = text;
  return element;
}

/** Diagram host plus Fit control; the figure is sized by CSS so imports can fit to a real box. */
export function miniDiagram(label: string) {
  const frame = document.createElement('div');
  frame.className = 'model-mini-frame';
  const figure = document.createElement('div');
  figure.className = 'model-mini-diagram';
  figure.setAttribute('role', 'figure');
  figure.setAttribute('aria-label', label);
  let fitAction: (() => void) | undefined;
  const fit = popupButton('Fit', () => fitAction?.(), `Fit ${label}`);
  fit.disabled = true;
  frame.append(figure, fit);
  return {
    frame, figure,
    ready(action: () => void) { fitAction = action; fit.disabled = false; action(); },
  };
}

/** Centers the active root's own elements (labels included) with padding; falls back to diagram-js fit. */
function fitCanvas(canvas: Canvas, registry: ElementRegistry) {
  canvas.resized();
  const root = canvas.getRootElement();
  const points: Array<[number, number]> = [];
  for (const element of registry.getAll() as ElementLike[]) {
    if (element === root || canvas.findRoot(element) !== root ||
        registry.getGraphics(element)?.closest('.model-scope-hidden')) continue;
    if (element.waypoints) for (const point of element.waypoints) points.push([point.x, point.y]);
    else if ([element.x, element.y, element.width, element.height].every(value => typeof value === 'number')) {
      points.push([element.x!, element.y!], [element.x! + element.width!, element.y! + element.height!]);
    }
  }
  const size = canvas.getSize();
  if (!points.length || !size.width || !size.height) { canvas.zoom('fit-viewport'); return; }
  const xs = points.map(point => point[0]);
  const ys = points.map(point => point[1]);
  const pad = 24;
  const minX = Math.min(...xs) - pad, minY = Math.min(...ys) - pad;
  const width = Math.max(...xs) + pad - minX, height = Math.max(...ys) + pad - minY;
  const scale = Math.min(size.width / width, size.height / height, 1.5);
  const viewWidth = size.width / scale, viewHeight = size.height / scale;
  canvas.viewbox({ x: minX - (viewWidth - width) / 2, y: minY - (viewHeight - height) / 2, width: viewWidth, height: viewHeight });
}

/**
 * Renders the embedded subprocess subtree in a separate read-only viewer. The main viewer's root,
 * selection, viewbox and call stack are never touched; the already-preflighted source is reused as-is.
 */
export async function renderSubprocessPreview(host: HTMLElement, source: string, diagramId: string | undefined,
  subprocessId: string, context: PreviewContext, called = false) {
  const label = called ? 'Called process' : 'Subprocess';
  const mini = miniDiagram(`${label} preview`);
  const status = previewNote(`Loading ${label.toLowerCase()} diagram…`);
  host.replaceChildren(mini.frame, status);
  const viewer = new NavigatedViewer({ container: mini.figure });
  context.cleanup(() => viewer.destroy());
  try {
    const imported = await viewer.importXML(source, diagramId);
    if (context.signal.aborted) return;
    if (imported.warnings.length) throw new Error('The model importer reported warnings.');
    const services = viewer as unknown as Services;
    const canvas = services.get<Canvas>('canvas');
    if (!called) {
      const root = canvas.findRoot(`${subprocessId}_plane`);
      if (!root) throw new Error('This subprocess has no separate diagram plane.');
      canvas.setRootElement(root);
    }
    services.get<Overlays>('overlays').remove({ type: 'drilldown' });
    const registry = services.get<ElementRegistry>('elementRegistry');
    if (called && canvas.getRootElement().id.replace(/_plane$/, '') !== subprocessId) {
      const participant = registry.getAll().find(element => element.businessObject?.processRef?.id === subprocessId);
      if (!participant) throw new Error('The called process has no available diagram.');
      for (const child of canvas.getRootElement().children || []) {
        if (child === participant) continue;
        const graphics = registry.getGraphics(child).parentElement;
        if (!(graphics instanceof SVGElement)) throw new Error('Called-process preview graphics are unavailable.');
        graphics.style.display = 'none';
        graphics.classList.add('model-scope-hidden');
      }
    }
    mini.ready(() => fitCanvas(canvas, registry));
    status.textContent = `Read-only authored diagram of this ${called ? 'called process' : 'subprocess'}; not runtime state.`;
  } catch (error) {
    if (context.signal.aborted) return;
    viewer.destroy();
    mini.figure.replaceChildren(previewNote(`${label} preview unavailable: ${(error as Error).message}`, true));
    status.remove();
  }
  context.reposition();
}

function decisionTarget(elements: Element[], decisionId: string | undefined) {
  const decisions = elements.filter(element => element.namespaceURI === DMN_NS && element.localName === 'decision');
  if (decisionId) {
    const matches = decisions.filter(decision => decision.getAttribute('id') === decisionId);
    if (matches.length !== 1) throw new Error(matches.length ? 'The decision id is not unique in the verified source.'
      : 'The referenced decision is not in the verified source.');
    return matches[0];
  }
  if (decisions.length !== 1) throw new Error(decisions.length ? 'The source has several decisions and no target was given.'
    : 'The verified source has no decision.');
  return decisions[0];
}

const drawsDecision = (diagram: Element, id: string) => [...diagram.getElementsByTagNameNS(DMNDI_NS, 'DMNShape')]
  .some(shape => shape.getAttribute('dmnElementRef') === id);

/**
 * Renders a returned decision source: preflight with the frame's parseSource, then an actual DRD with the
 * exact decision marked, plus its static authored logic. Errors stay inside the popup.
 */
export async function renderDecisionPreview(host: HTMLElement, preview: ModelDecisionPreview,
  parseSource: ParseSource, context: PreviewContext) {
  const parts: HTMLElement[] = [];
  if (preview.error || typeof preview.source !== 'string') {
    host.replaceChildren(previewNote(preview.error || 'No verified decision source was returned.', true));
    context.reposition();
    return;
  }
  let decision: Element;
  let parsed: ReturnType<ParseSource>;
  try {
    parsed = parseSource('dmn', preview.source);
    decision = decisionTarget(parsed.elements, preview.decisionId);
  } catch (error) {
    const failure = error as Error & { code?: string };
    host.replaceChildren(previewNote(`Decision source rejected (${failure.code ?? 'invalid'}): ${failure.message}`, true));
    context.reposition();
    return;
  }
  const decisionId = decision.getAttribute('id') || '';
  const name = decision.getAttribute('name') || decisionId;
  const diagrams = [...parsed.document.getElementsByTagNameNS(DMNDI_NS, 'DMNDiagram')];
  let diagram: Element | undefined;
  let diagramNotice: string | undefined;
  if (preview.diagramId) {
    diagram = diagrams.find(candidate => candidate.getAttribute('id') === preview.diagramId);
    if (!diagram) diagramNotice = 'The target diagram is not in the verified source; no diagram is shown.';
  } else {
    diagram = diagrams.find(candidate => drawsDecision(candidate, decisionId));
    if (!diagram) diagramNotice = 'No diagram interchange (DMNDI) draws this decision; only its logic is shown.';
  }
  const mini = diagram ? miniDiagram('DMN decision preview') : undefined;
  if (mini) parts.push(mini.frame);
  else parts.push(previewNote(diagramNotice!));
  const logic = document.createElement('div');
  logic.className = 'model-preview-logic';
  logic.append(previewNote(`Decision: ${name}`), ...renderDecisionLogic(decision));
  parts.push(logic);
  host.replaceChildren(...parts);
  context.reposition();
  if (!mini || !diagram) return;
  // Ephemeral projection like the main viewer: drop other DMNDiagrams from this parsed copy only.
  for (const other of diagrams) if (other !== diagram) other.remove();
  const projection = new XMLSerializer().serializeToString(parsed.document);
  const viewer = new DmnViewer({ container: mini.figure, drd: { drillDown: { enabled: false } } });
  context.cleanup(() => viewer.destroy());
  try {
    const imported = await viewer.importXML(projection);
    if (context.signal.aborted) return;
    if (imported.warnings.length) throw new Error('The model importer reported warnings.');
    const drd = viewer.getViews().find((view: { type: string }) => view.type === 'drd');
    if (!drd) throw new Error('No displayable decision requirements diagram.');
    await viewer.open(drd);
    if (context.signal.aborted) return;
    const active = viewer.getActiveViewer() as Services;
    const canvas = active.get<Canvas>('canvas');
    active.get<Overlays>('overlays').clear();
    const registry = active.get<ElementRegistry>('elementRegistry');
    if (registry.get(decisionId)) {
      canvas.addMarker(decisionId, 'model-preview-selected');
      canvas.addMarker(decisionId, 'issue78-selected');
    }
    else mini.frame.append(previewNote('The decision is not drawn in this diagram.'));
    mini.ready(() => fitCanvas(canvas, registry));
  } catch (error) {
    if (context.signal.aborted) return;
    viewer.destroy();
    mini.figure.replaceChildren(previewNote(`Decision diagram unavailable: ${(error as Error).message}`, true));
  }
  context.reposition();
}
