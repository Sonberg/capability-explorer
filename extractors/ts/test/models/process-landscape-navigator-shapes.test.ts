import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";
import { Ajv2020 } from "ajv/dist/2020.js";
import schema from "../../../../schema/models/process-landscape.v1.schema.json" with { type: "json" };
import { parseNavigatorBundle } from "../../src/models/process-landscape-navigator.ts";

const digest = (value: string | Uint8Array) => createHash("sha256").update(value).digest("hex");
const xml = "<synthetic/>";
const aliases: Record<string, string> = { catalogue: "files", elements: "el" };
const parse = (categories: Record<string, unknown>, verified = true) => {
  const bytes = Buffer.from(`window.NAV_DATA = ${JSON.stringify({ xml: { "synthetic.bpmn": xml }, ...categories })};`);
  return parseNavigatorBundle(bytes, { path: "navigator.js", byteLength: bytes.length, sha256: digest(bytes) },
    { raw: new Set(verified ? [digest(xml)] : []), newlineNormalized: new Set() });
};
const ajv = new Ajv2020({ strict: true, strictRequired: false });
ajv.addSchema(schema);
const validateNavigator = ajv.compile({ $ref: `${schema.$id}#/$defs/navigator` });
const envelope = { bundle: { path: "navigator.js", byteLength: 1, sha256: "a".repeat(64) }, verified: true };

const malformed: Array<[string, unknown]> = [
  ["catalogue", {}], ["catalogue", [null]], ["catalogue", [{ procs: [1] }]],
  ["elements", []], ["elements", { "a.bpmn": [] }], ["elements", { "a.bpmn": { Task: false } }],
  ["elements", { "a.bpmn": { Task: { ev: [["throw", "message"]] } } }],
  ["elements", { "a.bpmn": { Task: { out: {}, lane: false } } }],
  ["links", { "a.bpmn": { Task: { phase: [] } } }], ["links", []],
  ["events", []], ["events", { Event: { throw: [["a.bpmn"]] } }],
  ["events", { Event: { catch: [["a.bpmn", 1]] } }], ["events", { Event: { ref: null } }],
  ["lifecycle", [{ states: [{}] }]], ["lifecycle", {}], ["lifecycle", [{ tr: [null] }]],
  ["lifecycle", [{ set: { new: [["a.bpmn", "Task"]] } }]],
  ["journey", []], ["journey", { phases: [{ contexts: {} }] }], ["journey", { entries: [false] }],
  ["contexts", [{ color: 1 }]], ["contexts", {}], ["phases", ["Phase"]],
  ["dmn", { Decision: {} }], ["catalogue", null],
  ["catalogue", [{ file: "../a.bpmn" }]], ["elements", { "/a.bpmn": {} }],
  ["elements", { "a.bpmn": { Task: { tgt: "../a.bpmn" } } }],
  ["events", { Event: { ref: [["../a.bpmn", "Task"]] } }],
  ["lifecycle", [{ set: { new: [["../a.bpmn", "Task", "via"]] } }]],
  ["journey", { phases: [{ files: ["../a.bpmn"] }] }], ["journey", { entries: [{ file: "../a.bpmn" }] }],
  ["procFile", { Process: "../a.bpmn" }], ["procFile", { Process: {} }],
];

test("producer withholds API-invalid category shapes and schema rejects the same published payloads", () => {
  for (const [category, value] of malformed) {
    const sourceKey = aliases[category] ?? category;
    const result = parse({ [sourceKey]: value, generated: { retained: true } });
    assert.equal(result.navigator.verified, true, "XML verification is independent of category shape");
    assert.equal(Object.hasOwn(result.navigator, category), false, `${category} must not reach a strict API`);
    assert.deepEqual(result.navigator.generated, { retained: true });
    assert.ok(result.diagnostics.some(diagnostic =>
      diagnostic.code === "invalid-navigator-category-shape" && diagnostic.disposition === "partial"
      && diagnostic.source?.ref === sourceKey), `${category} omission must be explicit`);
    assert.equal(validateNavigator({ ...envelope, [category]: value }), false, `${category} schema must match API shape rejection`);
    assert.equal(validateNavigator(result.navigator), true);
  }
});

