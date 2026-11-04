import bpmnXml from './bpmn-representative.xml?raw';
import dmnXml from './dmn-representative.xml?raw';
import { createModelSandbox, loadTrustedViewerAssets } from '../../../platform/src/CapabilityExplorer.Explorer/client/src/models/viewer-sandbox.js';

const assets = await loadTrustedViewerAssets(new AbortController().signal);
const sandbox = createModelSandbox(
  document.querySelector<HTMLElement>('#viewer-host')!,
  assets
);
window.__issue78Sandbox = sandbox;
window.__issue78Fixtures = { bpmn: bpmnXml, dmn: dmnXml };
window.__issue78Events = [] as unknown[];
window.__issue78SwitchOnImportStarted = false;
sandbox.subscribe(event => {
  window.__issue78Events.push(event);
  if (window.__issue78SwitchOnImportStarted && event.status === 'import-started' && event.sourceId === 'stale-first') {
    window.__issue78SwitchOnImportStarted = false;
    sandbox.render({
      kind: 'bpmn',
      source: bpmnXml,
      scope: 'scope-current',
      generation: 42,
      sourceId: 'current-second'
    });
  }
});
window.__issue78Ready = true;
