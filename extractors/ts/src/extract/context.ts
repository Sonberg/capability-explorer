import { readFileSync } from "node:fs";
import { basename, dirname, relative, resolve } from "node:path";
import ts from "typescript";
import type { Span } from "../contracts/records.ts";
import type { ArtifactBuilder } from "./builder.ts";
import { posix, type UnitConfig } from "./project.ts";

/** Everything the declaration pass, the call pass and the rules share for one unit. */
export class Ctx {
  readonly checker: ts.TypeChecker;
  /** Source files analysed for this unit (each file belongs to exactly one unit). */
  readonly files: ts.SourceFile[];
  readonly generated = new Map<string, string | true>();
  private readonly fileNodes = new Set<string>();

  readonly root: string;
  readonly repo: string;
  readonly unit: UnitConfig;
  readonly program: ts.Program;
  readonly b: ArtifactBuilder;
  /** Keys for declarations in other units of the same repository (monorepos). */
  private readonly otherUnit: (file: string) => UnitConfig | undefined;

  constructor(root: string, repo: string, unit: UnitConfig, program: ts.Program, b: ArtifactBuilder, owned: Set<string>, otherUnit: (file: string) => UnitConfig | undefined) {
    this.root = root;
    this.repo = repo;
    this.unit = unit;
    this.program = program;
    this.b = b;
    this.otherUnit = otherUnit;
    this.checker = program.getTypeChecker();
    this.files = program.getSourceFiles().filter((sf) => owned.has(resolve(sf.fileName)));
  }

  get pkg() {
    return this.unit.packageName;
  }

  /** Path relative to the repository root (evidence paths). */
  rel(file: string) {
    return posix(relative(this.root, resolve(file)));
  }

  /** Module path relative to the package root (symbol keys). */
  mod(file: string, unit: UnitConfig = this.unit) {
    return posix(relative(unit.packageRoot, resolve(file)));
  }

  key(file: string, declPath: string, meaning: "value" | "type" | "namespace" = "value", unit: UnitConfig = this.unit) {
    return `ts|${this.repo}|${unit.packageName}|${this.mod(file, unit)}|${declPath}#${meaning}`;
  }

  unbound(kind: string, value: string) {
    return `@unbound:${this.repo}/${this.pkg}:${kind}=${value}`;
  }

  // ------------------------------------------------------------------ spans and evidence

  span(sf: ts.SourceFile, start: number, end: number): Span {
    const s = sf.getLineAndCharacterOfPosition(start);
    const e = sf.getLineAndCharacterOfPosition(Math.max(start, end));
    return { startLine: s.line + 1, startColumn: s.character + 1, endLine: e.line + 1, endColumn: e.character + 1 };
  }

  /** Evidence for a node, cut at the end of its first line so snippets stay small. */
  ev(node: ts.Node, sf = node.getSourceFile()): string {
    const start = node.getStart(sf);
    const lineEnd = sf.text.indexOf("\n", start);
    let end = Math.min(node.end, lineEnd < 0 ? node.end : lineEnd);
    while (end > start && /\s/.test(sf.text[end - 1]!)) end--;
    return this.b.ev(this.rel(sf.fileName), this.span(sf, start, end));
  }

