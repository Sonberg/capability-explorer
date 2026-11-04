import NavigatedViewer from 'bpmn-js/lib/NavigatedViewer.js';
// @ts-expect-error dmn-js 18.0.1 ships its viewer JavaScript without TypeScript declarations.
import DmnViewer from 'dmn-js/lib/Viewer.js';
import 'bpmn-js/dist/assets/diagram-js.css';
import 'bpmn-js/dist/assets/bpmn-js.css';
import 'dmn-js/dist/assets/dmn-js-shared.css';
import 'dmn-js/dist/assets/dmn-js-drd.css';
import './viewer-frame-entry.css';

const LIMIT_BYTES = 1_048_576;
const LIMIT_ELEMENTS = 500;
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
let selectedElement: string | undefined;
type CanvasService = {
  zoom(value?: number | 'fit-viewport'): number;
  addMarker(id: string, marker: string): void;
  removeMarker(id: string, marker: string): void;
};
type RegistryService = { get(id: string): unknown; getAll(): unknown[] };
type ViewerServices = { get(name: string): unknown };

function referenceId(reference: unknown): string | undefined {
  if (typeof reference === 'string') return reference;
  if (reference && typeof reference === 'object' && 'id' in reference && typeof reference.id === 'string') return reference.id;
  return undefined;
}

function send(status: string, message?: string, renderedCount?: number, elementId?: string, renderedIds?: string[], relationshipSourceId?: string, relationshipTargetId?: string, relationshipMatchCount?: number) {
  if (!requestIdentity) return;
  window.parent.postMessage({ ...requestIdentity, status, message, renderedCount, elementId, renderedIds, relationshipSourceId, relationshipTargetId, relationshipMatchCount }, '*');
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
    selectedElement = event.element.id;
    canvas.addMarker(selectedElement, 'issue78-selected');
    send('selection', undefined, undefined, selectedElement, undefined,
      referenceId(event.element.businessObject?.sourceRef), referenceId(event.element.businessObject?.targetRef));
  });
}

window.addEventListener('message', async event => {
  if (event.source !== window.parent) return;
  const request = event.data as Record<string, unknown>;
  if (request?.status === 'render-request' && typeof request.nonce === 'string') {
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
      const imported = await instance.importXML(source);
      if (imported.warnings.length) throw new Error('Model importer reported warnings');
      if (kind === 'bpmn') ((instance as unknown as ViewerServices).get('canvas') as CanvasService).zoom('fit-viewport');
      else {
        const dmn = instance as DmnViewer;
        const drd = dmn.getViews().find((view: { type: string }) => view.type === 'drd');
        if (!drd) throw new Error('DMN source has no displayable DRD');
        await dmn.open(drd);
      }
      installSelectionEvents(instance);
      const active = kind === 'bpmn' ? instance as unknown as ViewerServices : (instance as DmnViewer).getActiveViewer() as ViewerServices;
      const renderedCount = (active.get('elementRegistry') as RegistryService).getAll().length;
      if (renderedCount > LIMIT_ELEMENTS) throw Object.assign(new Error('Rendered element count exceeds 500'), { code: 'too-many-elements' });
      const renderedIds = (active.get('elementRegistry') as RegistryService).getAll()
        .map((element) => (element as { id?: unknown }).id)
        .filter((id): id is string => typeof id === 'string');
      send('rendered', undefined, renderedCount, undefined, renderedIds);
    } catch (error) {
      viewer?.destroy();
      viewer = undefined;
      const failure = error as Error & { code?: string };
      state.textContent = `${failure.code ?? 'render-error'}: ${failure.message}`;
      send(failure.code ?? 'render-error', failure.message);
    }
    return;
  }
  if (!sameIdentity(request)) return;
  if (request.status === 'select-element' && typeof request.elementId === 'string' && viewer) {
    const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
    const canvas = active.get('canvas') as CanvasService;
    const element = (active.get('elementRegistry') as RegistryService).get(request.elementId);
    if (selectedElement) canvas.removeMarker(selectedElement, 'issue78-selected');
    selectedElement = undefined;
    if (element) {
      selectedElement = request.elementId;
      canvas.addMarker(selectedElement, 'issue78-selected');
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
      selectedElement = matches[0].id;
      canvas.addMarker(selectedElement, 'issue78-selected');
    }
    send('selection-result', matches.length === 1 ? 'highlighted' : 'not-unique-or-unavailable',
      undefined, matches.length === 1 ? matches[0].id : undefined, undefined,
      request.relationshipSourceId, request.relationshipTargetId, matches.length);
  } else if (request.status === 'zoom' && viewer) {
    const active = viewer instanceof NavigatedViewer ? viewer as unknown as ViewerServices : viewer.getActiveViewer() as ViewerServices;
    const canvas = active.get('canvas') as CanvasService;
    if (request.action === 'fit') canvas.zoom('fit-viewport');
    else if (request.action === 'reset') canvas.zoom(1);
    else canvas.zoom(canvas.zoom() * (request.action === 'in' ? 1.2 : 1 / 1.2));
  }
});

window.addEventListener('pagehide', () => {
  viewer?.destroy();
  viewer = undefined;
  (window as Window & { __issue78Viewer?: unknown }).__issue78Viewer = undefined;
});
