import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import ts from "typescript";
import type { ToolRef } from "../contracts/records.ts";
import type { Props } from "./builder.ts";
import { isFunctionInit, type Ctx } from "./context.ts";
import { splitHttpPath } from "./http.ts";
import { postgresStore, serviceBusNamespace } from "./resource-identities.ts";

export const EXPRESS = { name: "express.routes", version: "1" };
export const AXIOS = { name: "axios", version: "1" };
export const OPENAPI = { name: "openapi.client", version: "1" };
export const FETCH = { name: "web.fetch", version: "1" };
export const PG = { name: "pg.sql", version: "1" };
export const SB = { name: "azure.servicebus", version: "1" };

export const HTTP_METHODS = ["get", "post", "put", "patch", "delete", "options", "head", "all"];

// ------------------------------------------------------------------ shared helpers

/** Parameters become {}, no query string, no trailing slash, lower case (matches the C# rules). */
export function normaliseRoute(route: string): string {
  let r = route.split("?")[0]!.split("#")[0]!;
  r = r.replace(/:[A-Za-z_]\w*(\([^)]*\))?\??/g, "{}").replace(/\{[^}]*\}/g, "{}");
  r = ("/" + r).replace(/\/+/g, "/");
  if (r.length > 1) r = r.replace(/\/$/, "");
  return r.toLowerCase();
}

export const joinRoute = (a: string, b: string) => ("/" + [a, b].map((x) => x.replace(/^\/+|\/+$/g, "")).filter(Boolean).join("/")).replace(/\/+/g, "/");

export const unwrap = (e: ts.Expression): ts.Expression => {
  while (ts.isParenthesizedExpression(e) || ts.isAsExpression(e) || ts.isNonNullExpression(e) || ts.isSatisfiesExpression(e) || ts.isAwaitExpression(e)) e = e.expression;
  return e;
};

/** Retain simple reference names, never arbitrary expression text containing source literals. */
function referenceLabel(expression: ts.Expression): string | undefined {
  const e = unwrap(expression);
  if (ts.isIdentifier(e)) return e.text;
  if (ts.isPropertyAccessExpression(e)) {
    const parent = referenceLabel(e.expression);
    return parent ? `${parent}.${e.name.text}` : undefined;
  }
  return undefined;
}

/** `process.env.NAME` / `process.env["NAME"]`, optionally with `?? "fallback"`. */
export function envVar(e: ts.Expression | undefined): { name: string; fallback?: string; node: ts.Expression } | undefined {
  if (!e) return undefined;
  e = unwrap(e);
  if (ts.isBinaryExpression(e) && (e.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken || e.operatorToken.kind === ts.SyntaxKind.BarBarToken)) {
    const inner = envVar(e.left);
    return inner && { ...inner, fallback: ts.isStringLiteralLike(e.right) ? e.right.text : undefined };
  }
  const isEnv = (x: ts.Expression) => ts.isPropertyAccessExpression(x) && x.name.text === "env" && ts.isIdentifier(x.expression) && x.expression.text === "process";
  if (ts.isPropertyAccessExpression(e) && isEnv(e.expression)) return { name: e.name.text, node: e };
  if (ts.isElementAccessExpression(e) && isEnv(e.expression) && ts.isStringLiteralLike(e.argumentExpression)) return { name: e.argumentExpression.text, node: e };
  return undefined;
}

/** `config.a.b` where `config` is an imported JSON file: the literal value and where it is written. */
export function jsonValue(ctx: Ctx, e: ts.Expression): { value: unknown; path: string; evidence: string } | undefined {
  const chain: string[] = [];
  let x = unwrap(e);
  while (ts.isPropertyAccessExpression(x)) {
    chain.unshift(x.name.text);
    x = x.expression;
  }
  if (!ts.isIdentifier(x) || chain.length === 0) return undefined;
  const decl = ctx.checker.getSymbolAtLocation(x)?.declarations?.[0];
  const imp = decl && (ts.isImportClause(decl) ? decl.parent : undefined);
  if (!imp || !ts.isStringLiteral(imp.moduleSpecifier) || !imp.moduleSpecifier.text.endsWith(".json")) return undefined;
  const file = resolve(dirname(x.getSourceFile().fileName), imp.moduleSpecifier.text);
  if (!existsSync(file)) return undefined;
  const text = readFileSync(file, "utf8").replace(/\r\n/g, "\n");
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return undefined;
  }
  for (const k of chain) value = value && typeof value === "object" ? (value as Record<string, unknown>)[k] : undefined;
  if (value === undefined || typeof value === "object") return undefined;
  const path = ctx.rel(file);
  const last = chain.at(-1)!;
  const m = new RegExp(`"${last.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}"\\s*:\\s*${JSON.stringify(value).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`).exec(text);
  const evidence = m ? ctx.textEv(path, text, m.index, m[0].length) : ctx.textEv(path, text, 0, 1);
  return { value, path, evidence };
}

