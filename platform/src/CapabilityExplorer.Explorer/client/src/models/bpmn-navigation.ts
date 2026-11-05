import type NavigatedViewer from "bpmn-js/lib/NavigatedViewer";
import type Canvas from "diagram-js/lib/core/Canvas";
import type ElementRegistry from "diagram-js/lib/core/ElementRegistry";
import type Overlays from "diagram-js/lib/features/overlays/Overlays";
import type { ElementLike, RootLike } from "diagram-js/lib/model/Types";
import type { ModelViewBox, ModelViewport } from "./viewer-sandbox";
import { createHoverPopup, popupButton, popupText, type HoverPopup, type PopupContext } from "./hover-popup";
import { previewNote, renderSubprocessPreview } from "./mini-model-preview";

type NavigationHooks = {
  select(id: string): void;
  notice(message: string, id: string): void;
  autoFit(enabled: boolean): void;
  getAutoFit(): boolean;
  scopeChanged(): void;
  selecting(): boolean;
  /** Already-preflighted main source; enables subprocess hover previews in a separate viewer. */
  preview?: { source: string; diagramId?: string };
};

const BPMN_NS = "http://www.omg.org/spec/BPMN/20100524/MODEL";

export function installBpmnNavigation(viewer: NavigatedViewer, document: Document, hooks: NavigationHooks) {
  const canvas = viewer.get<Canvas>("canvas");
  const registry = viewer.get<ElementRegistry>("elementRegistry");
  const overlays = viewer.get<Overlays>("overlays");
  const buttons = new Map<string, HTMLButtonElement>();
  const embeddedParents = new Map<RootLike, {
    root: RootLike; caller: string; view?: ModelViewBox; autoFit?: boolean;
  }>();
  const visited = new Set([canvas.getRootElement()]);
  let previousRoot = canvas.getRootElement();
  let restoring = false;
  const calls: Array<{
    root: RootLike; view: ReturnType<Canvas["viewbox"]>; caller: string; target: string; name: string;
    hidden: Array<{ graphics: SVGElement; display: string; scoped: boolean }>;
    buttons: Array<{ button: HTMLButtonElement; hidden: HTMLButtonElement["hidden"] }>;
    targetRoot: RootLike; roots: Set<RootLike>; autoFit: boolean;
  }> = [];
  const container = canvas.getContainer();
  let subprocessPopup: HoverPopup | undefined;
  const quietFocus = (button: HTMLButtonElement | undefined) => {
    if (!button) return;
    if (subprocessPopup) subprocessPopup.focusQuietly(button);
    else button.focus({ preventScroll: true });
  };
  const callBar = window.document.createElement("nav");
  callBar.className = "model-drill-navigation";
  callBar.setAttribute("aria-label", "Called process navigation");
  callBar.hidden = true;
  const title = window.document.createElement("span");
  const back = window.document.createElement("button");
  back.type = "button";
  const within = window.document.createElement("button");
  within.type = "button";
  callBar.append(back, title, within);
  container.append(callBar);

  const sourceElement = (id: string) => [...document.getElementsByTagNameNS(BPMN_NS, "*")]
    .find(element => element.getAttribute("id") === id);
  const sourceName = (id: string) => sourceElement(id)?.getAttribute("name") || id;
  const callTarget = (id: string) => {
    const root = canvas.findRoot(`${id}_plane`) || canvas.findRoot(id);
    if (root) return { target: root, root };
    const participant = [...document.getElementsByTagNameNS(BPMN_NS, "participant")]
      .find(candidate => candidate.getAttribute("processRef") === id);
    const target = registry.get(participant?.getAttribute("id") || id);
    return { target, root: target && canvas.findRoot(target) };
  };
  const updateCallBar = () => {
    const call = calls.at(-1);
    const embedded = embeddedParents.get(canvas.getRootElement());
    callBar.hidden = !call && !embedded;
    container.classList.toggle("model-call-view", !callBar.hidden);
    if (embedded) {
      title.textContent = `Embedded subprocess: ${sourceName(embedded.caller)}`;
      const ownerId = sourceElement(embedded.caller)?.parentElement?.getAttribute("id");
      back.textContent = `Back to ${sourceName(ownerId || embedded.root.id.replace(/_plane$/, ""))}`;
    } else if (call) {
      title.textContent = `Called process: ${call.name}`;
      back.textContent = `Back to ${sourceName(call.caller)}`;
      within.textContent = `Back to ${call.name}`;
    }
    within.hidden = !!embedded || !call || canvas.getRootElement() === call.targetRoot;
    back.title = back.textContent || "";
    title.title = title.textContent || "";
  };
  const restoreCall = () => {
    const call = calls.pop();
    if (!call) return undefined;
    canvas.removeMarker(call.target, "issue78-call-target");
    for (const { graphics, display, scoped } of call.hidden) {
      graphics.style.display = display;
      graphics.classList.toggle("model-scope-hidden", scoped);
    }
    for (const { button, hidden } of call.buttons) button.hidden = hidden;
    const previous = calls.at(-1);
    if (previous) canvas.addMarker(previous.target, "issue78-call-target");
    return call;
  };
  const exitCallView = () => {
    while (calls.length) restoreCall();
    updateCallBar();
  };
  const focusTarget = (target: ElementLike) => {
    if (typeof target.x === "number" && typeof target.y === "number" &&
        typeof target.width === "number" && typeof target.height === "number") {
      canvas.viewbox({ x: target.x - 40, y: target.y - 60, width: target.width + 80, height: target.height + 100 });
    } else {
      canvas.zoom("fit-viewport");
    }
  };
  const fit = () => {
    const call = calls.at(-1);
    const target = call && registry.get(call.target);
    if (target && canvas.getRootElement() === call.targetRoot) focusTarget(target);
    else canvas.zoom("fit-viewport");
  };
  within.addEventListener("click", () => {
    const call = calls.at(-1);
    if (!call) return;
    canvas.setRootElement(call.targetRoot);
    fit();
    back.focus({ preventScroll: true });
  });
  back.addEventListener("click", () => {
    const embedded = embeddedParents.get(canvas.getRootElement());
    if (embedded) {
      canvas.setRootElement(embedded.root);
      if (embedded.view) canvas.viewbox(embedded.view);
      hooks.autoFit(embedded.autoFit ?? false);
      hooks.select(embedded.caller);
      updateCallBar();
      quietFocus(buttons.get(embedded.caller));
      return;
    }
    const call = restoreCall();
    if (!call) return;
    if (canvas.getRootElement() !== call.root) canvas.setRootElement(call.root);
    canvas.viewbox(call.view);
    hooks.autoFit(call.autoFit);
    hooks.select(call.caller);
    updateCallBar();
    quietFocus(buttons.get(call.caller));
  });

  const drill = (element: ElementLike) => {
    const source = sourceElement(element.id);
    if (!source) return;
    if (source.localName === "subProcess") {
      const root = canvas.findRoot(`${element.id}_plane`);
      if (!root) return;
      embeddedParents.set(root, {
        root: canvas.getRootElement(), caller: element.id, view: canvas.viewbox(), autoFit: hooks.getAutoFit(),
      });
      calls.at(-1)?.roots.add(root);
      canvas.setRootElement(root);
      return;
    }
    if (source.localName !== "callActivity") return;
    const reference = source.getAttribute("calledElement") || "";
    const id = reference.startsWith("#") ? reference.slice(1) : reference;
    if (/[:/#]/.test(id)) {
      hooks.notice("This call points outside the file. External models are not fetched.", element.id);
      return;
    }
    const process = sourceElement(id);
    if (!process || process.localName !== "process") {
      hooks.notice("The called process was not found in this file.", element.id);
      return;
    }
    const { target, root } = callTarget(id);
    if (!target || !root) {
      hooks.notice("The called process is defined in this file but has no available diagram.", element.id);
      return;
    }
    const call = {
      root: canvas.getRootElement(), view: canvas.viewbox(), caller: element.id,
      target: target.id, name: sourceName(id),
      targetRoot: root, roots: new Set<RootLike>([root]), autoFit: hooks.getAutoFit(),
      hidden: (root.children || []).map((child: ElementLike) => {
        const graphics = registry.getGraphics(child).parentElement;
        if (!(graphics instanceof SVGElement)) throw new Error("BPMN scope graphics are unavailable");
        const saved = { graphics, display: graphics.style.display, scoped: graphics.classList.contains("model-scope-hidden") };
        graphics.style.display = child === target || target === root ? "" : "none";
        graphics.classList.toggle("model-scope-hidden", child !== target && target !== root);
        return saved;
      }),
      buttons: [...buttons].map(([id, button]) => {
        const hidden = button.hidden;
        let owner = registry.get(id);
        while (owner && owner !== target && owner !== root) owner = owner.parent;
        button.hidden = owner !== target && target !== root;
        return { button, hidden };
      }),
    };
    calls.push(call);
    if (canvas.getRootElement() !== root) canvas.setRootElement(root);
    focusTarget(target);
    hooks.autoFit(false);
    canvas.addMarker(target.id, "issue78-call-target");
    if (!restoring) hooks.select(id);
    updateCallBar();
    if (!restoring) back.focus({ preventScroll: true });
  };

  const subprocessContent = (element: ElementLike, preview: NonNullable<NavigationHooks["preview"]>) =>
    (popup: HTMLElement, context: PopupContext) => {
      const source = sourceElement(element.id);
      const called = source?.localName === "callActivity";
      const targetId = called ? source.getAttribute("calledElement")?.replace(/^#/, "") || "" : element.id;
      const diagrams = [...document.getElementsByTagNameNS("http://www.omg.org/spec/BPMN/20100524/DI", "BPMNDiagram")];
      const targetDiagram = called ? diagrams.find(diagram =>
        [...diagram.children].some(plane => plane.getAttribute("bpmnElement") === targetId)) : undefined;
      const body = window.document.createElement("div");
      body.className = "model-preview-body";
      const pin = popupButton("Pin preview", () => {
        context.pin();
        pin.disabled = true;
        pin.textContent = "Pinned";
      });
      const actions = window.document.createElement("div");
      actions.className = "model-preview-actions";
      actions.append(
        popupButton(called ? "Open called process" : "Open subprocess", () => {
          context.hide();
          drill(element);
        }),
        pin,
        popupButton("Close", () => context.hide(true)),
      );
      popup.replaceChildren(
        popupText("h2", called ? "Called process" : "Embedded subprocess"),
        popupText("strong", sourceName(targetId)),
        popupText("p", called ? "Invoked by this activity; defined separately from the caller."
          : "Work contained within this process."),
        actions, body);
      if (called && (/[:/#]/.test(targetId) || sourceElement(targetId)?.localName !== "process")) {
        body.replaceChildren(previewNote("The called process cannot be resolved within this source. External models are not fetched.", true));
      } else {
        void renderSubprocessPreview(body, preview.source, targetDiagram?.getAttribute("id") || preview.diagramId,
          targetId, context, called);
      }
    };
  if (hooks.preview) subprocessPopup = createHoverPopup("Process detail insight", "model-subprocess-preview");

  overlays.remove({ type: "drilldown" });
  for (const element of registry.getAll()) {
    const source = sourceElement(element.id);
    const subprocess = source?.localName === "subProcess" && canvas.findRoot(`${element.id}_plane`);
    const call = source?.localName === "callActivity" && source.hasAttribute("calledElement");
    if ((!subprocess && !call) || typeof element.width !== "number" || typeof element.height !== "number") continue;
    const button = window.document.createElement("button");
    button.type = "button";
    button.className = "model-drill-button";
    const reference = source?.getAttribute("calledElement")?.replace(/^#/, "") || "";
    button.setAttribute("aria-label", subprocess ? `Open ${sourceName(element.id)}` : `Go to called process ${sourceName(reference)}`);
    button.addEventListener("click", event => { event.stopPropagation(); drill(element); });
    if (subprocessPopup && hooks.preview) {
      subprocessPopup.attach(button, { content: subprocessContent(element, hooks.preview) });
    } else {
      button.title = subprocess ? `Open embedded subprocess ${sourceName(element.id)} (view only)`
        : `Open called process ${sourceName(reference)} (view only)`;
    }
    overlays.add(element.id, "model-drill", {
      position: { top: element.height - 25, left: element.width / 2 - 12.5 },
      html: button,
    });
    buttons.set(element.id, button);
    canvas.addMarker(element.id, "model-drillable");
  }
  viewer.on("element.dblclick", (event: { element: ElementLike }) => {
    if (buttons.has(event.element.id)) drill(event.element);
  });
  viewer.on("root.set", (event: { element: RootLike }) => {
    const root = event.element;
    subprocessPopup?.hide();
    if (restoring) {
      visited.add(root);
      previousRoot = root;
      updateCallBar();
      return;
    }
    if (calls.length && !calls.at(-1)?.roots.has(root)) exitCallView();
    let returning = embeddedParents.get(previousRoot);
    while (returning && returning.root !== root) returning = embeddedParents.get(returning.root);
    if (!visited.has(root)) canvas.zoom("fit-viewport");
    visited.add(root);
    previousRoot = root;
    hooks.autoFit(!returning);
    if (!hooks.selecting()) hooks.select(returning?.caller || sourceElement(root.id.replace(/_plane$/, ""))?.id || root.id);
    if (returning) quietFocus(buttons.get(returning.caller));
    updateCallBar();
  });
  viewer.on("canvas.viewbox.changed", () => subprocessPopup?.reposition());
  viewer.on("diagram.destroy", () => {
    subprocessPopup?.dispose();
    subprocessPopup = undefined;
    callBar.remove();
  });
  const rootById = (id: string) => {
    const root = canvas.findRoot(id);
    if (!root || root.id !== id) throw new Error(`Saved BPMN root "${id}" is unavailable.`);
    return root;
  };
  const box = (view: ModelViewBox): ModelViewBox => ({
    x: view.x, y: view.y, width: view.width, height: view.height,
  });
  const validBox = (view: unknown): view is ModelViewBox => {
    if (!view || typeof view !== "object") return false;
    const value = view as ModelViewBox;
    return [value.x, value.y, value.width, value.height].every(number =>
      typeof number === "number" && Number.isFinite(number)) && value.width > 0 && value.height > 0;
  };
  return {
    fit,
    dispose() { subprocessPopup?.dispose(); },
    captureContext(): Pick<ModelViewport, "callPath" | "embeddedPath"> {
      return {
        callPath: calls.map(call => ({
          callerId: call.caller, rootId: call.root.id, view: box(call.view), autoFit: call.autoFit,
        })),
        embeddedPath: [...embeddedParents].map(([root, parent]) => ({
          callerId: parent.caller, rootId: root.id, parentRootId: parent.root.id,
          view: parent.view && box(parent.view), autoFit: parent.autoFit,
        })),
      };
    },
    restoreContext(viewport: ModelViewport) {
      if (!viewport.rootId) throw new Error("Saved BPMN navigation requires a root.");
      const finalRoot = canvas.findRoot(`${viewport.rootId}_plane`) || canvas.findRoot(viewport.rootId);
      if (!finalRoot) throw new Error(`Saved BPMN root "${viewport.rootId}" is unavailable.`);
      const path = viewport.callPath === undefined ? [] : viewport.callPath;
      const embedded = viewport.embeddedPath === undefined ? [] : viewport.embeddedPath;
      if (!Array.isArray(path) || path.length > 500 || !Array.isArray(embedded) || embedded.length > 500) {
        throw new Error("Saved BPMN navigation exceeds its bounded path.");
      }
      const parents: typeof embeddedParents = new Map();
      for (const entry of embedded) {
        if (!entry || typeof entry.rootId !== "string" || typeof entry.parentRootId !== "string" ||
            typeof entry.callerId !== "string" ||
            (entry.view !== undefined && !validBox(entry.view)) ||
            (entry.autoFit !== undefined && typeof entry.autoFit !== "boolean")) throw new Error("Invalid saved embedded navigation.");
        const root = rootById(entry.rootId);
        const parent = rootById(entry.parentRootId);
        const caller = registry.get(entry.callerId);
        if (!caller || sourceElement(entry.callerId)?.localName !== "subProcess" ||
            canvas.findRoot(`${entry.callerId}_plane`) !== root || canvas.findRoot(caller) !== parent || parents.has(root)) {
          throw new Error("Saved embedded caller does not match this source.");
        }
        parents.set(root, { root: parent, caller: entry.callerId, view: entry.view, autoFit: entry.autoFit });
      }
      const chain = (root: RootLike, ancestor: RootLike): RootLike[] => {
        const result: RootLike[] = [];
        while (root !== ancestor) {
          if (result.includes(root)) throw new Error("Saved embedded navigation contains a cycle.");
          result.push(root);
          const parent = parents.get(root);
          if (!parent) throw new Error("Saved root is outside the called-process scope.");
          root = parent.root;
        }
        return result;
      };
      for (const root of parents.keys()) {
        const seen = new Set<RootLike>();
        let current: RootLike | undefined = root;
        while (current) {
          if (seen.has(current)) throw new Error("Saved embedded navigation contains a cycle.");
          seen.add(current);
          current = parents.get(current)?.root;
        }
      }
      const steps = path.map(entry => {
        if (!entry || typeof entry.callerId !== "string" || typeof entry.rootId !== "string" ||
            typeof entry.autoFit !== "boolean" || !validBox(entry.view)) throw new Error("Invalid saved called-process navigation.");
        const root = rootById(entry.rootId);
        const caller = registry.get(entry.callerId);
        const source = sourceElement(entry.callerId);
        const id = source?.getAttribute("calledElement")?.replace(/^#/, "") || "";
        const { root: targetRoot } = callTarget(id);
        if (!caller || canvas.findRoot(caller) !== root || source?.localName !== "callActivity" ||
            !id || /[:/#]/.test(id) || sourceElement(id)?.localName !== "process" || !targetRoot) {
          throw new Error("Saved call does not resolve to a local caller and target in this source.");
        }
        return { entry, root, caller, targetRoot };
      });
      for (let index = 0; index < steps.length; index++) {
        chain(steps[index + 1]?.root || finalRoot, steps[index].targetRoot);
      }
      restoring = true;
      try {
        exitCallView();
        embeddedParents.clear();
        for (const [root, parent] of parents) embeddedParents.set(root, parent);
        for (const step of steps) {
          const previous = calls.at(-1);
          if (previous) for (const root of chain(step.root, previous.targetRoot)) previous.roots.add(root);
          canvas.setRootElement(step.root);
          canvas.viewbox(step.entry.view);
          const length = calls.length;
          drill(step.caller);
          if (calls.length !== length + 1) throw new Error("Saved call could not be restored.");
          calls.at(-1)!.autoFit = step.entry.autoFit;
        }
        const last = calls.at(-1);
        if (last) for (const root of chain(finalRoot, last.targetRoot)) last.roots.add(root);
        canvas.setRootElement(finalRoot);
        updateCallBar();
      } finally {
        restoring = false;
      }
    },
    prepareSelection(id: string) {
      const element = registry.get(id);
      if (!calls.length || !element) return;
      const root = canvas.findRoot(element);
      if (registry.getGraphics(element)?.closest(".model-scope-hidden") || !root || !calls.at(-1)?.roots.has(root)) {
        exitCallView();
        hooks.scopeChanged();
      }
    },
  };
}
