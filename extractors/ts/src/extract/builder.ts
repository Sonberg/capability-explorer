import { createHash } from "node:crypto";
import type { Span, ToolRef } from "../contracts/records.ts";

export const sha256 = (s: string) => createHash("sha256").update(s, "utf8").digest("hex");
const short = (s: string) => sha256(s).slice(0, 16);

type Json = string | number | boolean | null | Json[] | { [k: string]: Json | undefined };
export type Props = Record<string, Json | undefined>;

export interface EdgeInput {
  from: string;
  rel: string;
  to?: string;
  evidence: string[];
  derivation?: "compiler" | "rule";
  resolution?: "resolved" | "static-target" | "candidate" | "unresolved";
  reason?: string;
  rule?: ToolRef;
  props?: Props;
}

/** Text of a span in LF-normalised file text (schema: 1-based, UTF-16 columns, end exclusive). */
export function spanText(text: string, s: Span): string {
  const lines = text.split("\n");
  if (s.startLine === s.endLine) return (lines[s.startLine - 1] ?? "").slice(s.startColumn - 1, s.endColumn - 1);
  const parts = [(lines[s.startLine - 1] ?? "").slice(s.startColumn - 1)];
  for (let l = s.startLine + 1; l < s.endLine; l++) parts.push(lines[l - 1] ?? "");
  parts.push((lines[s.endLine - 1] ?? "").slice(0, s.endColumn - 1));
  return parts.join("\n");
}

const clean = <T extends object>(o: T): T => Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined)) as T;

/**
 * Accumulates one ce.v1 artifact. Ids are content-derived (same scheme as the C# extractor and the golden
 * builder), so the same input always yields the same bytes.
 */
export class ArtifactBuilder {
  private readonly evidence = new Map<string, Record<string, unknown>>();
  private readonly nodes = new Map<string, Record<string, unknown> & { evidence: string[] }>();
  private readonly edges = new Map<string, Record<string, unknown>>();
  private readonly diagnostics = new Map<string, Record<string, unknown>>();
  private readonly incomplete = new Set<string>();

  readonly repositoryId: string;
  readonly commitSha: string;
  private readonly extractor: ToolRef;
  private readonly fileText: (path: string) => string;

  constructor(repositoryId: string, commitSha: string, extractor: ToolRef, fileText: (path: string) => string) {
    this.repositoryId = repositoryId;
    this.commitSha = commitSha;
    this.extractor = extractor;
    this.fileText = fileText;
  }

  hasNode(key: string) {
    return this.nodes.has(key);
  }

  ev(path: string, span: Span): string {
    const id = `ev:${short(`${path}:${span.startLine}:${span.startColumn}:${span.endLine}:${span.endColumn}`)}`;
    if (!this.evidence.has(id)) {
      this.evidence.set(id, {
        schema: "ce.v1", kind: "evidence", id, repositoryId: this.repositoryId, commitSha: this.commitSha, path, span,
        snippetSha256: sha256(spanText(this.fileText(path), span)), extractor: this.extractor,
      });
    }
    return id;
  }

  node(key: string, kind: string, name: string, evidence: string[], extra: { signature?: string; generated?: boolean; props?: Props } = {}) {
    const existing = this.nodes.get(key);
    if (existing) {
      // Overloads, merged declarations: one symbol, several declarations.
      for (const e of evidence) if (!existing.evidence.includes(e)) existing.evidence.push(e);
      return;
    }
    this.nodes.set(key, clean({
      schema: "ce.v1", kind: "node", id: `n:${short(key)}`, symbolKey: key, nodeKind: kind, name,
      signature: extra.signature, generated: extra.generated || undefined,
      props: extra.props && Object.keys(clean(extra.props)).length ? clean(extra.props) : undefined,
      evidence: [...new Set(evidence)],
    }));
  }

  /** Props of an already declared node (e.g. overloads collected after the fact). */
  nodeProps(key: string): Props | undefined {
    return this.nodes.get(key)?.props as Props | undefined;
  }

  setNodeProp(key: string, name: string, value: Json) {
    const n = this.nodes.get(key);
    if (!n) return;
    n.props = { ...((n.props as Props) ?? {}), [name]: value };
  }

  edge(e: EdgeInput) {
    const evidence = [...new Set(e.evidence)];
    const id = `e:${short(`${e.from}|${e.rel}|${e.to ?? "?"}|${evidence[0]}`)}`;
    if (this.edges.has(id)) return;
    this.edges.set(id, clean({
      schema: "ce.v1", kind: "edge", id, from: e.from, relation: e.rel, to: e.to,
      derivation: e.derivation ?? "compiler", resolution: e.resolution ?? "resolved", unresolvedReason: e.reason,
      rule: e.rule ? { name: e.rule.name, version: e.rule.version } : undefined,
      props: e.props && Object.keys(clean(e.props)).length ? clean(e.props) : undefined,
      evidence,
    }));
  }

  diagnostic(severity: "error" | "warning" | "info", code: string, message: string, path?: string, affectsCoverage = false, evidence?: string) {
    const id = `d:${short(`${code}|${path ?? ""}|${message}`)}`;
    this.diagnostics.set(id, clean({
      schema: "ce.v1", kind: "diagnostic", id, severity, code, message, path,
      affectsCoverage: affectsCoverage || undefined, evidence: evidence ? [evidence] : undefined,
    }));
  }

  markIncomplete(path: string) {
    this.incomplete.add(path);
  }

  toJsonl(unit: { path: string; name: string }, language: "typescript" | "javascript", rules: ToolRef[], configHash: string, inputHash: string, filesTotal: number): string {
    // Edges from symbols this artifact does not declare are dropped (a rule matched code the extractor skipped).
    const edges = [...this.edges.values()].filter((e) => this.nodes.has(e.from as string));
    const used = new Set<string>([
      ...[...this.nodes.values()].flatMap((n) => n.evidence),
      ...edges.flatMap((e) => e.evidence as string[]),
      ...[...this.diagnostics.values()].flatMap((d) => (d.evidence as string[] | undefined) ?? []),
    ]);
    const evidence = [...this.evidence.values()].filter((e) => used.has(e.id as string));
    const partial = this.incomplete.size > 0 || [...this.diagnostics.values()].some((d) => d.affectsCoverage);
    const header = clean({
      schema: "ce.v1", kind: "header", repositoryId: this.repositoryId, commitSha: this.commitSha, unit, language,
      extractor: this.extractor, rules: rules.length ? rules : undefined, configHash, inputHash,
    });
    const complete = {
      schema: "ce.v1", kind: "complete", status: partial ? "partial" : "full",
      counts: { node: this.nodes.size, edge: edges.length, evidence: evidence.length, calculation: 0, diagnostic: this.diagnostics.size },
      coverage: clean({
        filesTotal,
        filesAnalyzed: filesTotal - this.incomplete.size,
        incompletePaths: this.incomplete.size ? [...this.incomplete].sort() : undefined,
        unresolvedEdges: edges.filter((e) => e.resolution === "unresolved").length,
      }),
    };
    const byId = (a: Record<string, unknown>, b: Record<string, unknown>) => ((a.id as string) < (b.id as string) ? -1 : (a.id as string) > (b.id as string) ? 1 : 0);
    const body = [...evidence.sort(byId), ...[...this.nodes.values()].sort(byId), ...edges.sort(byId), ...[...this.diagnostics.values()].sort(byId)];
    return [header, ...body, complete].map((r) => JSON.stringify(r)).join("\n") + "\n";
  }
}