/** Literal string, JSON config value, or environment variable. */
export type Resolved = { kind: "literal"; value: string; evidence?: string; source?: string } | { kind: "env"; name: string; fallback?: string; evidence: string } | { kind: "unknown" };

export function resolveValue(ctx: Ctx, e: ts.Expression | undefined): Resolved {
  if (!e) return { kind: "unknown" };
  const x = unwrap(e);
  if (ts.isStringLiteralLike(x)) return { kind: "literal", value: x.text, evidence: ctx.ev(x) };
  const env = envVar(x);
  if (env) return { kind: "env", name: env.name, fallback: env.fallback, evidence: ctx.ev(env.node) };
  const json = jsonValue(ctx, x);
  if (json && typeof json.value === "string") return { kind: "literal", value: json.value, evidence: json.evidence, source: json.path };
  if (ts.isIdentifier(x)) {
    const init = ctx.constInit(x);
    if (init) return resolveValue(ctx, init);
  }
  return { kind: "unknown" };
}

/** Imports of a package in a file: default/namespace names and named imports (local name → imported name). */
export function importsOf(sf: ts.SourceFile, pkg: string) {
  const names = new Set<string>();
  const named = new Map<string, string>();
  for (const s of sf.statements) {
    if (!ts.isImportDeclaration(s) || !ts.isStringLiteral(s.moduleSpecifier) || s.moduleSpecifier.text !== pkg || !s.importClause) continue;
    if (s.importClause.name) names.add(s.importClause.name.text);
    const b = s.importClause.namedBindings;
    if (b && ts.isNamespaceImport(b)) names.add(b.name.text);
    if (b && ts.isNamedImports(b)) for (const el of b.elements) named.set(el.name.text, (el.propertyName ?? el.name).text);
  }
  return { names, named };
}

export function forEachNode(sf: ts.SourceFile, f: (n: ts.Node) => void) {
  const visit = (n: ts.Node) => {
    f(n);
    ts.forEachChild(n, visit);
  };
  ts.forEachChild(sf, visit);
}

/** Symbol of the variable an expression is assigned to (`const x = <e>`). */
export function assignedSymbol(ctx: Ctx, e: ts.Node): ts.Symbol | undefined {
  let p = e.parent;
  while (p && (ts.isParenthesizedExpression(p) || ts.isAsExpression(p) || ts.isAwaitExpression(p))) p = p.parent;
  if (p && ts.isVariableDeclaration(p) && ts.isIdentifier(p.name)) return ctx.checker.getSymbolAtLocation(p.name);
  if (p && ts.isPropertyDeclaration(p)) return ctx.checker.getSymbolAtLocation(p.name);
  return undefined;
}

/** The variable symbol an expression names, through import aliases and `export { a as b }`. */
export function symbolOf(ctx: Ctx, e: ts.Expression): ts.Symbol | undefined {
  const x = unwrap(e);
  if (ts.isIdentifier(x)) return ctx.resolveSymbol(x);
  if (ts.isPropertyAccessExpression(x) && x.expression.kind === ts.SyntaxKind.ThisKeyword) return ctx.checker.getSymbolAtLocation(x.name);
  return undefined;
}

export function typeArgKey(ctx: Ctx, args: ts.NodeArray<ts.TypeNode> | undefined): string | undefined {
  const t = args?.[0];
  if (!t || !ts.isTypeReferenceNode(t)) return undefined;
  const decl = ctx.resolveSymbol(t.typeName)?.declarations?.[0];
  return decl && ctx.isProjectFile(decl.getSourceFile().fileName) ? ctx.keyOf(decl, "type") : undefined;
}

/** `Promise<T>` return annotation of the enclosing function, when T is a project type. */
export function responseType(ctx: Ctx, node: ts.Node): string | undefined {
  for (let p: ts.Node | undefined = node.parent; p; p = p.parent) {
    if (ts.isFunctionLike(p) && p.type) {
      const t = p.type;
      if (ts.isTypeReferenceNode(t) && t.typeName.getText() === "Promise") return typeArgKey(ctx, t.typeArguments);
      return undefined;
    }
  }
  return undefined;
}

