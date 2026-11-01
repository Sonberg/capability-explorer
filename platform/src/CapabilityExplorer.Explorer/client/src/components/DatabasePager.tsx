import { ErrorBox } from "./ui";
const button = "rounded border border-zinc-300 px-3 py-1.5 text-sm disabled:cursor-default disabled:opacity-40 dark:border-zinc-700 hover:bg-zinc-100 dark:hover:bg-zinc-800";

export function DatabasePager({ page }: { page: { loading: boolean; error?: string; data?: { items: unknown[]; nextCursor: string | null }; next: () => void; previous: () => void; hasPrevious: boolean; number: number } }) {
  return <><div className="flex flex-wrap items-center gap-3" aria-live="polite">
    <button type="button" className={button} onClick={page.previous} disabled={!page.hasPrevious || page.loading}>Previous</button>
    <span className="text-sm text-zinc-500">Page {page.number} · {page.data?.items.length ?? 0} rows{page.loading ? " · updating…" : ""}</span>
    <button type="button" className={button} onClick={page.next} disabled={!page.data?.nextCursor || page.loading}>Next</button>
  </div>{page.error && <ErrorBox message={page.error} />}</>;
}

