import assert from "node:assert/strict";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { readFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { join, resolve } from "node:path";
import test from "node:test";
import { Ajv2020 } from "ajv/dist/2020.js";
import addFormatsModule from "ajv-formats";
import { buildProcessLandscapeSnapshot } from "../../src/models/process-landscape.ts";
import { ProcessLandscapeError } from "../../src/models/process-landscape-types.ts";

const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as (ajv: Ajv2020) => Ajv2020;

const bpmn = (processId: string, taskId = "review") => `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-${processId}" targetNamespace="urn:landscape">
  <process id="${processId}" name="Sample ${processId}">
    <startEvent id="start-${processId}"/>
    <task id="${taskId}"/>
    <endEvent id="end-${processId}"/>
    <sequenceFlow id="flow-1-${processId}" sourceRef="start-${processId}" targetRef="${taskId}"/>
    <sequenceFlow id="flow-2-${processId}" sourceRef="${taskId}" targetRef="end-${processId}"/>
  </process>
</definitions>
`;

async function makeWorkspace(): Promise<string> {
  const root = resolve(".sandbox-cache", "process-landscape", randomUUID());
  await mkdir(root, { recursive: true });
  return root;
}

const manifest = (entries: Array<{ path: string; role: string }>, repositoryId = "local:order-intake") => ({
  schema: "ce.process-landscape-manifest.v1",
  manifestVersion: 1,
  repositoryId,
  entries,
});


test("Given a snapshot claiming the structural profile, the schema rejects any source carrying sourceBytesBase64", async () => {
  const schemaUrl = new URL("../../../../schema/models/process-landscape.v1.schema.json", import.meta.url);
  const schema = JSON.parse(readFileSync(schemaUrl, "utf8")) as Record<string, unknown>;
  const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false, allowUnionTypes: true });
  addFormats(ajv);
  const validate = ajv.compile(schema);

  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    const input = manifest([{ path: "intake.bpmn", role: "bpmn" }]);

    const withContent = await buildProcessLandscapeSnapshot(root, input, { contentProfile: "model-content" });
    assert.equal(validate(withContent), true, "a model-content snapshot with sourceBytesBase64 must validate");

    // A structural-profile snapshot must never carry sourceBytesBase64, even if constructed by hand downstream.
    const smuggled = {
      ...withContent,
      contentProfile: "structural",
    };
    assert.equal(validate(smuggled), false, "a structural-profile snapshot carrying sourceBytesBase64 must fail schema validation");
    assert.ok(
      validate.errors?.some((e) => e.instancePath.includes("sources")),
      "schema validation errors must point at the offending sources entry",
    );

    const structural = await buildProcessLandscapeSnapshot(root, input);
    assert.equal(structural.contentProfile, "structural");
    assert.equal(validate(structural), true, "a genuine structural-profile snapshot without sourceBytesBase64 must validate");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a verified navigator-data bundle whose xml map matches the admitted BPMN input, its view data is folded into the snapshot", async () => {
  const root = await makeWorkspace();
  try {
    const xml = bpmn("intake");
    await writeFile(join(root, "intake.bpmn"), xml);
    const bundle = JSON.stringify({
      generated: "synthetic",
      xml: { "intake.bpmn": xml },
      files: [{ id: "cat-1" }],
      el: [{ id: "el-1" }],
      links: { "link-1": {} },
      events: [{ id: "evt-1" }],
      lifecycle: [{ id: "life-1" }],
      journey: { phases: [] },
      contexts: ["ctx-1"],
      procFile: { "intake": "intake.bpmn" },
      dmn: { "dmn-1": {} },
      phases: ["phase-1"],
    });
    await writeFile(join(root, "navigator.js"), `window.NAV_DATA = ${bundle};\n`);
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "navigator.js", role: "navigator-data" },
    ]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    assert.ok(snapshot.navigator, "a verified navigator bundle must populate the snapshot navigator field");
    assert.equal(snapshot.navigator?.verified, true);
    assert.equal(snapshot.navigator?.bundle.path, "navigator.js");
    assert.match(snapshot.navigator!.bundle.sha256, /^[a-f0-9]{64}$/);
    assert.deepEqual(snapshot.navigator?.catalogue, [{ id: "cat-1" }], "the source \"files\" key must map to the catalogue field");
    assert.deepEqual(snapshot.navigator?.elements, [{ id: "el-1" }], "the source \"el\" key must map to the elements field");
    assert.deepEqual(snapshot.navigator?.links, { "link-1": {} });
    assert.deepEqual(snapshot.navigator?.events, [{ id: "evt-1" }]);
    assert.deepEqual(snapshot.navigator?.lifecycle, [{ id: "life-1" }]);
    assert.deepEqual(snapshot.navigator?.journey, { phases: [] });
    assert.deepEqual(snapshot.navigator?.contexts, ["ctx-1"]);
    assert.deepEqual(snapshot.navigator?.procFile, { "intake": "intake.bpmn" }, "procFile must be preserved under its own name");
    assert.deepEqual(snapshot.navigator?.dmn, { "dmn-1": {} }, "dmn must be preserved under its own name");
    assert.deepEqual(snapshot.navigator?.phases, ["phase-1"], "phases must be preserved under its own name (not the singular \"phase\")");
    assert.equal(snapshot.navigator?.generated, "synthetic", "generated must be copied through for provenance");
    assert.ok(
      JSON.stringify(snapshot).includes(snapshot.navigator!.bundle.sha256),
      "the navigator bundle identity (including hash) must appear in the serialized snapshot",
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a navigator-data bundle whose xml entry differs from the admitted BPMN file only by line-ending style, verification still succeeds", async () => {
  const root = await makeWorkspace();
  try {
    const xml = bpmn("intake");
    await writeFile(join(root, "intake.bpmn"), xml);
    const crlfXml = xml.replace(/\n/g, "\r\n");
    const bundle = JSON.stringify({ xml: { "intake.bpmn": crlfXml }, files: [{ id: "cat-1" }] });
    await writeFile(join(root, "navigator.js"), `window.NAV_DATA = ${bundle};\n`);
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "navigator.js", role: "navigator-data" },
    ]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    assert.equal(snapshot.navigator?.verified, true, "a benign CRLF/LF re-serialization must not be treated as a content mismatch");
    assert.deepEqual(snapshot.navigator?.catalogue, [{ id: "cat-1" }]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a navigator-data bundle whose xml map does not match any admitted BPMN input, the bundle is reported unverified and its category data is withheld", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    const bundle = JSON.stringify({
      xml: { "intake.bpmn": "<definitions>not the admitted bytes</definitions>" },
      files: [{ id: "cat-1" }],
    });
    await writeFile(join(root, "navigator.js"), `window.NAV_DATA = ${bundle};\n`);
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "navigator.js", role: "navigator-data" },
    ]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    assert.equal(snapshot.navigator?.verified, false, "a bundle whose xml entries do not match admitted BPMN bytes must not be verified");
    assert.equal(snapshot.navigator?.catalogue, undefined, "unverified bundles must never silently serve stale/mismatched category data");
    assert.ok(snapshot.diagnostics.some((diagnostic) => diagnostic.code === "navigator-xml-mismatch"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a navigator-data bundle that does not match the exact window.NAV_DATA wrapper, the producer rejects it explicitly", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    await writeFile(join(root, "navigator.js"), "export default { xml: {} };\n");
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "navigator.js", role: "navigator-data" },
    ]);
    await assert.rejects(buildProcessLandscapeSnapshot(root, input), ProcessLandscapeError, "a non-wrapper bundle must be rejected");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a navigator-data bundle exceeding its bounded byte limit, the producer rejects it before parsing", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    const oversized = `window.NAV_DATA = {"xml":{},"padding":"${"a".repeat(17 * 1024 * 1024)}"};\n`;
    await writeFile(join(root, "navigator.js"), oversized);
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "navigator.js", role: "navigator-data" },
    ]);
    await assert.rejects(buildProcessLandscapeSnapshot(root, input), ProcessLandscapeError, "an oversized navigator-data bundle must be rejected");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a navigator-data bundle in the model-content profile, no raw/embedded XML is ever retained in the structural profile", async () => {
  const root = await makeWorkspace();
  try {
    const xml = bpmn("intake");
    await writeFile(join(root, "intake.bpmn"), xml);
    const bundle = JSON.stringify({ xml: { "intake.bpmn": xml } });
    await writeFile(join(root, "navigator.js"), `window.NAV_DATA = ${bundle};\n`);
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "navigator.js", role: "navigator-data" },
    ]);

    const structural = await buildProcessLandscapeSnapshot(root, input);
    assert.doesNotMatch(JSON.stringify(structural), /<definitions/, "the structural profile must never retain raw/embedded XML anywhere, including via the navigator bundle");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
