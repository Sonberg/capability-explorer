import type NavigatedViewer from 'bpmn-js/lib/NavigatedViewer';
import type Canvas from 'diagram-js/lib/core/Canvas';
import type ElementRegistry from 'diagram-js/lib/core/ElementRegistry';
import { importBpmnDiagram } from 'bpmn-js/lib/import/Importer.js';

/** bpmn-js loads a hierarchy, not unrelated called processes; retain their authored roots for navigation. */
export async function loadBpmnProcessDiagrams(viewer: NavigatedViewer) {
  const canvas = viewer.get<Canvas>('canvas');
  const registry = viewer.get<ElementRegistry>('elementRegistry');
  const original = canvas.getRootElement();
  const definitions: {
    diagrams?: Array<{ plane?: { bpmnElement?: { id: string; $type: string } } }>;
  } = viewer.getDefinitions();
  try {
    for (const diagram of definitions.diagrams || []) {
      const root = diagram.plane?.bpmnElement;
      if (!root || !['bpmn:Process', 'bpmn:Collaboration'].includes(root.$type) ||
          registry.get(root.id) || canvas.findRoot(`${root.id}_plane`)) continue;
      const represented = registry.getAll().some(element => element.businessObject?.processRef?.id === root.id);
      if (represented) continue;
      const imported = await importBpmnDiagram(viewer, definitions, diagram);
      if (imported.warnings.length) throw new Error('Called-process diagram importer reported warnings.');
    }
  } finally {
    canvas.setRootElement(original);
  }
}
