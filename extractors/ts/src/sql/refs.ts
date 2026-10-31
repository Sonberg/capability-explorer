import { identValue, lookupKey, parseName, qualify, type Catalog, type RoutineDef, type TableDef } from "./ddl.ts";
import { isIdent, isKw, isP, upper, type Tok } from "./lexer.ts";

/** A static reference from a routine or view body. `object` is the catalog key of the table, view or routine. */
export interface Ref {
  rel: "READS" | "WRITES" | "CALLS";
  object: string;
  column?: string;
  at: { start: number; end: number }[];
}
export interface UnresolvedRef { rel: "READS" | "WRITES" | "CALLS"; name: string; at: { start: number; end: number } }
export interface BodyAnalysis { refs: Ref[]; unresolved: UnresolvedRef[]; dynamic?: { start: number; end: number } }

/** Words that begin an alias position but are really clause keywords. */
const CLAUSE = new Set([
  "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT",
  "INTERSECT", "LIMIT", "OFFSET", "SET", "USING", "WHEN", "WITH", "FOR", "WINDOW", "RETURNING", "VALUES", "SELECT", "FETCH", "OPTION",
  "PIVOT", "UNPIVOT", "OUTPUT", "FROM", "INTO", "AS", "TABLESAMPLE", "LATERAL", "APPLY", "GO", "BEGIN", "END", "ELSE", "THEN", "AND", "OR",
  "NOT", "IN", "IS", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "IF", "WHILE", "RETURN", "EXEC", "EXECUTE", "PRINT", "DROP",
  "CREATE", "ALTER", "TRUNCATE", "COMMIT", "ROLLBACK", "RAISERROR", "THROW", "OPEN", "CLOSE", "DEALLOCATE", "USE", "BREAK", "CONTINUE",
  "GOTO", "WAITFOR", "CASE", "BETWEEN", "LIKE", "ILIKE", "EXISTS", "ONLY", "ON", "DO", "CONFLICT", "TOP", "DISTINCT", "ALL", "ANY",
]);
/** Words never read as a column. */
const NOT_COLUMN = new Set([
  ...CLAUSE, "NULL", "TRUE", "FALSE", "ASC", "DESC", "BY", "OVER", "PARTITION", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING", "FOLLOWING",
  "CURRENT", "ROW", "INTERVAL", "CAST", "SOME", "ESCAPE", "NULLS", "FIRST", "LAST", "PERCENT", "TIES", "KEY", "SEPARATOR", "MATERIALIZED",
]);
const DATEPART_FUNCTIONS = new Set(["DATEDIFF", "DATEADD", "DATEPART", "DATENAME", "DATEDIFF_BIG", "EXTRACT"]);

interface Src { table?: TableDef; opaque: boolean; writeOnly?: boolean }
interface Pending { rel: "READS" | "WRITES"; qualifier?: string; column: string; start: number; end: number; wildcard?: boolean }
interface Stmt {
  kind: string;
  ctx: string;
  aliases: Map<string, Src>;
  sources: Src[];
  ctes: Set<string>;
  pending: Pending[];
  writeTarget?: Src;
  writeAlias?: string;
  /** The next token starts a table source. */
  expect?: "READS" | "WRITES";
  /** Depth of a parenthesis opened where a table source was expected: its alias follows the closing parenthesis. */
  parenAlias?: number;
  assign: boolean;
  cteMode: boolean;
  skipName: boolean;
  /** T-SQL `UPDATE s SET … FROM dbo.T AS s`: the target names an alias that the FROM clause defines later. */
  deferred?: { name: string; start: number; end: number };
}
interface Scope { depth: number; stmt: Stmt }

const newStmt = (): Stmt => ({ kind: "", ctx: "", aliases: new Map(), sources: [], ctes: new Set(), pending: [], assign: false, cteMode: false, skipName: false });

