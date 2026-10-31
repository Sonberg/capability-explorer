import { identValue, parseName, parseSqlDataType, qualify, type Catalog, type RoutineDef } from "./ddl.ts";
import { isIdent, isKw, isP, type Dialect, type Tok } from "./lexer.ts";
import type { SqlCalculationParseOptions, SqlCalculationParseResult, SqlCalculationQuery, SqlDataType, SqlExpression, SqlSpan } from "./calculation-types.ts";

export const SQL_CALCULATION_MAX_TOKENS = 4096;
export const SQL_CALCULATION_MAX_DEPTH = 64;

class Unsupported extends Error {
  readonly at: SqlSpan;
  constructor(message: string, at: SqlSpan) { super(message); this.at = { start: at.start, end: at.end }; }
}

const PRECEDENCE: Record<string, number> = { AND: 1, "=": 2, "<>": 2, "!=": 2, "<": 2, "<=": 2, ">": 2, ">=": 2, "+": 3, "-": 3, "*": 4, "/": 4 };
const CALL_ARITY: Record<string, [number, number]> = { SUM: [1, 1], COUNT: [1, 1], ISNULL: [2, 2], NULLIF: [2, 2], ROUND: [2, 3], DATEDIFF: [3, 3], DATE_TRUNC: [2, 2], COALESCE: [2, 32] };
const RESERVED = new Set(["SELECT", "FROM", "WHERE", "AS", "AND", "OR", "NOT", "BETWEEN", "JOIN", "INNER", "OUTER", "LEFT", "RIGHT", "FULL", "CROSS", "ON", "GROUP", "HAVING", "ORDER", "UNION", "EXCEPT", "INTERSECT", "LIMIT", "OFFSET", "FETCH", "FOR", "OVER", "FILTER", "RETURN", "BEGIN", "END", "DISTINCT", "ALL", "TOP", "INTO", "WITH"]);

/** The lexer is intentionally permissive; calculation parsing requires a real closing delimiter. */
function closedQuotedToken(token: Tok): boolean {
  const close = token.text[0] === "[" ? "]" : token.text[0];
  for (let i = 1; i < token.text.length; i++) {
    if (token.text[i] !== close) continue;
    if (token.text[i + 1] === close) { i++; continue; }
    return i === token.text.length - 1;
  }
  return false;
}

class Parser {
  private readonly tokens: Tok[];
  private readonly dialect: Dialect;
  private readonly maxDepth: number;
  private readonly fallback: SqlSpan;
  private i = 0;
  constructor(tokens: Tok[], dialect: Dialect, maxDepth: number, fallback: SqlSpan) {
    this.tokens = tokens; this.dialect = dialect; this.maxDepth = maxDepth; this.fallback = fallback;
  }
  private current() { return this.tokens[this.i]; }
  private fail(message: string): never { throw new Unsupported(message, this.current() ?? this.tokens.at(-1) ?? this.fallback); }
  private keyword(word: string) { if (!isKw(this.current(), word)) return false; this.i++; return true; }
  private punctuation(mark: string) { if (!isP(this.current(), mark)) return false; this.i++; return true; }
  private expectP(mark: string) { if (!this.punctuation(mark)) this.fail(`expected ${mark}`); }
  private expectKw(word: string) { if (!this.keyword(word)) this.fail(`expected ${word}`); }
  private namePart() {
    const tok = this.current();
    if (!tok || !isIdent(tok) || (tok.kind === "word" && RESERVED.has(tok.value.toUpperCase()))) this.fail("expected an identifier");
    this.i++;
    return identValue(tok!, this.dialect);
  }

  parse(): { expression: SqlExpression; query?: SqlCalculationQuery; at: SqlSpan } {
    const start = this.current()?.start ?? this.fallback.start;
    let block = false;
    if (this.dialect === "tsql") {
      block = this.keyword("BEGIN");
      this.expectKw("RETURN");
    }
    let query: SqlCalculationQuery | undefined;
    let expression: SqlExpression;
    if (this.keyword("SELECT")) {
      expression = this.expression();
      if (this.keyword("AS")) this.namePart();
      else if (this.isAlias()) this.namePart();
      if (this.keyword("FROM")) {
        const tableAt = this.current();
        const name = parseName(this.tokens, this.i, this.dialect);
        if (!name || name.parts.length > 2) this.fail("expected one local table");
        if (this.tokens.slice(this.i, name.next).some((t) => t.kind === "word" && RESERVED.has(t.value.toUpperCase()))) this.fail("expected a table identifier");
        this.i = name.next;
        let alias: string | undefined;
        if (this.keyword("AS")) alias = this.namePart();
        else if (this.isAlias()) alias = this.namePart();
        const where = this.keyword("WHERE") ? this.expression() : undefined;
        query = { table: qualify(name.parts, this.dialect), ...(alias ? { alias } : {}), ...(where ? { where } : {}), at: { start: tableAt!.start, end: this.tokens[this.i - 1]!.end } };
      }
    } else {
      if (this.dialect !== "tsql") this.fail("expected a SQL SELECT body");
      expression = this.expression();
    }
    // One statement terminator is allowed; extra statements and every trailing clause fail closed.
    this.punctuation(";");
    if (block) { this.expectKw("END"); this.punctuation(";"); }
    if (this.i !== this.tokens.length) this.fail("unsupported trailing clause or multiple statements");
    this.validateDepth(expression);
    if (query?.where) this.validateDepth(query.where);
    return { expression, ...(query ? { query } : {}), at: { start, end: this.tokens.at(-1)?.end ?? expression.at.end } };
  }

