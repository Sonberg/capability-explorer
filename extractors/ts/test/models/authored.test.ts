import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, symlink, unlink, writeFile } from "node:fs/promises";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { Ajv2020 } from "ajv/dist/2020.js";
import { extractModelXml } from "../../src/models/extract.ts";
import { DEFAULT_XML_LIMITS, ModelXmlError, parseXml } from "../../src/models/xml.ts";

const fixture = (name: string) => fileURLToPath(new URL(`../../../../fixtures/models/${name}`, import.meta.url));
const options = (unitPath: string, contentProfile?: "structural" | "model-content") => ({
  repositoryId: "local:orders",
  commitSha: "0123456789abcdef0123456789abcdef01234567",
  unitPath,
  contentProfile,
});

test("Given BPMN 2.0.2, extraction preserves nested typed elements and flow conditions", async () => {
  const xml = await readFile(fixture("order.bpmn"), "utf8");
  const evidence = await extractModelXml(xml, options("models/order.bpmn", "model-content"));

  assert.equal(evidence.schema, "ce.models.v1");
  assert.equal(evidence.coverage.status, "full");
  assert.deepEqual(
    evidence.elements.filter(({ type }) => ["definitions", "process", "startEvent", "task", "sequenceFlow"].includes(type)).map(({ id }) => id),
    ["defs-order", "order-process", "start", "review", "flow-review"],
  );
  assert.equal(evidence.elements.find(({ id }) => id === "review")?.parent, evidence.elements[1]?.identity);
  assert.equal(evidence.relationships.length, 3);
  assert.equal(evidence.relationships[0]?.kind, "sequenceFlow");
  assert.deepEqual(evidence.relationships[0]?.condition, { text: "approved", language: "text/plain" });
  assert.equal(evidence.relationships[1]?.kind, "reference");
  assert.equal(evidence.relationships[2]?.kind, "messageFlow");
  const structural = await extractModelXml(xml, options("models/order.bpmn"));
  assert.deepEqual(structural.relationships[0]?.condition, { language: "text/plain" });
});

test("Given DMN 1.3, extraction preserves ordered rules and content only under the explicit profile", async () => {
  const xml = await readFile(fixture("decision.dmn"), "utf8");
  const structural = await extractModelXml(xml, options("models/decision.dmn"));
  const content = await extractModelXml(xml, options("models/decision.dmn", "model-content"));
  const rules = structural.elements.filter(({ type }) => type === "rule");

  assert.equal(structural.coverage.status, "partial");
  assert.ok(structural.diagnostics.some(({ code }) => code === "content-redacted"));
  assert.equal(JSON.stringify(structural).includes(">= 18"), false);
  assert.deepEqual(rules.map(({ id, order }) => [id, order]), [["rule-adult", 0], ["rule-minor", 1]]);
  assert.equal(rules[0]?.cells?.[0]?.text, undefined);
  assert.deepEqual(content.elements.find(({ id }) => id === "rule-adult")?.cells?.map(({ text }) => text), [">= 18", "true"]);
  assert.equal(content.elements.find(({ id }) => id === "eligibility-table")?.hitPolicy, "FIRST");

  const shuffled = xml.replace(
    /(<rule id="rule-adult">[\s\S]*?<\/rule>)\s*(<rule id="rule-minor">[\s\S]*?<\/rule>)/,
    "$2$1",
  );
  const reordered = await extractModelXml(shuffled, options("models/decision.dmn", "model-content"));
  const identityByRule = (evidence: typeof content) => new Map(
    evidence.elements.filter(({ type }) => type === "rule").map(({ id, identity }) => [id, identity]),
  );
  assert.deepEqual([...identityByRule(reordered)].sort(), [...identityByRule(content)].sort());
  assert.deepEqual(reordered.elements.filter(({ type }) => type === "rule").map(({ order }) => order), [0, 1]);
  assert.deepEqual(reordered.elements.filter(({ type }) => type === "rule").map(({ id }) => id), ["rule-minor", "rule-adult"]);
});

