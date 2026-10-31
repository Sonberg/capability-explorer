import ts from "typescript";
import type { Ctx } from "./context.ts";
import { isFunctionInit, isModuleLevel, nameOf } from "./context.ts";

/**
 * Calculations: numeric functions that round or aggregate become `calculation` records (IR `expr`, inputs,
 * output and the domain attributes). Nothing is executed; the IR is read from the syntax tree and the checker.
 * Anything the IR cannot express faithfully makes the function a diagnostic, never an approximation.
 */

type Expr = { op: string; [k: string]: unknown };
class Unsupported extends Error {}

const FLOAT = "float64";
const NUMERIC_CONTEXT = new Set(["member", "index", "call"]);
const ROUNDING_CALLS: Record<string, string> = { round: "halfTowardPositiveInfinity", trunc: "truncate", floor: "floor", ceil: "ceiling" };
const MINOR_UNITS = new Set(["ore", "cents", "cent", "pence"]);
const MONEY_WORDS = new Set([...MINOR_UNITS, "sek", "usd", "eur", "gbp", "amount", "price", "cost"]);
const PERIOD_UNITS = new Set(["weeks", "days", "months", "years", "hours"]);

const tokens = (name: string) => name.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase().split(/[\s_]+/).filter(Boolean);
const strip = (e: ts.Expression): ts.Expression => {
  while (ts.isParenthesizedExpression(e) || ts.isAsExpression(e) || ts.isNonNullExpression(e) || ts.isSatisfiesExpression(e) || ts.isTypeAssertionExpression(e)) e = e.expression;
  return e;
};
const eq = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

export function calculations(ctx: Ctx) {
  for (const sf of ctx.files) {
    for (const s of sf.statements) {
      if (ts.isFunctionDeclaration(s) && s.body && s.name) candidate(ctx, s, s.parameters, s.body);
      else if (ts.isVariableStatement(s)) {
        for (const d of s.declarationList.declarations) {
          if (isModuleLevel(d) && isFunctionInit(d.initializer) && ts.isBlock(d.initializer.body)) candidate(ctx, d, d.initializer.parameters, d.initializer.body);
        }
      }
    }
  }
}

/** A syntactic pre-filter: rounding or an array reduction. Everything else is not a calculation. */
function hasSignal(body: ts.Node): boolean {
  let found = false;
  const visit = (n: ts.Node) => {
    if (found) return;
    if (ts.isCallExpression(n)) {
      const c = n.expression;
      if (ts.isPropertyAccessExpression(c) && (c.name.text === "toFixed" || c.name.text === "reduce" || (ts.isIdentifier(c.expression) && c.expression.text === "Math" && c.name.text in ROUNDING_CALLS))) found = true;
    }
    ts.forEachChild(n, visit);
  };
  visit(body);
  return found;
}

function candidate(ctx: Ctx, decl: ts.FunctionDeclaration | ts.VariableDeclaration, params: ts.NodeArray<ts.ParameterDeclaration>, body: ts.Block) {
  const key = ctx.keyOf(decl);
  if (!key || !ctx.b.hasNode(key) || !hasSignal(body)) return;
  const fnNode = ts.isFunctionDeclaration(decl) ? decl : (decl.initializer as ts.ArrowFunction | ts.FunctionExpression);
  const sig = ctx.checker.getSignatureFromDeclaration(fnNode);
  if (!sig || !(ctx.checker.getReturnTypeOfSignature(sig).flags & ts.TypeFlags.NumberLike)) return;
  try {
    new Builder(ctx, key, decl, fnNode, params, body).run();
  } catch (e) {
    if (!(e instanceof Unsupported)) throw e;
    ctx.b.diagnostic("info", "CALCULATION_UNSUPPORTED", `${nameOf(decl)}: ${e.message}`, ctx.rel(decl.getSourceFile().fileName), false, ctx.headerEv(decl));
  }
}

interface Seq { over: Expr; filters: ts.ArrowFunction[]; map?: ts.ArrowFunction }
interface Meta { aggs: { fn: string; node: ts.Node; expr: Expr }[]; rounds: { mode: string; node: ts.Node }[] }

class Builder {
  private readonly checker: ts.TypeChecker;
  private readonly env = new Map<ts.Symbol, Expr>();
  private readonly cache = new Map<ts.Symbol, Expr>();
  private readonly src = new Map<object, ts.Node>();
  private readonly meta: Meta = { aggs: [], rounds: [] };
  private readonly includes = new Set<string>();
  private next = 0;
  /** `new Set(...)` nodes already turned into a distinct count, so every `.size` shares one placeholder. */
  private readonly distinct = new Map<ts.Node, Expr>();
  private readonly paramNames = new Map<string, string>();
  private readonly paramNodes = new Map<string, ts.ParameterDeclaration>();

