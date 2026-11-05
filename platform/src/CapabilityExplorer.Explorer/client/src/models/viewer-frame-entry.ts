import NavigatedViewer from 'bpmn-js/lib/NavigatedViewer.js';
// @ts-expect-error dmn-js 18.0.1 ships its viewer JavaScript without TypeScript declarations.
import DmnViewer from 'dmn-js/lib/Viewer.js';
import 'bpmn-js/dist/assets/diagram-js.css';
import 'bpmn-js/dist/assets/bpmn-js.css';
import 'dmn-js/dist/assets/dmn-js-shared.css';
import 'dmn-js/dist/assets/dmn-js-drd.css';
import './viewer-frame-entry.css';
import { installBpmnNavigation } from './bpmn-navigation';
import { loadBpmnProcessDiagrams } from './bpmn-diagrams';
import { installBpmnDecisionLinks } from './bpmn-decision-links';
import { randomId } from './hover-popup';
import type { ModelDecisionPreview, ModelRequest, ModelViewport } from './viewer-sandbox';

const LIMIT_BYTES = 1_048_576;
const LIMIT_ELEMENTS = 500;
const DECISION_PREVIEW_TIMEOUT_MS = 10_000;
const BPMN_NS = 'http://www.omg.org/spec/BPMN/20100524/MODEL';
const BPMN_NAMESPACES = new Set([
  BPMN_NS, 'http://www.omg.org/spec/BPMN/20100524/DI',
  'http://www.omg.org/spec/DD/20100524/DC', 'http://www.omg.org/spec/DD/20100524/DI'
]);
const DMN_NS = 'https://www.omg.org/spec/DMN/20191111/MODEL/';
const DMN_NAMESPACES = new Set([
  DMN_NS, 'https://www.omg.org/spec/DMN/20191111/DMNDI/',
  'http://www.omg.org/spec/DMN/20180521/DC/', 'http://www.omg.org/spec/DMN/20180521/DI/'
]);
const state = document.querySelector<HTMLElement>('#state')!;
let viewer: NavigatedViewer | DmnViewer | undefined;
let requestIdentity: Record<string, unknown> | undefined;
let decisionPreviewSupported = false;
let selectedElement: string | undefined;
let navigatingToSelection = false;
let autoFit = true;
let resizeTimer: number | undefined;
let bpmnNavigation: ReturnType<typeof installBpmnNavigation> | undefined;
let decisionLinks: ReturnType<typeof installBpmnDecisionLinks> | undefined;
const decisionPreviews = new Map<string, { elementId: string; settle(preview?: ModelDecisionPreview, error?: Error): void }>();
type DiagramElement = {
  id: string;
  businessObject?: { id?: string; sourceRef?: unknown; targetRef?: unknown };
};
type CanvasService = {
  zoom(value?: number | 'fit-viewport'): number;
  addMarker(id: string, marker: string): void;
  removeMarker(id: string, marker: string): void;
  getRootElement(): DiagramElement;
  setRootElement(element: DiagramElement): void;
  findRoot(element: DiagramElement | string): DiagramElement | undefined;
  viewbox(): ModelViewport;
  viewbox(viewport: ModelViewport): void;
  scrollToElement(id: string): void;
  resized(): void;
};
type RegistryService = {
  get(id: string): DiagramElement | undefined;
  getAll(): DiagramElement[];
  getGraphics(element: DiagramElement): SVGElement;
};
type ViewerServices = { get(name: string): unknown };

function referenceId(reference: unknown): string | undefined {
  if (typeof reference === 'string') return reference;
  if (reference && typeof reference === 'object' && 'id' in reference && typeof reference.id === 'string') return reference.id;
  return undefined;
}

function send(status: string, message?: string, renderedCount?: number, elementId?: string, renderedIds?: string[], relationshipSourceId?: string, relationshipTargetId?: string, relationshipMatchCount?: number, extra?: { requestId?: string; viewport?: ModelViewport }) {
  if (!requestIdentity) return;
  const active = viewer instanceof NavigatedViewer ? viewer : viewer?.getActiveViewer();
  const root = active?.get('canvas')?.getRootElement();
  const rootId = root?.businessObject?.id || root?.id;
  window.parent.postMessage({ ...requestIdentity, status, message, renderedCount, elementId, renderedIds, relationshipSourceId, relationshipTargetId, relationshipMatchCount, rootId, requestId: extra?.requestId, viewport: extra?.viewport }, '*');
}