/** Literal template: text with `{}` for substitutions; the leading substitution is reported separately as a base. */
export function urlTemplate(e: ts.Expression): { base?: ts.Expression; text: string; query: string[] } | undefined {
  e = unwrap(e);
  let parts: { text: string; expr?: ts.Expression }[];
  if (ts.isStringLiteralLike(e)) parts = [{ text: e.text }];
  else if (ts.isTemplateExpression(e)) {
    parts = [{ text: e.head.text }];
    for (const s of e.templateSpans) parts.push({ expr: s.expression, text: "" }, { text: s.literal.text });
  } else return undefined;
  let base: ts.Expression | undefined;
  if (parts[0]!.text === "" && parts[1]?.expr) {
    base = parts[1].expr;
    parts = parts.slice(2);
  }
  const text = parts.map((p) => (p.expr ? "{}" : p.text)).join("");
  const path = splitHttpPath(text);
  return { base, text: path.pathname, query: path.query };
}

// ------------------------------------------------------------------ express

interface RouterInfo { kind: "app" | "router"; mounts: { parent: ts.Symbol; prefix: string; evidence: string }[] }

export function express(ctx: Ctx, rules: Set<ToolRef>) {
  const service = `svc|${ctx.repo}|${ctx.pkg}`;
  const routers = new Map<ts.Symbol, RouterInfo>();
  const routes: { target: ts.Symbol; method: string; path: string; call: ts.CallExpression }[] = [];

  for (const sf of ctx.files) {
    const ex = importsOf(sf, "express");
    if (ex.names.size === 0 && ex.named.size === 0) continue;
    const isRouterFactory = (c: ts.CallExpression) => {
      const callee = c.expression;
      if (ts.isIdentifier(callee) && ex.names.has(callee.text)) return "app" as const;
      if (ts.isIdentifier(callee) && ex.named.get(callee.text) === "Router") return "router" as const;
      if (ts.isPropertyAccessExpression(callee) && callee.name.text === "Router" && ts.isIdentifier(callee.expression) && ex.names.has(callee.expression.text)) return "router" as const;
      return undefined;
    };
    forEachNode(sf, (n) => {
      if (ts.isCallExpression(n)) {
        const kind = isRouterFactory(n);
        const sym = kind && assignedSymbol(ctx, n);
        if (kind && sym) routers.set(sym, { kind, mounts: [] });
      }
    });
  }
  if (routers.size === 0) return;

  for (const sf of ctx.files) {
    forEachNode(sf, (n) => {
      if (!ts.isCallExpression(n) || !ts.isPropertyAccessExpression(n.expression)) return;
      const method = n.expression.name.text;
      let recv: ts.Expression = n.expression.expression;
      let path: string | undefined;
      // router.route("/x").get(handler)
      if (ts.isCallExpression(recv) && ts.isPropertyAccessExpression(recv.expression) && recv.expression.name.text === "route" && recv.arguments[0] && ts.isStringLiteralLike(recv.arguments[0])) {
        path = recv.arguments[0].text;
        recv = recv.expression.expression;
      }
      const target = symbolOf(ctx, recv);
      if (!target || !routers.has(target)) return;
      if (method === "use") {
        const [first, ...rest] = n.arguments;
        const prefix = first && ts.isStringLiteralLike(first) ? first.text : "";
        for (const arg of first && ts.isStringLiteralLike(first) ? rest : n.arguments) {
          const child = symbolOf(ctx, arg);
          if (child && routers.has(child)) routers.get(child)!.mounts.push({ parent: target, prefix, evidence: ctx.ev(n) });
        }
        return;
      }
      if (!HTTP_METHODS.includes(method)) return;
      if (path === undefined) {
        const first = n.arguments[0];
        if (!first || !ts.isStringLiteralLike(first)) return;
        path = first.text;
      }
      routes.push({ target, method: method === "all" ? "ALL" : method.toUpperCase(), path, call: n });
    });
  }

  // Every full prefix of a router, with the mount calls that prove it (outermost first).
  const prefixes = (sym: ts.Symbol, depth = 0): { prefix: string; chain: string[] }[] => {
    const info = routers.get(sym)!;
    if (info.kind === "app" || depth > 8) return [{ prefix: "", chain: [] }];
    if (info.mounts.length === 0) return [{ prefix: "", chain: [] }];
    return info.mounts.flatMap((m) => prefixes(m.parent, depth + 1).map((p) => ({ prefix: joinRoute(p.prefix, m.prefix), chain: [...p.chain, m.evidence] })));
  };

  let any = false;
  for (const r of routes) {
    for (const { prefix, chain } of prefixes(r.target)) {
      const route = joinRoute(prefix, r.path);
      const normalised = normaliseRoute(route);
      const endpoint = `http|${ctx.repo}/${ctx.pkg}|${r.method} ${normalised}`;
      const routeEv = ctx.ev(r.call);
      const direct = routers.get(r.target)!.kind === "app";
      ctx.b.node(endpoint, "Endpoint", `${r.method} ${route}`, [...chain, routeEv], {
        props: { method: r.method, route, normalizedRoute: normalised, style: direct ? "express-app" : "express-router", mountPath: prefix || undefined },
      });
      ctx.b.edge({ from: service, rel: "EXPOSES", to: endpoint, derivation: "rule", rule: EXPRESS, evidence: [chain[0] ?? routeEv] });
      const handler = r.call.arguments.at(-1);
      if (handler) {
        const lambda = isFunctionInit(unwrap(handler));
        const decl = lambda ? undefined : ctx.resolveSymbol(unwrap(handler))?.declarations?.[0];
        const from = lambda ? ctx.owner(handler) : decl ? ctx.keyOf(decl) : undefined;
        if (from) ctx.b.edge({ from, rel: "HANDLES", to: endpoint, derivation: "rule", rule: EXPRESS, props: lambda ? { handler: "lambda" } : undefined, evidence: [lambda ? ctx.ev(handler) : routeEv] });
      }
      any = true;
    }
  }
  if (any) rules.add(EXPRESS);
}

