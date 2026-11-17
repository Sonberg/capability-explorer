import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";

const run = promisify(execFile);

const bpmn = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-intake" targetNamespace="urn:landscape">
  <process id="intake" name="Intake">
    <startEvent id="start"/>
    <task id="review"/>
    <endEvent id="end"/>
    <sequenceFlow id="flow-1" sourceRef="start" targetRef="review"/>
    <sequenceFlow id="flow-2" sourceRef="review" targetRef="end"/>
  </process>
</definitions>
`;

async function makeWorkspace(): Promise<string> {
  const root = resolve(".sandbox-cache", "process-landscape-cli", randomUUID());
  await mkdir(root, { recursive: true });
  return root;
}

test("the process-landscape CLI writes its snapshot atomically and fails explicitly on invalid input", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(resolve(root, "intake.bpmn"), bpmn);
    const manifestPath = resolve(root, "manifest.json");
    await writeFile(manifestPath, JSON.stringify({
      schema: "ce.process-landscape-manifest.v1",
      manifestVersion: 1,
      repositoryId: "local:order-intake",
      entries: [{ path: "intake.bpmn", role: "bpmn" }],
    }));
    const outPath = resolve(root, "snapshot.json");
    const cli = new URL("../../src/models/process-landscape-cli.ts", import.meta.url).pathname;

    await run(process.execPath, [cli, "--manifest", manifestPath, "--base", root, "--out", outPath]);
    const snapshot = JSON.parse(await readFile(outPath, "utf8"));
    assert.equal(snapshot.schema, "ce.process-landscape.v1");
    assert.match(snapshot.snapshotId, /^landscape:[a-f0-9]{64}$/);
    assert.equal(snapshot.sources[0]?.sourceBytesBase64, undefined);
    assert.equal(Object.hasOwn(snapshot, "documents"), false);

    // Invalid manifest input must fail explicitly and must not disturb the existing output file.
    const invalidManifestPath = resolve(root, "invalid-manifest.json");
    await writeFile(invalidManifestPath, JSON.stringify({ schema: "ce.process-landscape-manifest.v1", manifestVersion: 1, repositoryId: "x", entries: [] }));
    await assert.rejects(run(process.execPath, [cli, "--manifest", invalidManifestPath, "--base", root, "--out", outPath]));
    assert.deepEqual(JSON.parse(await readFile(outPath, "utf8")), snapshot, "a failed run must not modify the prior output");

    await run(process.execPath, [cli, "--manifest", manifestPath, "--base", root, "--out", outPath, "--content-profile", "model-content"]);
    const withContent = JSON.parse(await readFile(outPath, "utf8"));
    assert.deepEqual(Buffer.from(withContent.sources[0].sourceBytesBase64, "base64"), Buffer.from(bpmn, "utf8"));
    await run(process.execPath, [cli, "--manifest", manifestPath, "--base", root, "--out", outPath, "--include-document-graph"]);
    assert.deepEqual(JSON.parse(await readFile(outPath, "utf8")).documents, [], "single-process files keep their existing implicit scope");
    await writeFile(resolve(root, "intake.bpmn"), bpmn.replace("</definitions>", `
      <process id="second"><task id="other"/></process>
      <collaboration id="collaboration">
        <participant id="pool1" processRef="intake"/><participant id="pool2"/>
        <messageFlow id="message" sourceRef="pool1" targetRef="pool2"/>
      </collaboration></definitions>`));
    await run(process.execPath, [cli, "--manifest", manifestPath, "--base", root, "--out", outPath, "--include-document-graph"]);
    const multi = JSON.parse(await readFile(outPath, "utf8"));
    assert.equal(multi.documents.length, 1);
    assert.equal(multi.documents[0].relationships[0].kind, "messageFlow");
    assert.equal(multi.sources.length, 1);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("the process-landscape CLI rejects an --out path that resolves to an admitted manifest input", async () => {
  const root = await makeWorkspace();
  try {
    const inputPath = resolve(root, "intake.bpmn");
    const originalBytes = bpmn;
    await writeFile(inputPath, originalBytes);
    const manifestPath = resolve(root, "manifest.json");
    await writeFile(manifestPath, JSON.stringify({
      schema: "ce.process-landscape-manifest.v1",
      manifestVersion: 1,
      repositoryId: "local:order-intake",
      entries: [{ path: "intake.bpmn", role: "bpmn" }],
    }));
    const cli = new URL("../../src/models/process-landscape-cli.ts", import.meta.url).pathname;

    // Directly resolving to the admitted BPMN input must be rejected.
    await assert.rejects(run(process.execPath, [cli, "--manifest", manifestPath, "--base", root, "--out", inputPath]));
    assert.equal(await readFile(inputPath, "utf8"), originalBytes, "the admitted BPMN input must not be overwritten");

    // A differently-spelled but equivalent path to the same admitted input must also be rejected.
    const indirectOutPath = resolve(root, "nested", "..", "intake.bpmn");
    await assert.rejects(run(process.execPath, [cli, "--manifest", manifestPath, "--base", root, "--out", indirectOutPath]));
    assert.equal(await readFile(inputPath, "utf8"), originalBytes, "an equivalent resolved path must not overwrite the admitted input either");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