function cancelDecisionPreviews(reason: string) {
  for (const pending of [...decisionPreviews.values()]) pending.settle(undefined, new Error(reason));
}

/** Correlated, bounded request to the parent, which alone fetches and verifies the target source. */
function requestDecisionPreview(elementId: string, signal: AbortSignal) {
  return new Promise<ModelDecisionPreview>((resolve, reject) => {
    if (!decisionPreviewSupported) {
      reject(new Error('The page and diagram viewer are different versions. Reload the page to load decision previews.'));
      return;
    }
    if (signal.aborted || !requestIdentity) { reject(new Error('Decision preview cancelled.')); return; }
    const requestId = randomId();
    let timer: number | undefined;
    const settle = (preview?: ModelDecisionPreview, error?: Error) => {
      if (!decisionPreviews.has(requestId)) return;
      decisionPreviews.delete(requestId);
      window.clearTimeout(timer);
      signal.removeEventListener('abort', abort);
      if (preview) resolve(preview);
      else {
        send('decision-preview-cancel', undefined, undefined, elementId, undefined, undefined, undefined, undefined, { requestId });
        reject(error ?? new Error('Decision preview cancelled.'));
      }
    };
    const abort = () => settle(undefined, new Error('Decision preview cancelled.'));
    decisionPreviews.set(requestId, { elementId, settle });
    signal.addEventListener('abort', abort, { once: true });
    timer = window.setTimeout(() => settle(undefined, new Error('The decision preview timed out.')), DECISION_PREVIEW_TIMEOUT_MS);
    send('decision-preview-request', undefined, undefined, elementId, undefined, undefined, undefined, undefined, { requestId });
  });
}

function decisionPreviewPayload(value: unknown): ModelDecisionPreview {
  if (!value || typeof value !== 'object') return { error: 'The decision preview response was invalid.' };
  const input = value as Record<string, unknown>;
  const text = (key: string, limit: number) =>
    typeof input[key] === 'string' && (input[key] as string).length <= limit ? input[key] as string : undefined;
  const preview: ModelDecisionPreview = {
    source: text('source', LIMIT_BYTES), decisionId: text('decisionId', 512), diagramId: text('diagramId', 512),
    provenance: text('provenance', 2_000), error: text('error', 2_000),
  };
  if (!preview.error && preview.source === undefined) preview.error = 'No verified decision source was returned.';
  return preview;
}

function validViewport(value: unknown): value is ModelViewport {
  if (!value || typeof value !== 'object') return false;
  const box = value as ModelViewport;
  return [box.x, box.y, box.width, box.height].every(number => typeof number === 'number' && Number.isFinite(number)) &&
    box.width > 0 && box.height > 0 && (box.rootId === undefined || typeof box.rootId === 'string');
}

function restoreViewport(active: ViewerServices, viewport: ModelViewport) {
  const canvas = active.get('canvas') as CanvasService;
  navigatingToSelection = true;
  try {
    if (bpmnNavigation) bpmnNavigation.restoreContext(viewport);
    else if (viewport.rootId) {
      const registry = active.get('elementRegistry') as RegistryService;
      const element = registry.get(viewport.rootId);
      const root = canvas.findRoot(`${viewport.rootId}_plane`) || canvas.findRoot(viewport.rootId) ||
        (element && canvas.findRoot(element));
      if (!root) throw new Error(`The saved model root "${viewport.rootId}" is unavailable in this source.`);
      if (canvas.getRootElement() !== root) canvas.setRootElement(root);
    }
    autoFit = false;
    canvas.viewbox({ x: viewport.x, y: viewport.y, width: viewport.width, height: viewport.height });
  } finally {
    navigatingToSelection = false;
  }
}

