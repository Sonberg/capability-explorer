import type { ReactNode } from "react";
import type {
  AuthoredModelEvidence, CandidateArtifact, CandidateNode, CandidateSource, ModelEvidence,
  ModelScope, ModelSummary,
} from "../models/contracts";

const card = "rounded-lg border border-zinc-200 bg-white p-3 dark:border-zinc-800 dark:bg-zinc-900";

export function ElementList({ elements, relationships, selected, onSelect }: {
  elements: AuthoredModelEvidence["elements"];
  relationships: AuthoredModelEvidence["relationships"];
  selected: string;
  onSelect: (element: AuthoredModelEvidence["elements"][number]) => void;
}) {
  const byIdentity = new Map(elements.map((element) => [element.identity, element]));
  return <div className="max-h-[70vh] overflow-auto p-3">
    <h2 className="font-semibold">Complete ordered text alternative</h2>
    <p className="mb-2 text-xs text-zinc-500">Elements follow source evidence order. Model identity is not a source line number.</p>
    <ol className="space-y-1">
      {elements.map((element) => <li key={element.identity} className="min-w-0">
        <button type="button" aria-pressed={selected === element.id} onClick={() => onSelect(element)} className={`w-full rounded border p-2 text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600 ${selected === element.id ? "border-blue-500 bg-blue-50 dark:bg-blue-950/50" : "border-zinc-200 dark:border-zinc-800"}`}>
          <span className="font-medium">{element.attributes.name || element.id}</span> <span className="text-xs text-zinc-500">{element.type}</span>
          <span className="block break-all font-mono text-[11px] text-zinc-500">{element.id}</span>
          <span className="block text-xs text-zinc-500">{element.parent ? `Parent: ${byIdentity.get(element.parent)?.attributes.name || byIdentity.get(element.parent)?.id || element.parent}` : "Top-level"}</span>
          {element.content?.text && <span className="block whitespace-pre-wrap text-xs">Expression/content ({element.content.language ?? "language not specified"}): {element.content.text}</span>}
          {element.attributes.isExpanded === "false" && <span className="block text-xs text-amber-700">Not expanded in authored diagram layout.</span>}
        </button>
      </li>)}
    </ol>
    {relationships.length > 0 && <div className="mt-4">
      <h3 className="font-semibold">Ordered relationships</h3>
      <ol className="mt-1 space-y-1">
        {[...relationships].sort((a, b) => a.order - b.order).map((relationship) => <li key={relationship.identity} className="rounded border border-zinc-200 p-2 text-sm dark:border-zinc-800">
          <span className="font-medium">{relationship.kind}</span>: {byIdentity.get(relationship.source)?.attributes.name || byIdentity.get(relationship.source)?.id || relationship.source} → {byIdentity.get(relationship.target)?.attributes.name || byIdentity.get(relationship.target)?.id || relationship.target}
          {relationship.condition?.text && <span className="block whitespace-pre-wrap text-xs">Condition ({relationship.condition.language ?? "language not specified"}): {relationship.condition.text}</span>}
        </li>)}
      </ol>
    </div>}
  </div>;
}