/** The Service node for a package that depends on a server framework. */
export function serviceNode(ctx: Ctx) {
  if (!ctx.unit.packageJson) return;
  const path = ctx.rel(ctx.unit.packageJson);
  const text = ctx.readRepoFile(path);
  const m = /"express"\s*:\s*"[^"]*"/.exec(text);
  if (!m) return;
  ctx.b.node(`svc|${ctx.repo}|${ctx.pkg}`, "Service", ctx.pkg, [ctx.textEv(path, text, m.index, m[0].length)], { props: { framework: "express" } });
}

export function projectNode(ctx: Ctx) {
  const unitPath = ctx.rel(ctx.unit.configFile);
  let evidence: string;
  if (ctx.unit.packageJson) {
    const path = ctx.rel(ctx.unit.packageJson);
    const text = ctx.readRepoFile(path);
    const m = /"name"\s*:\s*"[^"]*"/.exec(text);
    evidence = m ? ctx.textEv(path, text, m.index, m[0].length) : ctx.textEv(path, text, 0, 1);
  } else {
    const text = ctx.readRepoFile(unitPath);
    evidence = ctx.textEv(unitPath, text, 0, Math.min(1, text.length));
  }
  ctx.b.node(`proj|${ctx.repo}|${unitPath}`, "Project", ctx.pkg, [evidence]);
}

// ------------------------------------------------------------------ pg

const SQL_TABLE = String.raw`([A-Za-z_][\w$]*(?:\.[A-Za-z_][\w$]*)?)`;