export function analyzeBody(cat: Catalog, routine: RoutineDef): BodyAnalysis {
  const d = cat.dialect;
  const t = routine.body;
  const refs = new Map<string, Ref>();
  const unresolved: UnresolvedRef[] = [];
  let dynamic: BodyAnalysis["dynamic"];

  const add = (rel: Ref["rel"], object: string, start: number, end: number, column?: string) => {
    const key = `${rel}|${object}|${column ?? ""}`;
    const existing = refs.get(key);
    if (existing) existing.at.push({ start, end });
    else refs.set(key, { rel, object, ...(column ? { column } : {}), at: [{ start, end }] });
  };

  const scopes: Scope[] = [{ depth: 0, stmt: newStmt() }];
  let parens = 0;
  const top = () => scopes[scopes.length - 1]!;

  const lookupAlias = (name: string): Src | undefined => {
    const k = name.toLowerCase();
    for (let s = scopes.length - 1; s >= 0; s--) {
      const found = scopes[s]!.stmt.aliases.get(k);
      if (found && !found.writeOnly) return found;
    }
    return undefined;
  };
  const columnOf = (src: Src | undefined, name: string) => src?.table?.columns.get(lookupKey(d, name));

  function resolve(scope: Scope) {
    const st = scope.stmt;
    if (st.deferred) {
      const src = st.aliases.get(st.deferred.name.toLowerCase());
      if (src?.table && src.table.kind === "Table") {
        add("WRITES", lookupKey(d, src.table.name), st.deferred.start, st.deferred.end);
        st.writeTarget = src;
      } else unresolved.push({ rel: "WRITES", name: st.deferred.name, at: { start: st.deferred.start, end: st.deferred.end } });
      st.deferred = undefined;
    }
    for (const p of st.pending) {
      if (p.wildcard) {
        const targets = p.qualifier ? [lookupAlias(p.qualifier)] : st.sources.filter((s) => !s.writeOnly);
        for (const src of targets) {
          if (src?.table && src.table.kind === "Table") for (const c of src.table.columns.values()) add("READS", lookupKey(d, src.table.name), p.start, p.end, c.name);
        }
        continue;
      }
      let src: Src | undefined;
      if (p.qualifier) src = p.rel === "WRITES" && !st.aliases.has(p.qualifier) ? undefined : lookupAlias(p.qualifier) ?? (p.rel === "WRITES" ? st.writeTarget : undefined);
      else if (p.rel === "WRITES") src = st.writeTarget;
      else {
        // Unqualified: exactly one table of the statement has the column, and no source of unknown shape could own it.
        const candidates = st.sources.filter((s) => !s.writeOnly);
        const all = [...candidates, ...(st.writeTarget && st.writeTarget.writeOnly ? [] : st.writeTarget ? [st.writeTarget] : [])];
        const unique = [...new Set(all)];
        if (unique.some((s) => s.opaque || s.table?.kind === "View")) continue;
        const owners = unique.filter((s) => columnOf(s, p.column));
        if (owners.length === 1) src = owners[0];
        else if (owners.length === 0) {
          // Correlated reference to an enclosing statement.
          for (let s = scopes.length - 2; s >= 0 && !src; s--) {
            const outer = [...new Set(scopes[s]!.stmt.sources)].filter((x) => !x.writeOnly);
            if (outer.some((x) => x.opaque)) break;
            const hits = outer.filter((x) => columnOf(x, p.column));
            if (hits.length === 1) src = hits[0];
          }
        }
      }
      const col = columnOf(src, p.column);
      if (src?.table && col && src.table.kind === "Table") add(p.rel, lookupKey(d, src.table.name), p.start, p.end, col.name);
    }
    st.pending = [];
  }

  function endStatement(scope: Scope) {
    resolve(scope);
    scope.stmt = newStmt();
  }

  function takeAlias(i: number): { alias?: string; next: number } {
    if (isKw(t[i], "AS") && isIdent(t[i + 1])) {
      let next = i + 2;
      if (isP(t[next], "(")) next = skipParens(next);
      return { alias: identValue(t[i + 1]!, d), next };
    }
    const tok = t[i];
    if (tok && isIdent(tok) && !(tok.kind === "word" && CLAUSE.has(upper(tok))) && !isP(t[i + 1], ".") && !isP(t[i + 1], "(")) {
      return { alias: identValue(tok, d), next: i + 1 };
    }
    return { next: i };
  }

  function skipParens(i: number): number {
    let depth = 0;
    for (let k = i; k < t.length; k++) {
      if (isP(t[k], "(")) depth++;
      else if (isP(t[k], ")") && --depth === 0) return k + 1;
    }
    return t.length;
  }

  /** A table source at i. Returns the index after what was consumed, or i when the source is something else (a subquery). */
  function takeSource(i: number, rel: "READS" | "WRITES"): number {
    const st = top().stmt;
    while (isKw(t[i], "ONLY", "LATERAL")) i++;
    if (isP(t[i], "(")) {
      st.parenAlias = parens + 1;
      return i;
    }
    const n = parseName(t, i, d);
    if (!n) return i;
    const first = t[i]!;
    let end = t[n.next - 1]!.end;
    const lastPart = n.parts[n.parts.length - 1]!;
    if (rel === "READS" && isP(t[n.next], "(")) {
      // A function in FROM: its arguments are scanned normally; an alias may follow the closing parenthesis.
      callOf(n, first.start, end);
      st.parenAlias = parens + 1;
      return n.next;
    }
    let src: Src;
    const isCte = n.parts.length === 1 && scopes.some((s) => s.stmt.ctes.has(lastPart.toLowerCase()));
    if (isCte || /^[#@]/.test(lastPart) || first.kind === "var") src = { opaque: true };
    else {
      const obj = cat.objects.get(lookupKey(d, qualify(n.parts, d)));
      if (obj) src = { table: obj, opaque: false };
      else if (rel === "WRITES" && d === "tsql" && n.parts.length === 1 && (st.kind === "UPDATE" || st.kind === "DELETE")) {
        st.deferred = { name: lastPart, start: first.start, end };
        return n.next;
      } else {
        src = { opaque: true };
        unresolved.push({ rel, name: qualify(n.parts, d), at: { start: first.start, end } });
      }
    }
    let next = n.next;
    // Table hints: WITH (NOLOCK), (NOLOCK).
    if (isKw(t[next], "WITH") && isP(t[next + 1], "(")) next = skipParens(next + 1);
    const a = takeAlias(next);
    next = a.next;
    if (a.alias) end = t[next - 1]!.end;
    if (isKw(t[next], "WITH") && isP(t[next + 1], "(")) next = skipParens(next + 1);
    if (rel === "WRITES") {
      src.writeOnly = st.kind === "INSERT";
      st.writeTarget = src;
      st.writeAlias = (a.alias ?? lastPart).toLowerCase();
    }
    if (src.table) {
      // Writes through a view are not tracked: the relation catalogue only allows tables and columns as targets.
      if (!(rel === "WRITES" && src.table.kind === "View")) add(rel, lookupKey(d, src.table.name), first.start, end);
    }
    const key = (a.alias ?? lastPart).toLowerCase();
    st.aliases.set(key, src);
    if (!a.alias && n.parts.length > 1) st.aliases.set(n.parts.map((p) => p.toLowerCase()).join("."), src);
    st.sources.push(src);
    return next;
  }

  /** A call `name(`: a call to a declared routine of this unit becomes a CALLS reference. */
  function callOf(n: { parts: string[] }, start: number, end: number) {
    const key = lookupKey(d, qualify(n.parts, d));
    const target = cat.routines.get(key);
    if (target && target.kind === "Routine") add("CALLS", key, start, end);
  }

  function statementStarts(i: number): boolean {
    const w = upper(t[i]);
    if (!["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "WITH"].includes(w)) return false;
    const prev = t[i - 1];
    const st = top().stmt;
    if (i === 0) return true;
    if (isKw(prev, "UNION", "ALL", "EXCEPT", "INTERSECT", "THEN", "DO", "FOR", "ON", "OF", "DISTINCT", "AS", "FROM", "INTO", "JOIN")) return false;
    if (isP(prev, "(") || isP(prev, ",") || isP(prev, "=")) return false;
    if (w === "SELECT" || w === "WITH") return !(st.kind === "INSERT" || st.kind === "CREATE" || (w === "WITH" && isP(t[i + 1], "(")));
    if (w === "DELETE" && st.kind === "") return true;
    return true;
  }

  const isCteHead = (i: number) => {
    // name [(cols)] AS [NOT] [MATERIALIZED] (
    if (!isIdent(t[i])) return false;
    let k = i + 1;
    if (isP(t[k], "(")) k = skipParens(k);
    if (!isKw(t[k], "AS")) return false;
    k++;
    if (isKw(t[k], "NOT")) k++;
    if (isKw(t[k], "MATERIALIZED")) k++;
    return isP(t[k], "(");
  };

  for (let i = 0; i < t.length; i++) {
    const tok = t[i]!;
    const scope = top();
    const st = scope.stmt;
    const atBase = parens === scope.depth;

    if (isP(tok, "(")) {
      parens++;
      if (st.expect && atBase) { st.parenAlias = parens; st.expect = undefined; }
      if (isKw(t[i + 1], "SELECT", "WITH", "VALUES")) {
        const child: Scope = { depth: parens, stmt: newStmt() };
        child.stmt.kind = isKw(t[i + 1], "VALUES") ? "" : "SUBQUERY";
        scopes.push(child);
      }
      continue;
    }
    if (isP(tok, ")")) {
      if (scope.depth === parens && scopes.length > 1) {
        resolve(scope);
        scopes.pop();
      }
      parens--;
      const parent = top().stmt;
      if (parent.parenAlias === parens + 1) {
        parent.parenAlias = undefined;
        const a = takeAlias(i + 1);
        const src: Src = { opaque: true };
        if (a.alias) parent.aliases.set(a.alias.toLowerCase(), src);
        parent.sources.push(src);
        i = a.next - 1;
      }
      continue;
    }

    if (isP(tok, ";") && atBase) { endStatement(scope); continue; }
    if (isP(tok, ",") && atBase) {
      if (st.ctx === "from") st.expect = "READS";
      if (st.ctx === "set") st.assign = true;
    }

    // ---- a table source was expected ----
    if (st.expect && atBase) {
      const rel = st.expect;
      st.expect = undefined;
      if (isIdent(tok) || tok.kind === "var" || isKw(tok, "ONLY", "LATERAL")) {
        const next = takeSource(i, rel);
        if (next > i) {
          i = next - 1;
          // INSERT INTO t (a, b) …: the column list names written columns.
          if (rel === "WRITES" && st.kind === "INSERT" && isP(t[i + 1], "(") && !isKw(t[i + 2], "SELECT", "WITH", "VALUES")) {
            const close = skipParens(i + 1);
            for (let k = i + 2; k < close - 1; k++) {
              if (isIdent(t[k]) && (isP(t[k - 1], "(") || isP(t[k - 1], ","))) st.pending.push({ rel: "WRITES", column: identValue(t[k]!, d), start: t[k]!.start, end: t[k]!.end });
            }
            i = close - 1;
          }
          continue;
        }
      }
    }
    if (st.skipName && atBase && (isIdent(tok) || tok.kind === "var")) {
      st.skipName = false;
      const n = parseName(t, i, d);
      i = (n ? n.next : i + 1) - 1;
      continue;
    }

    if (tok.kind === "word" && !isP(t[i + 1], ".")) {
      const w = upper(tok);

      if (atBase && statementStarts(i)) {
        if (w === "WITH") {
          if (st.kind !== "" ) endStatement(scope);
          const s2 = top().stmt;
          s2.cteMode = true;
          s2.kind = "WITH";
          if (isCteHead(i + 1)) { s2.ctes.add(identValue(t[i + 1]!, d).toLowerCase()); i++; }
          continue;
        }
        if (st.kind !== "" && !(st.kind === "WITH" && st.cteMode)) endStatement(scope);
        const s2 = top().stmt;
        s2.cteMode = false;
        s2.kind = w === "TRUNCATE" ? "TRUNCATE" : w;
        s2.ctx = w === "SELECT" ? "select" : "";
        if (w === "INSERT") { s2.expect = "WRITES"; if (isKw(t[i + 1], "INTO")) i++; }
        else if (w === "UPDATE") s2.expect = "WRITES";
        else if (w === "DELETE") { if (isKw(t[i + 1], "FROM")) i++; s2.expect = "WRITES"; s2.ctx = "delete"; }
        else if (w === "MERGE") { if (isKw(t[i + 1], "INTO")) i++; s2.expect = "WRITES"; }
        else if (w === "TRUNCATE") { if (isKw(t[i + 1], "TABLE")) i++; s2.expect = "WRITES"; }
        continue;
      }

      if (st.cteMode && atBase && w === "AS") continue;
      if (st.cteMode && atBase && (isP(t[i - 1], ",")) && isCteHead(i)) { st.ctes.add(identValue(tok, d).toLowerCase()); continue; }

      if (atBase) {
        switch (w) {
          case "FROM":
            if (!(isKw(t[i - 1], "DISTINCT") && isKw(t[i - 2], "IS", "NOT"))) {
              st.ctx = "from";
              st.expect = "READS";
              continue;
            }
            break;
          case "JOIN":
          case "APPLY":
            st.ctx = "from";
            st.expect = "READS";
            continue;
          case "USING":
            if (st.kind === "MERGE") { st.expect = "READS"; st.ctx = "from"; } else st.ctx = "other";
            continue;
          case "INTO":
            if (st.kind === "SELECT" || st.kind === "") st.skipName = true;
            else if (st.kind === "MERGE") st.expect = "WRITES";
            continue;
          case "SET":
            if (st.kind === "UPDATE" || st.kind === "MERGE" || st.kind === "INSERT") { st.ctx = "set"; st.assign = true; }
            continue;
          case "DO":
            continue;
          case "VALUES":
            st.ctx = "values";
            continue;
          case "WHERE": case "GROUP": case "HAVING": case "ORDER": case "LIMIT": case "OFFSET": case "ON": case "RETURNING": case "OUTPUT":
          case "UNION": case "EXCEPT": case "INTERSECT":
            st.ctx = "other";
            continue;
          case "SELECT":
            st.ctx = "select";
            continue;
          case "THEN":
            if (st.kind === "MERGE") {
              // WHEN … THEN UPDATE SET … | INSERT (cols) VALUES …
              if (isKw(t[i + 1], "UPDATE") && isKw(t[i + 2], "SET")) { st.ctx = "set"; st.assign = true; i += 2; }
              else if (isKw(t[i + 1], "INSERT") && isP(t[i + 2], "(")) {
                const close = skipParens(i + 2);
                for (let k = i + 3; k < close - 1; k++) {
                  if (isIdent(t[k]) && (isP(t[k - 1], "(") || isP(t[k - 1], ","))) st.pending.push({ rel: "WRITES", column: identValue(t[k]!, d), start: t[k]!.start, end: t[k]!.end });
                }
                i = close - 1;
              }
            }
            continue;
          case "EXEC": case "EXECUTE": case "CALL": {
            const n = isIdent(t[i + 1]) && !isKw(t[i + 1], "FUNCTION", "PROCEDURE") ? parseName(t, i + 1, d) : undefined;
            if (n && !isP(t[n.next], "(") || (n && d === "postgresql" && w === "CALL")) {
              callOf(n!, tok.start, t[n!.next - 1]!.end);
              i = n!.next - 1;
              continue;
            }
            if (!isKw(t[i + 1], "FUNCTION", "PROCEDURE")) dynamic ??= { start: tok.start, end: t[i + 1]?.end ?? tok.end };
            continue;
          }
          default:
        }
      }
      if (w === "SP_EXECUTESQL") dynamic ??= { start: tok.start, end: tok.end };
    }

    // ---- set-clause assignment target ----
    if (st.assign && st.ctx === "set" && atBase && (isIdent(tok))) {
      st.assign = false;
      const n = parseName(t, i, d);
      const after = n ? t[n.next] : undefined;
      if (n && after && (isP(after, "=") || (["+", "-", "*", "/", "%"].includes(after.text) && isP(t[n.next + 1], "=")))) {
        const col = n.parts[n.parts.length - 1]!;
        st.pending.push({
          rel: "WRITES", ...(n.parts.length > 1 ? { qualifier: n.parts[n.parts.length - 2]!.toLowerCase() } : {}),
          column: col, start: tok.start, end: t[n.next - 1]!.end,
        });
        i = n.next - 1;
        continue;
      }
    }

    // ---- column references: a.b, a.b.c, a.*, b ----
    if (isIdent(tok)) {
      const n = parseName(t, i, d);
      if (!n) continue;
      const lastTok = t[n.next - 1]!;
      const prev = t[i - 1];
      const nextTok = t[n.next];
      // a.* / bare names in a select list
      if (n.parts.length >= 1 && isP(nextTok, ".") && isP(t[n.next + 1], "*")) {
        st.pending.push({ rel: "READS", qualifier: n.parts[n.parts.length - 1]!.toLowerCase(), column: "*", start: tok.start, end: t[n.next + 1]!.end, wildcard: true });
        i = n.next + 1;
        continue;
      }
      if (isP(nextTok, "(")) {
        // function call; an unqualified datepart/aggregate name is not a column either
        callOf(n, tok.start, lastTok.end);
        i = n.next - 1;
        continue;
      }
      if (isP(prev, "::") || isKw(prev, "AS")) { i = n.next - 1; continue; }
      if (tok.kind === "word" && n.parts.length === 1 && NOT_COLUMN.has(upper(tok))) continue;
      if (prev && isP(prev, "(") && DATEPART_FUNCTIONS.has(upper(t[i - 2]))) { i = n.next - 1; continue; }
      if (n.parts.length === 1) {
        if (routine.params.has(tok.value.toLowerCase())) continue;
        // alias without AS: `a b`
        if (prev && ((isIdent(prev) && !(prev.kind === "word" && NOT_COLUMN.has(upper(prev)))) || isP(prev, ")") || prev.kind === "string" || prev.kind === "number")) continue;
        if (isP(nextTok, "=") && st.ctx === "select" && !isP(t[n.next + 1], "=")) continue;
      }
      const column = n.parts[n.parts.length - 1]!;
      st.pending.push({
        rel: "READS", ...(n.parts.length > 1 ? { qualifier: n.parts[n.parts.length - 2]!.toLowerCase() } : {}),
        column, start: tok.start, end: lastTok.end,
      });
      i = n.next - 1;
      continue;
    }

    // bare `*` as a select item
    if (isP(tok, "*") && st.ctx === "select" && atBase && (isKw(t[i - 1], "SELECT", "DISTINCT", "ALL") || isP(t[i - 1], ",") || t[i - 1]?.kind === "number") && (isKw(t[i + 1], "FROM", "INTO") || isP(t[i + 1], ",") || !t[i + 1])) {
      st.pending.push({ rel: "READS", column: "*", start: tok.start, end: tok.end, wildcard: true });
    }
  }
  while (scopes.length > 1) { resolve(top()); scopes.pop(); }
  resolve(scopes[0]!);

  return { refs: [...refs.values()], unresolved, ...(dynamic ? { dynamic } : {}) };
}