  constructor(
    private readonly ctx: Ctx, private readonly key: string, private readonly decl: ts.FunctionDeclaration | ts.VariableDeclaration,
    private readonly fn: ts.FunctionDeclaration | ts.ArrowFunction | ts.FunctionExpression, private readonly params: ts.NodeArray<ts.ParameterDeclaration>, private readonly body: ts.Block,
  ) {
    this.checker = ctx.checker;
  }

  // ------------------------------------------------------------------ driver

  run() {
    const { ctx } = this;
    for (const p of this.params) {
      if (!ts.isIdentifier(p.name)) throw new Unsupported("destructured parameter");
      const ref = `$${this.next++}`;
      const sym = this.checker.getSymbolAtLocation(p.name)!;
      this.env.set(sym, { op: "var", ref });
      this.paramNames.set(ref, p.name.text);
      this.paramNodes.set(ref, p);
    }
    const expr = this.block(this.body.statements);
    const doc = this.docUnit();
    const inputs = this.params.map((p, i) => this.input(p, `$${i}`));
    const outputUnit = doc ? this.attr("known", doc.unit, [doc.evidence]) : this.derivedOutputUnit(inputs);
    const attributes = new Attributes(this, expr, doc).compute();
    ctx.b.calculation({
      symbolKey: this.key,
      includes: [...this.includes].sort(),
      output: { type: FLOAT, ...(outputUnit ? { unit: outputUnit } : { unit: { status: "unknown" } }) },
      inputs,
      expr,
      attributes,
      evidence: [ctx.headerEv(this.decl)],
    });
  }

  // ------------------------------------------------------------------ inputs and units

  private input(p: ts.ParameterDeclaration, ref: string): Record<string, unknown> {
    const typeNode = ts.getEffectiveTypeAnnotationNode(p);
    const type = typeNode ? typeNode.getText() : this.checker.typeToString(this.checker.getTypeAtLocation(p));
    let origin: string | undefined;
    const find = (n: ts.Node) => {
      if (origin) return;
      if (ts.isTypeReferenceNode(n)) {
        const d = this.ctx.resolveSymbol(n.typeName)?.declarations?.[0];
        const k = d && (ts.isInterfaceDeclaration(d) || ts.isTypeAliasDeclaration(d) || ts.isClassDeclaration(d)) && this.ctx.isProjectFile(d.getSourceFile().fileName) ? this.ctx.keyOf(d, "type") : undefined;
        if (k && this.ctx.b.hasNode(k)) origin = k;
      }
      ts.forEachChild(n, find);
    };
    if (typeNode) find(typeNode);
    const out: Record<string, unknown> = { ref, type };
    if (origin) out.origin = origin;
    // A minor-unit word in a parameter name is a hint, never proof: partial.
    const unit = tokens((p.name as ts.Identifier).text).find((t) => MINOR_UNITS.has(t));
    if (unit) out.unit = this.attr("partial", unit, [this.ctx.ev(p)]);
    return out;
  }

  private derivedOutputUnit(inputs: Record<string, unknown>[]) {
    const withUnit = inputs.filter((i) => i.unit);
    return withUnit.length === 1 && !this.meta.aggs.length ? (withUnit[0]!.unit as Record<string, unknown>) : undefined;
  }

  /** "... in SEK" in the doc comment of the function: the only source of a known unit. */
  private docUnit(): { unit: string; evidence: string } | undefined {
    const sf = this.decl.getSourceFile();
    const host = ts.isVariableDeclaration(this.decl) ? this.decl.parent.parent : this.decl;
    for (const doc of ts.getJSDocCommentsAndTags(host)) {
      if (!ts.isJSDoc(doc)) continue;
      const text = sf.text.slice(doc.getStart(sf), doc.end);
      const m = /\bin ([A-Z]{3})\b/.exec(text);
      if (m) return { unit: m[1]!, evidence: this.ctx.textEv(this.ctx.rel(sf.fileName), sf.text, doc.getStart(sf) + m.index, m[0].length) };
    }
    return undefined;
  }

  attr(status: "known" | "partial", value: string, evidence: string[], detail?: Record<string, string | number>) {
    return { status, value, ...(detail ? { detail } : {}), evidence };
  }

  // ------------------------------------------------------------------ statements