export function DmnTables({ elements, selected, onSelect }: {
  elements: AuthoredModelEvidence["elements"];
  selected: string;
  onSelect: (id: string) => void;
}) {
  const tables = elements.filter((element) => /decisionTable/i.test(element.type)).sort((a, b) => a.order - b.order);
  const expressions = elements.filter((element) => /literalExpression/i.test(element.type)).sort((a, b) => a.order - b.order);
  if (!tables.length) {
    return <div className="space-y-3 p-3">
      <h2 className="font-semibold">Authored literal expressions</h2>
      {expressions.length ? expressions.map((expression) => <section key={expression.identity} className={card}>
        <h3>{expression.attributes.name || expression.id}</h3>
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
      return <section key={table.identity} className="min-w-0">
        <h2 className="font-semibold">{table.attributes.name || table.id}</h2>
        <p className="mb-2 text-xs text-zinc-500">Hit policy: {table.hitPolicy || "not captured"}{table.aggregation ? ` · aggregation: ${table.aggregation}` : ""}. Authored expressions are shown literally, never evaluated.</p>
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse text-left text-sm">
            <caption className="sr-only">{table.attributes.name || table.id} authored decision table</caption>
            <thead><tr>{columns.map((column) => <th key={column.identity} scope="col" className="border border-zinc-300 bg-zinc-100 px-2 py-1 dark:border-zinc-700 dark:bg-zinc-800">{column.attributes.label || column.attributes.name || column.id}</th>)}</tr></thead>
            <tbody>{rules.map((rule) => <tr key={rule.identity}>
              {columns.map((column, columnIndex) => {
                const cells = (rule.cells ?? []).filter((cell) => cell.kind === (columnIndex < inputs.length ? "input" : "output")).sort((a, b) => a.order - b.order);
                const cell = cells[columnIndex < inputs.length ? columnIndex : columnIndex - inputs.length];
                return <td key={column.identity} className="border border-zinc-300 px-2 py-1 align-top dark:border-zinc-700">
                  <button type="button" aria-pressed={selected === rule.id} onClick={() => onSelect(rule.id)} className="whitespace-pre-wrap text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600">
                    {cell?.text ?? "Text not captured"}{cell?.language ? <span className="block text-[10px] text-zinc-500">{cell.language}</span> : null}
                  </button>
                </td>;
              })}
            </tr>)}</tbody>
          </table>
        </div>
      </section>;
    })}
    {expressions.length > 0 && <section>
      <h2 className="mb-2 font-semibold">Authored literal expressions</h2>
      <div className="space-y-2">{expressions.map((expression) => <article key={expression.identity} className={card}>
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

export function Diagnostics({ diagnostics }: { diagnostics: Array<{ code: string; message: string; disposition?: string; source?: object }> }) {
  if (!diagnostics.length) return null;
  return <section className={`${card} border-amber-300`} aria-label="Model diagnostics">
    <h2 className="font-semibold">Diagnostics ({diagnostics.length})</h2>
    <ul className="mt-1 space-y-1">{diagnostics.map((diagnostic, index) => <li key={`${diagnostic.code}:${index}`} className="text-sm">
      <span className="font-mono text-xs">{diagnostic.code}</span>{diagnostic.disposition && <span> · {diagnostic.disposition}</span>}
      <p>{diagnostic.message}</p>
      {diagnostic.source && <p className="break-all text-xs text-zinc-500">{Object.entries(diagnostic.source).map(([key, value]) => `${key}: ${String(value)}`).join(" · ")}</p>}
    </li>)}</ul>
  </section>;
}

export function Provenance({ summary, evidence, snapshot, selectedElement }: {
  summary: ModelSummary; evidence?: ModelEvidence; snapshot?: ModelScope; selectedElement: string;
}) {
  const authored = evidence?.schema === "ce.models.v1" ? evidence as AuthoredModelEvidence : undefined;
  const candidate = evidence?.schema === "ce.candidates.v1" ? evidence as CandidateArtifact : undefined;
  const selectedEvidenceElement = authored?.elements.find((element) => element.id === selectedElement);
  return <aside className={`${card} min-w-0 self-start`} aria-label="Model provenance">
    <h2 className="font-semibold">Provenance</h2>
    <dl className="mt-2 space-y-2 break-words text-sm">
      <Field label="Status">{summary.kind === "candidate" ? "Candidate · not approved / not executable" : "Authored source-backed evidence"}</Field>
      <Field label="Repository">{summary.repositoryId}</Field>
      <Field label="Commit"><code className="font-mono text-xs">{summary.commitSha}</code></Field>
      <Field label="Path"><code className="font-mono text-xs">{summary.unitPath}</code></Field>
      <Field label="Evidence digest"><code className="font-mono text-xs">{evidence?.sourceSha256 ?? summary.source.sha256 ?? "not supplied"}</code></Field>
      <Field label="Source access">{summary.source.status}</Field>
      <Field label="Coverage">{summary.coverage}{authored?.contentProfile ? ` · ${authored.contentProfile}` : ""}{candidate ? ` · recipe ${candidate.recipe}` : ""}</Field>
      <Field label="Diagnostics">{authored?.diagnostics.length ?? candidate?.diagnostics.length ?? summary.diagnosticCount} selected-evidence diagnostics</Field>
      <Field label="Snapshot">{snapshot?.snapshot ?? "not loaded"}{snapshot ? ` · generation ${snapshot.generation}` : ""}</Field>
      <Field label="Selected revisions">{Object.entries(snapshot?.selection ?? {}).map(([repo, sha]) => `${repo}@${sha.slice(0, 12)}`).join(", ") || "all repositories / server-resolved"}</Field>
    </dl>
    {selectedEvidenceElement && <section className="mt-3 border-t border-zinc-200 pt-2 dark:border-zinc-800" aria-label="Selected element provenance">
      <h3 className="font-semibold">Selected element</h3>
      <p className="break-all font-mono text-xs">{selectedEvidenceElement.id} · {selectedEvidenceElement.type}</p>
      <p className="break-all text-xs text-zinc-500">Namespace: {selectedEvidenceElement.namespace} · artifact: {selectedEvidenceElement.source.artifact} · element: {selectedEvidenceElement.source.element ?? "not supplied"}</p>
    </section>}
    {summary.kind === "authored" && summary.source.status !== "available" && <p className="mt-3 text-sm text-amber-800 dark:text-amber-200">{summary.source.status === "denied" ? "Source access denied; no fallback is attempted." : "Source is missing from this snapshot. Evidence-only view remains available."}</p>}
  </aside>;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return <div><dt className="text-xs font-semibold text-zinc-500">{label}</dt><dd>{children}</dd></div>;
}