export function pgRule(ctx: Ctx, rules: Set<ToolRef>) {
  const pools = new Map<ts.Symbol, { store: string; resolved: boolean; evidence?: string }>();
  for (const sf of ctx.files) {
    const pg = importsOf(sf, "pg");
    if (pg.names.size === 0 && pg.named.size === 0) continue;
    forEachNode(sf, (n) => {
      if (!ts.isNewExpression(n)) return;
      const e = n.expression;
      const ctor = ts.isIdentifier(e) ? pg.named.get(e.text) : ts.isPropertyAccessExpression(e) && ts.isIdentifier(e.expression) && pg.names.has(e.expression.text) ? e.name.text : undefined;
      if (ctor !== "Pool" && ctor !== "Client") return;
      const sym = assignedSymbol(ctx, n);
      if (!sym) return;
      const cfg = n.arguments?.[0] && unwrap(n.arguments[0]);
      const cs = cfg && ts.isObjectLiteralExpression(cfg) ? cfg.properties.find((p) => p.name?.getText() === "connectionString") : undefined;
      const value = cs && ts.isPropertyAssignment(cs) ? resolveValue(ctx, cs.initializer) : cfg ? resolveValue(ctx, cfg) : { kind: "env" as const, name: "PGDATABASE", evidence: ctx.ev(n) };
      if (value.kind === "env") pools.set(sym, { store: ctx.unbound("env", value.name), resolved: false, evidence: value.evidence });
      else if (value.kind === "literal") {
        const store = postgresStore(value.value);
        pools.set(sym, store ? { store: ctx.unbound("db", store), resolved: false, evidence: value.evidence } : { store: ctx.unbound("pool", sym.name), resolved: false });
        if (!store) {
          ctx.b.diagnostic("warning", "PG_CONNECTION_UNSUPPORTED", "Database identity is unavailable from this connection literal; credentials and ambiguous paths are not copied.", ctx.rel(sf.fileName), true, value.evidence);
          ctx.b.markIncomplete(ctx.rel(sf.fileName));
        }
      } else pools.set(sym, { store: ctx.unbound("pool", sym.name), resolved: false });
    });
  }
  if (pools.size === 0) return;

  for (const sf of ctx.files) {
    forEachNode(sf, (n) => {
      if (!ts.isCallExpression(n) || !ts.isPropertyAccessExpression(n.expression) || n.expression.name.text !== "query") return;
      const pool = symbolOf(ctx, n.expression.expression);
      const info = pool && pools.get(pool);
      const from = info && ctx.owner(n);
      if (!info || !from) return;
      const sqlArg = n.arguments[0] && unwrap(n.arguments[0]);
      if (!sqlArg) return;
      let sqlNode: ts.Expression = sqlArg;
      let constant: string | undefined;
      if (ts.isIdentifier(sqlArg)) {
        const init = ctx.constInit(sqlArg);
        if (init) {
          constant = sqlArg.text;
          sqlNode = init;
        }
      }
      let sql: string;
      const dynamicParts: string[] = [];
      let allowed: string[] | undefined;
      if (ts.isStringLiteralLike(sqlNode)) sql = sqlNode.text;
      else if (ts.isTemplateExpression(sqlNode)) {
        sql = sqlNode.head.text;
        for (const span of sqlNode.templateSpans) {
          const kw = /((?:ORDER|GROUP)\s+BY|[A-Za-z_]+)\s*$/i.exec(sql)?.[0] ?? "";
          dynamicParts.push(`${kw}\${${referenceLabel(span.expression) ?? "<dynamic-expression>"}}`);
          const t = ctx.checker.getTypeAtLocation(span.expression);
          if (t.isUnion() && t.types.every((x) => x.isStringLiteral())) allowed = t.types.map((x) => (x as ts.StringLiteralType).value);
          sql += `__dyn__${span.literal.text}`;
        }
      } else return;
      const statements = parseSql(sql);
      if (statements.length === 0) return;
      rules.add(PG);
      const evidence = [ctx.ev(sqlNode), ...(info.evidence ? [info.evidence] : [])];
      for (const s of statements) {
        const table = s.table.includes(".") ? s.table : `public.${s.table}`;
        ctx.b.edge({
          from, rel: s.write ? "WRITES" : "READS", to: `sql|${info.store}|${table}`, derivation: "rule", rule: PG,
          resolution: info.resolved ? "resolved" : "unresolved", reason: info.resolved ? undefined : "configuration",
          props: {
            columns: s.columns, sqlConstant: constant, sqlAnalysis: dynamicParts.length ? "partial" : "full",
            dynamicParts: dynamicParts.length ? dynamicParts : undefined, allowedValues: allowed,
          },
          evidence,
        });
      }
    });
  }
}