  private block(statements: readonly ts.Statement[]): Expr {
    for (let i = 0; i < statements.length; i++) {
      const s = statements[i]!;
      if (ts.isReturnStatement(s)) {
        if (!s.expression) throw new Unsupported("return without value");
        return this.expr(s.expression);
      }
      if (ts.isIfStatement(s)) {
        const test = this.expr(s.expression);
        const thenBranch = this.branch(s.thenStatement);
        if (s.elseStatement) return this.mk(s, { op: "cond", test, then: thenBranch, else: this.branch(s.elseStatement) });
        if (!thenBranch) throw new Unsupported("if without return");
        return this.mk(s, { op: "cond", test, then: thenBranch, else: this.block(statements.slice(i + 1)) });
      }
      if (ts.isVariableStatement(s)) {
        for (const d of s.declarationList.declarations) {
          if (!ts.isIdentifier(d.name) || !d.initializer) throw new Unsupported("unsupported declaration");
          const sym = this.checker.getSymbolAtLocation(d.name)!;
          const isConst = !!(s.declarationList.flags & ts.NodeFlags.Const);
          // `let total = 0; for (...) total += ...`: the loop defines it.
          const loop = !isConst ? statements.slice(i + 1).find((x): x is ts.ForOfStatement => ts.isForOfStatement(x)) : undefined;
          if (loop && ts.isNumericLiteral(d.initializer) && Number(d.initializer.text) === 0) {
            this.cache.set(sym, this.loopSum(loop, sym));
            statements = statements.filter((x) => x !== loop);
            i = statements.indexOf(s);
          }
          // Plain locals are inlined at their use.
        }
        continue;
      }
      if (ts.isBlock(s)) return this.block(s.statements);
      throw new Unsupported(`statement ${ts.SyntaxKind[s.kind]}`);
    }
    throw new Unsupported("function does not end in a return");
  }

  private branch(s: ts.Statement): Expr {
    return this.block(ts.isBlock(s) ? s.statements : [s]);
  }

  /** `for (const row of rows) { const k = ...; if (c) continue; total += e; }` as a filtered sum. */
  private loopSum(loop: ts.ForOfStatement, acc: ts.Symbol): Expr {
    const decl = ts.isVariableDeclarationList(loop.initializer) ? loop.initializer.declarations[0] : undefined;
    if (!decl || !ts.isIdentifier(decl.name)) throw new Unsupported("loop variable");
    const seq = this.seqOf(loop.expression);
    if (seq.filters.length || seq.map) throw new Unsupported("loop over a derived collection");
    const bind = `$${this.next++}`;
    const rowSym = this.checker.getSymbolAtLocation(decl.name)!;
    this.env.set(rowSym, { op: "var", ref: bind });
    const body = ts.isBlock(loop.statement) ? loop.statement.statements : [loop.statement];
    const filters: Expr[] = [];
    let value: Expr | undefined;
    for (const s of body) {
      if (ts.isVariableStatement(s)) continue; // inlined where used
      if (ts.isIfStatement(s) && (ts.isContinueStatement(s.thenStatement) || (ts.isBlock(s.thenStatement) && s.thenStatement.statements.length === 1 && ts.isContinueStatement(s.thenStatement.statements[0]!))) && !s.elseStatement) {
        filters.push(this.negate(this.expr(s.expression), s.expression));
      } else if (ts.isExpressionStatement(s) && ts.isBinaryExpression(s.expression) && s.expression.operatorToken.kind === ts.SyntaxKind.PlusEqualsToken
        && ts.isIdentifier(s.expression.left) && this.checker.getSymbolAtLocation(s.expression.left) === acc && !value) {
        value = this.operand(s.expression.right);
      } else throw new Unsupported("loop body");
    }
    if (!value) throw new Unsupported("loop without accumulation");
    return this.agg("sum", seq.over, bind, loop, value, filters.length ? this.all(filters) : undefined);
  }

  private negate(e: Expr, node: ts.Node): Expr {
    if (e.op === "cmp") {
      const flip: Record<string, string> = { "===": "!==", "!==": "===", "==": "!=", "!=": "==", "<": ">=", ">": "<=", "<=": ">", ">=": "<" };
      return this.mk(node, { ...e, o: flip[e.o as string] });
    }
    return this.mk(node, { op: "logic", o: "not", args: [e] });
  }

  private all(es: Expr[]): Expr {
    return es.length === 1 ? es[0]! : { op: "logic", o: "and", args: es };
  }

  // ------------------------------------------------------------------ expressions

  private mk<T extends Expr>(node: ts.Node, e: T): T {
    this.src.set(e, node);
    return e;
  }

  srcOf(e: unknown): ts.Node | undefined {
    return this.src.get(e as object);
  }

  get aggs() { return this.meta.aggs; }
  get rounds() { return this.meta.rounds; }
  get paramName() { return this.paramNames; }
  get paramNode() { return this.paramNodes; }
  get context() { return this.ctx; }
  get fnNode() { return this.fn; }
  get decl_() { return this.decl; }

  /** An operand of arithmetic: members, lookups and calls that the checker types as number carry float64. */
  private operand(node: ts.Expression): Expr {
    const e = this.expr(node);
    if (NUMERIC_CONTEXT.has(e.op) && !e.type && this.checker.getTypeAtLocation(node).flags & ts.TypeFlags.NumberLike) {
      const typed = { ...e, type: FLOAT };
      const origin = this.src.get(e);
      if (origin) this.src.set(typed, origin);
      return typed;
    }
    return e;
  }

