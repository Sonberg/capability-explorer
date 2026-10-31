import { isIdent, isKw, isP, tokenize, upper, type Dialect, type Tok } from "./lexer.ts";

/** A source range in one file (absolute offsets). */
export interface Range { path: string; start: number; end: number }

export interface ColumnDef { name: string; signature: string; at: Range }
export interface TableDef { kind: "Table" | "View"; name: string; columns: Map<string, ColumnDef>; at: Range; props?: Record<string, string | boolean> }
export interface RoutineDef {
  kind: "Routine" | "View";
  name: string;
  routineKind?: string;
  volatility?: string;
  at: Range;
  params: Set<string>;
  /** Tokens of the body to analyse for reads and writes. */
  body: Tok[];
  path: string;
  /** Set when the body could not be read as static SQL. */
  dynamic?: string;
}
export interface SchemaDef { name: string; at: Range }
export interface Catalog {
  dialect: Dialect;
  schemas: Map<string, SchemaDef>;
  objects: Map<string, TableDef>;
  routines: Map<string, RoutineDef>;
  diagnostics: { severity: "warning" | "info"; code: string; message: string; at: Range; affectsCoverage?: boolean }[];
}

export function newCatalog(dialect: Dialect): Catalog {
  return { dialect, schemas: new Map(), objects: new Map(), routines: new Map(), diagnostics: [] };
}

/** Lookup key: T-SQL names are case-insensitive, PostgreSQL names are folded when parsed. */
export const lookupKey = (dialect: Dialect, name: string) => (dialect === "tsql" ? name.toLowerCase() : name);

const defaultSchema = (d: Dialect) => (d === "tsql" ? "dbo" : "public");

export function identValue(t: Tok, dialect: Dialect): string {
  return t.kind === "word" && dialect === "postgresql" ? t.value.toLowerCase() : t.value;
}

/** `a.b.c` starting at i. Returns the parts and the index after the name. */
export function parseName(t: Tok[], i: number, dialect: Dialect): { parts: string[]; next: number } | undefined {
  if (!isIdent(t[i])) return undefined;
  const parts = [identValue(t[i]!, dialect)];
  let j = i + 1;
  while (isP(t[j], ".") && isIdent(t[j + 1])) {
    parts.push(identValue(t[j + 1]!, dialect));
    j += 2;
  }
  return { parts, next: j };
}

/** schema.object from the last one or two parts of a name; unqualified names get the dialect's default schema. */
export function qualify(parts: string[], dialect: Dialect): string {
  const obj = parts[parts.length - 1]!;
  const schema = parts.length >= 2 ? parts[parts.length - 2]! : defaultSchema(dialect);
  return `${schema}.${obj}`;
}

/** Splits batches (`GO`) and statements (`;`, or a new top-level CREATE/ALTER/DROP) outside of BEGIN/END blocks and parentheses. */
export function splitStatements(t: Tok[], dialect: Dialect, text: string): Tok[][] {
  const statements: Tok[][] = [];
  let cur: Tok[] = [];
  let parens = 0;
  let blocks = 0;
  const flush = () => { if (cur.length) statements.push(cur); cur = []; parens = 0; blocks = 0; };
  const lineStart = (tok: Tok) => { let k = tok.start - 1; while (k >= 0 && (text[k] === " " || text[k] === "\t")) k--; return k < 0 || text[k] === "\n"; };
  for (let i = 0; i < t.length; i++) {
    const tok = t[i]!;
    if (dialect === "tsql" && isKw(tok, "GO") && lineStart(tok) && !isP(t[i + 1], "(") && !isP(t[i + 1], ".")) {
      flush();
      continue;
    }
    if (isP(tok, ";") && parens === 0 && blocks === 0) { flush(); continue; }
    if (cur.length > 0 && parens === 0 && blocks === 0 && isKw(tok, "CREATE", "ALTER", "DROP") && lineStart(tok) && !isKw(cur[cur.length - 1], "ON", "TO", "FOR", "AS", "THEN", "ELSE", "BEGIN") && dialect === "tsql") flush();
    if (isP(tok, "(")) parens++;
    else if (isP(tok, ")")) parens = Math.max(0, parens - 1);
    else if (isKw(tok, "CASE")) blocks++;
    else if (isKw(tok, "BEGIN") && !isKw(t[i + 1], "TRAN", "TRANSACTION", "WORK", "ISOLATION", "READ", "DISTRIBUTED") && !isP(t[i + 1], ";")) blocks++;
    else if (isKw(tok, "END") && !isKw(t[i + 1], "IF", "LOOP", "WHILE", "FOR")) blocks = Math.max(0, blocks - 1);
    cur.push(tok);
  }
  flush();
  return statements;
}

