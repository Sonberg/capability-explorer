import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { ToolRef } from "../contracts/records.ts";
import { ArtifactBuilder, sha256 } from "../extract/builder.ts";
import { applyStatement, lookupKey, newCatalog, splitStatements, type Catalog, type Range } from "./ddl.ts";
import { LineIndex, tokenize } from "./lexer.ts";
import { discoverSqlUnits, type SqlUnit } from "./project.ts";
import { analyzeBody } from "./refs.ts";
import { emitRoutineCalculation } from "./calculations.ts";

export const SQL_EXTRACTOR: ToolRef = { name: "sql-parser", version: "0.2.0" };
const RULES: Record<SqlUnit["dialect"], ToolRef> = {
  tsql: { name: "tsql.objects", version: "2" },
  postgresql: { name: "postgres.objects", version: "2" },
};

export interface SqlExtractOptions { source: string; repository: string; commit: string }
export interface SqlUnitResult { unitPath: string; unitName: string; jsonl: string }

/**
 * Extracts the SQL units of a source tree: tables, columns, views and routines with what each routine reads,
 * writes and calls. The scripts are tokenised and read statically; nothing is run and no database is needed.
 */
export function extractSql(options: SqlExtractOptions): SqlUnitResult[] {
  const root = resolve(options.source);
  return discoverSqlUnits(root).map((unit) => extractUnit(root, unit, options));
}