  private expr(node: ts.Expression): Expr {
    const e = strip(node);
    if (ts.isNumericLiteral(e)) return this.mk(e, { op: "lit", type: FLOAT, value: Number(e.text.replace(/_/g, "")) });
    if (ts.isPrefixUnaryExpression(e) && e.operator === ts.SyntaxKind.MinusToken && ts.isNumericLiteral(e.operand)) return this.mk(e, { op: "lit", type: FLOAT, value: -Number(e.operand.text.replace(/_/g, "")) });
    if (e.kind === ts.SyntaxKind.NullKeyword) return this.mk(e, { op: "lit", value: null });
    if (e.kind === ts.SyntaxKind.TrueKeyword || e.kind === ts.SyntaxKind.FalseKeyword) return this.mk(e, { op: "lit", value: e.kind === ts.SyntaxKind.TrueKeyword });
    if (ts.isStringLiteralLike(e)) return this.mk(e, { op: "lit", value: e.text });
    if (ts.isIdentifier(e)) return this.identifier(e);
    if (ts.isConditionalExpression(e)) return this.mk(e, { op: "cond", test: this.expr(e.condition), then: this.expr(e.whenTrue), else: this.expr(e.whenFalse) });
    if (ts.isBinaryExpression(e)) return this.binary(e);
    if (ts.isPropertyAccessExpression(e)) return this.property(e);
    if (ts.isElementAccessExpression(e)) return this.mk(e, { op: "index", of: this.expr(e.expression), key: this.expr(e.argumentExpression) });
    if (ts.isCallExpression(e)) return this.call(e);
    throw new Unsupported(`expression ${ts.SyntaxKind[e.kind]}`);
  }

  private identifier(id: ts.Identifier): Expr {
    if (id.text === "undefined") return this.mk(id, { op: "opaque", reason: "undefined" });
    const sym = this.checker.getSymbolAtLocation(id);
    if (!sym) throw new Unsupported(`unresolved ${id.text}`);
    const bound = this.env.get(sym);
    if (bound) return bound;
    const cached = this.cache.get(sym);
    if (cached) return cached;
    const d = sym.valueDeclaration;
    if (d && ts.isVariableDeclaration(d) && d.initializer) {
      const out = this.expr(d.initializer);
      // Aggregations are built once so every use shares the same placeholder; plain values are re-read per use.
      if (out.op === "agg") this.cache.set(sym, out);
      return out;
    }
    throw new Unsupported(`unsupported reference ${id.text}`);
  }

  private binary(e: ts.BinaryExpression): Expr {
    const o = e.operatorToken.kind;
    const text = e.operatorToken.getText();
    if (["+", "-", "*", "/", "%"].includes(text)) return this.mk(e, { op: "bin", o: text, type: FLOAT, l: this.operand(e.left), r: this.operand(e.right) });
    if (["===", "!==", "==", "!=", "<", ">", "<=", ">="].includes(text)) return this.mk(e, { op: "cmp", o: text, l: this.expr(e.left), r: this.expr(e.right) });
    if (o === ts.SyntaxKind.AmpersandAmpersandToken || o === ts.SyntaxKind.BarBarToken) {
      return this.mk(e, { op: "logic", o: o === ts.SyntaxKind.BarBarToken ? "or" : "and", args: [this.expr(e.left), this.expr(e.right)] });
    }
    if (o === ts.SyntaxKind.QuestionQuestionToken) return this.mk(e, { op: "coalesce", args: [this.expr(e.left), this.expr(e.right)] });
    throw new Unsupported(`operator ${text}`);
  }

  private property(e: ts.PropertyAccessExpression): Expr {
    const name = e.name.text;
    if (name === "size" || name === "length") {
      const inner = strip(e.expression);
      // `new Set(rows.map(f)).size` (possibly through a local): distinct count. `rows.length`: count.
      const set = this.setOf(inner);
      if (name === "size" && set) {
        const seq = this.seqOf(set.arguments![0]!);
        if (!seq.map) throw new Unsupported("distinct count without a mapped value");
        const known = this.distinct.get(set);
        if (known) return known;
        const made = this.aggOver("countDistinct", seq, e, set);
        this.distinct.set(set, made);
        return made;
      }
      if (name === "length" && !this.isParamValue(inner)) return this.aggOver("count", this.seqOf(inner), e);
    }
    return this.mk(e, { op: "member", of: this.expr(e.expression), name });
  }

  private isParamValue(e: ts.Expression) {
    return ts.isIdentifier(e) && this.env.has(this.checker.getSymbolAtLocation(e)!) && !this.isSeqLocal(e);
  }

