import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { cp, mkdir, mkdtemp, open, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { validateCollectionBundle } from "../../src/collection/validator.ts";
import { canonicalContentBytes } from "../../src/collection/canonicalContent.ts";

const fixture = fileURLToPath(new URL("./fixtures/valid", import.meta.url));

test("independently validates a sealed two-repository collection", async () => {
  const result = await validateCollectionBundle(fixture);
  assert.equal(result.ok, true, result.problems.map((problem) => problem.message).join("; "));
  assert.equal(result.contentSha256, "309077bb610183045b650dac95972602f418dcc7f8033a4441244a3d5f95fa77");
});

test("recomputes cross-runtime identity for Unicode and JSON metacharacters", async () => {
  const result = await validateCollectionBundle(fileURLToPath(new URL("./fixtures/unicode", import.meta.url)));
  assert.equal(result.ok, true, result.problems.map((problem) => problem.message).join("; "));
  assert.equal(result.contentSha256, "01ec741e70af99231a58b85d9f241b0bee4bb158573697df39f20d5ba8f79044");
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
    envelope.contentSha256 = createHash("sha256").update(canonicalContentBytes(envelope.content)).digest("hex");
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

for (const mutation of ["invalid-element", "no-permission", "wrong-permission", "duplicate-member"] as const) {
  test(`refuses resealed evidence with ${mutation}`, async () => {
    const root = await mkdtemp(join(tmpdir(), "ce-collection-"));
    const bundle = join(root, "bundle");
    try {
      await cp(fixture, bundle, { recursive: true });
      const manifestPath = join(bundle, "collection.json");
      const envelope = JSON.parse(await readFile(manifestPath, "utf8")) as {
        contentSha256: string;
        content: {
          repositories: Array<{ permittedContentProfiles: string[] }>;
          inputs: Array<{ artifactPath: string; sha256: string; byteLength: number; contentProfile: string | null }>;
        };
      };
      const input = envelope.content.inputs[0]!;
      if (mutation === "invalid-element") {
        const path = join(bundle, input.artifactPath);
        const artifact = JSON.parse(await readFile(path, "utf8")) as { elements: unknown[] };
        artifact.elements = [{}];
        const bytes = JSON.stringify(artifact);
        await writeFile(path, bytes);
        input.byteLength = Buffer.byteLength(bytes);
        input.sha256 = createHash("sha256").update(bytes).digest("hex");
      } else if (mutation !== "duplicate-member") {
        const permission = mutation === "no-permission" ? null : "unrelated-content";
        input.contentProfile = permission;
        envelope.content.repositories[0]!.permittedContentProfiles = permission ? [permission] : [];
      }

      envelope.contentSha256 = createHash("sha256").update(canonicalContentBytes(envelope.content)).digest("hex");
      const encoded = JSON.stringify(envelope);
      await writeFile(manifestPath, mutation === "duplicate-member"
        ? encoded.replace('"schema":"ce.collection.v1"', '"schema":"ignored","schema":"ce.collection.v1"')
        : encoded);

      const result = await validateCollectionBundle(bundle);

      assert.equal(result.ok, false, "valid byte digests do not prove schema validity or content permission");
      assert.ok(result.problems.some(({ message }) =>
        message.includes(mutation === "invalid-element" ? "schema"
          : mutation === "duplicate-member" ? "JSON" : "contentProfile")));
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
}

for (const tailMiB of [8, 16]) {
  test(`stops aggregate reads before opening later artifacts with ${tailMiB} MiB tail`, async () => {
    const root = await mkdtemp(join(tmpdir(), "ce-collection-budget-"));
    try {
      await cp(fixture, root, { recursive: true });
      const path = join(root, "collection.json");
      const envelope = JSON.parse(await readFile(path, "utf8")) as {
        contentSha256: string;
        content: { inputs: Array<{ artifactPath: string; unitPath: string; sha256: string; byteLength: number }> };
      };
      const template = envelope.content.inputs[0]!;
      envelope.content.inputs = [];
      for (let index = 0; index < 9; index++) {
        const artifactPath = `artifacts/repo-a/ce.models.v1/${index.toString().padStart(4, "0")}.json`;
        const byteLength = (index === 7 ? tailMiB : 16) * 1_048_576;
        envelope.content.inputs.push({ ...template, artifactPath, byteLength,
          unitPath: `${index.toString().padStart(2, "0")}.json`, sha256: "a".repeat(64) });
        if (index === 8) continue;
        await mkdir(dirname(join(root, artifactPath)), { recursive: true });
        const file = await open(join(root, artifactPath), "w");
        try { await file.truncate(byteLength); } finally { await file.close(); }
      }
      envelope.contentSha256 = createHash("sha256").update(canonicalContentBytes(envelope.content)).digest("hex");
      await writeFile(path, JSON.stringify(envelope));

      const result = await validateCollectionBundle(root);

      assert.equal(result.ok, false);
      assert.ok(result.problems.some(({ message }) => message.includes("aggregate read budget")));
      assert.ok(!result.problems.some(({ message }) => message.includes("referenced artifact is missing")));
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
}