function parseSource(kind: 'bpmn' | 'dmn', source: string) {
  if (new TextEncoder().encode(source).byteLength > LIMIT_BYTES) throw Object.assign(new Error('Source exceeds the 1 MiB UTF-8 limit'), { code: 'too-large' });
  if (/<!\s*(?:DOCTYPE|ENTITY)\b/i.test(source)) throw Object.assign(new Error('DTD and entity declarations are not supported'), { code: 'unsafe-source' });
  const document = new DOMParser().parseFromString(source, 'application/xml');
  if (document.querySelector('parsererror')) throw Object.assign(new Error('Malformed XML'), { code: 'invalid-xml' });
  const root = document.documentElement;
  const expectedNamespace = kind === 'bpmn' ? BPMN_NS : DMN_NS;
  const namespaces = kind === 'bpmn' ? BPMN_NAMESPACES : DMN_NAMESPACES;
  if (root.localName !== 'definitions' || root.namespaceURI !== expectedNamespace) {
    throw Object.assign(new Error('Unsupported model root or namespace'), { code: 'unsupported-namespace' });
  }
  const elements = [...document.getElementsByTagName('*')];
  if (elements.length > LIMIT_ELEMENTS) throw Object.assign(new Error('Source exceeds the 500-element preflight limit'), { code: 'too-many-elements' });
  for (const element of elements) {
    if (!element.namespaceURI || !namespaces.has(element.namespaceURI)) {
      throw Object.assign(new Error(`Unsupported extension namespace: ${element.namespaceURI ?? '(none)'}`), { code: 'unsupported-namespace' });
    }
    if (element.localName.toLowerCase() === 'script') {
      throw Object.assign(new Error('Executable script elements are not supported'), { code: 'unsafe-source' });
    }
    for (const attribute of [...element.attributes]) {
      if (['href', 'src'].includes(attribute.localName.toLowerCase()) && attribute.value && !attribute.value.startsWith('#')) {
        throw Object.assign(new Error('External model references are not supported'), { code: 'external-reference' });
      }
    }
  }
  const hasDiagram = kind === 'bpmn'
    ? document.getElementsByTagNameNS('http://www.omg.org/spec/BPMN/20100524/DI', 'BPMNDiagram').length > 0
    : document.getElementsByTagNameNS('https://www.omg.org/spec/DMN/20191111/DMNDI/', 'DMNDiagram').length > 0;
  return { document, elements, hasDiagram };
}

function sameIdentity(message: Record<string, unknown>) {
  return requestIdentity && message.nonce === requestIdentity.nonce &&
    message.scope === requestIdentity.scope && message.generation === requestIdentity.generation &&
    message.sourceId === requestIdentity.sourceId && message.kind === requestIdentity.kind;
}

function installSelectionEvents(instance: NavigatedViewer | DmnViewer) {
  const active = instance instanceof NavigatedViewer ? instance : instance.getActiveViewer();
  active?.on('element.click', (event: { element: { id: string; businessObject?: { sourceRef?: unknown; targetRef?: unknown } } }) => {
    const canvas = (active as unknown as ViewerServices).get('canvas') as CanvasService;
    if (selectedElement) canvas.removeMarker(selectedElement, 'issue78-selected');
    if (event.element.id === canvas.getRootElement().id) {
      selectedElement = undefined;
      send('selection-cleared');
      return;
    }
    selectedElement = event.element.id;
    canvas.addMarker(selectedElement, 'issue78-selected');
    send('selection', undefined, undefined, selectedElement, undefined,
      referenceId(event.element.businessObject?.sourceRef), referenceId(event.element.businessObject?.targetRef));
  });
}

function visibleElementIds(active: ViewerServices): string[] {
  const canvas = active.get('canvas') as CanvasService;
  const root = canvas.getRootElement();
  const registry = active.get('elementRegistry') as RegistryService;
  return registry.getAll()
    .filter(element => canvas.findRoot(element) === root && !registry.getGraphics(element)?.closest('.model-scope-hidden'))
    .map(element => element.id);
}

function revealSelection(canvas: CanvasService, id: string) {
  navigatingToSelection = true;
  try {
    canvas.scrollToElement(id);
  } finally {
    navigatingToSelection = false;
  }
}

