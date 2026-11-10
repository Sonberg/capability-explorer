export interface ChatSource {
  n: number;
  kind: string;
  key: string;
  name: string;
  repository: string;
  found: string;
  text: string;
}

export interface ChatTurn {
  role: "user" | "assistant";
  content: string;
  sources?: ChatSource[];
  retrieval?: string[];
  steps?: string[];
}

export interface HistoryStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

type StorageAccess = () => HistoryStorage;
interface LoadedHistory { turns: ChatTurn[]; remember: boolean; notice?: string }
interface SavedHistory { saved: boolean; notice?: string }
interface HistoryRecord { version: 1; scope: string; turns: ChatTurn[] }

export const CHAT_HISTORY_KEY = "ce.chatHistory";
const maxBytes = 512 * 1024;
const maxTurns = 20;
const tabStorage: StorageAccess = () => window.sessionStorage;

/** Effective revisions, not labels or the mutable "latest" selector, identify evidence. */
export function chatScope(snapshotId: string, capability: string, view: Record<string, string>, path: string): string {
  const revisions = Object.entries(view).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0);
  return JSON.stringify([snapshotId, capability, revisions, path]);
}

function storageFailure(error: unknown): boolean {
  return error instanceof DOMException
    && ["SecurityError", "QuotaExceededError", "NS_ERROR_DOM_QUOTA_REACHED", "InvalidStateError"].includes(error.name);
}

export function clearChatHistory(access: StorageAccess = tabStorage): string | undefined {
  try {
    access().removeItem(CHAT_HISTORY_KEY);
    return undefined;
  } catch (error) {
    if (!storageFailure(error)) throw error;
    return "Could not remove the retained chat record: sessionStorage is unavailable. It may still be restored on reload; retry Clear chat when storage is available.";
  }
}

function discarded(reason: string, access: StorageAccess): LoadedHistory {
  const failure = clearChatHistory(access);
  return {
    turns: [], remember: false,
    notice: failure ? `${reason} ${failure}` : `${reason} The retained record was removed.`,
  };
}

function object(value: unknown, keys: string[]): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    && Object.getPrototypeOf(value) === Object.prototype
    && Object.keys(value).every(key => keys.includes(key));
}

function strings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every(item => typeof item === "string");
}

function source(value: unknown): ChatSource | undefined {
  if (!object(value, ["n", "kind", "key", "name", "repository", "found", "text"])
    || !Number.isSafeInteger(value.n) || (value.n as number) < 1
    || typeof value.kind !== "string" || typeof value.key !== "string" || typeof value.name !== "string"
    || typeof value.repository !== "string" || typeof value.found !== "string" || typeof value.text !== "string") return;
  return {
    n: value.n as number, kind: value.kind, key: value.key, name: value.name,
    repository: value.repository, found: value.found, text: value.text,
  };
}

function turn(value: unknown): ChatTurn | undefined {
  if (!object(value, ["role", "content", "sources", "retrieval", "steps"])
    || (value.role !== "user" && value.role !== "assistant") || typeof value.content !== "string"
    || (value.retrieval !== undefined && !strings(value.retrieval))
    || (value.steps !== undefined && !strings(value.steps))) return;
  let sources: ChatSource[] | undefined;
  if (value.sources !== undefined) {
    if (!Array.isArray(value.sources)) return;
    sources = [];
    for (const raw of value.sources) {
      const parsed = source(raw);
      if (!parsed) return;
      sources.push(parsed);
    }
  }
  const parsed: ChatTurn = { role: value.role, content: value.content };
  if (sources !== undefined) parsed.sources = sources;
  if (value.retrieval !== undefined) parsed.retrieval = value.retrieval as string[];
  if (value.steps !== undefined) parsed.steps = value.steps as string[];
  return parsed;
}

function validatedTurns(value: unknown): ChatTurn[] | undefined {
  if (!Array.isArray(value) || value.length > maxTurns) return;
  const turns: ChatTurn[] = [];
  for (const raw of value) {
    const parsed = turn(raw);
    if (!parsed) return;
    turns.push(parsed);
  }
  return turns;
}

export function loadChatHistory(scope: string, access: StorageAccess = tabStorage): LoadedHistory {
  let raw: string | null;
  try {
    raw = access().getItem(CHAT_HISTORY_KEY);
  } catch (error) {
    if (!storageFailure(error)) throw error;
    return { turns: [], remember: false, notice: "Chat history could not be read: sessionStorage is unavailable. This conversation remains in memory." };
  }
  if (raw === null) return { turns: [], remember: false };
  // Check code-unit length first to avoid allocating a UTF-8 copy of an unbounded record.
  if (raw.length > maxBytes || new TextEncoder().encode(raw).byteLength > maxBytes) {
    return discarded("Saved chat exceeds the 512 KiB limit and was not restored.", access);
  }
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    return discarded("Saved chat is malformed and was not restored.", access);
  }
  if (!object(value, ["version", "scope", "turns"]) || value.version !== 1 || typeof value.scope !== "string") {
    return discarded("Saved chat has an invalid or unsupported format and was not restored.", access);
  }
  const turns = validatedTurns(value.turns);
  if (!turns) return discarded("Saved chat contains invalid turns or sources and was not restored.", access);
  if (value.scope !== scope) {
    return discarded("Saved chat belongs to a different snapshot, capability or revision/view scope and was not restored.", access);
  }
  return { turns, remember: true };
}

export function saveChatHistory(scope: string, turns: ChatTurn[], access: StorageAccess = tabStorage): SavedHistory {
  const retained = validatedTurns(turns.slice(-maxTurns));
  const fail = (reason: string): SavedHistory => {
    const removal = clearChatHistory(access);
    return { saved: false, notice: removal ? `${reason} ${removal}` : `${reason} Any previous retained record was removed.` };
  };
  if (!retained) return fail("Chat was not saved because its turns or sources have an invalid format. The conversation remains in memory.");
  const record: HistoryRecord = { version: 1, scope, turns: retained };
  const raw = JSON.stringify(record);
  if (raw.length > maxBytes || new TextEncoder().encode(raw).byteLength > maxBytes) {
    return fail("Chat was not saved: the record for the most recent 20 whole turns exceeds 512 KiB. No turn or source text was truncated; the conversation remains in memory.");
  }
  try {
    access().setItem(CHAT_HISTORY_KEY, raw);
  } catch (error) {
    if (!storageFailure(error)) throw error;
    return fail("Chat was not saved: sessionStorage is unavailable or full. The conversation remains in memory.");
  }
  return {
    saved: true,
    ...(turns.length > maxTurns ? { notice: "Only the most recent 20 whole turns were saved; older turns remain in memory until you close or reload." } : {}),
  };
}
