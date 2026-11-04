export type ModelKind = 'bpmn' | 'dmn';

export interface ModelRequest {
  kind: ModelKind;
  source: string;
  scope: string;
  generation: number;
  sourceId: string;
}

interface ChildMessage {
  kind: ModelKind;
  scope: string;
  generation: number;
  sourceId: string;
  nonce: string;
  status: string;
  message?: string;
  renderedCount?: number;
  renderedIds?: string[];
  elementId?: string;
  relationshipSourceId?: string;
  relationshipTargetId?: string;
  relationshipMatchCount?: number;
}

export interface TrustedViewerAssets {
  script: string;
  stylesheet: string;
}

async function readBoundedAsset(path: string, maximumBytes: number, signal: AbortSignal): Promise<string> {
  const response = await fetch(path, { signal, credentials: "same-origin", headers: { accept: "text/plain" } });
  if (!response.ok || !response.body) throw new Error(`Trusted model viewer asset unavailable (${response.status}).`);
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.byteLength;
    if (length > maximumBytes) {
      await reader.cancel();
      throw new Error("Trusted model viewer asset exceeds its bounded inline size.");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

export async function loadTrustedViewerAssets(signal: AbortSignal): Promise<TrustedViewerAssets> {
  const base = new URL(import.meta.env.BASE_URL, location.origin);
  const [script, stylesheet] = await Promise.all([
    readBoundedAsset(new URL("assets/model-viewer-frame.js", base).href, 1_048_576, signal),
    readBoundedAsset(new URL("assets/model-viewer-frame.css", base).href, 512_000, signal),
  ]);
  return { script, stylesheet };
}

export interface SandboxEvent extends ChildMessage {
  acceptedByParent: boolean;
}

export interface ModelSandbox {
  render(request: ModelRequest): void;
  selectElement(elementId: string): void;
  selectRelationship(sourceId: string, targetId: string): void;
  zoom(action: "fit" | "in" | "out" | "reset"): void;
  dispose(): void;
  subscribe(listener: (event: SandboxEvent) => void): () => void;
  getDiagnostics(): { rejectedMessages: number; activeIdentity: string | null };
}

export function createModelSandbox(
  host: HTMLElement,
  assets: TrustedViewerAssets
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
    const identity: ChildMessage = {
      kind: request.kind, scope: request.scope, generation: request.generation,
      sourceId: request.sourceId, nonce, status: 'pending'
    };
    const policy = [
      "default-src 'none'",
      `script-src 'nonce-${nonce}'`,
      `style-src 'nonce-${nonce}'`,
      "style-src-attr 'unsafe-inline'",
      "font-src data:",
      'img-src data:',
      "connect-src 'none'",
      "object-src 'none'",
      "base-uri 'none'",
      "form-action 'none'",
      "frame-src 'none'"
    ].join('; ');
    const frame = document.createElement('iframe');
    frame.title = `${request.kind.toUpperCase()} read-only model`;
    frame.setAttribute('sandbox', 'allow-scripts allow-popups allow-popups-to-escape-sandbox');
    frame.referrerPolicy = 'no-referrer';
    const script = assets.script.replace(/<\/script/gi, "<\\/script");
    const stylesheet = assets.stylesheet.replace(/<\/style/gi, "\\3c /style");
    frame.srcdoc = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${policy}"><style nonce="${nonce}">${stylesheet}\n#state{height:100%;padding:0}.stage{height:100%;min-height:330px}.viewer{height:100%;min-height:330px}</style></head><body><main id="state" role="status" aria-live="polite">Loading source…</main><script nonce="${nonce}">${script}</script></body></html>`;
    state.active = { frame, identity };
    host.append(frame);
    frame.addEventListener('load', () => {
      if (state.active?.frame !== frame) return;
      frame.contentWindow?.postMessage({ ...identity, status: 'render-request', source: request.source }, '*');
    }, { once: true });
  };

  return {
    render,
    selectElement(elementId) {
      const active = state.active;
      if (active) active.frame.contentWindow?.postMessage({ ...active.identity, status: 'select-element', elementId }, '*');
    },
    selectRelationship(sourceId, targetId) {
      const active = state.active;
      if (active) active.frame.contentWindow?.postMessage({
        ...active.identity, status: 'select-relationship',
        relationshipSourceId: sourceId, relationshipTargetId: targetId
      }, '*');
    },
    zoom(action) {
      const active = state.active;
      if (active) active.frame.contentWindow?.postMessage({ ...active.identity, status: 'zoom', action }, '*');
    },
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
