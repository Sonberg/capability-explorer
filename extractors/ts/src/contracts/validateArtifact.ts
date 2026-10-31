import { readFileSync } from "node:fs";
import { Ajv2020, type ValidateFunction } from "ajv/dist/2020.js";
import addFormatsModule from "ajv-formats";
import {
  COUNTED_KINDS, ID_PREFIX, SCHEMA_ID,
  type AnyRecord, type CalculationRecord, type CompleteRecord, type CountedKind, type HeaderRecord,
} from "./records.ts";

// ajv-formats ships CommonJS; under NodeNext its default export is the module object.
const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as (ajv: Ajv2020) => Ajv2020;

export interface Problem { line: number; message: string }
export interface ValidationResult { ok: boolean; problems: Problem[]; header?: HeaderRecord; complete?: CompleteRecord }

const schemaUrl = new URL("../../../../schema/v1/record.schema.json", import.meta.url);
let compiled: { all: ValidateFunction; byKind: Map<string, ValidateFunction> } | undefined;

function validators() {
  if (!compiled) {
    const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false, allowUnionTypes: true });
    addFormats(ajv);
    const schema = JSON.parse(readFileSync(schemaUrl, "utf8")) as { $id: string };
    const all = ajv.compile(schema);
    const byKind = new Map<string, ValidateFunction>();
    for (const kind of ["header", "complete", ...COUNTED_KINDS]) byKind.set(kind, ajv.getSchema(`${schema.$id}#/$defs/${kind}`)!);
    compiled = { all, byKind };
  }
  return compiled;
}

/**
 * Validates one JSONL artifact: JSON Schema per line plus the structural rules from schema/README.md
 * that JSON Schema cannot express. The .NET validator (platform/) additionally checks relation
 * catalogue constraints and verifies evidence against git.
 */
export function validateArtifact(jsonl: string): ValidationResult {
  const problems: Problem[] = [];
  const add = (line: number, message: string) => problems.push({ line, message });
  const lines = jsonl.split("\n");
  if (lines.at(-1) === "") lines.pop();
  if (lines.length === 0) return { ok: false, problems: [{ line: 0, message: "empty artifact" }] };

  const { all: validate, byKind } = validators();
  const records: { line: number; record: AnyRecord }[] = [];
  lines.forEach((text, i) => {
    const line = i + 1;
    let value: unknown;
    try {
      value = JSON.parse(text);
    } catch (e) {
      add(line, `invalid JSON: ${(e as Error).message}`);
      return;
    }
    if (!validate(value)) {
      // oneOf failures produce one error per branch; re-validate against the record's own kind for a useful message.
      const own = byKind.get(String((value as { kind?: unknown }).kind));
      const errors = own && !own(value) ? own.errors ?? [] : validate.errors ?? [];
      for (const e of errors.slice(0, 5)) add(line, `schema: ${e.instancePath || "/"} ${e.message}`);
      return;
    }
    records.push({ line, record: value as AnyRecord });
  });
  if (problems.length) return { ok: false, problems };

  const first = records[0]!.record;
  const last = records.at(-1)!.record;
  if (first.kind !== "header") add(1, "first record must be the header");
  if (last.kind !== "complete") add(lines.length, "last record must be 'complete' (artifact is truncated or unfinished)");
  for (const { line, record } of records.slice(1, -1)) {
    if (record.kind === "header" || record.kind === "complete") add(line, `${record.kind} may only appear once, at the ${record.kind === "header" ? "start" : "end"}`);
  }
  if (problems.length) return { ok: false, problems };
  const header = first as HeaderRecord;
  const complete = last as CompleteRecord;

  const ids = new Set<string>();
  const evidenceIds = new Set<string>();
  const nodeKeys = new Set<string>();
  const counts: Record<CountedKind, number> = { node: 0, edge: 0, evidence: 0, calculation: 0, diagnostic: 0 };
  for (const { line, record } of records.slice(1, -1)) {
    const kind = record.kind as CountedKind;
    counts[kind]++;
    const id = (record as { id: string }).id;
    if (ids.has(id)) add(line, `duplicate id ${id}`);
    ids.add(id);
    if (!id.startsWith(ID_PREFIX[kind])) add(line, `${kind} id must start with '${ID_PREFIX[kind]}'`);
    if (record.kind === "evidence") {
      evidenceIds.add(id);
      if (record.commitSha !== header.commitSha) add(line, "evidence commitSha differs from header");
      if (record.repositoryId !== header.repositoryId) add(line, "evidence repositoryId differs from header");
      const s = record.span;
      if (s.endLine < s.startLine || (s.endLine === s.startLine && s.endColumn < s.startColumn)) add(line, "span ends before it starts");
    }
    if (record.kind === "node") {
      if (nodeKeys.has(record.symbolKey)) add(line, `duplicate node symbolKey ${record.symbolKey}`);
      nodeKeys.add(record.symbolKey);
    }
  }

  for (const { line, record } of records.slice(1, -1)) {
    for (const ref of referencedEvidence(record)) {
      if (!evidenceIds.has(ref)) add(line, `unknown evidence reference ${ref}`);
    }
    if ((record.kind === "node" || record.kind === "edge" || record.kind === "calculation") && record.evidence.length === 0) {
      add(line, `${record.kind} without evidence`);
    }
    if (record.kind === "edge" && !nodeKeys.has(record.from)) add(line, `edge source ${record.from} is not declared in this artifact`);
    if (record.kind === "calculation" && !nodeKeys.has(record.symbolKey)) add(line, `calculation symbol ${record.symbolKey} is not declared in this artifact`);
  }

  for (const kind of COUNTED_KINDS) {
    if (complete.counts[kind] !== counts[kind]) add(lines.length, `complete.counts.${kind} is ${complete.counts[kind]}, artifact has ${counts[kind]}`);
  }
  const cov = complete.coverage;
  if (cov.filesAnalyzed > cov.filesTotal) add(lines.length, "filesAnalyzed exceeds filesTotal");
  if (complete.status === "full" && (cov.filesAnalyzed !== cov.filesTotal || (cov.incompletePaths?.length ?? 0) > 0)) {
    add(lines.length, "status 'full' requires every file to be analysed");
  }
  if (complete.status === "partial" && cov.filesAnalyzed === cov.filesTotal && !(cov.incompletePaths?.length)) {
    add(lines.length, "status 'partial' must say what is incomplete");
  }
  if (header.schema !== SCHEMA_ID) add(1, `unsupported schema ${header.schema}`);

  return { ok: problems.length === 0, problems, header, complete };
}

function referencedEvidence(record: AnyRecord): string[] {
  switch (record.kind) {
    case "node":
    case "edge":
      return record.evidence;
    case "diagnostic":
      return record.evidence ?? [];
    case "calculation": {
      const c = record as CalculationRecord;
      return [
        ...c.evidence,
        ...Object.values(c.attributes).flatMap((a) => a.evidence ?? []),
        ...(c.output.unit?.evidence ?? []),
        ...c.inputs.flatMap((i) => i.unit?.evidence ?? []),
      ];
    }
    default:
      return [];
  }
}
