import type { ReactNode } from "react";
import type { Json, Props } from "../api";

export type Tone = "neutral" | "ok" | "info" | "warn" | "bad" | "kind";

const tones: Record<Tone, string> = {
  neutral: "bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300",
  ok: "bg-emerald-50 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300",
  info: "bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300",
  warn: "bg-amber-50 text-amber-800 dark:bg-amber-950 dark:text-amber-300",
  bad: "bg-red-50 text-red-700 dark:bg-red-950 dark:text-red-300",
  kind: "border border-zinc-200 bg-white font-medium text-zinc-700 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-200",
};

export function Pill({ tone = "neutral", title, children }: { tone?: Tone; title?: string; children: ReactNode }) {
  return (
    <span title={title} className={`inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-px text-[11.5px] font-semibold ${tones[tone]}`}>
      {children}
    </span>
  );
}

export function Card({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <section className={`min-w-0 rounded-lg border border-zinc-200 bg-white p-4 dark:border-zinc-800 dark:bg-zinc-900 ${className}`}>{children}</section>;
}

export function H1({ children }: { children: ReactNode }) {
  return <h1 className="mb-1 text-xl font-semibold tracking-tight">{children}</h1>;
}

export function H2({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <h2 className={`mb-2 mt-6 text-[15px] font-semibold first:mt-0 ${className}`}>{children}</h2>;
}

export function H3({ children }: { children: ReactNode }) {
  return <h3 className="mb-1.5 mt-4 text-xs font-semibold uppercase tracking-wide text-zinc-500 dark:text-zinc-400">{children}</h3>;
}

export function Muted({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <p className={`text-zinc-500 dark:text-zinc-400 ${className}`}>{children}</p>;
}

export function Note({ children }: { children: ReactNode }) {
  return <div className="my-2 rounded border-l-[3px] border-zinc-300 bg-zinc-100 px-3 py-1.5 text-[12.5px] text-zinc-600 dark:border-zinc-600 dark:bg-zinc-800/60 dark:text-zinc-300">{children}</div>;
}

export function Loading({ what = "Loading" }: { what?: string }) {
  return <Card className="text-zinc-500">{what}…</Card>;
}

export function ErrorBox({ message }: { message: string }) {
  return <Card className="border-red-200 text-red-700 dark:border-red-900 dark:text-red-300">{message}</Card>;
}

export function Mono({ children, className = "" }: { children: ReactNode; className?: string }) {
  return <span className={`font-mono text-[12.5px] ${className}`}>{children}</span>;
}

export function Key({ value }: { value: string }) {
  return <div className="break-all font-mono text-xs text-zinc-500 dark:text-zinc-400">{value}</div>;
}

export function Table({ columns, rows, onRowClick, selected }: { columns: ReactNode[]; rows: ReactNode[][]; onRowClick?: (i: number) => void; selected?: (i: number) => boolean }) {
  return (
    <div className="overflow-x-auto rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-900">
      <table className="w-full border-collapse text-[13px]">
        <thead>
          <tr>
            {columns.map((c, i) => (
              <th key={i} className="whitespace-nowrap border-b border-zinc-200 px-3 py-2 text-left text-xs font-semibold text-zinc-500 dark:border-zinc-800 dark:text-zinc-400">
                {c}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length} className="px-3 py-3 text-zinc-500">
                Nothing here.
              </td>
            </tr>
          ) : (
            rows.map((cells, i) => (
              <tr
                key={i}
                onClick={onRowClick ? () => onRowClick(i) : undefined}
                className={`${onRowClick ? "cursor-pointer hover:bg-zinc-50 dark:hover:bg-zinc-800/50" : ""} ${selected?.(i) ? "bg-blue-50 dark:bg-blue-950/40" : ""}`}
              >
                {cells.map((c, j) => (
                  <td key={j} className="border-b border-zinc-100 px-3 py-2 align-top last:border-b-0 dark:border-zinc-800/70 [tr:last-child_&]:border-b-0">
                    {c}
                  </td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

export function Kv({ pairs }: { pairs: [string, ReactNode][] }) {
  return (
    <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-[13px]">
      {pairs
        .filter(([, v]) => v !== undefined && v !== null && v !== "")
        .map(([k, v]) => [
          <dt key={`${k}-k`} className="text-zinc-500 dark:text-zinc-400">
            {k}
          </dt>,
          <dd key={`${k}-v`} className="m-0 break-words">
            {v}
          </dd>,
        ])}
    </dl>
  );
}

export function jsonText(v: Json | undefined): string {
  if (v === undefined || v === null) return "";
  if (Array.isArray(v)) return v.map(jsonText).join(", ");
  if (typeof v === "object") return JSON.stringify(v);
  return String(v);
}

export function PropsList({ props }: { props?: Props }) {
  if (!props) return null;
  return <Kv pairs={Object.entries(props).map(([k, v]) => [k, typeof v === "string" && v.includes("|") ? <Mono className="text-xs">{v}</Mono> : jsonText(v)])} />;
}

export function Button({ children, onClick, variant = "primary", disabled, type = "button", title }: { children: ReactNode; onClick?: () => void; variant?: "primary" | "secondary" | "danger"; disabled?: boolean; type?: "button" | "submit"; title?: string }) {
  const styles = {
    primary: "bg-blue-600 text-white hover:bg-blue-700 dark:bg-blue-500 dark:text-zinc-950 dark:hover:bg-blue-400",
    secondary: "border border-zinc-300 bg-white text-zinc-800 hover:bg-zinc-50 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-100 dark:hover:bg-zinc-800",
    danger: "border border-red-200 bg-white text-red-700 hover:bg-red-50 dark:border-red-900 dark:bg-zinc-900 dark:text-red-300 dark:hover:bg-red-950",
  };
  return (
    <button type={type} disabled={disabled} onClick={onClick} title={title} className={`rounded-md px-3 py-1.5 text-[13px] font-semibold disabled:cursor-not-allowed disabled:opacity-50 ${styles[variant]}`}>
      {children}
    </button>
  );
}

export const inputClass =
  "w-full rounded-md border border-zinc-300 bg-white px-2.5 py-1.5 text-[13px] outline-none focus:border-blue-500 focus:ring-2 focus:ring-blue-500/20 dark:border-zinc-700 dark:bg-zinc-900";

export function RatioBar({ label, ratio }: { label: string; ratio: { hit: number; total: number } }) {
  const pct = ratio.total === 0 ? 100 : (100 * ratio.hit) / ratio.total;
  return (
    <div className="grid grid-cols-[110px_1fr] items-center gap-x-3 gap-y-1 py-1 text-[13px] sm:grid-cols-[170px_1fr_130px]">
      <span>{label}</span>
      <div className="h-2 overflow-hidden rounded-full bg-zinc-100 dark:bg-zinc-800">
        <div className={`h-full ${pct < 100 ? "bg-amber-500" : "bg-emerald-600"}`} style={{ width: `${pct.toFixed(1)}%` }} />
      </div>
      <Mono className="col-start-2 text-xs sm:col-start-auto">
        {ratio.hit}/{ratio.total} · {pct.toFixed(1)}%
      </Mono>
    </div>
  );
}