  private isAlias() { const tok = this.current(); return tok?.kind === "quoted" || (tok?.kind === "word" && !RESERVED.has(tok.value.toUpperCase())); }

  private validateDepth(expression: SqlExpression) {
    const pending: { expression: SqlExpression; depth: number }[] = [{ expression, depth: 1 }];
    while (pending.length) {
      const item = pending.pop()!;
      if (item.depth > this.maxDepth) throw new Unsupported("calculation AST exceeds nesting limit", item.expression.at);
      const e = item.expression;
      const children = e.kind === "binary" ? [e.left, e.right] : e.kind === "between" ? [e.expression, e.lower, e.upper] : e.kind === "unary" ? [e.operand] : e.kind === "cast" ? [e.expression] : e.kind === "call" ? e.args : [];
      for (const child of children) pending.push({ expression: child, depth: item.depth + 1 });
    }
  }

  private expression(min = 1, depth = 0): SqlExpression {
    if (depth >= this.maxDepth) this.fail("calculation expression exceeds nesting limit");
    let left = this.primary(depth + 1);
    for (;;) {
      const tok = this.current();
      if (isKw(tok, "BETWEEN") && min <= 2) {
        if (left.kind === "between" || (left.kind === "binary" && PRECEDENCE[left.operator] === 2)) this.fail("chained comparisons are unsupported");
        this.i++;
        const lower = this.expression(3, depth + 1);
        this.expectKw("AND");
        const upper = this.expression(3, depth + 1);
        left = { kind: "between", expression: left, lower, upper, at: { start: left.at.start, end: upper.at.end } };
        continue;
      }
      const op = tok?.kind === "word" ? tok.value.toUpperCase() : tok?.text ?? "";
      const precedence = PRECEDENCE[op];
      if (!precedence || precedence < min) break;
      if (precedence === 2 && (left.kind === "between" || (left.kind === "binary" && PRECEDENCE[left.operator] === 2))) this.fail("chained comparisons are unsupported");
      this.i++;
      const right = this.expression(precedence + 1, depth + 1);
      left = { kind: "binary", operator: op as Extract<SqlExpression, { kind: "binary" }>["operator"], left, right, at: { start: left.at.start, end: right.at.end } };
    }
    return left;
  }