function extractUnit(root: string, unit: SqlUnit, options: SqlExtractOptions): SqlUnitResult {
  const d = unit.dialect;
  const texts = new Map<string, string>();
  const lines = new Map<string, LineIndex>();
  const read = (path: string) => {
    if (!texts.has(path)) {
      const text = readFileSync(resolve(root, path), "utf8").replace(/\r\n/g, "\n");
      texts.set(path, text);
      lines.set(path, new LineIndex(text));
    }
    return texts.get(path)!;
  };
  const b = new ArtifactBuilder(options.repository, options.commit, SQL_EXTRACTOR, read);
  const ev = (r: Range) => { read(r.path); return b.ev(r.path, lines.get(r.path)!.span(r.start, r.end)); };
  const evAt = (path: string, start: number, end: number) => ev({ path, start, end });

  // Pass 1: the declared objects, in script order (migrations are applied in sequence).
  const cat: Catalog = newCatalog(d);
  for (const file of unit.files) {
    const text = read(file);
    for (const statement of splitStatements(tokenize(text, d), d, text)) applyStatement(cat, statement, file, text);
  }

  const store = `@unbound:${options.repository}/${unit.name}:dbproject=${unit.name}`;
  const key = (object: string, column?: string) => `sql|${store}|${object}${column ? `#${column}` : ""}`;
  const projectKey = `proj|${options.repository}|${unit.dir}`;
  const rule = RULES[d];
  const display = (k: string) => cat.objects.get(k)?.name ?? cat.routines.get(k)?.name ?? k;

  // Pass 2: what each routine and view reads, writes and calls, against the final schema.
  const analyses = new Map<string, ReturnType<typeof analyzeBody>>();
  for (const [k, routine] of cat.routines) {
    const result = analyzeBody(cat, routine);
    analyses.set(k, result);
    if (routine.dynamic || result.dynamic) {
      const at = result.dynamic ?? { start: routine.at.start, end: routine.at.end };
      b.diagnostic("warning", "SQL_DYNAMIC", `${routine.name} builds SQL at run time (${routine.dynamic ?? "EXEC / EXECUTE"}), so its reads and writes are not complete`,
        routine.path, true, evAt(routine.path, result.dynamic ? at.start : routine.at.start, result.dynamic ? at.end : routine.at.end));
      b.markIncomplete(routine.path);
    }
  }

  // The project node is anchored at the first schema, else the most-read table, else the first object.
  const readCount = new Map<string, number>();
  for (const a of analyses.values()) for (const r of a.refs) if (r.rel === "READS") readCount.set(r.object, (readCount.get(r.object) ?? 0) + 1);
  const firstSchema = [...cat.schemas.values()][0];
  const tables = [...cat.objects.entries()].filter(([, o]) => o.kind === "Table");
  const anchorTable = [...tables].sort(([ka], [kb]) => (readCount.get(kb) ?? 0) - (readCount.get(ka) ?? 0))[0]?.[1];
  let projectEvidence: string | undefined;
  if (unit.projectFile) {
    const text = read(unit.projectFile);
    const m = /<Project\b[^>]*>/.exec(text);
    projectEvidence = evAt(unit.projectFile, m?.index ?? 0, (m?.index ?? 0) + (m?.[0].length ?? Math.min(1, text.length)));
  } else if (firstSchema) projectEvidence = ev(firstSchema.at);
  else if (anchorTable) projectEvidence = ev(anchorTable.at);
  else {
    const any = [...cat.routines.values()][0];
    if (any) projectEvidence = ev(any.at);
  }
  if (!projectEvidence) projectEvidence = unit.files[0] ? evAt(unit.files[0], 0, Math.min(1, read(unit.files[0]).length)) : undefined;
  if (projectEvidence) {
    b.node(projectKey, "Project", unit.name, [projectEvidence], { props: { dialect: d, ...(unit.migrations ? { migrations: true } : {}) } });
  }

  const declare = (from: string, to: string, at: Range) => b.edge({ from, rel: "DECLARES", to, evidence: [ev(at)], derivation: "rule", rule });

  for (const [k, obj] of cat.objects) {
    const objKey = key(obj.name);
    b.node(objKey, obj.kind, obj.name, [ev(obj.at)], { props: obj.props });
    declare(projectKey, objKey, obj.at);
    for (const col of obj.columns.values()) {
      const colKey = key(obj.name, col.name);
      b.node(colKey, "Column", col.name, [ev(col.at)], { signature: col.signature });
      declare(objKey, colKey, col.at);
    }
    void k;
  }
  for (const routine of cat.routines.values()) {
    if (routine.kind === "View") continue; // declared above as a View
    const routineKey = key(routine.name);
    b.node(routineKey, "Routine", routine.name, [ev(routine.at)], {
      props: { routineKind: routine.routineKind, ...(routine.volatility ? { volatility: routine.volatility } : {}) },
    });
    declare(projectKey, routineKey, routine.at);
  }

  // Reads, writes and calls. Each target gets one edge that cites every place it is used.
  for (const [k, routine] of cat.routines) {
    const from = key(routine.name);
    const a = analyses.get(k)!;
    for (const r of a.refs) {
      const targetObject = display(r.object);
      b.edge({
        from, rel: r.rel, to: key(targetObject, r.column), derivation: "rule", rule,
        evidence: r.at.map((x) => evAt(routine.path, x.start, x.end)),
      });
    }
    for (const u of a.unresolved) {
      b.edge({
        from, rel: u.rel, derivation: "rule", rule, resolution: "unresolved", reason: "missing-reference",
        props: { target: u.name }, evidence: [evAt(routine.path, u.at.start, u.at.end)],
      });
    }
  }

  // Calculation rejection does not weaken graph observations. Dynamic bodies
  // already carry SQL_DYNAMIC and partial coverage; never lower them.
  for (const [k, routine] of cat.routines) {
    if (!routine.dynamic && !analyses.get(k)?.dynamic) {
      emitRoutineCalculation(cat, routine, b, key, ev);
    }
  }

  if (unit.nameSource) {
    b.diagnostic("info", "SQL_UNIT_NAME_INFERRED", `no database project file names this unit; "${unit.name}" comes from ${unit.nameSource}`, unit.dir === "." ? undefined : unit.dir);
  }

  const inputs = unit.files.map((f) => `${f}\0${sha256(read(f))}\n`).join("") + (unit.projectFile ? `${unit.projectFile}\0${sha256(read(unit.projectFile))}\n` : "");
  const configHash = sha256(`${SQL_EXTRACTOR.name}/${SQL_EXTRACTOR.version}|${d}`);
  const jsonl = b.toJsonl({ path: unit.dir, name: unit.name }, "sql", [rule], configHash, sha256(inputs), unit.files.length);
  void lookupKey;
  return { unitPath: unit.dir, unitName: unit.name, jsonl };
}
