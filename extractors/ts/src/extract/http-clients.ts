import ts from "typescript";
import type { ToolRef } from "../contracts/records.ts";
import type { Props } from "./builder.ts";
import type { Ctx } from "./context.ts";
import { absoluteHttp, canonicalHttpPath, hasAuthorityOrScheme, splitHttpPath } from "./http.ts";
import { AXIOS, FETCH, OPENAPI, HTTP_METHODS, assignedSymbol, envVar, forEachNode, importsOf, joinRoute, normaliseRoute, resolveValue, responseType, symbolOf, typeArgKey, unwrap, urlTemplate, type Resolved } from "./rules.ts";

// ------------------------------------------------------------------ HTTP clients

function requestTarget(ctx: Ctx, base: Resolved | { kind: "variable"; name: string; key?: string }, method: string, route: string) {
  let store: string;
  const props: Props = {};
  const absolute = absoluteHttp(route);
  if (absolute) {
    store = ctx.unbound("host", absolute.host);
    props.baseUrl = absolute.origin;
    props.scheme = absolute.scheme;
    route = absolute.pathname;
  } else if (hasAuthorityOrScheme(route)) return undefined;
  else if (base.kind === "env") {
    store = ctx.unbound("env", base.name);
    props.baseUrlEnv = base.name;
    if (base.fallback) {
      const fallback = absoluteHttp(base.fallback);
      if (fallback) props.defaultBaseUrl = fallback.origin + fallback.pathname;
    }
  } else if (base.kind === "variable") {
    store = ctx.unbound("base", base.name);
    props.baseUrl = base.key;
  } else if (base.kind === "literal" && hasAuthorityOrScheme(base.value)) {
    const url = absoluteHttp(base.value);
    if (!url) return undefined;
    store = ctx.unbound("host", url.host);
    props.baseUrl = url.origin + (url.pathname === "/" ? "" : url.pathname);
    props.scheme = url.scheme;
    route = joinRoute(url.pathname, route);
  } else {
    store = ctx.unbound("base", "origin");
    if (base.kind === "literal") route = joinRoute(splitHttpPath(base.value).pathname, route);
  }
  const pathname = canonicalHttpPath(route);
  if (!pathname) return undefined;
  return { to: `http|${store}|${method} ${normaliseRoute(pathname)}`, normalised: normaliseRoute(pathname), props };
}

export function fetchRule(ctx: Ctx, rules: Set<ToolRef>) {
  for (const sf of ctx.files) {
    forEachNode(sf, (n) => {
      if (!ts.isCallExpression(n)) return;
      const callee = n.expression;
      const name = ts.isIdentifier(callee) ? callee.text : ts.isPropertyAccessExpression(callee) && ["globalThis", "window", "self"].includes(callee.expression.getText()) ? callee.name.text : undefined;
      if (name !== "fetch") return;
      const declared = ctx.resolveSymbol(ts.isIdentifier(callee) ? callee : (callee as ts.PropertyAccessExpression).name)?.declarations?.[0];
      if (declared && ctx.isProjectFile(declared.getSourceFile().fileName)) return; // a local function called fetch
      const from = ctx.owner(n);
      if (!from) return;
      const generated = ctx.generated.has(sf.fileName) || undefined;
      const init = n.arguments[1] && unwrap(n.arguments[1]);
      let method = "GET";
      if (init && ts.isObjectLiteralExpression(init)) {
        const m = init.properties.find((p) => p.name?.getText() === "method");
        if (m) method = ts.isPropertyAssignment(m) && ts.isStringLiteralLike(m.initializer) ? m.initializer.text.toUpperCase() : "dynamic";
      }
      const urlArg = n.arguments[0];
      const urlExpr = urlArg && (ts.isIdentifier(unwrap(urlArg)) ? ctx.constInit(unwrap(urlArg)) ?? urlArg : urlArg);
      const template = urlExpr && urlTemplate(urlExpr);
      const callEv = ctx.ev(n);
      rules.add(FETCH);
      if (!template || method === "dynamic") {
        ctx.b.edge({ from, rel: "REQUESTS", derivation: "rule", rule: FETCH, resolution: "unresolved", reason: "dynamic-string", props: { method, generated }, evidence: [callEv] });
        return;
      }
      let base: Parameters<typeof requestTarget>[1] = { kind: "unknown" };
      if (template.base) {
        const env = envVar(template.base);
        const decl = !env && ts.isIdentifier(template.base) ? ctx.resolveSymbol(template.base)?.declarations?.[0] : undefined;
        base = env ? { kind: "env", name: env.name, fallback: env.fallback, evidence: ctx.ev(env.node) } : decl && ts.isIdentifier(template.base) ? { kind: "variable", name: template.base.text, key: ctx.keyOf(decl) } : { kind: "unknown" };
        if (base.kind === "unknown") {
          ctx.b.edge({ from, rel: "REQUESTS", derivation: "rule", rule: FETCH, resolution: "unresolved", reason: "dynamic-string", props: { method, generated }, evidence: [ctx.ev(urlExpr), callEv] });
          return;
        }
      }
      const target = requestTarget(ctx, base, method, template.text);
      if (!target) {
        ctx.b.edge({ from, rel: "REQUESTS", derivation: "rule", rule: FETCH, resolution: "unresolved", reason: "dynamic-string", props: { method, generated }, evidence: [ctx.ev(urlExpr), callEv] });
        return;
      }
      ctx.b.edge({
        from, rel: "REQUESTS", to: target.to, derivation: "rule", rule: FETCH, resolution: "unresolved", reason: "configuration",
        props: { method, normalizedRoute: target.normalised, ...target.props, query: template.query.length ? template.query : undefined, responseType: responseType(ctx, n), generated },
        evidence: [ctx.ev(urlExpr), callEv],
      });
    });
  }
}

