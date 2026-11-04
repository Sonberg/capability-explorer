import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { randomUUID, createHash } from "node:crypto";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { promisify } from "node:util";
import { test } from "node:test";

const run = promisify(execFile);
const digest = (bytes: Buffer) => createHash("sha256").update(bytes).digest("hex");

test("the actual model CLI attaches exact BOM source only under explicit content opt-in", async () => {
  const root = resolve(".sandbox-cache", "model-source-cli", randomUUID());
  await mkdir(root, { recursive: true });
  try {
    const source = resolve(root, "original.bpmn");
    const model = resolve(root, "evidence.json");
    const attachment = resolve(root, "source.json");
    const original = Buffer.from('\uFEFF<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="d"><process id="p"><task id="t"/></process></definitions>\r\n');
    await writeFile(source, original);
    const cli = new URL("../../src/models/cli.ts", import.meta.url).pathname;
    const args = [cli, "--source", source, "--repository", "repo", "--commit", "a".repeat(40), "--out", model];
    await assert.rejects(run(process.execPath, [...args, "--source-out", attachment]), /requires explicit/);
    await run(process.execPath, [...args, "--content-profile", "model-content", "--source-out", attachment]);
    const modelBytes = await readFile(model);
    const envelope = JSON.parse(await readFile(attachment, "utf8"));
    assert.equal(envelope.schema, "ce.model-source.v1");
    assert.equal(envelope.modelArtifactSha256, digest(modelBytes));
    assert.equal(envelope.sourceSha256, digest(original));
    assert.deepEqual(Buffer.from(envelope.sourceBytesBase64, "base64"), original);
    assert.deepEqual(await readFile(source), original);
    await writeFile(source, original.toString("utf8").replace('id="t"', 'id="changed"'));
    await assert.rejects(run(process.execPath, [...args, "--content-profile", "model-content", "--source-out", attachment]));
    assert.deepEqual(await readFile(model), modelBytes, "refused source output must not replace its existing companion");
    assert.deepEqual(Buffer.from(JSON.parse(await readFile(attachment, "utf8")).sourceBytesBase64, "base64"), original);
  } finally { await rm(root, { recursive: true, force: true }); }
});