test("partial records, nullable model attributes, unresolved references and additive metadata survive verbatim", () => {
  const categories = {
    files: [{ file: "missing.bpmn", pid: "", group: "", name: "", phases: "", ctx: "", inc: "", grain: "", proc: "", purpose: "", procs: ["Missing"], extension: { nullable: null } }, {}],
    el: { "a.bpmn": { Task: { t: "task", n: "", pr: null, lane: null, st: null, src: null, dst: "Missing", msg: null, in: [null, ""], out: [], ev: [["custom-role", "", "Unknown"]], tgt: "missing.bpmn", extension: { data: [1, null] } } } },
    links: { "a.bpmn": { Missing: { phase: "", ctx: "", ent: "", ev: "", dmn: "", sf: "", inc: "", extra: false } } },
    events: { "Label with xml and source words": { ctx: "", kind: "non-canonical", agg: "", phase: "", throw: [["missing.bpmn", "Task"]], catch: [], ref: [], extra: null } },
    lifecycle: [{ name: "", entity: "", obj: "", ctx: "", states: ["new"], tr: [{ f: "", t: "new", cmd: "", ev: "", guard: "", dmn: "", extra: [] }], set: { new: [["missing.bpmn", "Missing", "unresolved"]] } }, {}],
    journey: { phases: [{ id: "", name: "", gate: "", purpose: "", contexts: ["unknown"], files: ["missing.bpmn"] }], entries: [{ name: "", file: "missing.bpmn", text: "" }], extra: { retained: true } },
    contexts: [{ id: "", name: "", cls: "", color: "", extra: 1 }, {}],
    procFile: { Missing: "missing.bpmn" }, dmn: { Missing: "" }, phases: [{ id: "", name: "", gate: "" }, {}],
    generated: { arbitrary: ["opaque", null] },
  };
  const result = parse(categories);
  assert.deepEqual(result.diagnostics, []);
  for (const [sourceKey, value] of Object.entries(categories)) {
    const category = sourceKey === "files" ? "catalogue" : sourceKey === "el" ? "elements" : sourceKey;
    assert.deepEqual((result.navigator as unknown as Record<string, unknown>)[category], value);
  }
  assert.equal(validateNavigator(result.navigator), true);
});

test("file-reference guards cover unsafe segments and controls without imposing cross-category membership", () => {
  for (const file of ["", "/", "./a.bpmn", "a/../b.bpmn", "a//b.bpmn", "a/", "C:/a.bpmn", "a\\b.bpmn", "https://a.bpmn", "a\nb.bpmn", "a\u0085b.bpmn", "a".repeat(1025)]) {
    const result = parse({ procFile: { Missing: file } });
    assert.equal(result.navigator.procFile, undefined);
    assert.equal(validateNavigator({ ...envelope, procFile: { Missing: file } }), false);
  }
  for (const file of ["missing.bpmn", "folder/unadmitted.dmn", "space name.bpmn", "unicode-å.bpmn", "a".repeat(1024)]) {
    const result = parse({ procFile: { Missing: file } });
    assert.deepEqual(result.navigator.procFile, { Missing: file });
    assert.deepEqual(result.diagnostics, []);
  }
});

test("an invalid category does not discard independent valid data and unverified bundles remain withheld", () => {
  const result = parse({ files: false, contexts: [{}], phases: [{}] });
  assert.equal(result.navigator.catalogue, undefined);
  assert.deepEqual(result.navigator.contexts, [{}]);
  assert.deepEqual(result.navigator.phases, [{}]);
  assert.equal(result.diagnostics.length, 1);
  const unverified = parse({ files: false, contexts: [{}] }, false);
  assert.equal(unverified.navigator.verified, false);
  assert.equal(unverified.navigator.contexts, undefined);
  assert.deepEqual(unverified.diagnostics.map(diagnostic => diagnostic.code), ["navigator-xml-mismatch"]);
});

test("UTF-16 file length matches the API without constraining arbitrary metadata strings", () => {
  const file = `${"😀".repeat(513)}.bpmn`;
  const invalidReferences = [
    { files: [{ file }] }, { el: { [file]: {} } }, { links: { [file]: {} } },
    { el: { "a.bpmn": { Task: { tgt: file } } } },
    { events: { Event: { throw: [[file, "Task"]] } } },
    { lifecycle: [{ set: { new: [[file, "Task", "unresolved"]] } }] },
    { journey: { phases: [{ files: [file] }] } }, { journey: { entries: [{ file }] } },
    { procFile: { Missing: file } },
  ];
  for (const categories of invalidReferences) {
    const result = parse(categories);
    assert.equal(result.diagnostics[0]?.code, "invalid-navigator-category-shape");
    const sourceKey = Object.keys(categories)[0]!;
    const category = sourceKey === "files" ? "catalogue" : sourceKey === "el" ? "elements" : sourceKey;
    assert.equal(Object.hasOwn(result.navigator, category), false);
  }
  const valid = { files: [{ file: "😀".repeat(512), name: "x".repeat(2048), extension: file }], dmn: { Decision: file } };
  const result = parse(valid);
  assert.deepEqual(result.diagnostics, []);
  assert.deepEqual(result.navigator.catalogue, valid.files);
  assert.deepEqual(result.navigator.dmn, valid.dmn);
});
