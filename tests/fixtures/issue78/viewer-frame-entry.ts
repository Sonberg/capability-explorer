import NavigatedViewer from 'bpmn-js/lib/NavigatedViewer.js';
import DmnViewer from 'dmn-js/lib/Viewer.js';
import 'bpmn-js/dist/assets/diagram-js.css';
import 'bpmn-js/dist/assets/bpmn-js.css';
import 'dmn-js/dist/assets/dmn-js-shared.css';
import 'dmn-js/dist/assets/dmn-js-drd.css';

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
let viewer: { destroy(): void } | undefined;
(window as Window & { __issue78Viewer?: unknown }).__issue78Viewer = undefined;

function send(request: Record<string, unknown>, status: string, message?: string, renderedCount?: number) {
  window.parent.postMessage({ ...request, status, message, renderedCount }, '*');
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

function textCell(parent: HTMLElement, tag: 'th' | 'td', value: string) {
  const cell = document.createElement(tag);
  cell.textContent = value;
  parent.append(cell);
}

function renderDmnSourceTable(document: Document) {
  const tableModel = document.getElementsByTagNameNS(DMN_NS, 'decisionTable').item(0);
  if (!tableModel) return false;
  const table = document.createElement('table');
  const caption = document.createElement('caption');
  caption.textContent = `Decision table: ${tableModel.getAttribute('hitPolicy') || 'UNSPECIFIED'} hit policy`;
  table.append(caption);
  const inputs = [...tableModel.getElementsByTagNameNS(DMN_NS, 'input')];
  const outputs = [...tableModel.getElementsByTagNameNS(DMN_NS, 'output')];
  const header = document.createElement('tr');
  for (const input of inputs) textCell(header, 'th', input.getAttribute('label') || input.getAttribute('name') || 'Input');
  for (const output of outputs) textCell(header, 'th', output.getAttribute('label') || output.getAttribute('name') || 'Output');
  const thead = document.createElement('thead');
  thead.append(header);
  table.append(thead);
  const tbody = document.createElement('tbody');
  for (const rule of [...tableModel.getElementsByTagNameNS(DMN_NS, 'rule')]) {
    const row = document.createElement('tr');
    for (const entry of [...rule.getElementsByTagNameNS(DMN_NS, 'inputEntry')]) {
      textCell(row, 'td', entry.getElementsByTagNameNS(DMN_NS, 'text').item(0)?.textContent ?? '');
    }
    for (const entry of [...rule.getElementsByTagNameNS(DMN_NS, 'outputEntry')]) {
      textCell(row, 'td', entry.getElementsByTagNameNS(DMN_NS, 'text').item(0)?.textContent ?? '');
    }
    tbody.append(row);
  }
  table.append(tbody);
  state.append(table);
  return true;
}

window.addEventListener('message', async event => {
  const request = event.data as Record<string, unknown>;
  if (event.source !== window.parent || request?.status !== 'render-request' || typeof request.nonce !== 'string') return;
  send(request, 'accepted');
  if (request.testLateMessage === true) queueMicrotask(() => send(request, 'late-test-message'));
  const kind = request.kind;
  const source = request.source;
  if ((kind !== 'bpmn' && kind !== 'dmn') || typeof source !== 'string') {
    send(request, 'invalid-request', 'Invalid render request');
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
    const instance = kind === 'bpmn'
      ? new NavigatedViewer({ container: canvas })
      : new DmnViewer({ container: canvas });
    viewer = instance;
    (window as Window & { __issue78Viewer?: unknown }).__issue78Viewer = instance;
    const imported = await instance.importXML(source);
    if (imported.warnings.length) throw new Error('Model importer reported warnings');
    if (kind === 'bpmn') (instance as NavigatedViewer).get('canvas').zoom('fit-viewport');
    else {
      const dmn = instance as DmnViewer;
      const drd = dmn.getViews().find(view => view.type === 'drd');
      if (!drd) throw new Error('DMN source has no displayable DRD');
      await dmn.open(drd);
      renderDmnSourceTable(parsed.document);
    }
    const renderedCount = kind === 'bpmn'
      ? (instance as NavigatedViewer).get('elementRegistry').getAll().length
      : (instance as DmnViewer).getActiveViewer().get('elementRegistry').getAll().length;
    if (renderedCount > LIMIT_ELEMENTS) throw Object.assign(new Error('Rendered element count exceeds 500'), { code: 'too-many-elements' });
    send(request, 'rendered', undefined, renderedCount);
  } catch (error) {
    viewer?.destroy();
    viewer = undefined;
    const failure = error as Error & { code?: string };
    state.textContent = `${failure.code ?? 'render-error'}: ${failure.message}`;
    send(request, failure.code ?? 'render-error', failure.message);
  }
});

window.addEventListener('pagehide', () => {
  viewer?.destroy();
  viewer = undefined;
  (window as Window & { __issue78Viewer?: unknown }).__issue78Viewer = undefined;
});
