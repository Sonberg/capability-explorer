// Golden artifact builder. Facts are written by hand in facts/*.mjs; every piece of evidence is
// anchored to a literal text needle that is located in the file *at the locked commit* (via git),
// so line/column spans and snippet hashes can never drift from the fixture sources.
// Zero dependencies: node:crypto + git.
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";

export const SCHEMA = "ce.v1";
export const GOLDEN = { name: "golden", version: "1" };

export const sha256 = (s) => createHash("sha256").update(s, "utf8").digest("hex");
const short = (s) => sha256(s).slice(0, 16);

// ---- symbol key helpers (grammar documented in schema/README.md) ----
export const key = {
  cs: (repo, assembly, docId) => `cs|${repo}|${assembly}|${docId}`,
  csExt: (assembly, docId) => `cs|@ext|${assembly}|${docId}`,
  ts: (repo, pkg, modulePath, declPath, meaning = "value") => `ts|${repo}|${pkg}|${modulePath}|${declPath}#${meaning}`,
  tsExt: (pkg, declFile, declPath) => `ts|@ext|${pkg}|${declFile}|${declPath}#value`,
  proj: (repo, unitPath) => `proj|${repo}|${unitPath}`,
  svc: (repo, name) => `svc|${repo}|${name}`,
  http: (serviceRef, method, route) => `http|${serviceRef}|${method} ${route}`,
  sql: (storeRef, object, column) => `sql|${storeRef}|${object}${column ? `#${column}` : ""}`,
  sqlBuiltin: (dialect, fn) => `sql|@builtin:${dialect}|${fn}`,
  chan: (provider, nsRef, entity) => `chan|${provider}|${nsRef}|${entity}`,
};

// ---- IR helpers ----
export const ir = {
  lit: (value, type) => (type ? { op: "lit", type, value } : { op: "lit", value }),
  v: (ref, type) => (type ? { op: "var", ref, type } : { op: "var", ref }),
  m: (of, name, type) => (type ? { op: "member", of, name, type } : { op: "member", of, name }),
  bin: (o, l, r, type) => (type ? { op: "bin", o, type, l, r } : { op: "bin", o, l, r }),
  cmp: (o, l, r) => ({ op: "cmp", o, l, r }),
  and: (...args) => ({ op: "logic", o: "and", args }),
  not: (a) => ({ op: "logic", o: "not", args: [a] }),
  cond: (test, then, otherwise) => ({ op: "cond", test, then, else: otherwise }),
  coalesce: (...args) => ({ op: "coalesce", args }),
  conv: (to, e) => ({ op: "conv", to, e }),
  round: (mode, e, digits, target) => ({ op: "round", mode, e, ...(digits ? { digits } : {}), ...(target ? { target } : {}) }),
  agg: (fn, over, { bind, value, filter, type } = {}) => ({
    op: "agg", fn, over, ...(bind ? { bind } : {}), ...(value ? { value } : {}), ...(filter ? { filter } : {}), ...(type ? { type } : {}),
  }),
  call: (target, args, type) => (type ? { op: "call", target, args, type } : { op: "call", target, args }),
  tuple: (...items) => ({ op: "tuple", items }),
};

// ---- git access ----
export class FixtureRepo {
  constructor(dir, repositoryId, tag, commitSha) {
    this.dir = dir;
    this.repositoryId = repositoryId;
    this.tag = tag;
    this.commitSha = commitSha;
    this.cache = new Map();
    const actual = this.git("rev-parse", `${tag}^{commit}`).trim();
    if (actual !== commitSha) throw new Error(`${repositoryId}@${tag} is ${actual}, lock says ${commitSha}`);
  }
  git(...args) {
    return execFileSync("git", ["-C", this.dir, ...args], { encoding: "utf8", maxBuffer: 64 << 20 });
  }
  file(path) {
    if (!this.cache.has(path)) this.cache.set(path, this.git("show", `${this.commitSha}:${path}`).replace(/\r\n/g, "\n"));
    return this.cache.get(path);
  }
  files(prefix) {
    const out = this.git("ls-tree", "-r", "--format=%(objectname) %(path)", this.commitSha, "--", prefix);
    return out.split("\n").filter(Boolean).map((l) => {
      const i = l.indexOf(" ");
      return { blob: l.slice(0, i), path: l.slice(i + 1) };
    });
  }
}

// Span math matches schema: 1-based lines/columns, columns in UTF-16 code units, end exclusive.
export function locate(text, needle, nth = 1) {
  let from = 0;
  let index = -1;
  for (let i = 0; i < nth; i++) {
    index = text.indexOf(needle, from);
    if (index < 0) return null;
    from = index + 1;
  }
  const pos = (offset) => {
    const before = text.slice(0, offset);
    const line = before.split("\n").length;
    const column = offset - (before.lastIndexOf("\n") + 1) + 1;
    return { line, column };
  };
  const start = pos(index);
  const end = pos(index + needle.length);
  return { startLine: start.line, startColumn: start.column, endLine: end.line, endColumn: end.column };
}

export function spanText(text, span) {
  const lines = text.split("\n");
  if (span.startLine === span.endLine) return lines[span.startLine - 1].slice(span.startColumn - 1, span.endColumn - 1);
  const parts = [lines[span.startLine - 1].slice(span.startColumn - 1)];
  for (let l = span.startLine + 1; l < span.endLine; l++) parts.push(lines[l - 1]);
  parts.push(lines[span.endLine - 1].slice(0, span.endColumn - 1));
  return parts.join("\n");
}

