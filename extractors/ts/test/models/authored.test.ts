import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, symlink, unlink, writeFile } from "node:fs/promises";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
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

test("Given the BPMN model namespace, extraction preserves nested typed elements and flow conditions", async () => {
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
  await reject('<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process>', "xml-malformed");
  await reject('<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"></bpmn:process>', "xml-malformed");
  assert.throws(() => parseXml("<a><b><c/></b></a>", { ...DEFAULT_XML_LIMITS, maxDepth: 2 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-depth-limit");
  assert.throws(() => parseXml("<a>long</a>", { ...DEFAULT_XML_LIMITS, maxTextBytes: 2 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-text-limit");
  assert.throws(() => parseXml("<a x=\"1\"/>", { ...DEFAULT_XML_LIMITS, maxAttributesPerElement: 0 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-attribute-limit");
  assert.throws(() => parseXml("<a/>", { ...DEFAULT_XML_LIMITS, maxParseMilliseconds: 0 }), (error: unknown) =>
    error instanceof ModelXmlError && error.code === "xml-time-limit");
});

test("Given exact XML byte, depth, count and attribute byte budgets, only one-over inputs fail", () => {
  const parse = (xml: string, limits: Partial<typeof DEFAULT_XML_LIMITS>) =>
    parseXml(xml, { ...DEFAULT_XML_LIMITS, ...limits });
  const overLimit = (xml: string, limits: Partial<typeof DEFAULT_XML_LIMITS>, code: string) =>
    assert.throws(() => parse(xml, limits), (error: unknown) =>
      error instanceof ModelXmlError && error.code === code);

  assert.equal(parse("<a/>", { maxBytes: 4 }).localName, "a");
  overLimit("<a />", { maxBytes: 4 }, "xml-byte-limit");
  assert.equal(parse("<a><b/></a>", { maxDepth: 2 }).children.length, 1);
  overLimit("<a><b/></a>", { maxDepth: 1 }, "xml-depth-limit");
  assert.equal(parse("<a><b/></a>", { maxElements: 2 }).children.length, 1);
  overLimit("<a><b/></a>", { maxElements: 1 }, "xml-element-limit");
  assert.equal(parse('<a x="é"/>', { maxAttributesPerElement: 1, maxAttributeBytes: 3 }).attributes.x, "é");
  overLimit('<a x="é"/>', { maxAttributeBytes: 2 }, "xml-attribute-byte-limit");
  assert.equal(parse("<a>é</a>", { maxTextBytes: 2 }).text, "é");
  overLimit("<a>é</a>", { maxTextBytes: 1 }, "xml-text-limit");
  overLimit('<a x="é" y="z"/>', { maxAttributesPerElement: 1 }, "xml-attribute-limit");
});

test("Given a positive worker deadline, timed-out extraction awaits actual worker termination", async () => {
  const tasks = Array.from({ length: 20_000 }, (_, index) => `<bpmn:task id="t${index}"/>`).join("");
  const xml = `<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p">${tasks}</bpmn:process></bpmn:definitions>`;
  const started = performance.now();
  const messagePortsBefore = process.getActiveResourcesInfo().filter((resource) => resource === "MessagePort").length;

  await assert.rejects(
    extractModelXml(xml, options("timeout.bpmn"), undefined, 50),
    (error: unknown) => error instanceof ModelXmlError && error.code === "xml-time-limit",
  );
  assert.ok(performance.now() - started < 2_000, "worker timeout must be bounded");
  assert.equal(
    process.getActiveResourcesInfo().filter((resource) => resource === "MessagePort").length,
    messagePortsBefore,
    "timed-out worker must not leave a message port active",
  );
});

test("Given duplicate or dangling BPMN ids, extraction fails rather than asserting complete evidence", async () => {
  const duplicate = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p"><bpmn:task id="x"/><bpmn:task id="x"/></bpmn:process></bpmn:definitions>';
  const dangling = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p"><bpmn:sequenceFlow id="f" sourceRef="missing" targetRef="also-missing"/></bpmn:process></bpmn:definitions>';
  const wrongType = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p"><bpmn:task id="a"/><bpmn:sequenceFlow id="f" sourceRef="p" targetRef="a"/></bpmn:process></bpmn:definitions>';
  await assert.rejects(extractModelXml(duplicate, options("duplicate.bpmn")), /Duplicate explicit model id/);
  await assert.rejects(extractModelXml(dangling, options("dangling.bpmn")), /Dangling sequenceFlow reference/);
  await assert.rejects(extractModelXml(wrongType, options("wrong-type.bpmn")), /Invalid sequenceFlow reference target type/);
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
  const directory = await mkdtemp(join(fileURLToPath(new URL("../../", import.meta.url)), ".model-source-"));
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

test("Given the bounded BPMN profile, extraction relates lanes, nested subprocesses, local calls and flow kinds", async () => {
  const xml = await readFile(fixture("workflow.bpmn"), "utf8");
  const support = JSON.parse(await readFile(fixture("../../schema/models/support.json"), "utf8"));
  const evidence = await extractModelXml(xml, options("models/workflow.bpmn", "model-content"));
  const byId = new Map(evidence.elements.map(element => [element.id, element]));
  const endpoints = evidence.relationships.map(relation => [
    relation.kind,
    [...byId.values()].find(element => element.identity === relation.source)?.id,
    [...byId.values()].find(element => element.identity === relation.target)?.id,
  ]);
  const reviewRouteIndex = endpoints.findIndex(([kind, source, target]) =>
    kind === "sequenceFlow" && source === "review-task" && target === "route-gateway");
  const reviewRoute = evidence.relationships[reviewRouteIndex];
  const expectedSupported = support.constructs.filter((item: { family: string; status: string; elements: string[] }) =>
    item.family === "bpmn" && item.status === "semantic-evidence-supported"
    && item.elements.some((type: string) => evidence.elements.some(element => element.type === type)))
    .map((item: { id: string }) => item.id);

  assert.ok(byId.get("nested-task")?.parent === byId.get("nested-work")?.identity);
  assert.ok(endpoints.some(([kind, source, target]) => kind === "reference" && source === "review-lane" && target === "start"));
  assert.ok(endpoints.some(([kind, source, target]) => kind === "reference" && source === "call-child" && target === "child-process"));
  assert.ok(endpoints.some(([kind, source, target]) => kind === "sequenceFlow" && source === "review-task" && target === "route-gateway"));
  assert.ok(endpoints.some(([kind, source, target]) => kind === "messageFlow" && source === "order-participant" && target === "child-participant"));
  assert.ok(endpoints.some(([kind, source, target]) => kind === "reference" && source === "review-record-ref" && target === "review-record"));
  assert.deepEqual(reviewRoute?.condition, {
    text: "approved = true", language: "text/feel",
  });
  assert.equal(evidence.coverage.status, "full");
  assert.deepEqual([...new Set(evidence.coverage.supported)].sort(), [...new Set(expectedSupported)].sort());
});

test("Given the bounded DMN profile, extraction retains typed dependencies, policy and ordered raw cells", async () => {
  const xml = await readFile(fixture("decision-rich.dmn"), "utf8");
  const evidence = await extractModelXml(xml, options("models/decision-rich.dmn", "model-content"));
  const byId = new Map(evidence.elements.map(element => [element.id, element]));
  const referenceTargets = evidence.relationships
    .map(relation => [...byId.values()].find(element => element.identity === relation.target)?.id);
  const decisionTable = byId.get("order-table");
  const ruleRows = evidence.elements.filter(element => element.type === "rule" && element.parent === decisionTable?.identity);
  const support = JSON.parse(await readFile(fixture("../../schema/models/support.json"), "utf8"));
  const expectedSupported = support.constructs.filter((item: { family: string; status: string; elements: string[] }) =>
    item.family === "dmn" && item.status === "semantic-evidence-supported"
    && item.elements.some((type: string) => evidence.elements.some(element => element.type === type)))
    .map((item: { id: string }) => item.id);

  assert.ok(byId.has("age-data"));
  assert.ok(byId.has("threshold-policy"));
  assert.ok(byId.has("threshold-logic"));
  assert.ok(byId.has("threshold-parameter"));
  assert.deepEqual(referenceTargets, ["age-data", "base-decision", "threshold-policy", "approval-policy"]);
  assert.ok(evidence.relationships.every(relation =>
    [...byId.values()].find(element => element.identity === relation.source)?.id === "order-decision"));
  assert.equal(byId.get("age-variable")?.attributes.typeRef, "age-type");
  assert.equal(decisionTable?.hitPolicy, "COLLECT");
  assert.equal(decisionTable?.aggregation, "SUM");
  assert.deepEqual(ruleRows.map(rule => rule.id), ["adult-rule", "minor-rule"]);
  assert.deepEqual(ruleRows.map(rule => rule.order), [0, 1]);
  assert.deepEqual(ruleRows[0]?.cells?.map(cell => [cell.kind, cell.order, cell.text]), [
    ["input", 0, ">= 18"],
    ["output", 1, "2"],
  ]);
  assert.equal(byId.get("threshold-expression")?.content?.language, "https://www.omg.org/spec/FEEL/");
  assert.equal(byId.get("threshold-expression")?.content?.text, "age >= 18");
  assert.deepEqual([...new Set(evidence.coverage.supported)].sort(), [...new Set(expectedSupported)].sort());
  assert.equal(evidence.coverage.status, "full");
});

test("Given an imported callActivity target, extraction retains the call with explicit partial coverage", async () => {
  const xml = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="local"><bpmn:callActivity id="call" calledElement="external:process"/></bpmn:process></bpmn:definitions>';
  const evidence = await extractModelXml(xml, options("imported-call.bpmn"));
  assert.equal(evidence.coverage.status, "partial");
  assert.ok(evidence.diagnostics.some(item => item.code === "external-called-element"));
  const unresolved = xml.replace("external:process", "missing-process");
  const unresolvedEvidence = await extractModelXml(unresolved, options("unresolved-call.bpmn"));
  assert.equal(unresolvedEvidence.coverage.status, "partial");
  assert.ok(unresolvedEvidence.diagnostics.some(item => item.code === "unresolved-called-element"));
});

test("Given the machine-readable census, each encountered core construct is supported or diagnosed", async () => {
  const support = JSON.parse(await readFile(fixture("../../schema/models/support.json"), "utf8"));
  const cases = [
    ["bpmn", "workflow.bpmn", "http://www.omg.org/spec/BPMN/20100524/MODEL"],
    ["dmn", "decision-rich.dmn", "https://www.omg.org/spec/DMN/20191111/MODEL/"],
  ] as const;
  for (const [family, file, namespace] of cases) {
    const xml = await readFile(fixture(file), "utf8");
    const evidence = await extractModelXml(xml, options(`models/${file}`, "model-content"));
    for (const element of evidence.elements.filter(item => item.namespace === namespace)) {
      const entries = support.constructs.filter((item: { family: string; namespace: string; elements: string[] }) =>
        item.family === family && item.namespace === namespace && item.elements.includes(element.type));
      const supportedEntries = entries.filter((item: { status: string }) => item.status === "semantic-evidence-supported");
      if (supportedEntries.length) {
        for (const item of supportedEntries) assert.ok(evidence.coverage.supported.includes(item.id));
      } else {
        assert.ok(evidence.diagnostics.some(item => item.source?.element === element.id),
          `Missing census disposition for ${family}:${element.type}/${element.id}`);
      }
    }
  }
  const unknown = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:unlistedThing id="future"/></bpmn:definitions>';
  const partial = await extractModelXml(unknown, options("unlisted.bpmn"));
  assert.equal(partial.coverage.status, "partial");
  assert.ok(partial.diagnostics.some(item => item.code === "unsupported-model-element" && item.source?.element === "future"));
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


test("Given issue78 BPMN and DMN diagram XML, standard layout is retained separately from lookalike extensions", async () => {
  const bpmnXml = await readFile(fileURLToPath(new URL("../../../../tests/fixtures/issue78/bpmn-representative.xml", import.meta.url)), "utf8");
  const dmnXml = await readFile(fileURLToPath(new URL("../../../../tests/fixtures/issue78/dmn-representative.xml", import.meta.url)), "utf8");
  const bpmn = await extractModelXml(bpmnXml, options("issue78.bpmn", "model-content"));
  const dmn = await extractModelXml(dmnXml, options("issue78.dmn", "model-content"));
  const standardLayoutNamespaces = new Set([
    "http://www.omg.org/spec/BPMN/20100524/DI",
    "http://www.omg.org/spec/DD/20100524/DC",
    "http://www.omg.org/spec/DD/20100524/DI",
    "https://www.omg.org/spec/DMN/20191111/DMNDI/",
    "http://www.omg.org/spec/DMN/20180521/DC/",
    "http://www.omg.org/spec/DMN/20180521/DI/",
  ]);
  const layout = [...bpmn.elements, ...dmn.elements].filter(element => standardLayoutNamespaces.has(element.namespace));

  assert.ok(layout.length > 0);
  assert.ok(layout.every(element => element.kind === "bpmn" || element.kind === "dmn"));
  const bpmnShape = bpmn.elements.find(element => element.id === "Shape_Task_Review");
  assert.equal(bpmnShape?.namespace, "http://www.omg.org/spec/BPMN/20100524/DI");
  assert.equal(bpmnShape?.attributes.bpmnElement, "Task_Review");
  assert.equal(bpmnShape?.parent, bpmn.elements.find(element => element.id === "Plane_Issue78")?.identity);
  const dmnShape = dmn.elements.find(element => element.id === "Shape_Decision_Eligibility");
  assert.equal(dmnShape?.namespace, "https://www.omg.org/spec/DMN/20191111/DMNDI/");
  assert.equal(dmnShape?.attributes.dmnElementRef, "Decision_Eligibility");
  assert.ok(![...bpmn.diagnostics, ...dmn.diagnostics].some(item => item.code === "opaque-extension-content"));
  assert.equal(bpmn.coverage.status, "full");
  assert.equal(dmn.coverage.status, "full");
  assert.ok(bpmn.coverage.supported.includes("bpmn.diagramInterchange"));
  assert.ok(bpmn.coverage.supported.includes("bpmn.diagramCommon"));
  assert.ok(bpmn.coverage.supported.includes("bpmn.diagramPrimitives"));
  assert.ok(dmn.coverage.supported.includes("dmn.diagramInterchange"));
  assert.ok(dmn.coverage.supported.includes("dmn.diagramCommon"));
  assert.ok(dmn.coverage.supported.includes("dmn.diagramPrimitives"));

  const unknownLayout = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"><bpmn:extensionElements><bpmndi:UnlistedLayout id="future-layout"/></bpmn:extensionElements></bpmn:definitions>';
  const partialLayout = await extractModelXml(unknownLayout, options("unknown-layout.bpmn"));
  assert.ok(partialLayout.elements.some(element => element.id === "future-layout" && element.kind === "unsupported"));
  assert.ok(partialLayout.diagnostics.some(item => item.code === "unsupported-model-element" && item.source?.element === "future-layout"));
  assert.equal(partialLayout.coverage.status, "partial");

  const lookalike = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:v="urn:lookalike"><bpmn:extensionElements><v:BPMNShape id="vendor-shape">opaque</v:BPMNShape></bpmn:extensionElements></bpmn:definitions>';
  const opaque = await extractModelXml(lookalike, options("lookalike.bpmn", "model-content"));
  assert.ok(opaque.elements.some(element => element.type === "BPMNShape" && element.namespace === "urn:lookalike" && element.kind === "opaqueExtension"));
  assert.equal(opaque.coverage.status, "partial");
});

test("Given BPMN incoming and outgoing declarations, references are optional but must agree with sequenceFlow endpoints", async () => {
  const xml = await readFile(fileURLToPath(new URL("../../../../tests/fixtures/issue78/bpmn-representative.xml", import.meta.url)), "utf8");
  const evidence = await extractModelXml(xml, options("issue78.bpmn"));
  assert.equal(evidence.elements.filter(element => element.type === "incoming" || element.type === "outgoing").length, 14);
  assert.equal(evidence.elements.find(element => element.type === "incoming")?.content?.text, "Flow_Start_Review");
  assert.equal(evidence.coverage.status, "full");
  assert.ok(evidence.coverage.supported.includes("bpmn.flowDirectionReferences"));
  assert.equal(evidence.relationships.filter(relation => relation.kind === "sequenceFlow").length, 7);

  const withoutOptionalDeclarations = '<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"><bpmn:process id="p"><bpmn:task id="a"/><bpmn:task id="b"/><bpmn:sequenceFlow id="f" sourceRef="a" targetRef="b"/></bpmn:process></bpmn:definitions>';
  const optional = await extractModelXml(withoutOptionalDeclarations, options("optional.bpmn"));
  assert.equal(optional.coverage.status, "full");
  assert.equal(optional.relationships.filter(relation => relation.kind === "sequenceFlow").length, 1);

  const rejects = async (changed: string, code: string) => assert.rejects(
    extractModelXml(changed, options("invalid-direction.bpmn")),
    (error: unknown) => error instanceof ModelXmlError && error.code === code,
  );
  await rejects(xml.replace("<bpmn:incoming>Flow_Start_Review</bpmn:incoming>", "<bpmn:incoming>missing-flow</bpmn:incoming>"), "dangling-flow-direction-reference");
  await rejects(xml.replace("<bpmn:incoming>Flow_Start_Review</bpmn:incoming>", "<bpmn:incoming>Flow_Review_Check</bpmn:incoming>"), "inconsistent-flow-direction-reference");
  await rejects(xml.replace("<bpmn:outgoing>Flow_Start_Review</bpmn:outgoing>", "<bpmn:outgoing>missing-flow</bpmn:outgoing>"), "dangling-flow-direction-reference");
  await rejects(xml.replace("<bpmn:outgoing>Flow_Start_Review</bpmn:outgoing>", "<bpmn:outgoing>Flow_Review_Check</bpmn:outgoing>"), "inconsistent-flow-direction-reference");
});

test("Given DMN allowed-values clauses, extraction retains ordered literal source text only with model-content consent", async () => {
  const xml = await readFile(fileURLToPath(new URL("../../../../tests/fixtures/issue78/dmn-representative.xml", import.meta.url)), "utf8");
  const structural = await extractModelXml(xml, options("issue78.dmn"));
  const content = await extractModelXml(xml, options("issue78.dmn", "model-content"));
  const inputValues = content.elements.find(element => element.id === "InputValues_Age");
  const outputValues = content.elements.find(element => element.id === "OutputValues_Eligibility");

  assert.equal(inputValues?.kind, "dmn");
  assert.equal(inputValues?.parent, content.elements.find(element => element.id === "InputClause_Age")?.identity);
  assert.equal(inputValues?.order, 0);
  assert.deepEqual(inputValues?.content, { text: "<18, [18..24], -" });
  assert.equal(outputValues?.kind, "dmn");
  assert.equal(outputValues?.parent, content.elements.find(element => element.id === "Output_Eligibility")?.identity);
  assert.equal(outputValues?.order, 0);
  assert.deepEqual(outputValues?.content, { text: '"deny", "manual", "approve"' });
  assert.equal(structural.elements.find(element => element.id === "InputValues_Age")?.content, undefined);
  assert.ok(structural.diagnostics.some(item => item.code === "content-redacted" && item.source?.element === "InputValues_Age"));
  assert.equal(JSON.stringify(structural).includes("[18..24]"), false);
  const withSecondClause = xml.replace(
    '<dmn:output id="Output_Eligibility"',
    '<dmn:input id="InputClause_Second"><dmn:inputValues id="InputValues_Second"><dmn:text>later clause</dmn:text></dmn:inputValues></dmn:input><dmn:output id="Output_Eligibility"',
  );
  const ordered = await extractModelXml(withSecondClause, options("issue78-ordered.dmn", "model-content"));
  assert.deepEqual(ordered.elements.filter(element => element.id.startsWith("InputClause_")).map(element => [element.id, element.order]), [
    ["InputClause_Age", 0], ["InputClause_Second", 1],
  ]);
  assert.equal(ordered.elements.find(element => element.id === "InputValues_Second")?.parent,
    ordered.elements.find(element => element.id === "InputClause_Second")?.identity);
  assert.equal(ordered.elements.find(element => element.id === "InputValues_Second")?.content?.text, "later clause");
  assert.equal(content.coverage.status, "full");
  assert.ok(content.coverage.supported.includes("dmn.allowedValues"));
});