const COLUMN_STOP = new Set([
  "NOT", "NULL", "DEFAULT", "PRIMARY", "UNIQUE", "REFERENCES", "CHECK", "IDENTITY", "CONSTRAINT", "COLLATE", "GENERATED",
  "SPARSE", "ROWGUIDCOL", "FILESTREAM", "MASKED", "ENCRYPTED", "AS", "ON", "WITH",
]);
const TABLE_CONSTRAINT = new Set(["CONSTRAINT", "PRIMARY", "UNIQUE", "FOREIGN", "CHECK", "INDEX", "EXCLUDE", "LIKE", "PERIOD", "CLUSTERED", "NONCLUSTERED"]);

/** Renders type tokens as `decimal(18, 3)`: no space before `(`, `)` and `,`, one after `,`. */
function renderType(tokens: Tok[]): string {
  let s = "";
  for (let k = 0; k < tokens.length; k++) {
    const tok = tokens[k]!;
    const prev = tokens[k - 1];
    const glue = isP(tok, "(") || isP(tok, ")") || isP(tok, ",") || isP(tok, ".") || isP(prev, "(") || isP(prev, ".");
    s += (k > 0 && !glue ? " " : "") + tok.text;
    if (isP(tok, ",") && tokens[k + 1]) s += " ";
  }
  return s.replace(/,\s{2,}/g, ", ");
}

/** One column definition: `name type [constraints]`. Returns undefined for table constraints. */
function parseColumn(el: Tok[], path: string, dialect: Dialect): ColumnDef | undefined {
  if (el.length < 2 || !isIdent(el[0])) return undefined;
  if (el[0]!.kind === "word" && TABLE_CONSTRAINT.has(upper(el[0]))) return undefined;
  const name = identValue(el[0]!, dialect);
  let j = 1;
  const type: Tok[] = [];
  let depth = 0;
  for (; j < el.length; j++) {
    const tok = el[j]!;
    if (isP(tok, "(")) depth++;
    if (depth === 0 && tok.kind === "word" && COLUMN_STOP.has(upper(tok))) break;
    type.push(tok);
    if (isP(tok, ")")) depth--;
  }
  if (type.length === 0) return undefined;
  const rest = el.slice(j);
  let notNull = false;
  let nullable = false;
  for (let k = 0; k < rest.length; k++) {
    if (isKw(rest[k], "NOT") && isKw(rest[k + 1], "NULL")) notNull = true;
    else if (isKw(rest[k], "NULL") && !isKw(rest[k - 1], "NOT")) nullable = true;
    else if (isKw(rest[k], "PRIMARY") && isKw(rest[k + 1], "KEY")) notNull = !nullable;
    else if (isKw(rest[k], "IDENTITY")) notNull = !nullable;
  }
  const constraint = notNull && !nullable ? "NOT NULL" : "NULL";
  return { name, signature: `${renderType(type)} ${constraint}`, at: { path, start: el[0]!.start, end: el[el.length - 1]!.end } };
}

function splitTopLevel(t: Tok[], from: number, to: number): Tok[][] {
  const parts: Tok[][] = [];
  let cur: Tok[] = [];
  let depth = 0;
  for (let i = from; i < to; i++) {
    const tok = t[i]!;
    if (isP(tok, "(")) depth++;
    if (isP(tok, ")")) depth--;
    if (isP(tok, ",") && depth === 0) { parts.push(cur); cur = []; continue; }
    cur.push(tok);
  }
  if (cur.length) parts.push(cur);
  return parts;
}

function matching(t: Tok[], open: number): number {
  let depth = 0;
  for (let i = open; i < t.length; i++) {
    if (isP(t[i], "(")) depth++;
    else if (isP(t[i], ")") && --depth === 0) return i;
  }
  return t.length;
}

const skipIfNotExists = (t: Tok[], i: number) => (isKw(t[i], "IF") && isKw(t[i + 1], "NOT") && isKw(t[i + 2], "EXISTS") ? i + 3 : i);
const skipIfExists = (t: Tok[], i: number) => (isKw(t[i], "IF") && isKw(t[i + 1], "EXISTS") ? i + 2 : i);

