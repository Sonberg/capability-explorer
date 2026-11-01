import { readFileSync } from "node:fs";
import ts from "typescript";
import type { ToolRef } from "../contracts/records.ts";
import type { Ctx } from "./context.ts";

export const NEXT = { name: "next.routes", version: "1" };

/** Verbs a Next.js App Router route handler may export. HEAD and OPTIONS are only extracted when exported explicitly. */
export const NEXT_METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"] as const;

export interface NextRoute {
  /** Display form: `[id]` -> `{id}`, `[...path]` -> `{...path}`. */
  route: string;
  /** Comparison form used in endpoint keys: `{}` for one segment, `{...}` for a catch-all. */
  normalized: string;
  catchAll?: boolean;
  optionalCatchAll?: boolean;
}

export type NextRouteResult = { route: NextRoute } | { skip: "private" } | { unsupported: string } | undefined;

const ROUTE_FILE = /(?:^|\/)(?:src\/)?app\/((?:.*\/)?)route\.(?:[mc]?[jt]sx?)$/;

/**
 * The URL a `route.ts` file serves, from its path relative to the package root. Undefined when the file is not an
 * App Router route handler. Route groups `(x)` add no segment, `_private` folders are not routable, parallel
 * (`@slot`) and intercepting (`(.)x`) folders are reported as unsupported rather than guessed.
 */
export function nextRouteFor(pathInPackage: string): NextRouteResult {
  const m = ROUTE_FILE.exec(pathInPackage);
  if (!m) return undefined;
  const segments = m[1]!.split("/").filter(Boolean);
  const display: string[] = [];
  const normalized: string[] = [];
  let catchAll = false;
  let optionalCatchAll = false;
  for (const [i, seg] of segments.entries()) {
    if (seg.startsWith("_")) return { skip: "private" };
    if (seg.startsWith("@")) return { unsupported: `parallel route folder "${seg}"` };
    if (/^\((\.{1,3})\)/.test(seg)) return { unsupported: `intercepting route folder "${seg}"` };
    if (/^\(.*\)$/.test(seg)) continue; // route group
    let d: RegExpExecArray | null;
    if ((d = /^\[\[\.\.\.([^\]]+)\]\]$/.exec(seg))) {
      if (i !== segments.length - 1) return { unsupported: `optional catch-all "${seg}" is not the last segment` };
      display.push(`{...${d[1]}}`); normalized.push("{...}"); catchAll = optionalCatchAll = true;
    } else if ((d = /^\[\.\.\.([^\]]+)\]$/.exec(seg))) {
      if (i !== segments.length - 1) return { unsupported: `catch-all "${seg}" is not the last segment` };
      display.push(`{...${d[1]}}`); normalized.push("{...}"); catchAll = true;
    } else if ((d = /^\[([^\].[]+)\]$/.exec(seg))) {
      display.push(`{${d[1]}}`); normalized.push("{}");
    } else if (/[[\]]/.test(seg)) {
      return { unsupported: `folder "${seg}"` };
    } else {
      display.push(seg); normalized.push(seg.toLowerCase());
    }
  }
  return {
    route: {
      route: "/" + display.join("/"),
      normalized: "/" + normalized.join("/"),
      catchAll: catchAll || undefined,
      optionalCatchAll: optionalCatchAll || undefined,
    },
  };
}

/** Whether the unit's package.json declares `next`. */
export function usesNext(ctx: Ctx): boolean {
  if (!ctx.unit.packageJson) return false;
  try {
    const pkg = JSON.parse(readFileSync(ctx.unit.packageJson, "utf8")) as Record<string, Record<string, string> | undefined>;
    return Boolean(pkg.dependencies?.next ?? pkg.devDependencies?.next ?? pkg.peerDependencies?.next);
  } catch {
    return false;
  }
}

interface Handler { method: string; decl: ts.Declaration; node: ts.Node }

/** Statically exported HTTP handlers of a route module: `export function GET`, `export const POST = ...`, `export { x as PUT }`. */
function exportedHandlers(ctx: Ctx, sf: ts.SourceFile): Handler[] {
  const out: Handler[] = [];
  const isVerb = (n: string): n is (typeof NEXT_METHODS)[number] => (NEXT_METHODS as readonly string[]).includes(n);
  const exported = (n: ts.Node) => ts.canHaveModifiers(n) && ts.getModifiers(n)?.some((m) => m.kind === ts.SyntaxKind.ExportKeyword);
  for (const s of sf.statements) {
    if (ts.isFunctionDeclaration(s) && s.name && exported(s) && isVerb(s.name.text)) {
      out.push({ method: s.name.text, decl: s, node: s.name });
    } else if (ts.isVariableStatement(s) && exported(s)) {
      for (const d of s.declarationList.declarations) {
        if (ts.isIdentifier(d.name) && isVerb(d.name.text)) out.push({ method: d.name.text, decl: d, node: d.name });
      }
    } else if (ts.isExportDeclaration(s) && !s.moduleSpecifier && s.exportClause && ts.isNamedExports(s.exportClause)) {
      for (const e of s.exportClause.elements) {
        if (!isVerb(e.name.text)) continue;
        const decl = ctx.resolveSymbol(e.propertyName ?? e.name)?.declarations?.[0];
        if (decl) out.push({ method: e.name.text, decl, node: e.name });
      }
    }
  }
  return out;
}

/** Next.js App Router handlers: `app/**\/route.ts` exports named after HTTP verbs, with the route taken from the folder path. */
export function nextRoutes(ctx: Ctx, rules: Set<ToolRef>) {
  if (!usesNext(ctx)) return;
  const service = `svc|${ctx.repo}|${ctx.pkg}`;
  let any = false;
  for (const sf of ctx.files) {
    const result = nextRouteFor(ctx.mod(sf.fileName));
    if (!result || "skip" in result) continue;
    if ("unsupported" in result) {
      const first = sf.statements[0];
      ctx.b.diagnostic("info", "NEXT_ROUTE_UNSUPPORTED", `route handler not extracted: ${result.unsupported}`, ctx.rel(sf.fileName), false, first ? ctx.ev(first, sf) : undefined);
      continue;
    }
    const { route } = result;
    for (const h of exportedHandlers(ctx, sf)) {
      const endpoint = `http|${ctx.repo}/${ctx.pkg}|${h.method} ${route.normalized}`;
      const ev = ctx.ev(h.node, sf);
      ctx.b.node(endpoint, "Endpoint", `${h.method} ${route.route}`, [ev], {
        props: { method: h.method, route: route.route, normalizedRoute: route.normalized, style: "next-app-router", catchAll: route.catchAll, optionalCatchAll: route.optionalCatchAll },
      });
      ctx.b.edge({ from: service, rel: "EXPOSES", to: endpoint, derivation: "rule", rule: NEXT, evidence: [ev] });
      const from = ctx.keyOf(h.decl);
      if (from) ctx.b.edge({ from, rel: "HANDLES", to: endpoint, derivation: "rule", rule: NEXT, evidence: [ev] });
      any = true;
    }
  }
  if (any) rules.add(NEXT);
}
