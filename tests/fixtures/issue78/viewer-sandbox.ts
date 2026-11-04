export type ModelKind = 'bpmn' | 'dmn';

export interface ModelRequest {
  kind: ModelKind;
  source: string;
  scope: string;
  generation: number;
  sourceId: string;
  testLateMessage?: boolean;
}

interface ChildMessage extends ModelRequest {
  nonce: string;
  status: string;
  message?: string;
  renderedCount?: number;
}

export interface SandboxEvent extends ChildMessage {
  acceptedByParent: boolean;
}

export interface ModelSandbox {
  render(request: ModelRequest): void;
  dispose(): void;
  subscribe(listener: (event: SandboxEvent) => void): () => void;
  getDiagnostics(): { rejectedMessages: number; activeIdentity: string | null };
}

export function createModelSandbox(
  host: HTMLElement,
  frameScript: string,
  stylesheetUrl: string
): ModelSandbox {
  const listeners = new Set<(event: SandboxEvent) => void>();
  const state = { active: null as null | { frame: HTMLIFrameElement; identity: ChildMessage }, rejectedMessages: 0 };

  const clear = () => {
    state.active?.frame.remove();
    state.active = null;
    host.replaceChildren();
  };

  const receive = (event: MessageEvent<ChildMessage>) => {
    const active = state.active;
    const message = event.data;
    const acceptedByParent = Boolean(active &&
      event.source === active.frame.contentWindow &&
      message?.nonce === active.identity.nonce &&
      message?.scope === active.identity.scope &&
      message?.generation === active.identity.generation &&
      message?.sourceId === active.identity.sourceId);
    if (!acceptedByParent) {
      state.rejectedMessages += 1;
      return;
    }
    for (const listener of listeners) {
      listener({ ...message, acceptedByParent });
    }
  };

  window.addEventListener('message', receive);

  const render = (request: ModelRequest) => {
    clear();
    const nonceBytes = crypto.getRandomValues(new Uint8Array(18));
    const nonce = btoa(String.fromCharCode(...nonceBytes));
    const identity: ChildMessage = { ...request, nonce, status: 'pending' };
    const origin = location.origin;
    const policy = [
      "default-src 'none'",
      `script-src 'nonce-${nonce}'`,
      `style-src ${origin}/assets/viewer-frame-entry.css 'nonce-${nonce}'`,
      "style-src-attr 'unsafe-inline'",
      `font-src ${origin}/assets/ data:`,
      'img-src data:',
      "connect-src 'none'",
      "object-src 'none'",
      "base-uri 'none'",
      "form-action 'none'",
      "frame-src 'none'"
    ].join('; ');
    const safeScript = frameScript.replace(/<\/script/gi, '<\\/script');
    const frame = document.createElement('iframe');
    frame.title = `${request.kind.toUpperCase()} read-only model`;
    frame.setAttribute('sandbox', 'allow-scripts');
    frame.referrerPolicy = 'no-referrer';
    frame.srcdoc = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${policy}"><link rel="stylesheet" href="${stylesheetUrl}"><style nonce="${nonce}">body{margin:0;font:14px sans-serif}.stage{height:560px;position:relative}.viewer{height:100%;width:100%}#state{padding:1rem}table{border-collapse:collapse}th,td{border:1px solid #777;padding:.35rem;text-align:left}caption{font-weight:bold;text-align:left}</style></head><body><main id="state" role="status" aria-live="polite">Loading source…</main><script type="module" nonce="${nonce}">${safeScript}</script></body></html>`;
    state.active = { frame, identity };
    host.append(frame);
    frame.addEventListener('load', () => {
      if (state.active?.frame !== frame) return;
      frame.contentWindow?.postMessage({ ...identity, status: 'render-request' }, '*');
    }, { once: true });
  };

  return {
    render,
    dispose() {
      clear();
      window.removeEventListener('message', receive);
      listeners.clear();
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getDiagnostics() {
      return { rejectedMessages: state.rejectedMessages, activeIdentity: state.active?.identity.sourceId ?? null };
    }
  };
}
