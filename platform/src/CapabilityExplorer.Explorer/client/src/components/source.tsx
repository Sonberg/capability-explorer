import { useState } from "react";
import { shortSha, type Evidence } from "../api";
import { useApi } from "../state";

interface SourceResponse {
  lines: { n: number; text: string }[];
  highlightStart?: number;
  highlightEnd?: number;
}

/** The cited span with a few lines of context, read from git at the evidence's exact commit. */
export function SourceView({ evidence }: { evidence: Evidence }) {
  const e = evidence;
  const { data, error } = useApi<SourceResponse>(
    `/api/source?repo=${encodeURIComponent(e.repositoryId)}&commit=${e.commitSha}&path=${encodeURIComponent(e.path)}&start=${e.startLine}&end=${e.endLine}`,
  );
  return (
    <div className="my-1.5 overflow-hidden rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950">
      <div className="flex items-center gap-2 border-b border-zinc-200 bg-zinc-50 px-3 py-1 font-mono text-xs text-zinc-500 dark:border-zinc-800 dark:bg-zinc-900">
        <span className="truncate">
          {e.path}:{e.startLine}
          {e.endLine !== e.startLine ? `-${e.endLine}` : ""}
        </span>
        <span className="ml-auto shrink-0">{shortSha(e.commitSha)}</span>
      </div>
      <pre className="m-0 overflow-x-auto py-1.5 font-mono text-[12.5px] leading-relaxed">
        {error && <span className="block px-3 text-zinc-500">{error}</span>}
        {!data && !error && <span className="block px-3 text-zinc-500">loading…</span>}
        {data?.lines.map((l) => {
          const hl = data.highlightStart !== undefined && l.n >= data.highlightStart && l.n <= (data.highlightEnd ?? l.n);
          return (
            <span key={l.n} className={`block whitespace-pre pr-3 ${hl ? "bg-yellow-100 dark:bg-yellow-900/40" : ""}`}>
              <span className="inline-block w-12 select-none pr-3 text-right text-zinc-400">{l.n}</span>
              {l.text || " "}
            </span>
          );
        })}
      </pre>
    </div>
  );
}

/** Evidence references that expand into source views. */
export function EvidenceLinks({ evidence }: { evidence?: Evidence[] }) {
  const [open, setOpen] = useState<string[]>([]);
  if (!evidence?.length) return <span className="text-zinc-400">—</span>;
  return (
    <div>
      <div className="flex flex-wrap gap-x-2">
        {evidence.map((e) => (
          <button
            key={e.id}
            type="button"
            title="Show source at this revision"
            onClick={() => setOpen((o) => (o.includes(e.id) ? o.filter((x) => x !== e.id) : [...o, e.id]))}
            className="font-mono text-xs text-blue-700 hover:underline dark:text-blue-300"
          >
            {e.path.split("/").at(-1)}:{e.startLine}
          </button>
        ))}
      </div>
      {evidence.filter((e) => open.includes(e.id)).map((e) => (
        <SourceView key={e.id} evidence={e} />
      ))}
    </div>
  );
}