  private isSeqLocal(e: ts.Identifier) {
    const d = this.checker.getSymbolAtLocation(e)?.valueDeclaration;
    return !!d && ts.isVariableDeclaration(d);
  }

  private setOf(e: ts.Expression): ts.NewExpression | undefined {
    e = strip(e);
    if (ts.isIdentifier(e)) {
      const d = this.checker.getSymbolAtLocation(e)?.valueDeclaration;
      return d && ts.isVariableDeclaration(d) && d.initializer ? this.setOf(d.initializer) : undefined;
    }
    return ts.isNewExpression(e) && ts.isIdentifier(e.expression) && e.expression.text === "Set" && e.arguments?.length === 1 ? e : undefined;
  }

  private call(e: ts.CallExpression): Expr {
    const callee = strip(e.expression);
    // Math.round / trunc / floor / ceil
    if (ts.isPropertyAccessExpression(callee) && ts.isIdentifier(callee.expression) && callee.expression.text === "Math" && callee.name.text in ROUNDING_CALLS && e.arguments.length === 1) {
      return this.round(e, ROUNDING_CALLS[callee.name.text]!, this.expr(e.arguments[0]!));
    }
    // x.toFixed(d), and Number(x.toFixed(d)) / parseFloat(...) around it
    if (ts.isPropertyAccessExpression(callee) && callee.name.text === "toFixed" && e.arguments.length === 1) {
      const digits = strip(e.arguments[0]!);
      if (!ts.isNumericLiteral(digits)) throw new Unsupported("toFixed with computed digits");
      return this.round(e, "halfTowardPositiveInfinity", this.expr(callee.expression), { op: "lit", type: "int32", value: Number(digits.text) });
    }
    if (ts.isIdentifier(callee) && (callee.text === "Number" || callee.text === "parseFloat") && e.arguments.length === 1 && this.isToFixed(e.arguments[0]!)) return this.expr(e.arguments[0]!);
    if (ts.isPropertyAccessExpression(callee) && ["reduce", "filter", "map"].includes(callee.name.text)) return this.reduction(e, callee);

    let decl: ts.Declaration | undefined;
    try {
      decl = this.checker.getResolvedSignature(e)?.declaration;
    } catch {
      decl = undefined;
    }
    const target = decl && !ts.isJSDocSignature(decl) ? this.ctx.keyOf(decl) : undefined;
    if (!target) throw new Unsupported(`call ${callee.getText()} cannot be resolved`);
    if (!target.startsWith("ts|@ext|")) this.includes.add(target);
    return this.mk(e, { op: "call", target, args: e.arguments.map((a) => this.expr(a)) });
  }

  private isToFixed(a: ts.Expression) {
    const s = strip(a);
    return ts.isCallExpression(s) && ts.isPropertyAccessExpression(strip(s.expression)) && (strip(s.expression) as ts.PropertyAccessExpression).name.text === "toFixed";
  }

  private round(node: ts.CallExpression, mode: string, e: Expr, digits?: Expr): Expr {
    let decl: ts.Declaration | undefined;
    try {
      decl = this.checker.getResolvedSignature(node)?.declaration;
    } catch {
      decl = undefined;
    }
    const target = decl && !ts.isJSDocSignature(decl) ? this.ctx.keyOf(decl) : undefined;
    if (!target) throw new Unsupported("rounding function cannot be resolved");
    const out = this.mk(node, { op: "round", mode, e, ...(digits ? { digits } : {}), target });
    this.meta.rounds.push({ mode, node });
    return out;
  }

  // ------------------------------------------------------------------ collections

  /** `rows.filter(f).map(g)` / a local bound to one / a collection parameter. */
  private seqOf(node: ts.Expression): Seq {
    const e = strip(node);
    if (ts.isIdentifier(e)) {
      const sym = this.checker.getSymbolAtLocation(e);
      const d = sym?.valueDeclaration;
      if (d && ts.isVariableDeclaration(d) && d.initializer && !(sym && this.env.has(sym))) return this.seqOf(d.initializer);
      if (sym && this.env.has(sym)) return { over: this.env.get(sym)!, filters: [] };
      throw new Unsupported(`collection ${e.text}`);
    }
    if (ts.isCallExpression(e) && ts.isPropertyAccessExpression(strip(e.expression))) {
      const callee = strip(e.expression) as ts.PropertyAccessExpression;
      const fn = e.arguments[0] && strip(e.arguments[0]);
      if ((callee.name.text === "filter" || callee.name.text === "map") && fn && ts.isArrowFunction(fn) && fn.parameters.length === 1) {
        const inner = this.seqOf(callee.expression);
        if (callee.name.text === "filter") {
          if (inner.map) throw new Unsupported("filter after map");
          return { ...inner, filters: [...inner.filters, fn] };
        }
        if (inner.map) throw new Unsupported("chained map");
        return { ...inner, map: fn };
      }
    }
    throw new Unsupported(`collection expression ${e.getText().slice(0, 40)}`);
  }