  /** Evidence for a declaration header: modifiers through the start of the body. */
  headerEv(node: ts.Node): string {
    const sf = node.getSourceFile();
    const start = node.getStart(sf);
    const body = (node as { body?: ts.Node }).body ?? (ts.isClassLike(node) || ts.isInterfaceDeclaration(node) || ts.isEnumDeclaration(node) ? firstBrace(node, sf) : undefined);
    let end = body ? body.getStart(sf) : node.end;
    while (end > start && /[\s{;]/.test(sf.text[end - 1]!)) end--;
    return this.b.ev(this.rel(sf.fileName), this.span(sf, start, end));
  }

  /** Evidence for an arbitrary text range of a repository file (JSON config values, package.json lines). */
  textEv(path: string, text: string, index: number, length: number): string {
    const before = text.slice(0, index);
    const line = before.split("\n").length;
    const col = index - (before.lastIndexOf("\n") + 1) + 1;
    const endText = text.slice(0, index + length);
    const endLine = endText.split("\n").length;
    const endCol = index + length - (endText.lastIndexOf("\n") + 1) + 1;
    return this.b.ev(path, { startLine: line, startColumn: col, endLine, endColumn: endCol });
  }

  readRepoFile(path: string): string {
    return readFileSync(resolve(this.root, path), "utf8").replace(/\r\n/g, "\n");
  }

  // ------------------------------------------------------------------ declarations → keys

  /** The module node for top-level code, declared on first use. */
  fileKey(sf: ts.SourceFile): string {
    const key = this.key(sf.fileName, "(module)", "namespace");
    if (!this.fileNodes.has(key)) {
      this.fileNodes.add(key);
      const first = sf.statements.find((s) => !ts.isImportDeclaration(s) && !ts.isImportEqualsDeclaration(s)) ?? sf.statements[0];
      const evidence = first ? this.ev(first, sf) : this.b.ev(this.rel(sf.fileName), { startLine: 1, startColumn: 1, endLine: 1, endColumn: 1 });
      this.b.node(key, "File", this.rel(sf.fileName), [evidence], { props: { topLevelCode: true }, generated: this.generated.has(sf.fileName) });
    }
    return key;
  }

  /** Chain of named containers: `SalesApi.getWeeklySales`, `Math.round`, `fetch`. */
  declPath(decl: ts.Declaration): string | undefined {
    const parts: string[] = [];
    let node: ts.Node | undefined = decl;
    while (node && !ts.isSourceFile(node)) {
      const name = nameOf(node);
      if (name) parts.unshift(name);
      else if (ts.isCallSignatureDeclaration(node)) parts.unshift("(call)");
      else if (ts.isConstructSignatureDeclaration(node) || ts.isConstructorDeclaration(node)) parts.unshift("(new)");
      if (ts.isModuleBlock(node)) {
        node = node.parent;
        continue;
      }
      if (ts.isFunctionLike(node) && node !== decl) return undefined; // local declaration inside a function
      node = node.parent;
      if (node && (ts.isVariableDeclarationList(node) || ts.isVariableStatement(node))) continue;
      if (node && ts.isModuleDeclaration(node) && ts.isStringLiteral(node.name)) break; // declare module "x" {}
    }
    return parts.length ? parts.join(".") : undefined;
  }

  /** Key of a declaration anywhere: this unit, another unit of this repository, the TypeScript libs or a package. */
  keyOf(decl: ts.Declaration, meaning?: "value" | "type"): string | undefined {
    const sf = decl.getSourceFile();
    const target = targetDeclaration(decl);
    if (!target) return undefined;
    const path = this.declPath(target);
    if (!path) return undefined;
    const m = meaning ?? meaningOf(target);
    if (this.program.isSourceFileDefaultLibrary(sf) || isTypeScriptLib(sf.fileName)) return `ts|@ext|typescript|${basename(sf.fileName)}|${path}#${m}`;
    const nm = sf.fileName.lastIndexOf("/node_modules/");
    if (nm >= 0) {
      const rest = sf.fileName.slice(nm + "/node_modules/".length).split("/");
      const pkg = rest[0]!.startsWith("@") ? `${rest[0]}/${rest[1]}` : rest[0]!;
      const inner = rest.slice(pkg.split("/").length).join("/");
      return `ts|@ext|${pkg}|${inner}|${path}#${m}`;
    }
    if (this.files.some((f) => f.fileName === sf.fileName)) return this.key(sf.fileName, path, m);
    const other = this.otherUnit(resolve(sf.fileName));
    return other ? this.key(sf.fileName, path, m, other) : undefined;
  }

  /** The named declaration the facts inside `node` belong to; anonymous code folds into its container. */
  owner(node: ts.Node): string | undefined {
    for (let p: ts.Node | undefined = node.parent; p; p = p.parent) {
      if (ts.isSourceFile(p)) return this.fileKey(p);
      if (ts.isFunctionDeclaration(p) && p.name && isModuleLevel(p)) return this.keyOf(p);
      if ((ts.isMethodDeclaration(p) || ts.isGetAccessor(p) || ts.isSetAccessor(p)) && ts.isClassLike(p.parent) && nameOf(p)) return this.keyOf(p);
      if (ts.isPropertyDeclaration(p) && ts.isClassLike(p.parent)) return isFunctionInit(p.initializer) ? this.keyOf(p) : this.classKey(p.parent);
      if (ts.isConstructorDeclaration(p) && ts.isClassLike(p.parent)) return this.classKey(p.parent);
      // A module-level variable owns code only when it is a function; other initializers are top-level code.
      if (ts.isVariableDeclaration(p) && ts.isIdentifier(p.name) && isModuleLevel(p) && isFunctionInit(p.initializer)) return this.keyOf(p);
      if (ts.isClassLike(p) && isModuleLevel(p)) return this.classKey(p);
    }
    return undefined;
  }

  private classKey(c: ts.ClassLikeDeclaration) {
    return c.name ? this.keyOf(c as ts.ClassDeclaration) : undefined;
  }

  /** Follows import aliases to the symbol's own declarations. */
  resolveSymbol(node: ts.Node): ts.Symbol | undefined {
    let s = this.checker.getSymbolAtLocation(node);
    if (s && s.flags & ts.SymbolFlags.Alias) {
      try {
        s = this.checker.getAliasedSymbol(s);
      } catch {
        /* unresolved import */
      }
    }
    return s;
  }

  /** For `x` that is a const initialised in place, its initializer (one hop). */
  constInit(id: ts.Expression): ts.Expression | undefined {
    if (!ts.isIdentifier(id)) return undefined;
    const decl = this.resolveSymbol(id)?.valueDeclaration;
    if (decl && ts.isVariableDeclaration(decl) && decl.initializer && ts.getCombinedNodeFlags(decl) & ts.NodeFlags.Const) return decl.initializer;
    return undefined;
  }

  isProjectFile(file: string) {
    return this.files.some((f) => f.fileName === file);
  }
}

function firstBrace(node: ts.Node, sf: ts.SourceFile): ts.Node | undefined {
  return node.getChildren(sf).find((c) => c.kind === ts.SyntaxKind.OpenBraceToken);
}

export function nameOf(node: ts.Node): string | undefined {
  const name = (node as { name?: ts.Node }).name;
  if (name && (ts.isIdentifier(name) || ts.isPrivateIdentifier(name) || ts.isStringLiteral(name) || ts.isNumericLiteral(name))) return name.text;
  if ((ts.isFunctionDeclaration(node) || ts.isClassDeclaration(node)) && !node.name && node.modifiers?.some((m) => m.kind === ts.SyntaxKind.DefaultKeyword)) return "default";
  return undefined;
}

export function isModuleLevel(node: ts.Node): boolean {
  let p = node.parent;
  if (p && ts.isVariableDeclarationList(p)) p = p.parent?.parent;
  return !!p && (ts.isSourceFile(p) || ts.isModuleBlock(p));
}

export const isFunctionInit = (e: ts.Node | undefined): e is ts.ArrowFunction | ts.FunctionExpression => !!e && (ts.isArrowFunction(e) || ts.isFunctionExpression(e));

/** A function expression assigned to a variable or property is keyed by that variable or property. */
function targetDeclaration(decl: ts.Declaration): ts.Declaration | undefined {
  if (isFunctionInit(decl)) {
    const p = decl.parent;
    if (p && (ts.isVariableDeclaration(p) || ts.isPropertyDeclaration(p) || ts.isPropertyAssignment(p))) return p;
    return undefined;
  }
  return decl;
}

function meaningOf(decl: ts.Declaration): "value" | "type" {
  return ts.isInterfaceDeclaration(decl) || ts.isTypeAliasDeclaration(decl) || ts.isClassDeclaration(decl) || ts.isEnumDeclaration(decl) ? "type" : "value";
}

function isTypeScriptLib(file: string) {
  return /\/typescript\/lib\/lib\.[^/]*\.d\.ts$/.test(file);
}

export const dirOf = dirname;
