import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import ts from "typescript";
import type { ToolRef } from "../contracts/records.ts";
import { ArtifactBuilder, sha256 } from "./builder.ts";
import { calls, HANDLER_MAP } from "./calls.ts";
import { Ctx } from "./context.ts";
import { declare, detectGenerated } from "./declarations.ts";
import { discoverUnits, posix, type UnitConfig } from "./project.ts";
import { axiosRule, express, fetchRule, openApiRule, pgRule, projectNode, serviceBusRule, serviceNode } from "./rules.ts";

export const EXTRACTOR: ToolRef = { name: "ts-compiler", version: "0.1.0" };

export interface ExtractOptions { source: string; repository: string; commit: string }
export interface UnitResult { unitPath: string; unitName: string; jsonl: string; language: "typescript" | "javascript" }

/** Module specifiers that did not resolve: packages that are not installed (nothing is ever installed). */
function missingModules(program: ts.Program, files: readonly ts.SourceFile[]): string[] {
  const checker = program.getTypeChecker();
  const missing = new Set<string>();
  for (const sf of files) {
    for (const s of sf.statements) {
      const spec = (ts.isImportDeclaration(s) || ts.isExportDeclaration(s)) && s.moduleSpecifier && ts.isStringLiteral(s.moduleSpecifier) ? s.moduleSpecifier : undefined;
      if (spec && !spec.text.startsWith(".") && !checker.getSymbolAtLocation(spec)) missing.add(spec.text.startsWith("@") ? spec.text.split("/").slice(0, 2).join("/") : spec.text.split("/")[0]!);
    }
  }
  return [...missing].sort();
}

export function extract(options: ExtractOptions): UnitResult[] {
  const root = resolve(options.source);
  const units = discoverUnits(root);
  const programs = new Map<UnitConfig, ts.Program>();
  for (const unit of units) programs.set(unit, ts.createProgram({ rootNames: unit.rootNames, options: unit.options }));

  // Each file belongs to the deepest unit whose program includes it, so overlapping configs never duplicate symbols.
  const ownerOf = new Map<string, UnitConfig>();
  for (const [unit, program] of programs) {
    for (const sf of program.getSourceFiles()) {
      const file = resolve(sf.fileName);
      if (!file.startsWith(root + "/") || sf.isDeclarationFile || file.includes("/node_modules/")) continue;
      const current = ownerOf.get(file);
      if (!current || dirname(unit.configFile).length > dirname(current.configFile).length) ownerOf.set(file, unit);
    }
  }

  const fileText = (path: string) => readFileSync(resolve(root, path), "utf8").replace(/\r\n/g, "\n");
  const results: UnitResult[] = [];
  for (const [unit, program] of programs) {
    const owned = new Set([...ownerOf].filter(([, u]) => u === unit).map(([f]) => f));
    if (owned.size === 0) continue;
    const b = new ArtifactBuilder(options.repository, options.commit, EXTRACTOR, fileText);
    const ctx = new Ctx(root, options.repository, unit, program, b, owned, (f) => ownerOf.get(f));
    const rules = new Set<ToolRef>();

    projectNode(ctx);
    serviceNode(ctx);
    detectGenerated(ctx);
    declare(ctx);
    calls(ctx);
    express(ctx, rules);
    axiosRule(ctx, rules);
    openApiRule(ctx, rules);
    fetchRule(ctx, rules);
    pgRule(ctx, rules);
    serviceBusRule(ctx, rules);
    rules.add(HANDLER_MAP);

    // Syntax errors make a file incomplete; missing packages only limit what external calls resolve to.
    for (const sf of ctx.files) {
      const syntax = program.getSyntacticDiagnostics(sf);
      if (syntax.length > 0) {
        const d = syntax[0]!;
        b.diagnostic("error", `SYNTAX_TS${d.code}`, ts.flattenDiagnosticMessageText(d.messageText, "\n"), ctx.rel(sf.fileName), true,
          d.start !== undefined ? b.ev(ctx.rel(sf.fileName), ctx.span(sf, d.start, d.start + (d.length ?? 0))) : undefined);
        b.markIncomplete(ctx.rel(sf.fileName));
      }
    }
    const missing = missingModules(program, ctx.files);
    if (missing.length > 0) {
      b.diagnostic("info", "DEPENDENCIES_NOT_INSTALLED",
        `packages are not installed, so calls into them are not resolved: ${missing.slice(0, 12).join(", ")}${missing.length > 12 ? `, … (${missing.length})` : ""}`);
    }
    for (const e of unit.configErrors) b.diagnostic("warning", "CONFIG", e, ctx.rel(unit.configFile));

    const language = ctx.files.some((f) => /\.(m|c)?tsx?$/.test(f.fileName)) ? "typescript" : "javascript";
    const inputs = [...owned].sort().map((f) => `${posix(f.slice(root.length + 1))}\0${sha256(readFileSync(f, "utf8"))}\n`).join("");
    const configHash = sha256(JSON.stringify(Object.keys(unit.options).sort().map((k) => [k, unit.options[k]])));
    const ordered = [...rules].sort((a, b2) => a.name.localeCompare(b2.name));
    const unitPath = ctx.rel(unit.configFile);
    results.push({ unitPath, unitName: unit.packageName, language, jsonl: b.toJsonl({ path: unitPath, name: unit.packageName }, language, ordered, configHash, sha256(inputs), owned.size) });
  }
  return results;
}