  private reduction(e: ts.CallExpression, callee: ts.PropertyAccessExpression): Expr {
    if (callee.name.text !== "reduce") throw new Unsupported(`${callee.name.text} outside an aggregation`);
    const [fn, init] = e.arguments.map((a) => strip(a));
    if (!fn || !init || !ts.isArrowFunction(fn) || fn.parameters.length !== 2 || !ts.isNumericLiteral(init) || Number(init.text) !== 0) throw new Unsupported("reduce is not a plain sum");
    const body = ts.isBlock(fn.body) ? undefined : strip(fn.body);
    const accSym = this.checker.getSymbolAtLocation(fn.parameters[0]!.name);
    if (!body || !ts.isBinaryExpression(body) || body.operatorToken.kind !== ts.SyntaxKind.PlusToken || !ts.isIdentifier(strip(body.left)) || this.checker.getSymbolAtLocation(strip(body.left)) !== accSym) {
      throw new Unsupported("reduce is not a plain sum");
    }
    const seq = this.seqOf(callee.expression);
    if (seq.map) throw new Unsupported("reduce after map");
    const bind = `$${this.next++}`;
    const row = this.checker.getSymbolAtLocation(fn.parameters[1]!.name)!;
    this.env.set(row, { op: "var", ref: bind });
    const value = this.operand(body.right);
    return this.agg("sum", seq.over, bind, e, value, this.seqFilter(seq, bind));
  }

  private seqFilter(seq: Seq, bind: string): Expr | undefined {
    const fs = seq.filters.map((f) => {
      this.env.set(this.checker.getSymbolAtLocation(f.parameters[0]!.name)!, { op: "var", ref: bind });
      if (ts.isBlock(f.body)) throw new Unsupported("filter with a block body");
      return this.expr(f.body);
    });
    return fs.length ? this.all(fs) : undefined;
  }

  private aggOver(fn: "count" | "countDistinct", seq: Seq, node: ts.Node, constructed?: ts.Node): Expr {
    const bind = `$${this.next++}`;
    const filter = this.seqFilter(seq, bind);
    let value: Expr | undefined;
    if (seq.map) {
      this.env.set(this.checker.getSymbolAtLocation(seq.map.parameters[0]!.name)!, { op: "var", ref: bind });
      if (ts.isBlock(seq.map.body)) throw new Unsupported("map with a block body");
      value = this.expr(seq.map.body);
    }
    return this.agg(fn, seq.over, bind, constructed ?? node, value, filter);
  }

  private agg(fn: string, over: Expr, bind: string, node: ts.Node, value?: Expr, filter?: Expr): Expr {
    const out = this.mk(node, { op: "agg", fn, over, bind, ...(value ? { value } : {}), ...(filter ? { filter } : {}), type: FLOAT });
    this.meta.aggs.push({ fn, node, expr: out });
    return out;
  }
}

// ---------------------------------------------------------------------- attributes

/** The domain attributes, read off the IR. Evidence is the source of the IR node each one rests on. */
class Attributes {
  constructor(private readonly b: Builder, private readonly expr: Expr, private readonly doc: { unit: string; evidence: string } | undefined) {}

  private get ctx() { return this.b.context; }
  private header() { return [this.ctx.headerEv(this.b.decl_)]; }
  private ev(e: unknown): string[] {
    const n = this.b.srcOf(e);
    return n ? [this.ctx.ev(n)] : this.header();
  }
  private known(value: string, evidence: string[], detail?: Record<string, string | number>) { return this.b.attr("known", value, evidence, detail); }
  private partial(value: string, evidence: string[], detail?: Record<string, string | number>) { return this.b.attr("partial", value, evidence, detail); }

