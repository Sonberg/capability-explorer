import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { AnyRecord } from "../src/contracts/records.ts";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";
import { extract } from "../src/extract/extractor.ts";

type Fact = AnyRecord & { signature?: string; props?: Record<string, unknown> };
const key = (symbol: string, file = "src/core.ts") => `ts|constructors|example|${file}|${symbol}`;

function run(source: string, javascript = false) {
  const root = mkdtempSync(join(tmpdir(), "ce-ts-constructors-"));
  try {
    mkdirSync(join(root, "src"));
    writeFileSync(join(root, "package.json"), JSON.stringify({ name: "example" }));
    if (!javascript) {
      writeFileSync(join(root, "tsconfig.json"), JSON.stringify({
        compilerOptions: { strict: true, target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext" },
        include: ["src"],
      }));
    }
    writeFileSync(join(root, javascript ? "src/core.js" : "src/core.ts"), source);
    const [unit] = extract({ source: root, repository: "constructors", commit: "a".repeat(40) });
    assert.ok(unit);
    assert.deepEqual(validateArtifact(unit.jsonl).problems, []);
    return { jsonl: unit.jsonl, records: unit.jsonl.trim().split("\n").map(line => JSON.parse(line) as Fact) };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function node(records: Fact[], symbol: string) {
  const found = records.find(record => record.kind === "node" && record.symbolKey === symbol);
  assert.ok(found?.kind === "node", `missing node ${symbol}`);
  return found;
}

test("TypeScript construction binds declared constructor nodes and their executable owners", () => {
  const source = [
    "export class Dependency {}",
    "export function initialize() {}",
    "export class Cart {",
    "  constructor(public dep: Dependency) { initialize(); }",
    "}",
    "export function build(dep: Dependency) { return new Cart(dep); }",
  ].join("\n");
  const result = run(source);
  const constructor = key("Cart.(new)#value");
  const declared = node(result.records, constructor);
  assert.equal(declared.nodeKind, "Method");
  assert.equal(declared.name, "Cart");
  assert.equal(declared.props?.constructor, true);
  assert.equal(declared.signature, "(public dep: Dependency)");
  const evidence = result.records.find(record => record.kind === "evidence" && declared.evidence.includes(record.id));
  assert.ok(evidence?.kind === "evidence");
  assert.equal(evidence.path, "src/core.ts");
  assert.equal(evidence.span.startLine, 4);
  const edges = result.records.filter(record => record.kind === "edge");
  assert.ok(edges.some(edge => edge.from === key("Cart#type") && edge.relation === "DECLARES" && edge.to === constructor));
  assert.ok(edges.some(edge => edge.from === key("build#value") && edge.relation === "CALLS" && edge.to === constructor));
  assert.ok(edges.some(edge => edge.from === constructor && edge.relation === "CALLS" && edge.to === key("initialize#value")));
  assert.ok(edges.some(edge => edge.from === constructor && edge.relation === "REFERENCES" && edge.to === key("Dependency#type")));
  assert.equal(result.jsonl, run(source).jsonl);
});

test("constructor overload signatures share one node and resolve construction to it", () => {
  const { records } = run([
    "export class Box {",
    "  constructor(value: string);",
    "  constructor(value: number);",
    "  constructor(value: string | number) {}",
    "}",
    'export function make() { return new Box("one"); }',
  ].join("\n"));
  const constructor = key("Box.(new)#value");
  assert.equal(records.filter(record => record.kind === "node" && record.symbolKey === constructor).length, 1);
  const declared = node(records, constructor);
  assert.equal(declared.signature, "(value: string | number)");
  assert.deepEqual(declared.props?.overloads, ["(value: string)", "(value: number)"]);
  assert.equal(declared.evidence.length, 3);
  assert.ok(records.some(record => record.kind === "edge" && record.from === key("make#value") &&
    record.relation === "CALLS" && record.to === constructor));
});

test("plain JavaScript constructors and body calls use the same declared ownership", () => {
  const { records } = run([
    "export function initialize() {}",
    "export class Cart { constructor(dep) { initialize(); this.dep = dep; } }",
    "export function build(dep) { return new Cart(dep); }",
  ].join("\n"), true);
  const constructor = key("Cart.(new)#value", "src/core.js");
  assert.equal(node(records, constructor).props?.constructor, true);
  const edges = records.filter(record => record.kind === "edge");
  assert.ok(edges.some(edge => edge.from === key("build#value", "src/core.js") && edge.relation === "CALLS" && edge.to === constructor));
  assert.ok(edges.some(edge => edge.from === constructor && edge.relation === "CALLS" && edge.to === key("initialize#value", "src/core.js")));
});

test("implicit, external, unresolved and dynamic construction does not invent constructor calls", () => {
  const { records } = run([
    "export class Default {}",
    "export interface Factory { new(): Default; }",
    "export function build(dynamic: Factory) {",
    "  return [new Default(), new Date(), new Missing(), new dynamic()];",
    "}",
  ].join("\n"));
  assert.equal(records.filter(record => record.kind === "node" && record.props?.constructor === true).length, 0);
  assert.equal(records.filter(record => record.kind === "edge" && record.from === key("build#value") && record.relation === "CALLS").length, 0);
  assert.ok(records.some(record => record.kind === "edge" && record.from === key("build#value") &&
    record.relation === "REFERENCES" && record.to === key("Default#type")));
});
