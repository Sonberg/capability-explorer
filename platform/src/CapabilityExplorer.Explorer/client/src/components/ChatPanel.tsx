import { useEffect, useRef, useState, type ReactNode } from "react";
import { request } from "../api";
import { useFocus } from "../focus";
import { symbolHref, useRoute, useSelection } from "../state";
import { Button, ErrorBox, inputClass, Mono, Muted, Note, Pill } from "./ui";

interface Source {
  n: number;
  kind: string;
  key: string;
  name: string;
  repository: string;
  found: string;
  text: string;
}

interface Turn {
  role: "user" | "assistant";
  content: string;
  sources?: Source[];
  retrieval?: string[];
  steps?: string[];
}

type Chip = "capability" | "view" | "item";

/** Concepts, integrations and paths are summaries, not a single symbol to open. */
const isSymbol = (s: Source) => !["Concept", "Integration", "Path"].includes(s.kind);

/** Renders [n] citations as links to the symbol they cite; everything else stays plain text. */
function Cited({ text, sources }: { text: string; sources: Source[] }) {
  const parts: ReactNode[] = [];
  text.split(/(\[\d+\])/).forEach((part, i) => {
    const source = /^\[(\d+)\]$/.exec(part) && sources.find((s) => s.n === Number(part.slice(1, -1)));
    parts.push(source && isSymbol(source) ? <a key={i} href={symbolHref(source.key)} title={`${source.kind} ${source.name}`}>{part}</a> : part);
  });
  return <p className="whitespace-pre-wrap">{parts}</p>;
}

/** Cmd/Ctrl+J: ask about the selected capability. Answers cite numbered items retrieved on the server. */
export function ChatPanel({ onClose }: { onClose: () => void }) {
  const { meta, scope, capability } = useSelection();
  const route = useRoute();
  const focus = useFocus();
  const [turns, setTurns] = useState<Turn[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [off, setOff] = useState<Set<Chip>>(new Set());
  const end = useRef<HTMLDivElement>(null);
  const capabilityName = meta.capabilities.find((c) => c.id === capability)?.name;
  const chips: [Chip, string, string | undefined][] = [
    ["capability", "Capability", capabilityName ?? "all repositories"],
    ["view", "View", route.path],
    ["item", focus?.kind ?? "Selection", focus?.label],
  ];

  useEffect(() => {
    end.current?.scrollIntoView({ block: "end" }); // block body: an effect must not return scrollIntoView's result
  }, [turns, busy]);

  const toggle = (chip: Chip) => setOff((s) => {
    const next = new Set(s);
    if (!next.delete(chip)) next.add(chip);
    return next;
  });

  async function send() {
    const question = input.trim();
    if (!question || busy) return;
    const history: Turn[] = [...turns, { role: "user", content: question }];
    const params = new URLSearchParams(scope);
    setTurns(history);
    setInput("");
    setBusy(true);
    setError(undefined);
    try {
      const answer = await request<{ answer: string; sources: Source[]; retrieval: string[]; steps: string[] }>("/api/chat", {
        method: "POST",
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
      setTurns([...history, { role: "assistant", content: answer.answer, sources: answer.sources, retrieval: answer.retrieval, steps: answer.steps }]);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside aria-label="Ask about capabilities" className="fixed inset-y-0 right-0 z-20 flex w-full flex-col border-l border-zinc-200 bg-white shadow-xl sm:w-[420px] dark:border-zinc-800 dark:bg-zinc-900">
      <header className="flex items-center justify-between border-b border-zinc-200 px-4 py-2.5 dark:border-zinc-800">
        <h2 className="font-semibold">Ask</h2>
        <button type="button" onClick={onClose} aria-label="Close chat" className="rounded-md px-2 py-1 text-zinc-500 hover:bg-zinc-100 dark:hover:bg-zinc-800">✕</button>
      </header>
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
              <Cited text={t.content} sources={t.sources ?? []} />
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
