import ts from "typescript";
import { isFunctionInit, type Ctx } from "./context.ts";

export const HANDLER_MAP = { name: "ts.handler-map", version: "1" };

/** CALLS for every call the checker can bind, REFERENCES to project variables, types and functions used as values. */
export function calls(ctx: Ctx) {
  for (const sf of ctx.files) {
    const referenced = new Set<string>();
    const visit = (node: ts.Node) => {
      if (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) return;
      if (ts.isCallExpression(node) && node.expression.kind !== ts.SyntaxKind.SuperKeyword && node.expression.kind !== ts.SyntaxKind.ImportKeyword) call(ctx, node);
      if (ts.isIdentifier(node)) reference(ctx, node, referenced);
      ts.forEachChild(node, visit);
    };
    ts.forEachChild(sf, visit);
  }
}

function call(ctx: Ctx, node: ts.CallExpression) {
  const from = ctx.owner(node, true);
  if (!from) return;
  let signature: ts.Signature | undefined;
  try {
    signature = ctx.checker.getResolvedSignature(node);
  } catch {
    signature = undefined;
  }
  const decl = signature?.declaration;
  const concrete = decl && !ts.isJSDocSignature(decl) && (ts.isFunctionDeclaration(decl) || ts.isMethodDeclaration(decl) || ts.isMethodSignature(decl) || isFunctionInit(decl) || ts.isCallSignatureDeclaration(decl) && ts.isInterfaceDeclaration(decl.parent));
  if (decl && concrete && !ts.isJSDocSignature(decl)) {
    const to = ctx.keyOf(decl);
    if (to) {
      const props: Record<string, number> = {};
      if (ts.isFunctionDeclaration(decl) && !decl.body) {
        const overloads = (ctx.checker.getSymbolAtLocation(decl.name ?? decl)?.declarations ?? []).filter((d) => ts.isFunctionDeclaration(d) && !d.body);
        props.overload = overloads.indexOf(decl);
      }
      ctx.b.edge({ from, rel: "CALLS", to, evidence: [ctx.ev(node)], props });
      return;
    }
  }
  dynamicDispatch(ctx, node, from);
}

/**
 * `handlers[key](...)`, or a local bound to `handlers[key]`: the target is chosen at run time. One unresolved
 * edge says so; when the map is a literal object of functions, each entry becomes a rule-derived candidate.
 */
function dynamicDispatch(ctx: Ctx, node: ts.CallExpression, from: string) {
  let access: ts.Expression | undefined = node.expression;
  if (ts.isIdentifier(access)) access = ctx.constInit(access);
  if (!access || !ts.isElementAccessExpression(access)) return;
  const dispatch = access.getText();
  const at = ctx.ev(access);
  ctx.b.edge({ from, rel: "CALLS", resolution: "unresolved", reason: "dynamic-dispatch", props: { expression: dispatch }, evidence: [at] });

  const map = ts.isIdentifier(access.expression) ? ctx.constInit(access.expression) : undefined;
  if (!map || !ts.isObjectLiteralExpression(map)) return;
  for (const p of map.properties) {
    const value = ts.isPropertyAssignment(p) ? p.initializer : ts.isShorthandPropertyAssignment(p) ? p.name : undefined;
    if (!value || !ts.isIdentifier(value)) continue;
    const decl = ctx.resolveSymbol(value)?.declarations?.[0];
    const to = decl && (ts.isFunctionDeclaration(decl) || (ts.isVariableDeclaration(decl) && isFunctionInit(decl.initializer))) ? ctx.keyOf(decl) : undefined;
    if (!to) continue;
    const mapKey = p.name && (ts.isIdentifier(p.name) || ts.isStringLiteral(p.name)) ? p.name.text : undefined;
    ctx.b.edge({
      from, rel: "CALLS", to, derivation: "rule", rule: HANDLER_MAP, resolution: "candidate",
      props: { mapKey, dispatchExpression: dispatch }, evidence: [ctx.ev(p), at],
    });
  }
}

function reference(ctx: Ctx, id: ts.Identifier, seen: Set<string>) {
  const parent = id.parent;
  // Declaration names, the callee of a call (CALLS covers it), import/export names and property names are not references.
  if ((ts.isDeclarationStatement(parent) || ts.isVariableDeclaration(parent) || ts.isParameter(parent) || ts.isPropertyDeclaration(parent) || ts.isMethodDeclaration(parent) || ts.isPropertySignature(parent) || ts.isMethodSignature(parent) || ts.isBindingElement(parent) || ts.isPropertyAssignment(parent)) && (parent as ts.NamedDeclaration).name === id) return;
  if (ts.isCallExpression(parent) && parent.expression === id) return;
  if (ts.isPropertyAccessExpression(parent) && parent.name === id) return;
  if (ts.isImportSpecifier(parent) || ts.isExportSpecifier(parent) || ts.isImportClause(parent) || ts.isNamespaceImport(parent)) return;
  if (ts.isQualifiedName(parent) && parent.right === id) return;

  const symbol = ctx.resolveSymbol(id);
  const decl = symbol?.declarations?.[0];
  if (!decl || !ctx.isProjectFile(decl.getSourceFile().fileName)) return;
  const isType = ts.isInterfaceDeclaration(decl) || ts.isTypeAliasDeclaration(decl) || ts.isClassDeclaration(decl) || ts.isEnumDeclaration(decl);
  // Functions passed as values are not REFERENCES targets in the relation catalogue (CALLS and HANDLES cover them).
  const isValue = ts.isVariableDeclaration(decl) && isModuleLevelVar(decl) && !isFunctionInit(decl.initializer);
  if (!isType && !isValue) return;
  const to = ctx.keyOf(decl);
  const from = to && ctx.owner(id);
  if (!to || !from || from === to) return;
  // One edge per (container, target): the first use is the evidence.
  if (seen.has(`${from}|${to}`)) return;
  seen.add(`${from}|${to}`);
  ctx.b.edge({ from, rel: "REFERENCES", to, evidence: [ctx.ev(id)] });
}

function isModuleLevelVar(d: ts.VariableDeclaration) {
  const statement = d.parent?.parent;
  return !!statement && (ts.isSourceFile(statement.parent) || ts.isModuleBlock(statement.parent));
}
