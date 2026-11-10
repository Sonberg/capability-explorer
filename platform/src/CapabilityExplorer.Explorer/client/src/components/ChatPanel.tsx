import { useEffect, useLayoutEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import { ApiError, request } from "../api";
import { chatScope, clearChatHistory, loadChatHistory, saveChatHistory, type ChatSource as Source, type ChatTurn as Turn } from "../chatHistory";
import { useFocus } from "../focus";
import { symbolHref, useApi, useRoute, useSelection } from "../state";
import { Button, ErrorBox, inputClass, Mono, Muted, Note, Pill } from "./ui";

type Chip = "capability" | "view" | "item";
interface Draft {
  input: string;
  setInput: Dispatch<SetStateAction<string>>;
  off: Set<Chip>;
  setOff: Dispatch<SetStateAction<Set<Chip>>>;
  onClose: () => void;
}

/** Concepts, integrations and paths are summaries, not a single symbol to open. */
const isSymbol = (s: Source) => !["Concept", "Integration", "Path"].includes(s.kind);

/** [n], [n-m] and [n, m] become markdown links to the symbols they cite; non-symbol sources stay plain text. */
function linkCitations(text: string, sources: Source[]): string {
  return text.replace(/\[(\d+(?:\s*[-–,]\s*\d+)*)\]/g, (_, list: string) =>
    "[" + list.split(",").flatMap((x) => {
      const [from, to = from] = x.split(/[-–]/).map(Number);
      return Array.from({ length: Math.min(Math.max(to - from + 1, 1), 20) }, (_, k) => from + k);
    }).map((n) => {
      const source = sources.find((s) => s.n === n);
      return source && isSymbol(source) ? `[${n}](${symbolHref(source.key)} "${source.kind} ${source.name.replace(/"/g, "")}")` : String(n);
    }).join(", ") + "]");
}

const markdown: Components = {
  p: (props) => <p className="my-1.5 first:mt-0 last:mb-0" {...props} />,
  ol: (props) => <ol className="my-1.5 list-decimal space-y-1 pl-5" {...props} />,
  ul: (props) => <ul className="my-1.5 list-disc space-y-1 pl-5" {...props} />,
  h1: (props) => <h3 className="mb-1 mt-3 font-semibold" {...props} />,
  h2: (props) => <h3 className="mb-1 mt-3 font-semibold" {...props} />,
  h3: (props) => <h3 className="mb-1 mt-3 font-semibold" {...props} />,
  code: ({ className, ...props }) => <code className={`rounded bg-zinc-100 px-1 font-mono text-[12.5px] dark:bg-zinc-800 ${className ?? ""}`} {...props} />,
  pre: (props) => <pre className="my-2 overflow-x-auto rounded bg-zinc-100 p-2 dark:bg-zinc-800 [&>code]:bg-transparent [&>code]:p-0" {...props} />,
};

/** Cmd/Ctrl+J: ask about the selected capability. Answers cite numbered items retrieved on the server. */
export function ChatPanel({ onClose }: { onClose: () => void }) {
  const { scope } = useSelection();
  const route = useRoute();
  const [input, setInput] = useState("");
  const [off, setOff] = useState<Set<Chip>>(new Set());
  return <ScopedConversation key={JSON.stringify([scope, route.path])} {...{ input, setInput, off, setOff, onClose }} />;
}

/** Resolve this exact query afresh: SelectionProvider can briefly expose the previous view while loading. */
function ScopedConversation(props: Draft) {
  const { scope, meta, capability } = useSelection();
  const route = useRoute();
  const resolved = useApi<Record<string, string>>(`/api/view?${scope}`);
  if (!resolved.data) {
    return (
      <aside aria-label="Ask about capabilities" className="fixed inset-y-0 right-0 z-20 flex w-full flex-col border-l border-zinc-200 bg-white p-4 shadow-xl sm:w-[420px] dark:border-zinc-800 dark:bg-zinc-900">
        <button type="button" onClick={props.onClose} aria-label="Close chat">✕</button>
        {resolved.error ? <ErrorBox message={resolved.error} /> : <Muted>Resolving the selected revisions…</Muted>}
      </aside>
    );
  }
  const identity = chatScope(meta.snapshotId, capability, resolved.data, route.path);
  return <Conversation key={identity} identity={identity} {...props} />;
}

function Conversation({ identity, input, setInput, off, setOff, onClose }: Draft & { identity: string }) {
  const { meta, scope, capability } = useSelection();
  const route = useRoute();
  const focus = useFocus();
  const [turns, setTurns] = useState<Turn[]>([]);
  const [remember, setRemember] = useState(false);
  const [notice, setNotice] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const remembering = useRef(false);
  const initialized = useRef(false);
  const pending = useRef<AbortController | null>(null);
  const end = useRef<HTMLDivElement>(null);
  const capabilityName = meta.capabilities.find((c) => c.id === capability)?.name;
  const chips: [Chip, string, string | undefined][] = [
    ["capability", "Capability", capabilityName ?? "all repositories"],
    ["view", "View", route.path],
    ["item", focus?.kind ?? "Selection", focus?.label],
  ];

  useLayoutEffect(() => {
    if (!initialized.current) {
      initialized.current = true;
      const saved = loadChatHistory(identity);
      setTurns(saved.turns);
      remembering.current = saved.remember;
      setRemember(saved.remember);
      setNotice(saved.notice);
    }
    return () => {
      pending.current?.abort();
      pending.current = null;
    };
  }, [identity]);

  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" }); // block body: an effect must not return scrollIntoView's result
  }, [turns, busy]);

  const retain = (next: Turn[]) => {
    if (!remembering.current) return;
    const saved = saveChatHistory(identity, next);
    setNotice(saved.notice ?? "Chat saved in sessionStorage for this snapshot, capability and revision/view.");
    if (!saved.saved) {
      remembering.current = false;
      setRemember(false);
    }
  };

  const changeRemember = (enabled: boolean) => {
    remembering.current = enabled;
    setRemember(enabled);
    if (enabled) retain(turns);
    else setNotice(clearChatHistory() ?? "Remembering is off. The retained chat record was removed.");
  };

  const clear = () => {
    pending.current?.abort();
    pending.current = null;
    remembering.current = false;
    setRemember(false);
    setTurns([]);
    setBusy(false);
    setError(undefined);
    setNotice(clearChatHistory() ?? "Chat cleared. Remembering is off; the retained chat record was removed.");
  };

  const toggle = (chip: Chip) => setOff((s) => {
    const next = new Set(s);
    if (!next.delete(chip)) next.add(chip);
    return next;
  });

  async function send() {
    const question = input.trim();
    if (!question || pending.current) return;
    const controller = new AbortController();
    pending.current = controller;
    const history: Turn[] = [...turns, { role: "user", content: question }];
    const params = new URLSearchParams(scope);
    setTurns(history);
    setInput("");
    setBusy(true);
    setError(undefined);
    retain(history);
    let completed: Turn[] | undefined;
    try {
      const answer = await request<{ answer: string; sources: Source[]; retrieval: string[]; steps: string[] }>("/api/chat", {
        method: "POST",
        signal: controller.signal,
        body: JSON.stringify({
          messages: history.slice(-10).map(({ role, content }) => ({ role, content })),
          view: params.get("view") ?? undefined,
          cap: params.get("cap") ?? undefined,
          focus: {
            capability: off.has("capability") ? undefined : capabilityName,
            view: off.has("view") ? undefined : route.path,
            kind: off.has("item") ? undefined : focus?.kind,
            label: off.has("item") ? undefined : focus?.label,
            key: off.has("item") ? undefined : focus?.key,
          },
        }),
      });
      if (pending.current !== controller || controller.signal.aborted) return;
      completed = [...history, { role: "assistant", content: answer.answer, sources: answer.sources, retrieval: answer.retrieval, steps: answer.steps }];
    } catch (e) {
      if (pending.current !== controller || controller.signal.aborted) return;
      if (!(e instanceof ApiError || e instanceof TypeError || e instanceof SyntaxError)) throw e;
      setError(e.message);
    } finally {
      if (pending.current === controller) {
        pending.current = null;
        setBusy(false);
      }
    }
    if (completed && !controller.signal.aborted) {
      setTurns(completed);
      retain(completed);
    }
  }

  return (
    <aside aria-label="Ask about capabilities" className="fixed inset-y-0 right-0 z-20 flex w-full flex-col border-l border-zinc-200 bg-white shadow-xl sm:w-[420px] dark:border-zinc-800 dark:bg-zinc-900">
      <header className="flex items-center justify-between border-b border-zinc-200 px-4 py-2.5 dark:border-zinc-800">
        <h2 className="font-semibold">Ask</h2>
        <button type="button" onClick={onClose} aria-label="Close chat" className="rounded-md px-2 py-1 text-zinc-500 hover:bg-zinc-100 dark:hover:bg-zinc-800">✕</button>
      </header>
      <div className="space-y-1 border-b border-zinc-200 px-4 py-2 text-xs dark:border-zinc-800">
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={remember} onChange={(e) => changeRemember(e.target.checked)} />
          Remember chat in this tab
        </label>
        <p>Default: memory only. Opt-in retains one conversation: the most recent 20 whole turns, at most 512 KiB including sources. No text is truncated.</p>
        <p>sessionStorage only, not encrypted. Browser session restore or duplicated tabs may keep a copy; closing a tab does not guarantee deletion.</p>
        <button type="button" onClick={clear} className="underline">Clear chat</button>
        {notice && <p role="status">{notice}</p>}
      </div>
      <div className="min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-3 text-[13px]">
        {!meta.interpretation.chat && (
          <Note>No chat model is configured. Set one in <a href="#/settings">Settings</a>.</Note>
        )}
        {turns.length === 0 && meta.interpretation.chat && (
          <Muted>Ask what the analysed code does, e.g. “where is VAT calculated?” or, with something selected, “what calls this?”. Answers use only what was extracted and cite it.</Muted>
        )}
        {turns.map((t, i) =>
          t.role === "user" ? (
            <p key={i} className="ml-8 rounded-lg bg-blue-50 px-3 py-2 dark:bg-blue-950/50">{t.content}</p>
          ) : (
            <div key={i} className="space-y-1.5">
              <ReactMarkdown components={markdown}>{linkCitations(t.content, t.sources ?? [])}</ReactMarkdown>
              {t.retrieval?.filter((r) => r.startsWith("semantic search")).map((r) => <Muted key={r} className="text-xs">{r}</Muted>)}
              {t.steps && t.steps.length > 0 && (
                <details className="text-xs">
                  <summary className="cursor-pointer text-zinc-500">{t.steps.length} lookups the model made</summary>
                  <ul className="mt-1 space-y-0.5">
                    {t.steps.map((step, k) => (
                      <li key={k}><Mono>{step}</Mono></li>
                    ))}
                  </ul>
                </details>
              )}
              {t.sources && t.sources.length > 0 && (
                <details className="text-xs">
                  <summary className="cursor-pointer text-zinc-500">{t.sources.length} sources · AI-written, unreviewed</summary>
                  <ul className="mt-1 space-y-0.5">
                    {t.sources.map((s) => (
                      <li key={s.n}>
                        [{s.n}] {!isSymbol(s) ? s.name : <a href={symbolHref(s.key)}>{s.name}</a>} <span className="text-zinc-500 dark:text-zinc-400" title={s.text}>{s.kind} · {s.repository} · {s.found}</span>
                      </li>
                    ))}
                  </ul>
                </details>
              )}
            </div>
          ),
        )}
        {busy && <Muted>Thinking…</Muted>}
        {error && <ErrorBox message={error} />}
        <div ref={end} />
      </div>
      <form
        className="space-y-2 border-t border-zinc-200 p-3 dark:border-zinc-800"
        onSubmit={(e) => {
          e.preventDefault();
          void send();
        }}
      >
        <div className="flex flex-wrap gap-1.5" aria-label="In focus">
          {chips.filter(([, , value]) => value).map(([chip, label, value]) => (
            <button key={chip} type="button" onClick={() => toggle(chip)} aria-pressed={!off.has(chip)} title={off.has(chip) ? "Excluded from the question: click to include" : "Sent with the question: click to exclude"}>
              <Pill tone={off.has(chip) ? "neutral" : "info"}>
                <span className={off.has(chip) ? "line-through opacity-60" : ""}>{label}: {value}</span>
              </Pill>
            </button>
          ))}
        </div>
        <textarea
          aria-label="Question"
          rows={2}
          maxLength={1000}
          value={input}
          autoFocus
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void send();
            }
          }}
          placeholder="Ask about the code…"
          className={inputClass}
        />
        <Button type="submit" disabled={busy || !input.trim() || !meta.interpretation.chat}>Ask</Button>
      </form>
    </aside>
  );
}
