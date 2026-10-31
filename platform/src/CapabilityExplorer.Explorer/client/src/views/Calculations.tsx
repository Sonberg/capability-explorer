import { useEffect, useMemo, useRef, useState } from "react";
import { request, type AttributeValue as AttrValue, type Calculation, type Comparison, type Proposal } from "../api";
import { SourceView } from "../components/source";
import { Button, Card, ErrorBox, H1, H2, inputClass, Loading, Mono, Muted, Note, Pill, Table } from "../components/ui";
import { href, symbolHref, useApi, useSelection } from "../state";
import { ATTRIBUTES, AttributeValue, CalculationCard } from "./Explore";

// The most common value in a column stays plain; every other value gets its own tint.
const swatches = [
  "border-l-[3px] border-l-blue-500 bg-blue-500/10",
  "border-l-[3px] border-l-orange-500 bg-orange-500/10",
  "border-l-[3px] border-l-emerald-500 bg-emerald-500/10",
  "border-l-[3px] border-l-fuchsia-500 bg-fuchsia-500/10",
  "border-l-[3px] border-l-yellow-500 bg-yellow-500/15",
  "border-l-[3px] border-l-cyan-500 bg-cyan-500/10",
];

let remembered = { filter: "", highlight: true, compare: [] as string[] };

