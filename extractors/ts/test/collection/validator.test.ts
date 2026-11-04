import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { validateCollectionBundle } from "../../src/collection/validator.ts";

const fixture = fileURLToPath(new URL("./fixtures/valid", import.meta.url));

test("independently validates a sealed two-repository collection", async () => {
  const result = await validateCollectionBundle(fixture);
  assert.equal(result.ok, true, result.problems.map((problem) => problem.message).join("; "));
  assert.equal(result.contentSha256, "775010c538a4f024dd4281a0430b7b1ff01a410f2e3fe7c82e298cceb06e71d2");
});

test("recomputes cross-runtime identity for Unicode and JSON metacharacters", async () => {
  const result = await validateCollectionBundle(fileURLToPath(new URL("./fixtures/unicode", import.meta.url)));
  assert.equal(result.ok, true, result.problems.map((problem) => problem.message).join("; "));
  assert.equal(result.contentSha256, "ca81a48e8258c3006236d0737befa83616b88762e09311402721434809756b85");
});

test("rejects artifact bytes altered after the descriptor was sealed", async () => {
  const artifact = join(fixture, "artifacts/repo-a/ce.models.v1/0000.json");
  const original = await readFile(artifact);
  try {
    await writeFile(artifact, Buffer.from("{}"));
    const result = await validateCollectionBundle(fixture);
    assert.equal(result.ok, false);
    assert.ok(result.problems.some((problem) => problem.message.includes("bytes")));
  } finally {
    await writeFile(artifact, original);
  }
});

test("rejects a traversal reference even when its content digest is recomputed", async () => {
  const manifest = join(fixture, "collection.json");
  const original = await readFile(manifest);
  try {
    const envelope = JSON.parse(original.toString("utf8")) as {
      content: { inputs: Array<{ artifactPath: string }>;
        [key: string]: unknown };
      [key: string]: unknown;
    };
    envelope.content.inputs[0]!.artifactPath = "../escape.json";
    envelope.contentSha256 = createHash("sha256").update(JSON.stringify(envelope.content)).digest("hex");
    await writeFile(manifest, JSON.stringify(envelope));

    const result = await validateCollectionBundle(fixture);

    assert.equal(result.ok, false);
    assert.ok(result.problems.some((problem) => problem.message.includes("descriptor")));
  } finally {
    await writeFile(manifest, original);
  }
});

test("rejects an invalid collection directory without following directory symlinks", async () => {
  const result = await validateCollectionBundle(dirname(fixture));
  assert.equal(result.ok, false);
});
