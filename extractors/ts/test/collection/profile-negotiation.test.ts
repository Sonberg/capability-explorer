import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { canonicalContentBytes } from "../../src/collection/canonicalContent.ts";
import { validateCollectionBundle } from "../../src/collection/validator.ts";

const fixture = fileURLToPath(new URL("./fixtures/valid", import.meta.url));

test("the unchanged evidence-only collection remains independently readable", async () => {
  const result = await validateCollectionBundle(fixture);
  assert.equal(result.ok, true, result.problems.map(({ message }) => message).join("; "));
});

for (const mutation of ["unknown-profile", "no-model-permission", "unrelated-permission"] as const) {
  test(`collection negotiation refuses ${mutation} despite a valid manifest digest`, async () => {
    const root = resolve(".sandbox-cache", "profile-negotiation", randomUUID());
    await mkdir(root, { recursive: true });
    const bundle = resolve(root, "bundle");
    try {
      await cp(fixture, bundle, { recursive: true });
      const path = resolve(bundle, "collection.json");
      const envelope = JSON.parse(await readFile(path, "utf8")) as {
        contentSha256: string;
        content: {
          inputs: Array<{ profile: string; artifactPath: string; contentProfile: string | null }>;
          repositories: Array<{ permittedContentProfiles: string[] }>;
        };
      };
      if (mutation === "unknown-profile") {
        envelope.content.inputs[0]!.profile = "ce.unsupported-model-source.v1";
      } else {
        envelope.content.repositories[0]!.permittedContentProfiles =
          mutation === "no-model-permission" ? [] : ["candidate-content"];
      }
      envelope.contentSha256 = createHash("sha256")
        .update(canonicalContentBytes(envelope.content)).digest("hex");
      await writeFile(path, JSON.stringify(envelope));

      const result = await validateCollectionBundle(bundle);

      assert.equal(result.ok, false, "resealing cannot negotiate unknown profiles or grant content access");
      assert.ok(result.problems.some(({ message }) => message.includes(
        mutation === "unknown-profile" ? "included artifact descriptor" : "contentProfile",
      )), result.problems.map(({ message }) => message).join("; "));
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
}