/** Parses one statement into the catalog. Statements that are not schema objects (DML, grants, indexes) are ignored. */
export function applyStatement(cat: Catalog, t: Tok[], path: string, text: string): void {
  const d = cat.dialect;
  const verb = upper(t[0]);
  if (verb === "CREATE" || verb === "ALTER") {
    let i = 1;
    // CREATE [OR REPLACE | OR ALTER] [TEMP | UNLOGGED | MATERIALIZED …]
    if (isKw(t[i], "OR")) i += 2;
    let materialized = false;
    while (isKw(t[i], "TEMP", "TEMPORARY", "UNLOGGED", "GLOBAL", "LOCAL", "MATERIALIZED", "RECURSIVE", "EXTERNAL")) {
      if (isKw(t[i], "MATERIALIZED")) materialized = true;
      if (isKw(t[i], "TEMP", "TEMPORARY")) return;
      i++;
    }
    const what = upper(t[i]);
    if (what === "SCHEMA") return createSchema(cat, t, i + 1, path);
    if (what === "TABLE") return verb === "CREATE" ? createTable(cat, t, i + 1, path) : alterTable(cat, t, i + 1, path);
    if (what === "VIEW") return createView(cat, t, i + 1, path, materialized);
    if (what === "FUNCTION" || what === "PROCEDURE" || what === "PROC") return createRoutine(cat, t, i, path, text);
    return;
  }
  if (verb === "DROP") {
    let i = 2;
    const what = upper(t[1]);
    if (what === "TABLE" || what === "VIEW" || what === "FUNCTION" || what === "PROCEDURE" || what === "PROC") {
      i = skipIfExists(t, i);
      for (;;) {
        const n = parseName(t, i, d);
        if (!n) break;
        const key = lookupKey(d, qualify(n.parts, d));
        cat.objects.delete(key);
        cat.routines.delete(key);
        i = n.next;
        if (isP(t[i], "(")) i = matching(t, i) + 1;
        if (!isP(t[i], ",")) break;
        i++;
      }
    }
  }
}

function createSchema(cat: Catalog, t: Tok[], i: number, path: string) {
  i = skipIfNotExists(t, i);
  const n = parseName(t, i, cat.dialect);
  if (!n) return;
  cat.schemas.set(n.parts[n.parts.length - 1]!, { name: n.parts[n.parts.length - 1]!, at: { path, start: t[0]!.start, end: t[n.next - 1]!.end } });
}