export function axiosRule(ctx: Ctx, rules: Set<ToolRef>) {
  const clients = new Map<ts.Symbol, { base: Resolved; evidence: string; allowAbsolute: boolean }>();
  for (const sf of ctx.files) {
    const ax = importsOf(sf, "axios");
    if (ax.names.size === 0) continue;
    forEachNode(sf, (n) => {
      if (!ts.isCallExpression(n) || !ts.isPropertyAccessExpression(n.expression) || n.expression.name.text !== "create") return;
      if (!ts.isIdentifier(n.expression.expression) || !ax.names.has(n.expression.expression.text)) return;
      const cfg = n.arguments[0] && unwrap(n.arguments[0]);
      const baseProp = cfg && ts.isObjectLiteralExpression(cfg) ? cfg.properties.find((p) => p.name?.getText() === "baseURL") : undefined;
      const base = baseProp && ts.isPropertyAssignment(baseProp) ? resolveValue(ctx, baseProp.initializer) : { kind: "unknown" as const };
      const absoluteProp = cfg && ts.isObjectLiteralExpression(cfg) ? cfg.properties.find((p) => p.name?.getText() === "allowAbsoluteUrls") : undefined;
      const allowAbsolute = !absoluteProp || (ts.isPropertyAssignment(absoluteProp) && absoluteProp.initializer.kind === ts.SyntaxKind.TrueKeyword);
      const sym = assignedSymbol(ctx, n);
      if (sym) clients.set(sym, { base, evidence: ctx.ev(n), allowAbsolute });
    });
  }
  for (const sf of ctx.files) {
    const ax = importsOf(sf, "axios");
    forEachNode(sf, (n) => {
      if (!ts.isCallExpression(n) || !ts.isPropertyAccessExpression(n.expression)) return;
      const verb = n.expression.name.text;
      if (!HTTP_METHODS.includes(verb) || verb === "all") return;
      const recv = n.expression.expression;
      const client = symbolOf(ctx, recv);
      const info = (client && clients.get(client)) ?? (ts.isIdentifier(recv) && ax.names.has(recv.text) ? { base: { kind: "unknown" } as Resolved, evidence: undefined, allowAbsolute: true } : undefined);
      if (!info) return;
      const from = ctx.owner(n);
      const url = n.arguments[0] && urlTemplate(n.arguments[0]);
      if (!from) return;
      rules.add(AXIOS);
      const method = verb.toUpperCase();
      const callEv = ctx.ev(n);
      if (!url || url.base) {
        ctx.b.edge({ from, rel: "REQUESTS", derivation: "rule", rule: AXIOS, resolution: "unresolved", reason: "dynamic-string", props: { method }, evidence: [callEv] });
        return;
      }
      const cfg = n.arguments[["post", "put", "patch"].includes(verb) ? 2 : 1];
      const object = cfg && ts.isObjectLiteralExpression(unwrap(cfg)) ? unwrap(cfg) as ts.ObjectLiteralExpression : undefined;
      const params = object?.properties.find((p) => p.name?.getText() === "params");
      const query = [...new Set([...url.query, ...(params && ts.isPropertyAssignment(params) && ts.isObjectLiteralExpression(params.initializer) ? params.initializer.properties.map((p) => p.name?.getText() ?? "").filter(Boolean) : [])])].sort();
      const baseProp = object?.properties.find((p) => p.name?.getText() === "baseURL");
      const base = baseProp && ts.isPropertyAssignment(baseProp) ? resolveValue(ctx, baseProp.initializer) : info.base;
      const absoluteProp = object?.properties.find((p) => p.name?.getText() === "allowAbsoluteUrls");
      const allowAbsolute = absoluteProp ? ts.isPropertyAssignment(absoluteProp) && absoluteProp.initializer.kind === ts.SyntaxKind.TrueKeyword : info.allowAbsolute;
      const opaqueConfig = !!cfg && (!object || object.properties.some(ts.isSpreadAssignment));
      const target = opaqueConfig || (hasAuthorityOrScheme(url.text) && !allowAbsolute) ? undefined : requestTarget(ctx, base, method, url.text);
      if (!target) {
        ctx.b.edge({ from, rel: "REQUESTS", derivation: "rule", rule: AXIOS, resolution: "unresolved", reason: "dynamic-string", props: { method }, evidence: [callEv] });
        return;
      }
      ctx.b.edge({
        from, rel: "REQUESTS", to: target.to, derivation: "rule", rule: AXIOS, resolution: "unresolved", reason: "configuration",
        props: { method, normalizedRoute: target.normalised, query: query.length ? query : undefined, ...target.props, responseType: typeArgKey(ctx, n.typeArguments) },
        evidence: info.evidence ? [callEv, info.evidence] : [callEv],
      });
    });
  }
}