/** Tables a statement reads and writes, by keyword; enough for the common single-statement queries. */
export function parseSql(sql: string): { table: string; write: boolean; columns?: string[] }[] {
  const out: { table: string; write: boolean; columns?: string[] }[] = [];
  const seen = new Set<string>();
  const add = (table: string, write: boolean, columns?: string[]) => {
    if (table.startsWith("__dyn__") || seen.has(`${table}|${write}`)) return;
    seen.add(`${table}|${write}`);
    out.push({ table, write, columns });
  };
  const ins = new RegExp(String.raw`\bINSERT\s+INTO\s+${SQL_TABLE}\s*(?:\(([^)]*)\))?`, "gi");
  for (const m of sql.matchAll(ins)) add(m[1]!, true, m[2]?.split(",").map((c) => c.trim()).filter(Boolean));
  for (const m of sql.matchAll(new RegExp(String.raw`\bUPDATE\s+${SQL_TABLE}`, "gi"))) add(m[1]!, true);
  for (const m of sql.matchAll(new RegExp(String.raw`\bDELETE\s+FROM\s+${SQL_TABLE}`, "gi"))) add(m[1]!, true);
  const select = /\bSELECT\s+([\s\S]+?)\s+FROM\b/i.exec(sql);
  const columns = select && /^[\w\s,."]+$/.test(select[1]!) && select[1] !== "*" ? select[1]!.split(",").map((c) => c.trim().split(/\s+/)[0]!.split(".").at(-1)!) : undefined;
  for (const m of sql.matchAll(new RegExp(String.raw`(?<!DELETE\s)\b(?:FROM|JOIN)\s+${SQL_TABLE}`, "gi"))) add(m[1]!, false, columns);
  return out;
}

// ------------------------------------------------------------------ Azure Service Bus

export function serviceBusRule(ctx: Ctx, rules: Set<ToolRef>) {
  const clients = new Map<ts.Symbol, { ns: Resolved; evidence: string; credential?: string }>();
  for (const sf of ctx.files) {
    const sb = importsOf(sf, "@azure/service-bus");
    if (sb.named.size === 0 && sb.names.size === 0) continue;
    forEachNode(sf, (n) => {
      if (!ts.isNewExpression(n) || !ts.isIdentifier(n.expression) || sb.named.get(n.expression.text) !== "ServiceBusClient") return;
      const sym = assignedSymbol(ctx, n);
      if (!sym) return;
      const ns = resolveValue(ctx, n.arguments?.[0]);
      const cred = n.arguments?.[1] && unwrap(n.arguments[1]);
      clients.set(sym, { ns, evidence: ctx.ev(n), credential: cred && ts.isNewExpression(cred) ? referenceLabel(cred.expression) : undefined });
    });
  }
  for (const sf of ctx.files) {
    forEachNode(sf, (n) => {
      if (!ts.isCallExpression(n) || !ts.isPropertyAccessExpression(n.expression)) return;
      const op = n.expression.name.text;
      if (op !== "createReceiver" && op !== "createSender") return;
      const client = symbolOf(ctx, n.expression.expression);
      const info = client && clients.get(client);
      const from = info && ctx.owner(n);
      if (!info || !from) return;
      rules.add(SB);
      const entity = resolveValue(ctx, n.arguments[0]);
      const subscription = op === "createReceiver" && n.arguments.length > 1 && !ts.isObjectLiteralExpression(unwrap(n.arguments[1]!));
      const entityType = op === "createSender" ? "entity" : subscription ? "topic" : "queue";
      const rel = op === "createSender" ? "PUBLISHES" : "CONSUMES";
      const nsRef = info.ns.kind === "literal" ? serviceBusNamespace(info.ns.value) : info.ns.kind === "env" ? ctx.unbound("env", info.ns.name) : undefined;
      const props: Props = { configSource: info.ns.kind === "literal" ? info.ns.source : undefined, credential: info.credential };
      if (!nsRef || entity.kind === "unknown") {
        if (!nsRef && info.ns.kind === "literal") {
          ctx.b.diagnostic("warning", "SERVICEBUS_NAMESPACE_UNSUPPORTED", "Namespace identity is unavailable from this connection literal; credentials and malformed values are not copied.", ctx.rel(sf.fileName), true, info.evidence);
          ctx.b.markIncomplete(ctx.rel(sf.fileName));
        }
        ctx.b.edge({ from, rel, derivation: "rule", rule: SB, resolution: "unresolved", reason: "configuration", props, evidence: [ctx.ev(n), info.evidence] });
        return;
      }
      const name = entity.kind === "literal" ? entity.value : `{${entity.name}}`;
      const channel = `chan|servicebus|${nsRef}|${entityType}/${name}`;
      const resolved = info.ns.kind === "literal" && entity.kind === "literal";
      const evidence = [info.ns.kind === "literal" ? info.ns.evidence : undefined, entity.kind === "literal" ? entity.evidence : undefined].filter((e): e is string => !!e);
      ctx.b.node(channel, "Channel", name, evidence.length ? evidence : [ctx.ev(n)], {
        props: { provider: "azure-servicebus", entityType: entityType === "entity" ? "queue-or-topic" : entityType, configSource: props.configSource },
      });
      ctx.b.edge({
        from, rel, to: channel, derivation: "rule", rule: SB, resolution: resolved ? "resolved" : "unresolved", reason: resolved ? undefined : "configuration",
        props, evidence: [ctx.ev(n), info.evidence],
      });
    });
  }
}
