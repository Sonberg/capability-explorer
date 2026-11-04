import bpmnXml from './bpmn-representative.xml?raw';
import dmnXml from './dmn-representative.xml?raw';
import { createModelSandbox } from './viewer-sandbox.js';

const response = await fetch('/assets/viewer-frame-entry.js');
if (!response.ok) throw new Error(`Viewer frame bundle unavailable: ${response.status}`);
const frameScript = await response.text();
const sandbox = createModelSandbox(
  document.querySelector<HTMLElement>('#viewer-host')!,
  frameScript,
  '/assets/viewer-frame-entry.css'
);
window.__issue78Sandbox = sandbox;
window.__issue78Fixtures = { bpmn: bpmnXml, dmn: dmnXml };
window.__issue78Events = [] as unknown[];
window.__issue78SwitchOnAccepted = false;
sandbox.subscribe(event => {
  window.__issue78Events.push(event);
  if (window.__issue78SwitchOnAccepted && event.status === 'accepted' && event.sourceId === 'stale-first') {
    window.__issue78SwitchOnAccepted = false;
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
