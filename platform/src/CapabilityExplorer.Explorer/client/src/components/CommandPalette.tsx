import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import type { SearchHit, SimilarHit } from "../api";
import { href, symbolHref, useApi, useSelection } from "../state";
import { KindPill } from "./symbols";
import { Pill } from "./ui";

interface Item {
  id: string;
  section: string;
  label: string;
  detail?: string;
  href: string;
  /** Symbols can be pivoted to "similar to". */
  hit?: { key: string; name: string; kind: string; repositoryId: string; hasCalculation: boolean };
  score?: number;
}

/** Cmd/Ctrl+K: jump to a view or a symbol; → on a symbol lists similar functionality (vector search). */
export function CommandPalette({ open, setOpen, views }: { open: boolean; setOpen: (open: boolean) => void; views: readonly (readonly [string, string])[] }) {
  const { meta, scope } = useSelection();
  const dialog = useRef<HTMLDialogElement>(null);
  const list = useId();
  const [q, setQ] = useState("");
  const [meaningQ, setMeaningQ] = useState("");
  const [pivot, setPivot] = useState<Item["hit"] | null>(null);
  const [active, setActive] = useState(0);
  const similarity = meta.similarity.database && !!meta.similarity.embeddings;

  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen(!open);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, setOpen]);

  useEffect(() => {
    const d = dialog.current;
    if (!d) return;
    if (open && !d.open) {
      setQ("");
      setPivot(null);
      setActive(0);
      d.showModal();
    } else if (!open && d.open) d.close();
  }, [open]);

  useEffect(() => {
    const t = setTimeout(() => setMeaningQ(q.trim()), 250);
    return () => clearTimeout(t);
  }, [q]);

  const term = q.trim();
  const symbols = useApi<SearchHit[]>(!pivot && term ? `/api/search?q=${encodeURIComponent(term)}&${scope}` : null);
  const meaning = useApi<SimilarHit[]>(!pivot && similarity && meaningQ.length >= 3 ? `/api/search/semantic?q=${encodeURIComponent(meaningQ)}&${scope}` : null);
  const similar = useApi<SimilarHit[]>(pivot ? `/api/similar?key=${encodeURIComponent(pivot.key)}&${scope}` : null);

  const asItem = (section: string, h: SearchHit | SimilarHit): Item => ({
    id: `${section}:${h.key}`,
    section,
    label: h.name,
    href: symbolHref(h.key),
    hit: h,
    score: "score" in h ? h.score : undefined,
  });

  let items: Item[];
  if (pivot) {
    items = (similar.data ?? []).filter((h) => !term || h.name.toLowerCase().includes(term.toLowerCase())).map((h) => asItem(`Similar to ${pivot.name}`, h));
  } else {
    const goTo = [...views, ["settings", "Settings"] as const]
      .filter(([, label]) => !term || label.toLowerCase().includes(term.toLowerCase()))
      .map(([id, label]): Item => ({ id: `view:${id}`, section: "Go to", label, href: href(id) }));
    const named = (symbols.data ?? []).slice(0, 8).map((h) => asItem("Symbols", h));
    const seen = new Set(named.map((i) => i.hit!.key));
    const meant = term.length >= 3 ? (meaning.data ?? []).filter((h) => !seen.has(h.key)).slice(0, 6).map((h) => asItem("Meaning", h)) : [];
    items = term ? [...named, ...meant, ...goTo] : goTo;
  }
  const index = Math.min(active, Math.max(items.length - 1, 0));
  const loading = pivot ? similar.loading : symbols.loading || (term.length >= 3 && meaning.loading);
  const error = pivot ? similar.error : meaning.error;

  useEffect(() => {
    document.getElementById(`${list}-${index}`)?.scrollIntoView({ block: "nearest" });
  }, [index, list]);

  const go = (item: Item) => {
    window.location.hash = item.href;
    setOpen(false);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    const item = items[index];
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActive((index + 1) % Math.max(items.length, 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((index - 1 + items.length) % Math.max(items.length, 1));
    } else if (e.key === "Enter" && item) {
      e.preventDefault();
      go(item);
    } else if ((e.key === "ArrowRight" || e.key === "Tab") && similarity && item?.hit && e.currentTarget.selectionStart === q.length) {
      e.preventDefault();
      setPivot(item.hit);
      setQ("");
      setActive(0);
    } else if (pivot && (e.key === "ArrowLeft" || e.key === "Backspace") && q === "") {
      e.preventDefault();
      setPivot(null);
      setActive(0);
    }
  };

  return (
    <dialog
      ref={dialog}
      aria-label="Command menu"
      onClose={() => setOpen(false)}
      onClick={(e) => e.target === dialog.current && setOpen(false)}
      className="mx-auto mt-[12vh] w-[min(640px,calc(100vw-32px))] overflow-hidden rounded-xl border border-zinc-200 bg-white p-0 text-zinc-900 shadow-2xl backdrop:bg-zinc-950/40 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-100"
    >
      <div className="flex items-center gap-2 border-b border-zinc-200 px-3 dark:border-zinc-800">
        {pivot && (
          <button type="button" onClick={() => setPivot(null)} className="shrink-0 rounded-md bg-blue-50 px-2 py-0.5 text-xs font-semibold text-blue-700 dark:bg-blue-950 dark:text-blue-300" title="Back to search (←)">
            ≈ {pivot.name} ×
          </button>
        )}
        <input
          autoFocus
          role="combobox"
          aria-expanded="true"
          aria-controls={list}
          aria-activedescendant={items.length ? `${list}-${index}` : undefined}
          aria-label={pivot ? `Filter functionality similar to ${pivot.name}` : "Search symbols and views"}
          placeholder={pivot ? "Filter similar…" : "Search symbols, calculations, views…"}
          value={q}
          onChange={(e) => {
            setQ(e.target.value);
            setActive(0);
          }}
          onKeyDown={onKeyDown}
          className="h-12 min-w-0 flex-1 bg-transparent text-[15px] outline-none"
        />
        {loading && <span className="text-xs text-zinc-400">…</span>}
      </div>
      <ul id={list} role="listbox" className="max-h-[min(420px,60vh)] overflow-y-auto py-1">
        {items.length === 0 && !loading && <li className="px-4 py-6 text-center text-zinc-500">{pivot ? "No similar functionality in the selected revisions." : "Nothing matches."}</li>}
        {items.map((item, i) => (
          <li key={item.id} role="presentation">
            {(i === 0 || items[i - 1].section !== item.section) && <div className="px-4 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wide text-zinc-500">{item.section}</div>}
            <a
              id={`${list}-${i}`}
              role="option"
              aria-selected={i === index}
              href={item.href}
              onClick={(e) => {
                e.preventDefault();
                go(item);
              }}
              onMouseMove={() => i !== index && setActive(i)}
              className={`mx-1 flex items-center gap-2 rounded-md px-3 py-1.5 hover:no-underline ${i === index ? "bg-blue-50 dark:bg-blue-950/60" : ""}`}
            >
              <span className="min-w-0 truncate font-medium text-zinc-900 dark:text-zinc-100">{item.label}</span>
              {item.hit && (
                <span className="flex shrink-0 items-center gap-1.5 text-xs text-zinc-500">
                  <KindPill kind={item.hit.kind} />
                  {item.hit.repositoryId}
                  {item.hit.hasCalculation && <Pill tone="info">calculation</Pill>}
                </span>
              )}
              {item.score !== undefined && <span className="ml-auto shrink-0 font-mono text-xs text-zinc-500" title="Cosine similarity">{item.score.toFixed(2)}</span>}
            </a>
          </li>
        ))}
      </ul>
      {error && <div className="border-t border-zinc-200 px-4 py-2 text-xs text-red-600 dark:border-zinc-800 dark:text-red-300">{error}</div>}
      <div className="flex flex-wrap gap-x-4 gap-y-1 border-t border-zinc-200 px-4 py-2 text-xs text-zinc-500 dark:border-zinc-800">
        <span>↑↓ move</span>
        <span>↵ open</span>
        {similarity ? <span>→ similar functionality{pivot ? " · ← back" : ""}</span> : <span>Similarity search: configure Foundry in Settings and set CE_DATABASE</span>}
        <span className="ml-auto">esc close</span>
      </div>
    </dialog>
  );
}
