import type { ReactNode } from "react";
import { buildModelIndex, elementName, lanesFor, relationshipName } from "../models/model-index";
import type {
  AuthoredModelEvidence, CandidateArtifact, CandidateNode, CandidateSource, ModelEvidence,
  ModelScope, ModelSummary,
} from "../models/contracts";

const card = "rounded-lg border border-zinc-200 bg-white p-3 dark:border-zinc-800 dark:bg-zinc-900";
const localTypeName = (type: string) => type.split(/[:/#}]/).at(-1)?.toLowerCase();
const dmnNamespace = "https://www.omg.org/spec/DMN/20191111/MODEL/";
const button = "rounded-md border border-zinc-300 px-3 py-1.5 text-sm hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 disabled:opacity-50 dark:border-zinc-700 dark:hover:bg-zinc-800";
const input = "rounded-md border border-zinc-300 bg-white px-3 py-2 dark:border-zinc-700 dark:bg-zinc-900";

export function ModelRail(props: {
  idPrefix: string; items: ModelSummary[]; allItems: ModelSummary[]; selectedId: string;
  query: string; setQuery: (value: string) => void;
  kind: "all" | "process" | "decision"; setKind: (value: "all" | "process" | "decision") => void;
  selectModel: (id: string) => void; listLoading: boolean; deepLinkLoading: boolean;
  cursor: string | null; pageBusy: boolean; appendPage: () => Promise<void>; candidateKindPending: boolean;
}) {
  const { idPrefix, items, allItems, selectedId, query, setQuery, kind, setKind, selectModel, listLoading, deepLinkLoading, cursor, pageBusy, appendPage, candidateKindPending } = props;
  return <>
    <label className="block text-xs font-semibold" htmlFor={`${idPrefix}-model-search`}>Search models</label>
    <input id={`${idPrefix}-model-search`} className={`${input} mt-1 w-full`} value={query} onChange={(event) => setQuery(event.target.value)} />
    <div className="mt-2 flex flex-wrap gap-1" role="group" aria-label="Filter processes and decisions">
      {(["all", "process", "decision"] as const).map((value) => <button key={value} type="button" aria-pressed={kind === value} onClick={() => setKind(value)} className={`${button} px-2 py-1 text-xs`}>{value === "all" ? "All" : value === "process" ? "Processes" : "Decisions"}</button>)}
    </div>
    <h2 className="mt-4 font-semibold">Authored models</h2>
    <ul className="mt-1 space-y-1">
      {items.filter((item) => item.kind === "authored").map((item) => <ModelButton key={item.id} item={item} selected={item.id === selectedId} onClick={() => selectModel(item.id)} />)}
    </ul>
    <h2 className="mt-4 font-semibold">Code candidates</h2>
    {candidateKindPending && kind !== "all" && <p className="mt-1 text-xs text-zinc-500">Candidate process/decision kind is not included in summaries. Select All to load candidate evidence before filtering its kind.</p>}
    <ul className="mt-1 space-y-1">
      {items.filter((item) => item.kind === "candidate").map((item) => <ModelButton key={item.id} item={item} selected={item.id === selectedId} onClick={() => selectModel(item.id)} />)}
    </ul>
    {(listLoading || deepLinkLoading) && <p role="status" className="mt-3 text-sm text-zinc-500">{deepLinkLoading ? "Finding linked model…" : "Loading bounded model summaries…"}</p>}
    {!listLoading && allItems.length === 0 && <p className="mt-3 text-sm text-zinc-500">No model summaries are available in this scope.</p>}
    {cursor && <button type="button" className={`${button} mt-3 w-full`} disabled={pageBusy || deepLinkLoading} onClick={() => void appendPage()}>{pageBusy ? "Loading…" : "Load next page"}</button>}
  </>;
}

function ModelButton({ item, selected, onClick }: { item: ModelSummary; selected: boolean; onClick: () => void }) {
  return <li><button type="button" aria-current={selected ? "true" : undefined} onClick={onClick} className={`w-full rounded-md border p-2 text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 ${selected ? "border-blue-500 bg-blue-50 dark:bg-blue-950/50" : "border-zinc-200 hover:bg-zinc-50 dark:border-zinc-800 dark:hover:bg-zinc-800"}`}>
    <span className="flex items-center justify-between gap-2 font-medium"><span className="truncate">{item.unitPath.split("/").at(-1)}</span><span className="shrink-0 text-[11px] text-zinc-500">{item.coverage} extraction</span></span>
    <span className="block truncate text-xs text-zinc-500">{item.repositoryId} · {item.modelKind}</span>
    <span className="block truncate font-mono text-[10px] text-zinc-500">{item.unitPath}</span>
  </button></li>;
}

export function DmnTables({ elements, selected, onSelect }: {
  elements: AuthoredModelEvidence["elements"];
  selected: string;
  onSelect: (id: string) => void;
}) {
  const tables = elements.filter((element) => /decisionTable/i.test(element.type)).sort((a, b) => a.order - b.order);
  const expressions = elements.filter((element) => /literalExpression/i.test(element.type)).sort((a, b) => a.order - b.order);
  const byIdentity = new Map(elements.map((element) => [element.identity, element]));
  if (!tables.length) {
    return <div className="space-y-3 p-3">
      <h2 className="font-semibold">Authored literal expressions</h2>
      {expressions.length ? expressions.map((expression) => <section id={`model-logic-${expression.id}`} key={expression.identity} className={card}>
        <h3>{expression.attributes.name || expression.id} · {byIdentity.get(expression.parent ?? "")?.attributes.name || byIdentity.get(expression.parent ?? "")?.id || "owning decision not captured"}</h3>
        <p className="mt-1 whitespace-pre-wrap font-mono text-sm">{expression.content?.text ?? "Expression text was not captured in this evidence profile."}</p>
        <p className="mt-1 text-xs text-zinc-500">Language: {expression.content?.language ?? "not specified"} · text only; not evaluated.</p>
      </section>) : <p className="text-sm text-zinc-500">No decision table or literal expression was captured.</p>}
    </div>;
  }
  return <div className="space-y-4 overflow-auto p-3">
    {tables.map((table) => {
      const children = elements.filter((element) => element.parent === table.identity);
      const inputs = children.filter((element) => /(^|:)input$/i.test(element.type)).sort((a, b) => a.order - b.order);
      const outputs = children.filter((element) => /(^|:)output$/i.test(element.type)).sort((a, b) => a.order - b.order);
      const rules = children.filter((element) => /(^|:)rule$/i.test(element.type)).sort((a, b) => a.order - b.order);
      const columns = [...inputs, ...outputs];
      const declarationFor = (column: AuthoredModelEvidence["elements"][number], kind: "input" | "output") => {
        const declarationName = `${kind}values`;
        const declarations = elements.filter((element) =>
          element.parent === column.identity && element.namespace === dmnNamespace &&
          localTypeName(element.type) === declarationName
        ).sort((a, b) => a.order - b.order);
        if (!declarations.length) return "No standard DMN allowed-value declaration is present in this evidence. The reason is not established by this view.";
        return declarations.map((declaration) => declaration.content?.text ||
          "Declaration present; its text is empty or was not captured. The reason is not established by this view."
        ).join("\n");
      };
      const tableShapeValid = columns.length > 0 && rules.every((rule) => {
        const inputCells = (rule.cells ?? []).filter((cell) => cell.kind === "input").sort((a, b) => a.order - b.order);
        const outputCells = (rule.cells ?? []).filter((cell) => cell.kind === "output").sort((a, b) => a.order - b.order);
        return inputCells.length === inputs.length && outputCells.length === outputs.length &&
          inputCells.every((cell, index) => index === 0 || inputCells[index - 1].order < cell.order) &&
          outputCells.every((cell, index) => index === 0 || outputCells[index - 1].order < cell.order);
      });
      return <section id={`model-logic-${table.id}`} key={table.identity} className={`min-w-0 ${selected === table.id ? "rounded outline outline-2 outline-blue-500" : ""}`}>
        <h2 className="font-semibold">{table.attributes.name || table.id}</h2>
        <p className="mb-2 text-xs text-zinc-500">Hit policy: {table.hitPolicy || "not captured"}{table.aggregation ? ` · aggregation: ${table.aggregation}` : ""}. Authored expressions are shown literally, never evaluated.</p>
        {!tableShapeValid && <p role="alert" className="mb-2 rounded border border-amber-400 p-2 text-sm text-amber-900 dark:text-amber-200">Rule cells do not match the captured ordered input/output columns. Values are not aligned or padded.</p>}
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse text-left text-sm">
            <caption className="sr-only">{table.attributes.name || table.id} authored decision table</caption>
            <thead><tr>{columns.map((column, index) => {
              const kind = index < inputs.length ? "input" : "output";
              const declaration = declarationFor(column, kind);
              return <th key={column.identity} scope="col" className="border border-zinc-300 bg-zinc-100 px-2 py-1 align-top dark:border-zinc-700 dark:bg-zinc-800">
                <span className="block">{column.attributes.label || column.attributes.name || column.id}</span>
                <span className="mt-1 block text-xs font-normal text-zinc-600 dark:text-zinc-300">{kind === "input" ? "Input allowed values" : "Output allowed values"} (literal, not evaluated)</span>
                <span className="block whitespace-pre-wrap break-words font-mono text-xs font-normal">{declaration}</span>
              </th>;
            })}</tr></thead>
            <tbody>{tableShapeValid && rules.map((rule) => <tr key={rule.identity} className={selected === rule.id ? "bg-blue-50 outline outline-2 outline-blue-500 dark:bg-blue-950/60" : ""}>
              {columns.map((column, columnIndex) => {
                const cells = (rule.cells ?? []).filter((cell) => cell.kind === (columnIndex < inputs.length ? "input" : "output")).sort((a, b) => a.order - b.order);
                const cell = cells[columnIndex < inputs.length ? columnIndex : columnIndex - inputs.length];
                return <td key={column.identity} className="border border-zinc-300 px-2 py-1 align-top dark:border-zinc-700">
                  {columnIndex === 0 ? <button type="button" aria-pressed={selected === rule.id} onClick={() => onSelect(rule.id)} className="whitespace-pre-wrap text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600">
                    {cell?.text ?? "Text not captured"}{cell?.language ? <span className="block text-[10px] text-zinc-500">{cell.language}</span> : null}
                  </button> : <span className="whitespace-pre-wrap">{cell?.text ?? "Text not captured"}{cell?.language ? <span className="block text-[10px] text-zinc-500">{cell.language}</span> : null}</span>}
                </td>;
              })}
            </tr>)}</tbody>
          </table>
        </div>
      </section>;
    })}
    {expressions.length > 0 && <section>
      <h2 className="mb-2 font-semibold">Authored literal expressions</h2>
      <div className="space-y-2">{expressions.map((expression) => <article id={`model-logic-${expression.id}`} key={expression.identity} className={card}>
        <h3>{expression.attributes.name || expression.id}</h3>
        <p className="mt-1 whitespace-pre-wrap font-mono text-sm">{expression.content?.text ?? "Expression text was not captured in this evidence profile."}</p>
        <p className="mt-1 text-xs text-zinc-500">Language: {expression.content?.language ?? "not specified"} · text only; not evaluated.</p>
      </article>)}</div>
    </section>}
  </div>;
}

export function CandidateEvidence({ candidate }: { candidate: CandidateArtifact }) {
  const renderNode = (node: CandidateNode, index: number): ReactNode => <li key={`${node.kind}-${node.source.start}-${index}`} className="ml-4 border-l border-amber-300 pl-2">
    <span className="font-mono">{node.kind}</span>{node.operator && ` · ${node.operator}`}{node.value !== undefined && ` · ${node.value}`}{node.symbol && ` · ${node.symbol}`}
    {node.rawPredicate && <p className="whitespace-pre-wrap break-all">Raw predicate candidate text: {node.rawPredicate}</p>}
    <SourceLocator source={node.source} />
    {node.children.length > 0 && <ol>{node.children.map(renderNode)}</ol>}
  </li>;
  return <section className={`${card} border-amber-300`} aria-label="Candidate evidence">
    <h2 className="font-semibold">Bounded candidate recipe: {candidate.recipe}</h2>
    <p className="text-xs text-zinc-600 dark:text-zinc-400">Coverage scope: {candidate.coverage.scope}. No execution, FEEL translation, whole-journey or runtime-equivalence claim.</p>
    {candidate.candidates.map((item) => <article key={item.id} className="mt-3 border-t border-amber-200 pt-2">
      <h3 className="font-medium">{item.kind}: {item.id}</h3>
      <p className="text-xs">Review: {item.reviewStatus} · Executable: no · Inputs written: {item.inputsWritten.length}</p>
      <p className="text-xs">Parameters: {item.parameters.map((parameter) => `${parameter.name}: ${parameter.type}`).join(", ") || "none captured"} · Inputs read: {item.inputsRead.join(", ") || "none captured"}</p>
      <SourceLocator source={item.source} />
      <ol className="mt-2">{renderNode(item.body, 0)}</ol>
    </article>)}
    {candidate.coverage.status === "partial" && <p className="mt-2 text-amber-800 dark:text-amber-200">Partial recipe coverage · {candidate.coverage.unsupportedMethods} unsupported methods.</p>}
  </section>;
}

function SourceLocator({ source }: { source: CandidateSource }) {
  return <p className="break-all font-mono text-[10px] text-zinc-500">{source.unitPath} · {source.commitSha.slice(0, 12)} · lines {source.startLine}:{source.startColumn}–{source.endLine}:{source.endColumn} · UTF-16 span {source.start}+{source.length} · SHA-256 {source.sourceSha256}</p>;
}

type DiagnosticRecord = { code: string; message: string; disposition?: string; source?: object };

function sourceRecord(diagnostic: DiagnosticRecord): Record<string, unknown> | undefined {
  return diagnostic.source && typeof diagnostic.source === "object"
    ? diagnostic.source as Record<string, unknown>
    : undefined;
}

function constructFor(diagnostic: DiagnosticRecord, elements: AuthoredModelEvidence["elements"]): string {
  const source = sourceRecord(diagnostic);
  const explicitNamespace = typeof source?.namespace === "string" ? source.namespace : undefined;
  const explicitType = typeof source?.type === "string" ? source.type :
    typeof source?.construct === "string" ? source.construct :
      typeof source?.elementKind === "string" ? source.elementKind :
        typeof source?.kind === "string" ? source.kind : undefined;
  if (explicitNamespace || explicitType) return [explicitNamespace, explicitType].filter(Boolean).join(" · ");
  const locator = source?.element;
  const artifact = source?.artifact;
  const element = elements.find((candidate) =>
    (typeof artifact !== "string" || candidate.source.artifact === artifact) &&
    (candidate.id === locator || candidate.identity === locator ||
      (candidate.source.element === locator && candidate.source.artifact === artifact))
  );
  return element ? `${element.namespace} · ${element.type}` : "Construct not identified";
}

function compareText(a: string, b: string): number {
  return a === b ? 0 : a < b ? -1 : 1;
}

export function groupModelDiagnostics(
  diagnostics: DiagnosticRecord[],
  elements: AuthoredModelEvidence["elements"] = []
) {
  const groups = new Map<string, { code: string; disposition: string; construct: string; items: DiagnosticRecord[] }>();
  for (const diagnostic of diagnostics) {
    const disposition = diagnostic.disposition ?? "not specified";
    const construct = constructFor(diagnostic, elements);
    const key = JSON.stringify([diagnostic.code, disposition, construct]);
    const group = groups.get(key) ?? { code: diagnostic.code, disposition, construct, items: [] };
    group.items.push(diagnostic);
    groups.set(key, group);
  }
  return [...groups.values()].sort((a, b) =>
    compareText(a.code, b.code) || compareText(a.disposition, b.disposition) || compareText(a.construct, b.construct)
  );
}

export function Diagnostics({ diagnostics, elements = [] }: {
  diagnostics: DiagnosticRecord[];
  elements?: AuthoredModelEvidence["elements"];
}) {
  if (!diagnostics.length) return null;
  const groups = groupModelDiagnostics(diagnostics, elements);
  return <section className={`${card} border-amber-300`} aria-label="Model diagnostics">
    <h2 className="font-semibold">Diagnostics ({diagnostics.length})</h2>
    <ul className="mt-1 space-y-2">{groups.map((group) => <li key={JSON.stringify([group.code, group.disposition, group.construct])} className="rounded border border-amber-200 p-2 text-sm dark:border-amber-900">
      <details>
        <summary className="cursor-pointer rounded focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600">
          <span className="font-mono text-xs">{group.code}</span> · {group.disposition} · {group.construct} · {group.items.length} {group.items.length === 1 ? "occurrence" : "occurrences"}
        </summary>
        <ol className="mt-2 list-decimal space-y-2 pl-5">
          {group.items.map((diagnostic, index) => <li key={`${group.code}:${group.construct}:${index}`} className="min-w-0">
            <p className="whitespace-pre-wrap break-words">{diagnostic.message}</p>
            {sourceRecord(diagnostic)
              ? <dl className="mt-1 grid min-w-0 gap-x-2 gap-y-1 text-xs text-zinc-600 dark:text-zinc-400">
                {Object.entries(sourceRecord(diagnostic)!).map(([key, value]) => <div key={key} className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-1">
                  <dt>{key}:</dt><dd className="min-w-0 break-all font-mono">{String(value)}</dd>
                </div>)}
              </dl>
              : <p className="mt-1 text-xs text-zinc-500">Source locator not supplied.</p>}
          </li>)}
        </ol>
      </details>
    </li>)}</ul>
  </section>;
}

export function Provenance({ summary, evidence, snapshot, selectedElement, sourceVerified, renderStatus }: {
  summary: ModelSummary; evidence?: ModelEvidence; snapshot?: ModelScope; selectedElement: string;
  sourceVerified: boolean; renderStatus: string;
}) {
  const authored = evidence?.schema === "ce.models.v1" ? evidence as AuthoredModelEvidence : undefined;
  const index = authored && buildModelIndex(authored);
  const candidate = evidence?.schema === "ce.candidates.v1" ? evidence as CandidateArtifact : undefined;
  const selectedEvidenceElement = authored?.elements.find((element) => element.id === selectedElement);
  const selectedRelationship = authored?.relationships.find((relationship) => relationship.identity === selectedElement);
  const relationshipSource = authored?.elements.find((element) => element.identity === selectedRelationship?.source);
  const relationshipTarget = authored?.elements.find((element) => element.identity === selectedRelationship?.target);
  const sourceStatus = summary.kind === "candidate" ? "Candidate · not approved / not executable" :
    sourceVerified ? renderStatus.startsWith("Rendered") ? "Authored evidence · original source verified · viewer projection rendered" :
      `Authored evidence · original source verified · viewer projection unavailable (${renderStatus || "not rendered"})` :
      `Authored evidence · original source not verified (${summary.source.status})`;
  return <aside className={`${card} min-w-0 self-start`} aria-label="Model insights">
    <h2 className="font-semibold">Insights</h2>
    <p className="mt-1 text-xs text-zinc-500">Source provenance and selected model details.</p>
    {selectedEvidenceElement && <section className="mt-3 border-t border-zinc-200 pt-2 dark:border-zinc-800" aria-label="Selected element provenance">
      <h3 className="font-semibold">Selected element</h3>
      <p className="font-medium">{selectedEvidenceElement.attributes.name || selectedEvidenceElement.id}</p>
      <p className="break-all font-mono text-xs">{selectedEvidenceElement.id} · {selectedEvidenceElement.type}</p>
      <dl className="mt-2 space-y-2 text-sm">
        <Field label="Parent">{authored?.elements.find(element => element.identity === selectedEvidenceElement.parent)?.attributes.name ||
          authored?.elements.find(element => element.identity === selectedEvidenceElement.parent)?.id || "Top-level"}</Field>
        <Field label="Lane (as authored)">{index && lanesFor(index, selectedEvidenceElement).map(elementName).join(", ") || "No lane membership captured"}</Field>
        {(["incoming", "outgoing"] as const).map(direction => <Field key={direction} label={direction === "incoming" ? "Incoming captured relationships" : "Outgoing captured relationships"}>
          {authored?.relationships.filter(relationship =>
            (direction === "incoming" ? relationship.target : relationship.source) === selectedEvidenceElement.identity)
            .map(relationship => {
              const endpoint = authored.elements.find(element => element.identity === (direction === "incoming" ? relationship.source : relationship.target));
              return `${index ? relationshipName(index, relationship) : relationship.kind}: ${elementName(endpoint)}`;
            }).join(", ") || "None captured; not proof of no dependencies"}
        </Field>)}
        {selectedEvidenceElement.attributes.calledElement && <Field label="Called process">{selectedEvidenceElement.attributes.calledElement}</Field>}
      </dl>
      <p className="mt-2 text-xs text-zinc-500">Lane membership is not accountability. Business owner, approval and capability mapping are not supplied by this evidence.</p>
      <p className="break-all text-xs text-zinc-500">Namespace: {selectedEvidenceElement.namespace} · artifact: {selectedEvidenceElement.source.artifact} · element: {selectedEvidenceElement.source.element ?? "not supplied"}</p>
    </section>}
    {selectedRelationship && <section className="mt-3 min-w-0 max-w-full border-t border-zinc-200 pt-2 dark:border-zinc-800" aria-label="Selected relationship provenance">
      <h3 className="font-semibold">Selected {selectedRelationship.kind}</h3>
      <p className="max-w-full break-all font-mono text-xs">{selectedRelationship.identity}</p>
      <p className="min-w-0 max-w-full break-words text-xs">
        From {relationshipSource?.attributes.name || relationshipSource?.id || selectedRelationship.source}
        {relationshipSource?.id ? ` (${relationshipSource.id})` : ""}
        {" to "}
        {relationshipTarget?.attributes.name || relationshipTarget?.id || selectedRelationship.target}
        {relationshipTarget?.id ? ` (${relationshipTarget.id})` : ""}
        {" · evidence order "}{selectedRelationship.order} · element source locator not supplied.
      </p>
      {selectedRelationship.condition && <p className="whitespace-pre-wrap text-sm">Condition ({selectedRelationship.condition.language || "language not specified"}): {selectedRelationship.condition.text ?? "Text not captured"}</p>}
      <p className="max-w-full break-all text-[10px] text-zinc-500">Source element identity: {selectedRelationship.source} · target element identity: {selectedRelationship.target}</p>
    </section>}
    <details className="mt-3" open={!selectedEvidenceElement && !selectedRelationship}>
      <summary className="cursor-pointer font-semibold">Source provenance</summary>
      <dl className="mt-2 space-y-2 break-words text-sm">
        <Field label="Status">{sourceStatus}</Field>
        <Field label="Repository">{summary.repositoryId}</Field>
        <Field label="Commit"><code className="font-mono text-xs">{summary.commitSha}</code></Field>
        <Field label="Path"><code className="font-mono text-xs">{summary.unitPath}</code></Field>
        <Field label="Evidence digest"><code className="font-mono text-xs">{evidence?.sourceSha256 ?? summary.source.sha256 ?? "not supplied"}</code></Field>
        <Field label="Source access">{summary.source.status}{sourceVerified ? " · original UTF-8 bytes verified" : " · source bytes not verified"}</Field>
        <Field label="Extraction coverage">{summary.coverage}{authored?.contentProfile ? ` · ${authored.contentProfile}` : ""}{candidate ? ` · recipe ${candidate.recipe}` : ""} · not business completeness, validity or approval</Field>
        <Field label="Diagnostics">{authored?.diagnostics.length ?? candidate?.diagnostics.length ?? summary.diagnosticCount} selected-evidence diagnostics</Field>
        <Field label="Snapshot">{snapshot?.snapshot ?? "not loaded"}{snapshot ? ` · generation ${snapshot.generation}` : ""}</Field>
        <Field label="Selected revisions">{Object.entries(snapshot?.selection ?? {}).map(([repo, sha]) => `${repo}@${sha.slice(0, 12)}`).join(", ") || "all repositories / server-resolved"}</Field>
      </dl>
    </details>
    {summary.kind === "authored" && summary.source.status !== "available" && <p className="mt-3 text-sm text-amber-800 dark:text-amber-200">{summary.source.status === "denied" ? "Source access denied; no fallback is attempted." : "Source is missing from this snapshot. Evidence-only view remains available."}</p>}
  </aside>;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return <div><dt className="text-xs font-semibold text-zinc-500">{label}</dt><dd>{children}</dd></div>;
}
