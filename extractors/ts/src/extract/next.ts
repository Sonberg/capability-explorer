import { existsSync } from "node:fs";
import { join } from "node:path";
import ts from "typescript";
import type { ToolRef } from "../contracts/records.ts";
import type { Ctx } from "./context.ts";

export const NEXT: ToolRef = { name: "next.app-router", version: "1" };
const METHODS = new Set(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"]);

/** File conventions only: no Next build/config execution, implicit methods or rewrites. */
export function nextRoutes(ctx: Ctx, rules: Set<ToolRef>) {
  if (!ctx.unit.packageJson) return;
  const packagePath = ctx.rel(ctx.unit.packageJson);
  const text = ctx.readRepoFile(packagePath);
  let manifest: { dependencies?: Record<string, unknown>; devDependencies?: Record<string, unknown> };
  try { manifest = JSON.parse(text); } catch { return; }
  if (typeof (manifest.dependencies?.next ?? manifest.devDependencies?.next) !== "string") return;
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
      const decl = symbol.valueDeclaration;
      const initializer = decl && ts.isVariableDeclaration(decl) ? decl.initializer : undefined;
      const callable = decl && decl.getSourceFile() === sf && (
        (ts.isFunctionDeclaration(decl) && !!decl.body) ||
        (ts.isVariableDeclaration(decl) && !!(ts.getCombinedNodeFlags(decl) & ts.NodeFlags.Const) && initializer && (ts.isArrowFunction(initializer) || ts.isFunctionExpression(initializer)))
      );
      const from = callable && ctx.keyOf(decl!);
      if (!from || !ctx.b.hasNode(from)) { warn(`exported ${method} handler is not a supported local function declaration`, exportDecl); continue; }
      const evidence = [...new Set([ctx.ev(exportDecl ?? decl!), ctx.headerEv(decl!)])];
      const endpoint = `http|${ctx.repo}/${ctx.pkg}|${method} ${normalizedRoute}`;
      const prior = seen.get(endpoint);
      if (prior) { warn(`multiple route files declare ${method} ${normalizedRoute}; first declaration: ${prior}`, exportDecl); }
      seen.set(endpoint, ctx.rel(sf.fileName));
      ctx.b.node(service, "Service", ctx.pkg, [evidence[0]!], { props: { framework: "next" } });
      ctx.b.node(endpoint, "Endpoint", `${method} ${route}`, evidence, {
        props: { method, route, normalizedRoute, style: "next-app-router",
          routeParameterNames: parameters.length ? parameters.map((p) => p.name) : undefined,
          routeParameterKinds: parameters.length ? parameters.map((p) => p.kind) : undefined },
      });
      ctx.b.edge({ from: service, rel: "EXPOSES", to: endpoint, derivation: "rule", rule: NEXT, evidence });
      ctx.b.edge({ from, rel: "HANDLES", to: endpoint, derivation: "rule", rule: NEXT, evidence });
    }
  }
}
