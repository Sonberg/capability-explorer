import type NavigatedViewer from 'bpmn-js/lib/NavigatedViewer';
import type Canvas from 'diagram-js/lib/core/Canvas';
import type ElementRegistry from 'diagram-js/lib/core/ElementRegistry';
import type Overlays from 'diagram-js/lib/features/overlays/Overlays';
import type { ModelDecisionPreview, ModelRequest } from './viewer-sandbox';
import { createHoverPopup, popupButton, popupText, type PopupContext } from './hover-popup';
import { previewNote, renderDecisionPreview, type ParseSource } from './mini-model-preview';

type DecisionLink = NonNullable<ModelRequest['decisionLinks']>[number];
export type DecisionPreviewLoader = (elementId: string, signal: AbortSignal) => Promise<ModelDecisionPreview>;

export function installBpmnDecisionLinks(
  viewer: NavigatedViewer,
  links: ModelRequest['decisionLinks'],
  open: (elementId: string) => void,
  preview?: DecisionPreviewLoader,
  parseSource?: ParseSource
) {
  const canvas = viewer.get<Canvas>('canvas');
  const registry = viewer.get<ElementRegistry>('elementRegistry');
  const overlays = viewer.get<Overlays>('overlays');
  const buttons = new Map<string, HTMLButtonElement>();
  const overlayIds: string[] = [];
  const cache = new Map<string, ModelDecisionPreview>();
  let disposed = false;
  const popup = createHoverPopup('Authored decision reference insight', 'model-decision-preview');

  const content = (link: DecisionLink) => (element: HTMLElement, context: PopupContext) => {
    const references = (link.references || []).filter(reference => typeof reference === 'string');
    const list = document.createElement('ul');
    for (const reference of references) {
      const item = document.createElement('li');
      item.textContent = reference;
      list.append(item);
    }
    const summary = popupText('p', typeof link.preview === 'string' ? link.preview :
      (link.available ? 'View the linked decision model.' : 'Target unavailable. Open the insight for an explanation.'));
    const body = document.createElement('div');
    body.className = 'model-preview-body';
    const provenance = popupText('small', '', 'model-preview-provenance');
    provenance.hidden = true;
    const actions = document.createElement('div');
    actions.className = 'model-preview-actions';
    actions.append(
      popupButton(references.length > 1 ? 'View decision references' : 'Open decision', () => {
        context.hide();
        open(link.elementId);
      }),
      popupButton('Close', () => context.hide(true)),
    );
    element.replaceChildren(
      popupText('h2', 'Authored decision reference'),
      popupText('strong', link.label),
      actions,
      popupText('p', 'Read-only Explorer annotation. Authored rules are not evaluated or approved.', 'model-preview-explanation'),
      summary, list, body, provenance);
    if (!link.available || !preview || !parseSource) return;
    const load = (result: ModelDecisionPreview) => {
      if (context.signal.aborted) return;
      if (typeof result.provenance === 'string' && result.provenance) {
        provenance.textContent = `Source: ${result.provenance}`;
        provenance.hidden = false;
      }
      void renderDecisionPreview(body, result, parseSource, context);
    };
    const cached = cache.get(link.elementId);
    if (cached) { load(cached); return; }
    // Reserve the mini-diagram box while the parent verifies the source so the popup does not jump.
    const placeholder = document.createElement('div');
    placeholder.className = 'model-mini-diagram model-mini-placeholder';
    placeholder.append(previewNote('Loading verified decision source…'));
    body.replaceChildren(placeholder);
    preview(link.elementId, context.signal).then(result => {
      if (context.signal.aborted || disposed) return;
      if (!result.error) cache.set(link.elementId, result);
      load(result);
    }, (error: Error) => {
      if (context.signal.aborted || disposed) return;
      body.replaceChildren(previewNote(`Decision preview unavailable: ${error.message}`, true));
      context.reposition();
    });
  };

  const update = () => {
    for (const [id, button] of buttons) {
      const element = registry.get(id);
      const graphics = element && registry.getGraphics(element);
      button.hidden = !element || canvas.findRoot(element) !== canvas.getRootElement() ||
        !graphics || !!graphics.closest('.model-scope-hidden, [style*="display: none"]');
    }
    popup.reposition();
  };
  for (const link of links || []) {
    if (!link || typeof link.elementId !== 'string' || typeof link.label !== 'string' ||
        typeof link.available !== 'boolean' || buttons.has(link.elementId)) continue;
    const element = registry.get(link.elementId);
    if (!element || typeof element.width !== 'number' || typeof element.height !== 'number' ||
        !registry.getGraphics(element)) continue;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'model-decision-link';
    button.classList.toggle('model-decision-link-unavailable', !link.available);
    button.textContent = 'DMN';
    // No native title: the browser tooltip would compete with the deliberate hover delay.
    button.setAttribute('aria-label', `Open decision reference for ${link.label}`);
    popup.attach(button, { content: content(link), disclosure: true, pinOnClick: true });
    button.addEventListener('dblclick', event => event.stopPropagation());
    overlayIds.push(overlays.add(element.id, 'model-decision-reference', {
      position: { top: 4, left: Math.max(0, element.width - 39) },
      html: button,
    }));
    buttons.set(element.id, button);
  }
  // Call scopes hide SVG ancestors independently of diagram-js overlays.
  const observer = new MutationObserver(update);
  const svg = canvas.getContainer().querySelector('svg');
  if (svg) observer.observe(svg, { subtree: true, attributes: true, attributeFilter: ['class', 'style'] });
  const rootChanged = () => { popup.hide(); update(); };
  const viewboxChanged = () => popup.reposition();
  viewer.on('root.set', rootChanged);
  viewer.on('canvas.viewbox.changed', viewboxChanged);
  update();
  const dispose = () => {
    if (disposed) return;
    disposed = true;
    observer.disconnect();
    popup.dispose();
    viewer.off('root.set', rootChanged);
    viewer.off('canvas.viewbox.changed', viewboxChanged);
    viewer.off('diagram.destroy', dispose);
    for (const id of overlayIds) overlays.remove(id);
    buttons.clear();
    cache.clear();
  };
  viewer.on('diagram.destroy', dispose);
  return {
    dispose,
    /** Programmatic focus (parent selection): never opens a preview by itself. */
    focus(id: string) {
      update();
      const button = buttons.get(id);
      if (button && !button.hidden) popup.focusQuietly(button);
    },
  };
}