  compute(): Record<string, unknown> {
    const out: Record<string, unknown> = {};
    const aggs = this.b.aggs;
    const valueAgg = aggs.find((a) => a.fn !== "count" && a.fn !== "countDistinct") ?? aggs[0];
    out.aggregation = valueAgg ? this.known(valueAgg.fn, [this.ctx.ev(valueAgg.node)]) : this.known("none", this.header());

    const periodParam = [...this.b.paramName].find(([, n]) => PERIOD_UNITS.has(n.toLowerCase()));
    const bucketCall = this.find((e) => e.op === "call" && /week/i.test(String(e.target).split("|").pop() ?? ""));
    out.timeBucket = bucketCall ? this.known("isoWeek", this.ev(bucketCall), { source: "bucketing function call" })
      : periodParam && valueAgg ? this.partial("suppliedByCaller", [this.ctx.ev(this.b.paramNode.get(periodParam[0])!)], { note: `${periodParam[1]} is a parameter` })
      : this.known("none", this.header());
    if (valueAgg) {
      out.period = periodParam ? this.partial("suppliedByCaller", [this.ctx.ev(this.b.paramNode.get(periodParam[0])!)])
        : this.known("none", [this.ctx.ev(valueAgg.node)], { description: "no period parameter; all supplied rows count" });
    }

    const divisions = this.divisions();
    const dataDivisor = divisions.find((d) => this.contains(d.r, (e) => e.op === "agg"));
    const paramDivisor = divisions.find((d) => d.r.op === "var");
    out.denominatorSource = dataDivisor ? this.known("data", this.ev(dataDivisor.r))
      : paramDivisor ? this.known("parameter", this.ev(paramDivisor.r))
      : divisions.length ? this.known("constant", this.ev(divisions[0]!.r))
      : this.known("none", this.header());

    const filters = this.filters();
    if (filters) out.filters = filters;

    const round = this.find((e) => e.op === "round");
    out.rounding = round ? this.known(String(round.mode), this.ev(round), round.digits ? { digits: (round.digits as { value: number }).value } : undefined) : this.known("none", this.header());
    out.numericType = this.known(FLOAT, this.header());
    out.nullHandling = this.nullHandling();
    out.zeroDenominator = this.zeroDenominator(divisions.filter((d) => d.r.op !== "lit"));
    out.currency = this.currency();
    return out;
  }

  // -- IR walking

  private find(pred: (e: Expr) => boolean, from: unknown = this.expr): Expr | undefined {
    let hit: Expr | undefined;
    const walk = (v: unknown) => {
      if (hit || !v || typeof v !== "object") return;
      if (Array.isArray(v)) return void v.forEach(walk);
      const e = v as Expr;
      if (typeof e.op === "string" && pred(e)) return void (hit = e);
      for (const x of Object.values(e)) walk(x);
    };
    walk(from);
    return hit;
  }

  private contains(from: unknown, pred: (e: Expr) => boolean) { return !!this.find(pred, from); }

  private divisions(): { node: Expr; r: Expr }[] {
    const out: { node: Expr; r: Expr }[] = [];
    const walk = (v: unknown) => {
      if (!v || typeof v !== "object") return;
      if (Array.isArray(v)) return void v.forEach(walk);
      const e = v as Expr;
      if (e.op === "bin" && e.o === "/") out.push({ node: e, r: e.r as Expr });
      for (const x of Object.values(e)) walk(x);
    };
    walk(this.expr);
    // Dividing by a rounding scale (x * 100 ... / 100) or a unit factor says nothing about the denominator.
    const nonLiteral = out.filter((d) => d.r.op !== "lit");
    return nonLiteral.length ? nonLiteral : out;
  }

  // -- filters

  private text(e: Expr, bind?: string): string {
    switch (e.op) {
      case "lit": return e.value === null ? "null" : String(e.value);
      case "opaque": return String(e.reason);
      case "var": return this.b.paramName.get(String(e.ref)) ?? "";
      case "member": return (e.of as Expr).op === "var" && (e.of as Expr).ref === bind ? String(e.name) : `${this.text(e.of as Expr, bind)}.${e.name}`;
      case "index": return `${this.text(e.of as Expr, bind)}[${this.text(e.key as Expr, bind)}]`;
      case "cmp": return `${this.text(e.l as Expr, bind)} ${e.o} ${this.text(e.r as Expr, bind)}`;
      case "logic": return (e.args as Expr[]).map((a) => this.text(a, bind)).join(e.o === "and" ? " and " : e.o === "or" ? " or " : ", ");
      case "bin": {
        const side = (x: Expr) => (x.op === "bin" && /[*/]/.test(String(e.o)) && /[+-]/.test(String(x.o)) ? `(${this.text(x, bind)})` : this.text(x, bind));
        return `${side(e.l as Expr)} ${e.o} ${side(e.r as Expr)}`;
      }
      case "call": return `${String(e.target).split("|").pop()!.split(".").pop()!.replace(/#.*/, "")}(${(e.args as Expr[]).map((a) => this.text(a, bind)).join(", ")})`;
      default: return e.op;
    }
  }