export function Calculations({ params }: { params: URLSearchParams }) {
  const { scope } = useSelection();
  const { data: all, error } = useApi<Calculation[]>(`/api/calculations?${scope}`);
  const [filter, setFilter] = useState(remembered.filter);
  const [highlight, setHighlight] = useState(remembered.highlight);
  const [compare, setCompare] = useState<string[]>(remembered.compare);
  const selectedKey = params.get("key");
  const detailRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    remembered = { filter, highlight, compare };
  }, [filter, highlight, compare]);
  useEffect(() => {
    if (selectedKey) detailRef.current?.scrollIntoView({ behavior: "smooth", block: "start" });
  }, [selectedKey, all]);

  const rows = useMemo(() => {
    const f = filter.toLowerCase();
    return (all ?? []).filter((c) => !f || [c.name, c.repositoryId, c.key, ...Object.values(c.attributes).map((a) => String(a.value ?? ""))].some((s) => s.toLowerCase().includes(f)));
  }, [all, filter]);

  const tints = useMemo(() => {
    const result: Record<string, Record<string, string>> = {};
    for (const a of ATTRIBUTES) {
      const counts = new Map<string, number>();
      for (const c of rows) {
        const attr = c.attributes[a];
        if (attr && attr.status !== "unknown" && attr.value !== undefined && attr.value !== null) counts.set(String(attr.value), (counts.get(String(attr.value)) ?? 0) + 1);
      }
      const ordered = [...counts.entries()].sort((x, y) => y[1] - x[1]).map(([v]) => v);
      result[a] = Object.fromEntries(ordered.slice(1).map((v, i) => [v, swatches[i % swatches.length]]));
    }
    return result;
  }, [rows]);

  if (error) return <ErrorBox message={error} />;
  if (!all) return <Loading />;
  const live = compare.filter((k) => all.some((c) => c.key === k));
  const selected = all.find((c) => c.key === selectedKey);

  return (
    <div>
      <H1>Calculations</H1>
      <Muted>
        Each row is one implementation, kept separate even when they look like the same business concept. Differences are what the code says; “unknown” means there is no evidence, not that a default applies.
        Tick two rows to compare them.
      </Muted>
      <div className="my-3 flex flex-wrap items-center gap-3">
        <input type="search" aria-label="Filter calculations" placeholder="Filter by name, repository or value" value={filter} onChange={(e) => setFilter(e.target.value)} className={`${inputClass} max-w-md`} />
        <label className="flex items-center gap-1.5 text-xs">
          <input type="checkbox" checked={highlight} onChange={(e) => setHighlight(e.target.checked)} /> highlight differences
        </label>
        <span className="text-xs text-zinc-500">{all.length} calculations in scope</span>
      </div>
      <div className="overflow-x-auto rounded-lg border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-900">
        <table className="w-full border-collapse text-[13px]">
          <thead>
            <tr className="text-left text-xs text-zinc-500">
              <th className="border-b border-zinc-200 px-3 py-2 dark:border-zinc-800">Compare</th>
              <th className="border-b border-zinc-200 px-3 py-2 dark:border-zinc-800">Calculation</th>
              <th className="border-b border-zinc-200 px-3 py-2 dark:border-zinc-800">Repository</th>
              {ATTRIBUTES.map((a) => (
                <th key={a} className="whitespace-nowrap border-b border-zinc-200 px-3 py-2 font-semibold dark:border-zinc-800">
                  {a}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((c) => (
              <tr
                key={c.key}
                onClick={() => (window.location.hash = href("calculations", { key: c.key }))}
                className={`cursor-pointer hover:bg-zinc-50 dark:hover:bg-zinc-800/50 ${c.key === selectedKey ? "bg-blue-50 dark:bg-blue-950/40" : ""}`}
              >
                <td className="border-b border-zinc-100 px-3 py-2 dark:border-zinc-800" onClick={(e) => e.stopPropagation()}>
                  <input
                    type="checkbox"
                    aria-label={`Compare ${c.name}`}
                    checked={live.includes(c.key)}
                    onChange={(e) => setCompare((cur) => (e.target.checked ? [...cur.filter((k) => k !== c.key), c.key].slice(-2) : cur.filter((k) => k !== c.key)))}
                  />
                </td>
                <td className="border-b border-zinc-100 px-3 py-2 dark:border-zinc-800">
                  <div className="font-semibold">{c.name}</div>
                  <div className="text-xs text-zinc-500">
                    {c.language} · {c.output.type}
                  </div>
                </td>
                <td className="border-b border-zinc-100 px-3 py-2 text-xs dark:border-zinc-800">
                  {c.repositoryId}
                  <div className="text-zinc-500">{c.unit}</div>
                </td>
                {ATTRIBUTES.map((a) => {
                  const attr = c.attributes[a];
                  const tint = highlight && attr && attr.status !== "unknown" ? tints[a][String(attr.value)] : undefined;
                  return (
                    <td key={a} className={`min-w-[120px] border-b border-zinc-100 px-3 py-2 dark:border-zinc-800 ${tint ?? ""}`}>
                      <AttributeValue attribute={attr} />
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {live.length === 1 && <Note>Pick one more calculation to compare.</Note>}
      {live.length === 2 && <ComparisonPanel a={live[0]} b={live[1]} />}

      {selected && (
        <div ref={detailRef} className="mt-4 scroll-mt-32 space-y-3">
          <div className="flex items-center gap-3">
            <H2 className="mb-0">{selected.name}</H2>
            <a href={symbolHref(selected.key)} className="text-blue-700 hover:underline dark:text-blue-300">
              open symbol →
            </a>
          </div>
          <CalculationCard calculation={selected} />
          <Card>
            <H2>Source</H2>
            {selected.evidence.map((e) => (
              <SourceView key={e.id} evidence={e} />
            ))}
          </Card>
        </div>
      )}
    </div>
  );
}

function AttrText({ value }: { value?: AttrValue }) {
  if (!value) return <span className="text-zinc-400">—</span>;
  if (value.status === "unknown") return <span className="italic text-zinc-500">unknown</span>;
  const numbers = value.detail ? Object.entries(value.detail).filter(([, v]) => typeof v === "number") : [];
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5">
      <span>{value.value ?? value.status}</span>
      {numbers.length > 0 && <span className="text-xs text-zinc-500">({numbers.map(([k, v]) => `${k} ${v}`).join(", ")})</span>}
      {value.status === "partial" && <Pill tone="warn">partial</Pill>}
    </span>
  );
}

const kindLabel: Record<string, string> = { ValueDiffers: "different value", DetailDiffers: "different parameter", KnownOnOneSide: "known on one side only" };

function ComparisonPanel({ a, b }: { a: string; b: string }) {
  const { meta, scope, capability } = useSelection();
  const { data: c, error } = useApi<Comparison>(`/api/compare?a=${encodeURIComponent(a)}&b=${encodeURIComponent(b)}&${scope}`);
  const [proposal, setProposal] = useState<{ busy?: boolean; result?: Proposal; error?: string }>({});
  useEffect(() => setProposal({}), [a, b]);
  if (error) return <ErrorBox message={error} />;
  if (!c) return <Loading what="Comparing" />;
  const ai = meta.interpretation;

  const explain = async () => {
    setProposal({ busy: true });
    try {
      const view = new URLSearchParams(scope).get("view") ?? "";
      const result = await request<Proposal>("/api/explanations", { method: "POST", body: JSON.stringify({ a, b, view, cap: capability || null }) });
      setProposal({ result });
    } catch (e) {
      setProposal({ error: (e as Error).message });
    }
  };

  return (
    <Card className="mt-4">
      <div className="flex flex-wrap items-center gap-2">
        <H2 className="mb-0">Comparison</H2>
        <Pill tone="ok" title="Computed from the extracted attributes; no model involved">
          deterministic
        </Pill>
        {c.sameExpression && <Pill tone="info">same normalised expression</Pill>}
      </div>
      <p className="my-2 text-xs text-zinc-500">
        <a href={symbolHref(c.left.key)} className="text-blue-700 hover:underline dark:text-blue-300">
          {c.left.name}
        </a>{" "}
        ({c.left.repositoryId}, {c.left.outputType}) vs{" "}
        <a href={symbolHref(c.right.key)} className="text-blue-700 hover:underline dark:text-blue-300">
          {c.right.name}
        </a>{" "}
        ({c.right.repositoryId}, {c.right.outputType})
      </p>
      {c.differences.length ? (
        <Table
          columns={["", "Attribute", c.left.name, c.right.name, "Kind"]}
          rows={c.differences.map((d) => [
            <Mono className="text-xs text-zinc-500">{d.id}</Mono>,
            d.attribute,
            <AttrText value={d.left} />,
            <AttrText value={d.right} />,
            <span className="text-xs text-zinc-500">{kindLabel[d.kind] ?? d.kind}</span>,
          ])}
        />
      ) : (
        <p>No attribute differs.</p>
      )}
      {c.agreements.length > 0 && <Muted className="mt-2 text-xs">Same on both: {c.agreements.join(", ")}</Muted>}
      {c.unknownOnBoth.length > 0 && <Muted className="mt-1 text-xs">Unknown on both (no evidence either side): {c.unknownOnBoth.join(", ")}</Muted>}

      {c.differences.length > 0 &&
        (ai.enabled ? (
          <div className="mt-3 flex flex-wrap items-center gap-3">
            <Button onClick={explain} disabled={proposal.busy}>
              {proposal.busy ? "Asking the model…" : "Explain differences"}
            </Button>
            <span className="text-xs text-zinc-500">Optional AI proposal via {ai.model}. Only the attribute table above is sent.</span>
          </div>
        ) : (
          <Muted className="mt-3 text-xs">
            LLM integration is off — this comparison is complete without it. Configure <Mono>interpretation:provider</Mono> to get written explanations.
          </Muted>
        ))}

      {proposal.error && <Note>{proposal.error}</Note>}
      {proposal.result?.status === "rejected" && (
        <Note>
          <strong>The model's answer was rejected and is not shown.</strong> {proposal.result.problems.join("; ")}
        </Note>
      )}
      {proposal.result?.status === "proposed" && (
        <div className="mt-3 rounded-lg border border-dashed border-amber-400 bg-amber-50 px-4 py-3 dark:border-amber-700 dark:bg-amber-950/40">
          <div className="flex flex-wrap items-center gap-2">
            <Pill tone="warn">AI proposal</Pill>
            <Pill>{proposal.result.reviewStatus}</Pill>
            <span className="text-xs text-zinc-500">
              {proposal.result.model} · {proposal.result.promptVersion}
              {proposal.result.cached ? " · cached" : ""}
            </span>
          </div>
          <p className="my-2">{proposal.result.summary}</p>
          <ul className="list-disc space-y-1 pl-5">
            {proposal.result.points.map((p, i) => (
              <li key={i}>
                <Mono className="text-xs text-zinc-500">{p.differenceId}</Mono> {p.text}
              </li>
            ))}
          </ul>
          <p className="mt-2 text-xs text-zinc-500">Input {proposal.result.inputHash.slice(0, 12)} — explains only the differences listed above; it does not change any extracted fact.</p>
        </div>
      )}
    </Card>
  );
}
