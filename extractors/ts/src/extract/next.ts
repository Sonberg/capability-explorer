import { existsSync } from "node:fs";
import { join } from "node:path";
import ts from "typescript";
import type { ToolRef } from "../contracts/records.ts";
import type { Ctx } from "./context.ts";

export const NEXT: ToolRef = { name: "next.app-router", version: "1" };
export const NEXT_METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"] as const;
const METHODS = new Set<string>(NEXT_METHODS);

export interface NextRoute {
  route: string;
  normalized: string;
  catchAll?: boolean;
  optionalCatchAll?: boolean;
}
export type NextRouteResult = { route: NextRoute } | { skip: "private" } | { unsupported: string } | undefined;

/** Legacy display helper. Artifacts retain distinct required/optional catch-all identities below. */
export function nextRouteFor(pathInPackage: string): NextRouteResult {
  const match = /^(?:src\/)?app\/((?:.*\/)?)route\.(?:[mc]?[jt]sx?)$/.exec(pathInPackage);
  if (!match) return undefined;
  const segments = match[1]!.split("/").filter(Boolean);
  const display: string[] = [], normalized: string[] = [];
  let catchAll = false, optionalCatchAll = false;
  for (const [i, segment] of segments.entries()) {
    if (segment.startsWith("_")) return { skip: "private" };
    if (/^\([^.)][^/]*\)$/.test(segment)) continue;
    if (segment.startsWith("@") || segment.startsWith("(") || /[%?#{}\s]/.test(segment)) return { unsupported: `route folder "${segment}"` };
    const optional = /^\[\[\.\.\.([^\[\]]+)\]\]$/.exec(segment);
    const required = /^\[\.\.\.([^\[\]]+)\]$/.exec(segment);
    const dynamic = /^\[([^\[\].]+)\]$/.exec(segment);
    if ((optional || required) && i !== segments.length - 1) return { unsupported: `catch-all "${segment}" is not the last segment` };
    if (optional || required) {
      display.push(`{...${(optional ?? required)![1]}}`); normalized.push("{...}");
      catchAll = true; optionalCatchAll = !!optional;
    } else if (dynamic) { display.push(`{${dynamic[1]}}`); normalized.push("{}"); }
    else if (/[\[\]]/.test(segment)) return { unsupported: `route folder "${segment}"` };
    else { display.push(segment); normalized.push(segment.toLowerCase()); }
  }
  return { route: { route: "/" + display.join("/"), normalized: "/" + normalized.join("/"),
    catchAll: catchAll || undefined, optionalCatchAll: optionalCatchAll || undefined } };
}

/** Read package evidence through the same source boundary as the compiler. */
export function usesNext(ctx: Ctx): boolean {
  if (!ctx.unit.packageJson) return false;
  try {
    const pkg = JSON.parse(ctx.readRepoFile(ctx.rel(ctx.unit.packageJson))) as {
      dependencies?: Record<string, unknown>; devDependencies?: Record<string, unknown>; peerDependencies?: Record<string, unknown>;
    };
    return typeof (pkg.dependencies?.next ?? pkg.devDependencies?.next ?? pkg.peerDependencies?.next) === "string";
  } catch { return false; }
}

const unwrap = (expression: ts.Expression): ts.Expression => {
  while (ts.isParenthesizedExpression(expression) || ts.isAsExpression(expression) || ts.isNonNullExpression(expression) || ts.isSatisfiesExpression(expression)) expression = expression.expression;
  return expression;
};

/** Local immutable aliases are source proof; factories, mutable bindings and cross-module re-exports are not. */
function handlerOwner(ctx: Ctx, symbol: ts.Symbol, sf: ts.SourceFile): { key: string; declaration: ts.Declaration } | undefined {
  let declaration = symbol.valueDeclaration;
  const seen = new Set<ts.Declaration>();
  while (declaration && !seen.has(declaration) && declaration.getSourceFile() === sf) {
    seen.add(declaration);
    if (ts.isFunctionDeclaration(declaration) && declaration.body) {
      const key = ctx.keyOf(declaration);
      return key && ctx.b.hasNode(key) ? { key, declaration } : undefined;
    }
    if (!ts.isVariableDeclaration(declaration) || !declaration.initializer || !(ts.getCombinedNodeFlags(declaration) & ts.NodeFlags.Const)) return undefined;
    const initializer = unwrap(declaration.initializer);
    if (ts.isArrowFunction(initializer) || ts.isFunctionExpression(initializer)) {
      const key = ctx.keyOf(declaration);
      return key && ctx.b.hasNode(key) ? { key, declaration } : undefined;
    }
    if (ts.isIdentifier(initializer)) { declaration = ctx.resolveSymbol(initializer)?.valueDeclaration; continue; }
    // A known function property (e.g. Response.json) has no local function node. Keep module ownership.
    if (ts.isPropertyAccessExpression(initializer) && ctx.checker.getTypeAtLocation(initializer).getCallSignatures().length > 0)
      return { key: ctx.fileKey(sf), declaration };
    return undefined;
  }
  return undefined;
}

/** File conventions only: no Next build/config execution, implicit methods or rewrites. */
export function nextRoutes(ctx: Ctx, rules: Set<ToolRef>) {
  if (!usesNext(ctx)) return;
  const root = existsSync(join(ctx.unit.packageRoot, "app")) ? "app/" : "src/app/";
  const config = ["next.config.ts", "next.config.js", "next.config.mjs", "next.config.cjs"].find((f) => existsSync(join(ctx.unit.packageRoot, f)));
  const service = `svc|${ctx.repo}|${ctx.pkg}`;
  const seen = new Map<string, string>();
  for (const sf of [...ctx.files].sort((a, b) => a.fileName < b.fileName ? -1 : a.fileName > b.fileName ? 1 : 0)) {
    const modulePath = ctx.mod(sf.fileName);
    if (!modulePath.startsWith(root) || !/(^|\/)route\.(ts|js)$/.test(modulePath)) continue;
    const segments = modulePath.slice(root.length).split("/").slice(0, -1);
    if (segments.some((s) => s.startsWith("_"))) continue; // private folders opt out of routing
    rules.add(NEXT);
    const warn = (message: string, node: ts.Node = sf.statements[0] ?? sf) => {
      ctx.b.diagnostic("warning", "NEXT_ROUTE_UNSUPPORTED", message, ctx.rel(sf.fileName), true, ctx.ev(node));
      ctx.b.markIncomplete(ctx.rel(sf.fileName));
    };
    const routeParts: string[] = [], normalizedParts: string[] = [];
    const parameters: { name: string; kind: string }[] = [];
    let unsupported = false;
    for (const [i, segment] of segments.entries()) {
      if (/^\([^.)][^/]*\)$/.test(segment)) continue;
      if (segment.startsWith("@") || segment.startsWith("(") || /[%?#{}\s]/.test(segment)) { unsupported = true; break; }
      const optional = /^\[\[\.\.\.([^\[\]]+)\]\]$/.exec(segment);
      const catchAll = /^\[\.\.\.([^\[\]]+)\]$/.exec(segment);
      const dynamic = /^\[([^\[\].]+)\]$/.exec(segment);
      const match = optional ?? catchAll ?? dynamic;
      if (!match && /[\[\]]/.test(segment)) { unsupported = true; break; }
      if ((optional || catchAll) && i !== segments.length - 1) { unsupported = true; break; }
      routeParts.push(segment);
      normalizedParts.push(optional ? "{**}" : catchAll ? "{*}" : dynamic ? "{}" : segment.toLowerCase());
      if (match) parameters.push({ name: match[1]!, kind: optional ? "optional-catch-all" : catchAll ? "catch-all" : "dynamic" });
    }
    if (unsupported) { warn("route path uses unsupported parallel, intercepting or malformed segments"); continue; }
    const route = "/" + routeParts.join("/");
    const normalizedRoute = "/" + normalizedParts.join("/");
    if (config) {
      ctx.b.diagnostic("warning", "NEXT_CONFIGURATION_NOT_EVALUATED", `paths are app-relative; ${config} basePath, rewrites and pageExtensions are not evaluated`, ctx.rel(sf.fileName), true, ctx.ev(sf.statements[0] ?? sf));
      ctx.b.markIncomplete(ctx.rel(sf.fileName));
    }
    const moduleSymbol = ctx.checker.getSymbolAtLocation(sf);
    const exports = moduleSymbol ? ctx.checker.getExportsOfModule(moduleSymbol) : [];
    for (const exported of [...exports].sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0)) {
      const method = exported.name;
      if (!METHODS.has(method)) {
        if (method === "default" || ["TRACE", "CONNECT", "ALL"].includes(method)) warn(`export ${method} is not a supported App Router HTTP method handler`, exported.declarations?.[0]);
        continue;
      }
      const exportDecl = exported.declarations?.[0];
      // Cross-module re-exports and factory results require more than file-convention evidence.
      const symbol = exported.flags & ts.SymbolFlags.Alias ? ctx.checker.getAliasedSymbol(exported) : exported;
      const owner = handlerOwner(ctx, symbol, sf);
      if (!owner) { warn(`exported ${method} handler is not a supported local function declaration`, exportDecl); continue; }
      const from = owner.key;
      const evidence = [...new Set([ctx.ev(exportDecl ?? owner.declaration), ctx.headerEv(owner.declaration)])];
      const endpoint = `http|${ctx.repo}/${ctx.pkg}|${method} ${normalizedRoute}`;
      const prior = seen.get(endpoint);
      if (prior) { warn(`multiple route files declare ${method} ${normalizedRoute}; first declaration: ${prior}`, exportDecl); }
      seen.set(endpoint, ctx.rel(sf.fileName));
      ctx.b.node(service, "Service", ctx.pkg, [evidence[0]!], { props: { framework: "next" } });
      ctx.b.node(endpoint, "Endpoint", `${method} ${route}`, evidence, {
        props: { method, route, normalizedRoute, style: "next-app-router",
          catchAll: parameters.some((p) => p.kind !== "dynamic") || undefined,
          optionalCatchAll: parameters.some((p) => p.kind === "optional-catch-all") || undefined,
          routeParameterNames: parameters.length ? parameters.map((p) => p.name) : undefined,
          routeParameterKinds: parameters.length ? parameters.map((p) => p.kind) : undefined },
      });
      ctx.b.edge({ from: service, rel: "EXPOSES", to: endpoint, derivation: "rule", rule: NEXT, evidence });
      ctx.b.edge({ from, rel: "HANDLES", to: endpoint, derivation: "rule", rule: NEXT, evidence });
    }
  }
}