test("Given identical input and identity, extraction has stable evidence and source digest", async () => {
  const xml = await readFile(fixture("order.bpmn"), "utf8");
  const first = await extractModelXml(xml, options("models/order.bpmn"));
  const repeated = await extractModelXml(xml, options("models/order.bpmn"));

  assert.deepEqual(first, repeated);
  assert.match(first.sourceSha256, /^[a-f0-9]{64}$/);
});

test("Given a shuffled attribute order, model identities remain stable while byte evidence changes", async () => {
  const xml = await readFile(fixture("order.bpmn"), "utf8");
  const shuffled = xml.replace('id="defs-order" targetNamespace="urn:example:orders"', 'targetNamespace="urn:example:orders" id="defs-order"');
  const first = await extractModelXml(xml, options("models/order.bpmn"));
  const second = await extractModelXml(shuffled, options("models/order.bpmn"));

  assert.notEqual(first.sourceSha256, second.sourceSha256);
  assert.deepEqual(first.elements.map(({ identity }) => identity), second.elements.map(({ identity }) => identity));
  assert.deepEqual(first.elements[0]?.attributes, second.elements[0]?.attributes);
});

test("Given unsafe XML and namespace constructs, extraction rejects without fetching or evaluating", async () => {
  const reject = async (xml: string, code: string) => {
    await assert.rejects(extractModelXml(xml, options("unsafe.bpmn")), (error: unknown) =>
      error instanceof ModelXmlError && error.code === code);
  };
  await reject('<!DOCTYPE definitions [<!ENTITY x SYSTEM "file:///etc/passwd">]><definitions/>', "xml-doctype-disabled");
  await reject('<definitions>&external;</definitions>', "xml-entity-disabled");
  await reject('<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"><script>run()</script></definitions>', "xml-script-disabled");
  await reject('<definitions xmlns="https://www.omg.org/spec/DMN/20230324/MODEL/"/>', "unsupported-model-namespace");
  assert.throws(() => parseXml("<a><b><c/></b></a>", { ...DEFAULT_XML_LIMITS, maxDepth: 2 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-depth-limit");
  assert.throws(() => parseXml("<a>long</a>", { ...DEFAULT_XML_LIMITS, maxTextBytes: 2 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-text-limit");
  assert.throws(() => parseXml("<a x=\"1\"/>", { ...DEFAULT_XML_LIMITS, maxAttributesPerElement: 0 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-attribute-limit");
  assert.throws(() => parseXml("<a/>", { ...DEFAULT_XML_LIMITS, maxParseMilliseconds: 0 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-time-limit");
});

test("Given duplicate or dangling BPMN ids, extraction fails rather than asserting complete evidence", async () => {
  const duplicate = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p"><bpmn:task id="x"/><bpmn:task id="x"/></bpmn:process></bpmn:definitions>';
  const dangling = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p"><bpmn:sequenceFlow id="f" sourceRef="missing" targetRef="also-missing"/></bpmn:process></bpmn:definitions>';
  await assert.rejects(extractModelXml(duplicate, options("duplicate.bpmn")), /Duplicate explicit model id/);
  await assert.rejects(extractModelXml(dangling, options("dangling.bpmn")), /Dangling sequenceFlow reference/);
  await assert.rejects(extractModelXml('<definitions/>', options("../outside.bpmn")), /repository-relative path/);
});

test("Given vendor extension content, extraction retains an opaque disposition and partial coverage", async () => {
  const xml = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:v="urn:vendor"><bpmn:extensionElements><v:policy key="x">opaque</v:policy></bpmn:extensionElements></bpmn:definitions>';
  const evidence = await extractModelXml(xml, options("extension.bpmn", "model-content"));

  assert.equal(evidence.coverage.status, "partial");
  assert.ok(evidence.diagnostics.some(({ code }) => code === "opaque-extension-content"));
  assert.ok(evidence.elements.some(({ kind, namespace, content }) => kind === "opaqueExtension" && namespace === "urn:vendor" && content?.text === "opaque"));
});

test("Given vendor names that resemble core constructs, opaque evidence stays valid without invented flow facts", async () => {
  const xml = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:v="urn:vendor"><bpmn:process id="p"><bpmn:task id="a"/><bpmn:task id="b"/><bpmn:extensionElements><v:text>opaque</v:text><v:sequenceFlow id="vendor-flow" sourceRef="a" targetRef="b"/></bpmn:extensionElements></bpmn:process></bpmn:definitions>';
  const evidence = await extractModelXml(xml, options("vendor.bpmn", "model-content"));
  const schema = JSON.parse(await readFile(fixture("../../schema/models/v1.schema.json"), "utf8"));
  const validate = new Ajv2020().compile(schema);
  assert.equal(validate(evidence), true, JSON.stringify(validate.errors));
  assert.equal(evidence.relationships.length, 0);
  assert.ok(evidence.elements.some(element => element.kind === "opaqueExtension" && element.type === "text"));
  assert.equal(evidence.coverage.status, "partial");
});

test("Given missing flow endpoints, extraction never silently claims complete mapping", async () => {
  const incomplete = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p"><bpmn:task id="a"/><bpmn:sequenceFlow id="f" sourceRef="a"/></bpmn:process></bpmn:definitions>';
  await assert.rejects(extractModelXml(incomplete, options("incomplete.bpmn")), /Flow requires sourceRef and targetRef/);
});

test("Given external decision references, coverage explicitly reports unresolved external content", async () => {
  const external = '<definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/" id="d" namespace="urn:test"><decision id="local"><informationRequirement><requiredDecision href="https://example.invalid/decision#remote"/></informationRequirement></decision></definitions>';
  const evidence = await extractModelXml(external, options("external.dmn", "model-content"));
  assert.equal(evidence.coverage.status, "partial");
  assert.ok(evidence.diagnostics.some(item => item.code === "external-model-reference"));
});

test("Given XML and claimed original bytes that differ, source evidence cannot bind to the wrong content", async () => {
  const xml = await readFile(fixture("order.bpmn"), "utf8");
  await assert.rejects(extractModelXml(xml, options("mismatch.bpmn"), Buffer.from("different input")),
    /XML does not match the supplied source bytes/);
});

test("Given an output symlink to the input, the standalone command preserves original model bytes", async () => {
  const directory = await mkdtemp(join(tmpdir(), "ce-model-source-"));
  try {
    const source = join(directory, "source.bpmn"), output = join(directory, "output.json");
    const bytes = await readFile(fixture("order.bpmn"));
    await writeFile(source, bytes);
    await symlink(source, output);
    const result = spawnSync(process.execPath, [
      fileURLToPath(new URL("../../src/models/cli.ts", import.meta.url)), "--source", source,
      "--repository", "local:orders", "--commit", "a".repeat(40), "--out", output
    ], { encoding: "utf8", timeout: 15_000 });
    assert.ifError(result.error);
    assert.equal(createHash("sha256").update(await readFile(source)).digest("hex"),
      createHash("sha256").update(bytes).digest("hex"));
    assert.equal(result.status, 1, result.stderr);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test("Given a real fixture and schema, the standalone command emits valid JSON evidence", async () => {
  const packageDir = fileURLToPath(new URL("../../", import.meta.url));
  const output = fileURLToPath(new URL(`./cli-output-${randomUUID()}.json`, import.meta.url));
  try {
    execFileSync(process.execPath, [
      "src/models/cli.ts", "--source", fixture("decision.dmn"), "--repository", "local:orders",
      "--commit", "0123456789abcdef0123456789abcdef01234567", "--out", output, "--content-profile", "model-content",
    ], { cwd: packageDir, stdio: "pipe" });
    const evidence = JSON.parse(await readFile(output, "utf8"));
    const schema = JSON.parse(await readFile(fileURLToPath(new URL("../../../../schema/models/v1.schema.json", import.meta.url)), "utf8"));
    const validate = new Ajv2020({ allErrors: true }).compile(schema);

    assert.equal(validate(evidence), true, JSON.stringify(validate.errors));
    assert.equal(evidence.schema, "ce.models.v1");
    const sourceBytes = await readFile(fixture("decision.dmn"));
    assert.equal(evidence.sourceSha256, createHash("sha256").update(sourceBytes).digest("hex"));
    assert.deepEqual(evidence.elements.filter((element: { type: string }) => element.type === "rule").map((rule: { order: number }) => rule.order), [0, 1]);
  } finally {
    await unlink(output).catch((error: NodeJS.ErrnoException) => {
      if (error.code !== "ENOENT") throw error;
    });
  }
});