  private primary(depth: number): SqlExpression {
    const tok = this.current();
    if (!tok) this.fail("expected an expression");
    let expression: SqlExpression;
    if (this.punctuation("+") || this.punctuation("-")) {
      const operand = this.expression(5, depth);
      expression = { kind: "unary", operator: tok!.text as "+" | "-", operand, at: { start: tok!.start, end: operand.at.end } };
    } else if (this.punctuation("(")) {
      expression = this.expression(1, depth);
      this.expectP(")");
      expression = { ...expression, at: { start: tok!.start, end: this.tokens[this.i - 1]!.end } };
    } else if (tok!.kind === "number") {
      if (!/^(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(tok!.value)) this.fail("invalid numeric literal");
      this.i++;
      expression = { kind: "number", value: tok!.value, at: { start: tok!.start, end: tok!.end } };
    } else if (tok!.kind === "string") {
      if (!tok!.text.endsWith("'") || tok!.text.length < 2) this.fail("unterminated string literal");
      this.i++;
      expression = { kind: "string", value: tok!.value, at: { start: tok!.start, end: tok!.end } };
    } else if (this.keyword("NULL")) {
      expression = { kind: "null", at: { start: tok!.start, end: tok!.end } };
    } else if (tok!.kind === "var") {
      this.i++;
      expression = { kind: "reference", parts: [tok!.value], at: { start: tok!.start, end: tok!.end } };
    } else if (isIdent(tok)) {
      const parts = [this.namePart()];
      while (this.punctuation(".")) parts.push(this.namePart());
      if (this.punctuation("(")) {
        if (parts.length !== 1 || tok!.kind !== "word") this.fail("qualified or quoted function calls are unsupported");
        expression = this.call(parts[0]!.toUpperCase(), tok!, depth);
      } else expression = { kind: "reference", parts, at: { start: tok!.start, end: this.tokens[this.i - 1]!.end } };
    } else this.fail("unsupported expression token");
    while (this.punctuation("::")) {
      if (this.dialect !== "postgresql") this.fail("PostgreSQL cast syntax in T-SQL");
      const type = this.dataType();
      expression = { kind: "cast", expression, type, at: { start: expression.at.start, end: type.at.end } };
    }
    return expression;
  }

  private call(name: string, start: Tok, depth: number): SqlExpression {
    if (name === "CAST") {
      const expression = this.expression(1, depth);
      this.expectKw("AS");
      const type = this.dataType();
      this.expectP(")");
      return { kind: "cast", expression, type, at: { start: start.start, end: this.tokens[this.i - 1]!.end } };
    }
    const arity = CALL_ARITY[name];
    if (!arity || (this.dialect === "tsql" && name === "DATE_TRUNC") || (this.dialect === "postgresql" && ["ISNULL", "DATEDIFF"].includes(name))) this.fail(`unsupported function ${name}`);
    const distinct = this.keyword("DISTINCT");
    if (distinct && name !== "COUNT" && name !== "SUM") this.fail("DISTINCT is only supported on aggregate calls");
    const args: SqlExpression[] = [];
    if (!this.punctuation(")")) {
      do { args.push(this.expression(1, depth)); } while (this.punctuation(","));
      this.expectP(")");
    }
    if (args.length < arity[0] || args.length > arity[1]) this.fail(`unsupported ${name} argument count`);
    return { kind: "call", name, args, distinct, at: { start: start.start, end: this.tokens[this.i - 1]!.end } };
  }

  private dataType(): SqlDataType {
    const from = this.i;
    if (!isIdent(this.current())) this.fail("expected a scalar cast type");
    this.i++;
    if (isKw(this.tokens[from], "DOUBLE") && isKw(this.current(), "PRECISION")) this.i++;
    if (this.punctuation("(")) {
      if (this.current()?.kind !== "number") this.fail("expected type precision");
      this.i++;
      if (this.punctuation(",")) { if (this.current()?.kind !== "number") this.fail("expected type scale"); this.i++; }
      this.expectP(")");
    }
    if (isKw(this.tokens[from], "TIMESTAMP", "TIME") && isKw(this.current(), "WITH", "WITHOUT")) {
      this.i++; this.expectKw("TIME"); this.expectKw("ZONE");
    }
    const type = parseSqlDataType(this.tokens.slice(from, this.i), this.dialect);
    if (!type) this.fail("unsupported scalar cast type");
    return type;
  }
}

/** Parse the bounded static calculation subset without executing SQL. */
export function parseRoutineCalculation(
  catalog: Catalog,
  routine: RoutineDef,
  options: SqlCalculationParseOptions = {},
): SqlCalculationParseResult {
  if (routine.kind !== "Routine" || !routine.routineKind?.includes("function")) return { status: "not-candidate" };
  const unsupported = (reason: string, at: SqlSpan = routine.at): SqlCalculationParseResult => ({ status: "unsupported", reason, at: { start: at.start, end: at.end } });
  if (routine.dynamic) return unsupported("dynamic SQL body is unsupported");
  if (!["scalar-function", "inline-table-valued-function", "sql-function"].includes(routine.routineKind)) return unsupported("procedural function bodies are unsupported");
  if (!routine.returnType || !routine.parameterTypes || routine.params.size !== routine.parameterTypes.length) return unsupported("missing or unsupported routine type declarations");
  const maxTokens = options.maxTokens ?? SQL_CALCULATION_MAX_TOKENS;
  const maxDepth = options.maxDepth ?? SQL_CALCULATION_MAX_DEPTH;
  if (!Number.isInteger(maxTokens) || maxTokens < 1 || maxTokens > SQL_CALCULATION_MAX_TOKENS || !Number.isInteger(maxDepth) || maxDepth < 1 || maxDepth > SQL_CALCULATION_MAX_DEPTH) return unsupported("invalid calculation parser limits");
  if (routine.body.length > maxTokens) return unsupported("calculation body exceeds token limit");
  for (const tok of routine.body) {
    if ((tok.kind === "quoted" || tok.kind === "string") && !closedQuotedToken(tok)) return unsupported("unterminated quoted token", tok);
    if (catalog.dialect === "postgresql" && tok.kind === "string" && tok.value.includes("\\")) return unsupported("escaped PostgreSQL string semantics are unsupported", tok);
  }
  try {
    const parsed = new Parser(routine.body, catalog.dialect, maxDepth, routine.at).parse();
    return { status: "supported", calculation: { dialect: catalog.dialect, ...parsed, parameters: routine.parameterTypes, outputType: routine.returnType } };
  } catch (error) {
    if (error instanceof Unsupported) return unsupported(error.message, error.at);
    throw error;
  }
}
