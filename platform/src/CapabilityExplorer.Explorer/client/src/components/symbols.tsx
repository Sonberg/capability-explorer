import type { Edge, SymbolRef } from "../api";
import { symbolHref } from "../state";
import { Key, Mono, Pill } from "./ui";

export function KindPill({ kind }: { kind?: string }) {
  return kind ? <Pill tone="kind">{kind}</Pill> : null;
}

const resolutionTitles: Record<Edge["resolution"], string> = {
  resolved: "Target proven",
  "static-target": "Static target proven; the runtime target is not (interface/virtual dispatch, DI)",
  candidate: "One of several possible targets",
  unresolved: "Target unknown",
};

export function ResolutionPill({ resolution, reason }: { resolution: Edge["resolution"]; reason?: string }) {
  const tone = ({ resolved: "ok", "static-target": "info", candidate: "warn", unresolved: "bad" } as const)[resolution];
  return (
    <Pill tone={tone} title={resolutionTitles[resolution]}>
      {reason ? `${resolution}: ${reason}` : resolution}
    </Pill>
  );
}

export function StatusPill({ status }: { status: string }) {
  return <Pill tone={status === "full" ? "ok" : status === "partial" ? "warn" : "neutral"}>{status}</Pill>;
}

/** A link when the symbol is declared in the view; otherwise its key and why it is not navigable. */
export function SymbolLink({ symbol }: { symbol?: SymbolRef }) {
  if (!symbol) return <span className="text-zinc-400">—</span>;
  if (symbol.declared) {
    return (
      <span className="inline-flex flex-wrap items-center gap-1.5">
        <a href={symbolHref(symbol.key)} title={symbol.key} className="text-blue-700 hover:underline dark:text-blue-300">
          {symbol.name}
        </a>
        <KindPill kind={symbol.kind} />
        {symbol.repositoryId && <span className="text-xs text-zinc-500">{symbol.repositoryId}</span>}
      </span>
    );
  }
  const unbound = symbol.key.includes("@unbound:");
  const external = symbol.key.includes("|@ext|") || symbol.key.includes("@builtin:");
  return (
    <span title={symbol.key}>
      <span className="inline-flex flex-wrap items-center gap-1.5">
        <Mono>{symbol.name}</Mono>
        <Pill tone={unbound ? "warn" : "neutral"}>{unbound ? "unbound" : external ? "external" : "not in view"}</Pill>
      </span>
      <Key value={symbol.key} />
    </span>
  );
}