window.addEventListener('message', async event => {
  if (event.source !== window.parent) return;
  const request = event.data as Record<string, unknown>;
  if (request?.status === 'render-request' && typeof request.nonce === 'string') {
    cancelDecisionPreviews('The model was replaced.');
    decisionLinks?.dispose();
    decisionLinks = undefined;
    viewer?.destroy();
    viewer = undefined;
    bpmnNavigation = undefined;
    selectedElement = undefined;
    decisionPreviewSupported = request.decisionPreviewSupported === true;
    requestIdentity = {
      kind: request.kind, scope: request.scope, generation: request.generation,
      sourceId: request.sourceId, nonce: request.nonce
    };
    const kind = request.kind;
    const source = request.source;
    send('accepted');
    if ((kind !== 'bpmn' && kind !== 'dmn') || typeof source !== 'string') {
      send('invalid-request', 'Invalid render request');
      return;
    }
    try {
      const parsed = parseSource(kind, source);
      if (kind === 'bpmn' && !parsed.hasDiagram) throw Object.assign(new Error('BPMN source has no BPMNDiagram DI'), { code: 'missing-di' });
      if (kind === 'dmn' && !parsed.hasDiagram) throw Object.assign(new Error('DMN source has no DMNDiagram DI'), { code: 'missing-di' });
      state.replaceChildren();
      const stage = document.createElement('div');
      stage.className = 'stage';
      const canvas = document.createElement('div');
      canvas.className = 'viewer';
      stage.append(canvas);
      state.append(stage);
      send('import-started', undefined, parsed.elements.length);
      const instance = kind === 'bpmn'
        ? new NavigatedViewer({ container: canvas })
        : new DmnViewer({ container: canvas });
      viewer = instance;
      autoFit = true;
      const diagramId = typeof request.diagramId === 'string' ? request.diagramId : undefined;
      const diagram = diagramId && parsed.elements.find(element => element.getAttribute('id') === diagramId &&
        ['BPMNDiagram', 'DMNDiagram'].includes(element.localName));
      if (diagramId && !diagram) throw new Error('Requested diagram is not in this verified source');
      let projection = source;
      if (kind === 'dmn' && diagram) {
        for (const other of parsed.document.getElementsByTagNameNS('https://www.omg.org/spec/DMN/20191111/DMNDI/', 'DMNDiagram')) {
          if (other !== diagram) other.remove();
        }
        projection = new XMLSerializer().serializeToString(parsed.document);
      }
      bpmnNavigation = undefined;
      const imported = instance instanceof NavigatedViewer ? await instance.importXML(source, diagramId) : await instance.importXML(projection);
      if (imported.warnings.length) throw new Error('Model importer reported warnings');
      if (instance instanceof NavigatedViewer) await loadBpmnProcessDiagrams(instance);
      if (kind === 'bpmn') ((instance as unknown as ViewerServices).get('canvas') as CanvasService).zoom('fit-viewport');
      else {
        const dmn = instance as DmnViewer;
        const drd = dmn.getViews().find((view: { type: string }) => view.type === 'drd');
        if (!drd) throw new Error('DMN source has no displayable DRD');
        await dmn.open(drd);
        (dmn.getActiveViewer().get('canvas') as CanvasService).zoom('fit-viewport');
      }
      installSelectionEvents(instance);
      if (instance instanceof NavigatedViewer) {
        bpmnNavigation = installBpmnNavigation(instance, parsed.document, {
          select(id) {
            const canvas = instance.get<CanvasService>('canvas');
            if (selectedElement) canvas.removeMarker(selectedElement, 'issue78-selected');
            selectedElement = undefined;
            if (instance.get<RegistryService>('elementRegistry').get(id)) {
              selectedElement = id;
              canvas.addMarker(id, 'issue78-selected');
            }
            const ids = visibleElementIds(instance);
            send('navigation', undefined, ids.length, id, ids);
          },
          notice(message, id) { send('navigation-notice', message, undefined, id); },
          autoFit(enabled) { autoFit = enabled; },
          getAutoFit() { return autoFit; },
          scopeChanged() {
            const ids = visibleElementIds(instance);
            send('navigation', undefined, ids.length, undefined, ids);
          },
          selecting() { return navigatingToSelection; },
          preview: { source, diagramId },
        });
        decisionLinks = installBpmnDecisionLinks(instance,
          Array.isArray(request.decisionLinks) ? request.decisionLinks as ModelRequest['decisionLinks'] : undefined,
          id => send('decision-reference', undefined, undefined, id),
          requestDecisionPreview, parseSource);
      }
      const active = kind === 'bpmn' ? instance as unknown as ViewerServices : (instance as DmnViewer).getActiveViewer() as ViewerServices;
      const renderedCount = (active.get('elementRegistry') as RegistryService).getAll().length;
      if (renderedCount > LIMIT_ELEMENTS) throw Object.assign(new Error('Rendered element count exceeds 500'), { code: 'too-many-elements' });
      const renderedIds = visibleElementIds(active);
      send('rendered', undefined, renderedIds.length, undefined, renderedIds);
    } catch (error) {
      decisionLinks?.dispose();
      decisionLinks = undefined;
      viewer?.destroy();
      viewer = undefined;
      bpmnNavigation = undefined;
      const failure = error as Error & { code?: string };
      state.textContent = `${failure.code ?? 'render-error'}: ${failure.message}`;
      send(failure.code ?? 'render-error', failure.message);
    }
    return;
  }
  if (!sameIdentity(request)) return;
  if (request.status === 'decision-preview-result') {
    const pending = typeof request.requestId === 'string' ? decisionPreviews.get(request.requestId) : undefined;
    // Late, unknown or mismatched replies are ignored.
    if (pending && pending.elementId === request.elementId) pending.settle(decisionPreviewPayload(request.preview));
    return;
  }
  if (request.status === 'select-element' && typeof request.elementId === 'string' && viewer) {
    const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
    const canvas = active.get('canvas') as CanvasService;
    const element = (active.get('elementRegistry') as RegistryService).get(request.elementId);
    if (selectedElement) canvas.removeMarker(selectedElement, 'issue78-selected');
    selectedElement = undefined;
    if (element) {
      bpmnNavigation?.prepareSelection(request.elementId);
      revealSelection(canvas, request.elementId);
      selectedElement = request.elementId;
      canvas.addMarker(selectedElement, 'issue78-selected');
      decisionLinks?.focus(selectedElement);
    }
  } else if (request.status === 'select-relationship' &&
    typeof request.relationshipSourceId === 'string' && typeof request.relationshipTargetId === 'string' && viewer) {
    const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
    const canvas = active.get('canvas') as CanvasService;
    const registry = active.get('elementRegistry') as RegistryService;
    if (selectedElement) canvas.removeMarker(selectedElement, 'issue78-selected');
    selectedElement = undefined;
    const matches = registry.getAll().filter((candidate) => {
      const businessObject = (candidate as { businessObject?: { sourceRef?: unknown; targetRef?: unknown } }).businessObject;
      return referenceId(businessObject?.sourceRef) === request.relationshipSourceId &&
        referenceId(businessObject?.targetRef) === request.relationshipTargetId;
    }) as Array<{ id: string }>;
    if (matches.length === 1) {
      bpmnNavigation?.prepareSelection(matches[0].id);
      revealSelection(canvas, matches[0].id);
      selectedElement = matches[0].id;
      canvas.addMarker(selectedElement, 'issue78-selected');
    }
    send('selection-result', matches.length === 1 ? 'highlighted' : 'not-unique-or-unavailable',
      undefined, matches.length === 1 ? matches[0].id : undefined, undefined,
      request.relationshipSourceId, request.relationshipTargetId, matches.length);
  } else if (request.status === 'capture-view' && typeof request.requestId === 'string' && viewer) {
    const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
    const canvas = active.get('canvas') as CanvasService;
    const box = canvas.viewbox();
    const root = canvas.getRootElement();
    const viewport = {
      x: box.x, y: box.y, width: box.width, height: box.height,
      rootId: root.businessObject?.id || root.id, ...bpmnNavigation?.captureContext(),
    };
    send('view-state', undefined, undefined, undefined, undefined, undefined, undefined, undefined,
      { requestId: request.requestId, viewport: validViewport(viewport) ? viewport : undefined });
  } else if (request.status === 'restore-view') {
    try {
      if (!validViewport(request.viewport)) throw new Error('The saved model viewport is invalid.');
      if (!viewer) throw new Error('No active model viewer is available for restoration.');
      const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
      restoreViewport(active, request.viewport);
      send('view-restored');
    } catch (error) {
      send('view-restore-error', (error as Error).message);
    }
  } else if (request.status === 'zoom' && viewer) {
    const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
    const canvas = active.get('canvas') as CanvasService;
    autoFit = request.action === 'fit';
    if (request.action === 'fit') {
      if (bpmnNavigation) bpmnNavigation.fit();
      else canvas.zoom('fit-viewport');
    }
    else if (request.action === 'reset') canvas.zoom(1);
    else canvas.zoom(canvas.zoom() * (request.action === 'in' ? 1.2 : 1 / 1.2));
  }
});

window.addEventListener('resize', () => {
  window.clearTimeout(resizeTimer);
  resizeTimer = window.setTimeout(() => {
    if (!viewer) return;
    const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
    const canvas = active.get('canvas') as CanvasService;
    canvas.resized();
    if (autoFit) {
      if (bpmnNavigation) bpmnNavigation.fit();
      else canvas.zoom('fit-viewport');
    }
  }, 120);
});

window.addEventListener('pagehide', () => {
  window.clearTimeout(resizeTimer);
  cancelDecisionPreviews('The model viewer was closed.');
  decisionLinks?.dispose();
  decisionLinks = undefined;
  viewer?.destroy();
  viewer = undefined;
  (window as Window & { __issue78Viewer?: unknown }).__issue78Viewer = undefined;
});