// ---- artifact builder ----
export class Artifact {
  constructor(repo, { unitPath, unitName, language, sourcePrefix, sourceExtensions, inputPaths, extractor = GOLDEN, rules = [] }) {
    this.repo = repo;
    this.unit = { path: unitPath, name: unitName };
    this.language = language;
    this.sourcePrefix = sourcePrefix;
    this.sourceExtensions = sourceExtensions;
    this.inputPaths = inputPaths ?? [sourcePrefix];
    this.extractor = extractor;
    this.rules = rules;
    this.records = { node: [], edge: [], evidence: [], calculation: [], diagnostic: [] };
    this.evidenceById = new Map();
    this.nodeKeys = new Set();
    this.incomplete = [];
  }

  // at: [path, needle, nth?] or an array of those.
  ev(at) {
    const anchors = Array.isArray(at[0]) ? at : [at];
    return anchors.map(([path, needle, nth = 1]) => {
      const text = this.repo.file(path);
      const span = locate(text, needle, nth);
      if (!span) throw new Error(`${this.repo.repositoryId}@${this.repo.tag}: needle not found in ${path}: ${JSON.stringify(needle)}`);
      const id = `ev:${short(`${path}:${span.startLine}:${span.startColumn}:${span.endLine}:${span.endColumn}`)}`;
      if (!this.evidenceById.has(id)) {
        const record = {
          schema: SCHEMA, kind: "evidence", id,
          repositoryId: this.repo.repositoryId, commitSha: this.repo.commitSha,
          path, span, snippetSha256: sha256(spanText(text, span)), extractor: this.extractor,
        };
        this.evidenceById.set(id, record);
        this.records.evidence.push(record);
      }
      return id;
    });
  }

  node({ key: symbolKey, kind, name, signature, generated, props, at }) {
    if (this.nodeKeys.has(symbolKey)) throw new Error(`duplicate node ${symbolKey}`);
    this.nodeKeys.add(symbolKey);
    this.records.node.push(clean({
      schema: SCHEMA, kind: "node", id: `n:${short(symbolKey)}`, symbolKey, nodeKind: kind, name,
      signature, generated, props, evidence: this.ev(at),
    }));
    return symbolKey;
  }

  edge({ from, rel, to, derivation = "compiler", resolution = "resolved", reason, rule, props, at }) {
    const evidence = this.ev(at);
    this.records.edge.push(clean({
      schema: SCHEMA, kind: "edge", id: `e:${short(`${from}|${rel}|${to ?? "?"}|${evidence[0]}`)}`,
      from, relation: rel, to, derivation, resolution, unresolvedReason: reason, rule, props, evidence,
    }));
  }

  calc({ key: symbolKey, includes, output, inputs, expr, attributes, at }) {
    const attrs = {};
    for (const [name, a] of Object.entries(attributes)) {
      attrs[name] = clean({ status: a.status, value: a.value, detail: a.detail, evidence: a.at ? this.ev(a.at) : undefined });
    }
    this.records.calculation.push(clean({
      schema: SCHEMA, kind: "calculation", id: `c:${short(symbolKey)}`, symbolKey, includes, output, inputs, expr,
      attributes: attrs, irHash: sha256(canonicalJson(expr)), evidence: this.ev(at),
    }));
  }

  diag({ severity, code, message, path, affectsCoverage, at }) {
    this.records.diagnostic.push(clean({
      schema: SCHEMA, kind: "diagnostic", id: `d:${short(`${code}|${path ?? ""}|${message}`)}`,
      severity, code, message, path, affectsCoverage, evidence: at ? this.ev(at) : undefined,
    }));
  }

  markIncomplete(path) {
    this.incomplete.push(path);
  }

  sourceFiles() {
    return this.repo.files(this.sourcePrefix).filter((f) => this.sourceExtensions.some((e) => f.path.endsWith(e)));
  }

  toJsonl() {
    const files = this.inputPaths.flatMap((p) => this.repo.files(p));
    const inputHash = sha256(files.map((f) => `${f.path}\0${f.blob}\n`).sort().join(""));
    const header = clean({
      schema: SCHEMA, kind: "header", repositoryId: this.repo.repositoryId, commitSha: this.repo.commitSha,
      unit: this.unit, language: this.language, extractor: this.extractor,
      rules: this.rules.length ? this.rules : undefined,
      configHash: sha256("golden-config-v1"), inputHash,
    });
    const sources = this.sourceFiles();
    const unresolved = this.records.edge.filter((e) => e.resolution === "unresolved").length;
    const complete = {
      schema: SCHEMA, kind: "complete",
      status: this.incomplete.length ? "partial" : "full",
      counts: Object.fromEntries(Object.entries(this.records).map(([k, v]) => [k, v.length])),
      coverage: clean({
        filesTotal: sources.length,
        filesAnalyzed: sources.length - this.incomplete.length,
        incompletePaths: this.incomplete.length ? [...this.incomplete].sort() : undefined,
        unresolvedEdges: unresolved,
      }),
    };
    const byId = (a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
    const body = ["evidence", "node", "edge", "calculation", "diagnostic"].flatMap((k) => [...this.records[k]].sort(byId));
    return [header, ...body, complete].map((r) => JSON.stringify(r)).join("\n") + "\n";
  }
}

function clean(o) {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined));
}

export function canonicalJson(value) {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonicalJson(value[k])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}
