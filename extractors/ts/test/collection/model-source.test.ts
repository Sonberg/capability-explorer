import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { test } from "node:test";
import { canonicalContentBytes } from "../../src/collection/canonicalContent.ts";
import { validateCollectionBundle } from "../../src/collection/validator.ts";

const digest = (bytes: string | Buffer) => createHash("sha256").update(bytes).digest("hex");
const xml = Buffer.from('\uFEFF<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL/" id="d"/>'.replace("MODEL/", "MODEL"));

for (const mutation of ["none", "hash", "base64", "companion", "dtd", "permission"] as const) {
  test(`source attachment independently validates exact bytes and binding: ${mutation}`, async () => {
    const root = resolve(".sandbox-cache", "model-source-tests", randomUUID());
    await mkdir(root, { recursive: true });
    try {
      const fixture = new URL("./fixtures/valid/collection.json", import.meta.url);
      const envelope = JSON.parse(await readFile(fixture, "utf8"));
      envelope.content.repositories = [envelope.content.repositories[0]];
      const original = envelope.content.inputs[0];
      const model = JSON.parse(await readFile(new URL(`./fixtures/valid/${original.artifactPath}`, import.meta.url), "utf8"));
      let source = xml;
      if (mutation === "dtd") source = Buffer.from('<!DOCTYPE definitions [<!ENTITY x "x">]><definitions/>');
      model.sourceSha256 = digest(source);
      const modelBytes = JSON.stringify(model);
      const attachment = {
        schema: "ce.model-source.v1", repositoryId: model.repositoryId, commitSha: model.commitSha,
        unitPath: model.unitPath, modelArtifactSha256: digest(modelBytes), sourceSha256: digest(source),
        sourceFormat: "bpmn+xml", sourceBytesBase64: source.toString("base64"),
      };
      if (mutation === "hash") attachment.sourceSha256 = "f".repeat(64);
      if (mutation === "base64") attachment.sourceBytesBase64 += "\n";
      if (mutation === "companion") attachment.modelArtifactSha256 = "f".repeat(64);
      if (mutation === "permission") envelope.content.repositories[0].permittedContentProfiles = [];
      envelope.content.inputs = [];
      for (const [index, [profile, bytes]] of [
        ["ce.model-source.v1", JSON.stringify(attachment)], ["ce.models.v1", modelBytes],
      ].entries()) {
        const artifactPath = `artifacts/${model.repositoryId}/${profile}/${String(index).padStart(4, "0")}.json`;
        await mkdir(dirname(resolve(root, artifactPath)), { recursive: true });
        await writeFile(resolve(root, artifactPath), bytes!);
        envelope.content.inputs.push({ ...original, profile, artifactPath, sha256: digest(bytes!),
          byteLength: Buffer.byteLength(bytes!), contentProfile: "model-content" });
      }
      envelope.contentSha256 = digest(canonicalContentBytes(envelope.content));
      await writeFile(resolve(root, "collection.json"), JSON.stringify(envelope));
      await writeFile(resolve(root, "attempt.json"), JSON.stringify({
        attemptId: "a".repeat(32), observedAt: "2026-11-04T00:00:00Z",
      }));
      const result = await validateCollectionBundle(root);
      assert.equal(result.ok, mutation === "none", result.problems.map(p => p.message).join("; "));
      if (mutation === "none") {
        const stored = JSON.parse(await readFile(resolve(root, envelope.content.inputs[0].artifactPath), "utf8"));
        assert.deepEqual(Buffer.from(stored.sourceBytesBase64, "base64"), xml);
      }
    } finally { await rm(root, { recursive: true, force: true }); }
  });
}