/**
 * Generated OpenAPI clients: a method in a generated class that passes an HTTP method literal and a literal path
 * to a shared request helper. The base URL comes from where the client is constructed.
 */
export function openApiRule(ctx: Ctx, rules: Set<ToolRef>) {
  for (const sf of ctx.files) {
    if (!ctx.generated.has(sf.fileName)) continue;
    for (const c of sf.statements.filter(ts.isClassDeclaration)) {
      if (!c.name) continue;
      const classSym = ctx.checker.getSymbolAtLocation(c.name);
      const construction = findConstruction(ctx, classSym);
      for (const m of c.members.filter(ts.isMethodDeclaration)) {
        let method: string | undefined;
        let path: ts.StringLiteralLike | undefined;
        const visit = (n: ts.Node) => {
          if (ts.isStringLiteralLike(n)) {
            if (/^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)$/.test(n.text)) method ??= n.text;
            else if (/^\/[\w\-./{}]*$/.test(n.text) && n.text.length > 1) path ??= n;
          }
          ts.forEachChild(n, visit);
        };
        if (m.body) visit(m.body);
        if (!method || !path) continue;
        const from = ctx.keyOf(m);
        if (!from) continue;
        const env = construction && envVar(construction.arguments?.[0]);
        const base: Resolved = env ? { kind: "env", name: env.name, fallback: env.fallback, evidence: ctx.ev(env.node) } : { kind: "unknown" };
        const target = base.kind === "unknown"
          ? { to: `http|${ctx.unbound("client", c.name.text)}|${method} ${normaliseRoute(path.text)}`, normalised: normaliseRoute(path.text), props: {} }
          : requestTarget(ctx, base, method, path.text);
        if (!target) continue;
        const operationId = /operationId:\s*([\w.-]+)/.exec(ts.getJSDocCommentsAndTags(m).map((j) => j.getText()).join("\n"))?.[1];
        ctx.b.edge({
          from, rel: "REQUESTS", to: target.to, derivation: "rule", rule: OPENAPI, resolution: "unresolved", reason: "configuration",
          props: { method, normalizedRoute: target.normalised, operationId, ...target.props, generated: true },
          evidence: construction ? [ctx.ev(path), ctx.ev(construction)] : [ctx.ev(path)],
        });
        rules.add(OPENAPI);
      }
    }
  }
}

function findConstruction(ctx: Ctx, classSym: ts.Symbol | undefined): ts.NewExpression | undefined {
  if (!classSym) return undefined;
  for (const sf of ctx.files) {
    let found: ts.NewExpression | undefined;
    forEachNode(sf, (n) => {
      if (!found && ts.isNewExpression(n) && ctx.resolveSymbol(n.expression) === classSym) found = n;
    });
    if (found) return found;
  }
  return undefined;
}

