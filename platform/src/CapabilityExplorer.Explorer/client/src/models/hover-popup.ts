export const HOVER_OPEN_DELAY_MS = 450;
export const HOVER_CLOSE_DELAY_MS = 200;

export type PopupContext = {
  signal: AbortSignal;
  /** Registers teardown that runs when this popup content is hidden, replaced or disposed. */
  cleanup(callback: () => void): void;
  hide(returnFocus?: boolean): void;
  pin(): void;
  reposition(): void;
};

export type PopupContent = (popup: HTMLElement, context: PopupContext) => void;

type AttachOptions = {
  content: PopupContent;
  /** Mirror popup state into aria-expanded/aria-controls (disclosure-style anchors). */
  disclosure?: boolean;
  /** Pin and open immediately when the anchor itself is clicked. */
  pinOnClick?: boolean;
};

export type HoverPopup = ReturnType<typeof createHoverPopup>;

/** RFC 4122 v4 id; falls back to getRandomValues where randomUUID needs a secure context. */
export function randomId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map(byte => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

let openPopup: { hide(): void } | undefined;

/** True when the anchor currently has a visible box (overlay roots and call scopes hide anchors). */
function visible(anchor: HTMLElement) {
  return anchor.isConnected && !anchor.hidden && anchor.getClientRects().length > 0 &&
    !anchor.closest('[hidden], .model-scope-hidden');
}

/**
 * Deliberate hover popup: pointer hover opens after a delay and is cancelled when the pointer leaves first;
 * user keyboard focus opens immediately; programmatic focus can be suppressed with focusQuietly().
 * Only one popup is open per document.
 */
export function createHoverPopup(label: string, className = '') {
  const popup = document.createElement('section');
  popup.id = `model-hover-popup-${randomId()}`;
  popup.className = `model-hover-popup ${className}`.trim();
  popup.setAttribute('role', 'region');
  popup.setAttribute('aria-label', label);
  popup.hidden = true;
  document.body.append(popup);
  const anchors = new Map<HTMLElement, { options: AttachOptions; remove(): void }>();
  let current: HTMLElement | undefined;
  let controller: AbortController | undefined;
  let cleanups: Array<() => void> = [];
  let pinned = false;
  let quiet = false;
  let openTimer: number | undefined;
  let closeTimer: number | undefined;
  let disposed = false;
  // Placement is chosen once per opening so async content growth never flips the popup across the anchor.
  let side: 'below' | 'above' | undefined;

  const cancelOpen = () => { window.clearTimeout(openTimer); openTimer = undefined; };
  const cancelClose = () => { window.clearTimeout(closeTimer); closeTimer = undefined; };
  const teardown = () => {
    controller?.abort();
    controller = undefined;
    const pending = cleanups;
    cleanups = [];
    for (const callback of pending) {
      try { callback(); } catch (error) { console.error('Model preview cleanup failed.', error); }
    }
  };
  const hide = (returnFocus = false) => {
    cancelOpen();
    cancelClose();
    teardown();
    const anchor = current;
    current = undefined;
    side = undefined;
    pinned = false;
    popup.hidden = true;
    popup.replaceChildren();
    if (openPopup === controllerHandle) openPopup = undefined;
    if (anchor && anchors.get(anchor)?.options.disclosure) anchor.setAttribute('aria-expanded', 'false');
    if (returnFocus && anchor && visible(anchor)) focusQuietly(anchor);
  };
  const controllerHandle = { hide: () => hide() };
  const position = () => {
    if (!current || popup.hidden) return;
    if (!visible(current)) { hide(); return; }
    const anchor = current.getBoundingClientRect();
    const box = popup.getBoundingClientRect();
    const left = Math.max(8, Math.min(anchor.left, window.innerWidth - box.width - 8));
    side ??= anchor.bottom + 8 + box.height <= window.innerHeight - 8 || anchor.top < window.innerHeight / 2 ? 'below' : 'above';
    const preferred = side === 'below' ? anchor.bottom + 8 : anchor.top - box.height - 8;
    const top = Math.max(8, Math.min(preferred, window.innerHeight - box.height - 8));
    popup.style.left = `${left}px`;
    popup.style.top = `${top}px`;
  };
  const resize = new ResizeObserver(position);
  resize.observe(popup);

  const show = (anchor: HTMLElement, pin = false) => {
    cancelOpen();
    cancelClose();
    const entry = anchors.get(anchor);
    if (disposed || !entry || !visible(anchor)) return;
    if (current !== anchor) {
      hide();
      if (openPopup && openPopup !== controllerHandle) openPopup.hide();
      openPopup = controllerHandle;
      current = anchor;
      controller = new AbortController();
      popup.hidden = false;
      if (entry.options.disclosure) {
        anchor.setAttribute('aria-expanded', 'true');
        anchor.setAttribute('aria-controls', popup.id);
      }
      const signal = controller.signal;
      entry.options.content(popup, {
        signal,
        cleanup(callback) { if (signal.aborted) callback(); else cleanups.push(callback); },
        hide(returnFocus) { if (!signal.aborted) hide(returnFocus); },
        pin() { if (!signal.aborted) pinned = true; },
        reposition() { if (!signal.aborted) position(); },
      });
    }
    pinned = pinned || pin;
    popup.hidden = false;
    position();
  };
  const scheduleClose = () => {
    cancelOpen();
    cancelClose();
    if (!current) return;
    closeTimer = window.setTimeout(() => {
      closeTimer = undefined;
      const active = document.activeElement;
      if (!pinned && !popup.matches(':hover') && !current?.matches(':hover') &&
          !popup.contains(active) && active !== current) hide();
    }, HOVER_CLOSE_DELAY_MS);
  };
  const focusFirst = () => popup.querySelector<HTMLElement>('button:not([disabled])')?.focus({ preventScroll: true });
  const focusQuietly = (anchor: HTMLElement) => {
    quiet = true;
    try { anchor.focus({ preventScroll: true }); } finally { quiet = false; }
  };
  const outside = (event: PointerEvent) => {
    if (current && event.target instanceof Node && !popup.contains(event.target) && !current.contains(event.target)) hide();
  };
  const keydown = (event: KeyboardEvent) => {
    if (event.key === 'Escape' && current) { event.preventDefault(); hide(popup.contains(document.activeElement) || document.activeElement === current); }
  };
  popup.addEventListener('pointerenter', cancelClose);
  popup.addEventListener('pointerleave', scheduleClose);
  popup.addEventListener('focusin', cancelClose);
  popup.addEventListener('focusout', scheduleClose);
  popup.addEventListener('click', event => event.stopPropagation());
  popup.addEventListener('dblclick', event => event.stopPropagation());
  document.addEventListener('pointerdown', outside, true);
  document.addEventListener('keydown', keydown);
  window.addEventListener('resize', position);

  const attach = (anchor: HTMLElement, options: AttachOptions) => {
    if (anchors.has(anchor)) anchors.get(anchor)!.remove();
    const enter = (event: PointerEvent) => {
      if (event.pointerType === 'touch') return;
      cancelClose();
      if (current === anchor || (pinned && current)) return;
      cancelOpen();
      openTimer = window.setTimeout(() => {
        openTimer = undefined;
        if (anchor.matches(':hover')) show(anchor);
      }, HOVER_OPEN_DELAY_MS);
    };
    const focus = () => {
      // Only user keyboard focus opens immediately; pointer focus waits for the hover delay.
      if (!quiet && anchor.matches(':focus-visible')) show(anchor);
    };
    const keys = (event: KeyboardEvent) => {
      if (event.key === 'ArrowDown' || (event.key === 'Tab' && !event.shiftKey && current === anchor)) {
        event.preventDefault();
        show(anchor, true);
        focusFirst();
      }
    };
    const click = (event: MouseEvent) => {
      if (!options.pinOnClick) { hide(); return; }
      event.stopPropagation();
      show(anchor, true);
      focusFirst();
    };
    anchor.addEventListener('pointerenter', enter);
    anchor.addEventListener('pointerleave', scheduleClose);
    anchor.addEventListener('focus', focus);
    anchor.addEventListener('blur', scheduleClose);
    anchor.addEventListener('keydown', keys);
    anchor.addEventListener('click', click);
    if (options.disclosure) anchor.setAttribute('aria-expanded', 'false');
    const remove = () => {
      anchor.removeEventListener('pointerenter', enter);
      anchor.removeEventListener('pointerleave', scheduleClose);
      anchor.removeEventListener('focus', focus);
      anchor.removeEventListener('blur', scheduleClose);
      anchor.removeEventListener('keydown', keys);
      anchor.removeEventListener('click', click);
      if (current === anchor) hide();
      anchors.delete(anchor);
    };
    anchors.set(anchor, { options, remove });
    return remove;
  };

  return {
    element: popup,
    attach,
    show,
    hide,
    focusQuietly,
    focusFirst,
    reposition: position,
    isOpenFor(anchor: HTMLElement) { return current === anchor; },
    dispose() {
      if (disposed) return;
      hide();
      disposed = true;
      for (const entry of [...anchors.values()]) entry.remove();
      resize.disconnect();
      document.removeEventListener('pointerdown', outside, true);
      document.removeEventListener('keydown', keydown);
      window.removeEventListener('resize', position);
      popup.remove();
    },
  };
}

export function popupButton(text: string, action: (event: MouseEvent) => void, label?: string) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = text;
  if (label) button.setAttribute('aria-label', label);
  button.addEventListener('click', event => { event.stopPropagation(); action(event); });
  return button;
}

export function popupText(tag: 'h2' | 'p' | 'strong' | 'small', text: string, className?: string) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) element.className = className;
  return element;
}