function createTable(cat: Catalog, t: Tok[], i: number, path: string) {
  const d = cat.dialect;
  i = skipIfNotExists(t, i);
  const n = parseName(t, i, d);
  if (!n) return;
  const name = qualify(n.parts, d);
  if (/^[#@]/.test(n.parts[n.parts.length - 1]!)) return; // temp tables and table variables
  const table: TableDef = { kind: "Table", name, columns: new Map(), at: { path, start: t[0]!.start, end: t[n.next - 1]!.end } };
  if (isP(t[n.next], "(")) {
    const close = matching(t, n.next);
    for (const el of splitTopLevel(t, n.next + 1, close)) {
      const col = parseColumn(el, path, d);
      if (col) table.columns.set(lookupKey(d, col.name), col);
    }
  }
  cat.objects.set(lookupKey(d, name), table);
}

function alterTable(cat: Catalog, t: Tok[], i: number, path: string) {
  const d = cat.dialect;
  i = skipIfExists(t, i);
  if (isKw(t[i], "ONLY")) i++;
  const n = parseName(t, i, d);
  if (!n) return;
  const table = cat.objects.get(lookupKey(d, qualify(n.parts, d)));
  if (!table) return;
  for (const el of splitTopLevel(t, n.next, t.length)) {
    let j = 0;
    if (isKw(el[j], "ADD")) {
      j++;
      if (isKw(el[j], "COLUMN")) j++;
      j = skipIfNotExists(el, j);
      if (isKw(el[j], "CONSTRAINT", "PRIMARY", "UNIQUE", "FOREIGN", "CHECK", "DEFAULT", "INDEX", "EXCLUDE")) continue;
      const col = parseColumn(el.slice(j), path, d);
      if (col) table.columns.set(lookupKey(d, col.name), col);
    } else if (isKw(el[j], "DROP")) {
      j++;
      if (isKw(el[j], "COLUMN")) j++;
      else if (isKw(el[j], "CONSTRAINT", "INDEX")) continue;
      j = skipIfExists(el, j);
      // T-SQL: DROP COLUMN a, b — the following elements are bare names.
      if (isIdent(el[j])) table.columns.delete(lookupKey(d, identValue(el[j]!, d)));
    } else if (isIdent(el[0]) && el.length === 1) {
      table.columns.delete(lookupKey(d, identValue(el[0]!, d)));
    } else if (isKw(el[j], "RENAME") && isKw(el[j + 1], "COLUMN") && isIdent(el[j + 2]) && isKw(el[j + 3], "TO") && isIdent(el[j + 4])) {
      const old = table.columns.get(lookupKey(d, identValue(el[j + 2]!, d)));
      if (old) {
        table.columns.delete(lookupKey(d, old.name));
        const renamed = identValue(el[j + 4]!, d);
        table.columns.set(lookupKey(d, renamed), { ...old, name: renamed, at: { path, start: el[0]!.start, end: el[el.length - 1]!.end } });
      }
    }
  }
}

function createView(cat: Catalog, t: Tok[], i: number, path: string, materialized: boolean) {
  const d = cat.dialect;
  const n = parseName(t, i, d);
  if (!n) return;
  const name = qualify(n.parts, d);
  let j = n.next;
  if (isP(t[j], "(")) j = matching(t, j) + 1;
  while (j < t.length && !isKw(t[j], "AS")) j++;
  const at = { path, start: t[0]!.start, end: t[n.next - 1]!.end };
  const key = lookupKey(d, name);
  cat.objects.set(key, { kind: "View", name, columns: new Map(), at, ...(materialized ? { props: { materialized: true } } : {}) });
  cat.routines.set(key, { kind: "View", name, at, params: new Set(), body: t.slice(j + 1), path });
}

function createRoutine(cat: Catalog, t: Tok[], i: number, path: string, text: string) {
  const d = cat.dialect;
  const isProc = isKw(t[i], "PROCEDURE", "PROC");
  const n = parseName(t, i + 1, d);
  if (!n) return;
  const name = qualify(n.parts, d);
  const at = { path, start: t[0]!.start, end: t[n.next - 1]!.end };
  const params = new Set<string>();
  let j = n.next;

  if (isP(t[j], "(")) {
    const close = matching(t, j);
    for (const el of splitTopLevel(t, j + 1, close)) {
      let k = 0;
      while (isKw(el[k], "IN", "OUT", "INOUT", "VARIADIC")) k++;
      const first = el[k];
      if (first && (first.kind === "var" || isIdent(first))) params.add(first.value.toLowerCase());
    }
    j = close + 1;
  } else if (d === "tsql") {
    // Procedures may declare parameters without parentheses: CREATE PROCEDURE p @a int, @b int AS …
    while (j < t.length && !isKw(t[j], "AS")) {
      if (t[j]!.kind === "var" && !isKw(t[j - 1], "=")) params.add(t[j]!.value.toLowerCase());
      j++;
    }
  }

  const routine: RoutineDef = { kind: "Routine", name, at, params, body: [], path };
  if (d === "tsql") {
    let k = j;
    const returns: Tok[] = [];
    if (isKw(t[k], "RETURNS")) {
      let m = k + 1;
      while (m < t.length && !isKw(t[m], "AS", "WITH")) { returns.push(t[m]!); m++; }
      k = m;
    }
    if (isKw(t[k], "WITH")) { while (k < t.length && !isKw(t[k], "AS")) k++; }
    routine.body = t.slice(k + 1);
    if (isProc) routine.routineKind = "stored-procedure";
    else if (isKw(returns[0], "TABLE")) routine.routineKind = "inline-table-valued-function";
    else if (returns.some((r) => isKw(r, "TABLE"))) routine.routineKind = "multi-statement-table-valued-function";
    else routine.routineKind = "scalar-function";
  } else {
    let language = "";
    for (let k = j; k < t.length; k++) {
      const tok = t[k]!;
      if (isKw(tok, "LANGUAGE") && t[k + 1]) language = t[k + 1]!.value.toLowerCase();
      else if (isKw(tok, "STABLE", "IMMUTABLE", "VOLATILE")) routine.volatility = tok.value.toUpperCase();
      else if (isKw(tok, "AS") && (t[k + 1]?.kind === "dollar" || t[k + 1]?.kind === "string")) {
        const body = t[k + 1]!;
        if (body.kind === "dollar") routine.body = tokenize(text, d, body.inner![0], body.inner![1]);
        else if (!body.value.includes("\\") && !body.text.includes("''")) routine.body = tokenize(text, d, body.start + 1, body.end - 1);
        else routine.dynamic = "the body is a string literal with escapes";
      }
      if (isKw(tok, "BEGIN") && isKw(t[k + 1], "ATOMIC")) routine.body = t.slice(k + 2);
      if (isKw(tok, "RETURN") && routine.body.length === 0 && !language) routine.body = t.slice(k + 1);
    }
    if (isProc) routine.routineKind = "procedure";
    else routine.routineKind = language ? (language === "sql" ? "sql-function" : `${language}-function`) : "function";
  }
  cat.routines.set(lookupKey(d, name), routine);
}