  private clamp(e: Expr): { x: Expr; lo: Expr; hi: Expr } | undefined {
    const fn = (c: Expr) => (c.op === "call" ? String(c.target).split("|").pop()!.replace(/#.*/, "") : "");
    if (e.op !== "call" || !["Math.min", "Math.max"].includes(fn(e))) return undefined;
    const [a, b] = e.args as Expr[];
    const [outer, inner, other] = a && fn(a) && fn(a) !== fn(e) ? [e, a, b] : b && fn(b) && fn(b) !== fn(e) ? [e, b, a] : [];
    if (!outer || !inner || !other) return undefined;
    const [x, bound] = inner.args as Expr[];
    const [lo, hi] = fn(outer) === "Math.min" ? [bound, other] : [other, bound];
    return x && lo && hi ? { x, lo, hi } : undefined;
  }

  private filters() {
    const parts: string[] = [];
    const evidence: string[] = [];
    const flt = this.find((e) => e.op === "agg" && !!e.filter);
    if (flt) {
      const filter = flt.filter as Expr;
      const rate = this.rateLookup(filter);
      parts.push(rate ? `${rate.key} has a known ${this.doc?.unit ?? "target currency"} rate` : this.text(filter, String(flt.bind)));
      evidence.push(...this.ev(filter));
    }
    const walk = (v: unknown) => {
      if (!v || typeof v !== "object") return;
      if (Array.isArray(v)) return void v.forEach(walk);
      const e = v as Expr;
      const arms = e.op === "bin" ? [e.l as Expr, e.r as Expr] : [];
      const idx = arms.findIndex((a) => this.clamp(a));
      if (idx >= 0) {
        const c = this.clamp(arms[idx]!)!;
        const other = arms[1 - idx]!;
        const base = `clamp(${this.text(c.x)}, ${this.text(c.lo)}, ${this.text(c.hi)})`;
        parts.push(other.op === "lit" ? (idx === 0 ? `${base} ${e.o} ${this.text(other)}` : `${this.text(other)} ${e.o} ${base}`) : base);
        evidence.push(...this.ev(arms[idx]));
        return;
      }
      if (this.clamp(e) && !parts.length) {
        const c = this.clamp(e)!;
        parts.push(`clamp(${this.text(c.x)}, ${this.text(c.lo)}, ${this.text(c.hi)})`);
        evidence.push(...this.ev(e));
        return;
      }
      for (const x of Object.values(e)) walk(x);
    };
    walk(this.expr);
    return parts.length ? this.known(parts.join(" and "), [...new Set(evidence)]) : undefined;
  }

  /** `rates[row.currency] !== undefined`: a lookup table keyed by a currency field. */
  private rateLookup(e: Expr): { key: string; node: Expr } | undefined {
    const hit = this.find((x) => x.op === "index" && (x.of as Expr).op === "var" && (x.key as Expr).op === "member" && /currency/i.test(String((x.key as Expr).name)), e);
    return hit ? { key: String((hit.key as Expr).name), node: hit } : undefined;
  }

  // -- the rest

  private nullHandling() {
    const check = this.find((e) => e.op === "cmp" && (e.o === "!==" || e.o === "!=") && (e.l as Expr).op === "member" && ((e.r as Expr).op === "opaque" || ((e.r as Expr).op === "lit" && (e.r as Expr).value === null)));
    if (check) return this.known("excluded", this.ev(check), { note: "rows whose field is null are filtered out" });
    const nullable = [...this.b.paramNode.values()].find((p) => {
      const t = ts.getEffectiveTypeAnnotationNode(p);
      return t && /\b(null|undefined)\b|\?/.test(t.getText());
    });
    return nullable ? this.partial("notChecked", [this.ctx.ev(nullable)]) : this.known("notApplicable", this.paramEvidence());
  }

  private paramEvidence() {
    const first = [...this.b.paramNode.values()][0];
    return first ? [this.ctx.ev(first)] : this.header();
  }

  private zeroDenominator(divisions: { node: Expr; r: Expr }[]) {
    if (!divisions.length) return this.known("notApplicable", this.header());
    for (const d of divisions) {
      const guard = this.find((e) => e.op === "cond" && this.mentions(e.test as Expr, d.r)
        && (this.isZero(e.then as Expr) || this.isZero(e.else as Expr)) && (this.contains(e.then, (x) => x === d.node) || this.contains(e.else, (x) => x === d.node)));
      if (guard) return this.known("returnsZero", this.ev(guard.test));
    }
    return this.known("unguarded", this.ev(divisions[0]!.node), { note: "JavaScript yields Infinity or NaN" });
  }

  private mentions(test: Expr, divisor: Expr) {
    return this.contains(test, (x) => eq(x, divisor));
  }

  private isZero(e: Expr) { return e.op === "lit" && e.value === 0; }

  private currency() {
    const lookup = this.rateLookup(this.expr);
    if (lookup) {
      return this.doc ? this.known(`convertedTo${this.doc.unit[0]}${this.doc.unit.slice(1).toLowerCase()}`, [...this.ev(lookup.node), this.doc.evidence], { unknownCurrencies: "dropped when no rate exists" })
        : this.partial("converted", this.ev(lookup.node));
    }
    const money = [...this.b.paramNode.values()].find((p) => tokens((p.name as ts.Identifier).text).some((t) => MONEY_WORDS.has(t)));
    return money ? this.partial("sameAsInput", [this.ctx.ev(money)], { note: "no conversion; currency is whatever the caller supplies" }) : { status: "unknown" };
  }
}
