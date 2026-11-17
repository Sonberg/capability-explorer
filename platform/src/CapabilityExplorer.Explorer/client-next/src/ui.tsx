import { useState, type ReactNode } from "react";
import type { Coverage as CoverageData, Diagnostic } from "./contracts";
import { PAGE_SIZE } from "./model";

export function Paged<T>({ items, render, label, ordered = false }: {
  items: readonly T[]; render: (item: T, index: number) => ReactNode; label: string; ordered?: boolean;
}) {
  const [page, setPage] = useState(0);
  const pages = Math.max(1, Math.ceil(items.length / PAGE_SIZE));
  const current = Math.min(page, pages - 1), start = current * PAGE_SIZE;
  const List = ordered ? "ol" : "ul";
  return <section aria-label={label} className="paged">
    {!items.length ? <p className="empty">No {label.toLowerCase()} in this view.</p>
      : <List start={ordered ? start + 1 : undefined} className={ordered ? "ordered-list" : "plain-list"}>
        {items.slice(start, start + PAGE_SIZE).map((item, index) =>
          <li key={start + index}>{render(item, start + index)}</li>)}
      </List>}
    {items.length > PAGE_SIZE && <nav aria-label={`${label} pages`} className="pager">
      <button disabled={current === 0} onClick={() => setPage(current - 1)}>Previous</button>
      <span>{start + 1}–{Math.min(start + PAGE_SIZE, items.length)} of {items.length}</span>
      <button disabled={current === pages - 1} onClick={() => setPage(current + 1)}>Next</button>
    </nav>}
  </section>;
}

export function Coverage({ value }: { value: CoverageData }) {
  return <div className={value.status === "full" ? "note" : "notice"}>
    <strong>Coverage: {value.status}</strong>
    <p>Coverage describes extraction, not approval or executable completeness.</p>
    {value.supported.length > 0 && <p>Supported: {value.supported.slice(0, 40).join(", ")}
      {value.supported.length > 40 && " (first 40 shown)"}</p>}
    <Paged label="Coverage limitations" items={value.limitations} render={text => <p>{text}</p>} />
  </div>;
}

export function Diagnostics({ items }: { items: Diagnostic[] }) {
  return <details><summary>Diagnostics ({items.length})</summary>
    <Paged items={items} label="Diagnostics" render={d => <div className="diagnostic">
      <strong>{d.disposition}: {d.code}</strong><p>{d.message}</p>
      {d.source && <small>{[d.source.path, d.source.element, d.source.ref].filter(Boolean).join(" · ")}</small>}
    </div>} />
  </details>;
}

export function Field({ name, children }: { name: string; children: ReactNode }) {
  return <div className="field"><dt>{name}</dt><dd>
    {children == null || children === "" ? "Not supplied" : children}
  </dd></div>;
}
