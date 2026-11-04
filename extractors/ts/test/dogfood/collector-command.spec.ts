import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { validateCollectionBundle } from "../../src/collection/validator.ts";
import { canonicalContentBytes } from "../../src/collection/canonicalContent.ts";

const root = resolve(import.meta.dirname, "../../../..");
const cli = join(root, "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll");
const hash = (value: string | Buffer): string => createHash("sha256").update(value).digest("hex");

function runCli(args: string[], expected = 0): string {
  const result = spawnSync("dotnet", [cli, ...args], { cwd: root, encoding: "utf8", timeout: 60_000 });
  assert.ifError(result.error);
  assert.equal(result.status, expected, `${result.stderr}\n${result.stdout}`);
  return result.stdout.trim();
}

test("Given a valid native fixture and explicit local plan, both consumers accept clean output and refuse non-header privacy canaries", async () => {
  const directory = mkdtempSync(join(tmpdir(), "ce-collector-command-"));
  const run = runCli;
  try {
    const source = join(root, "fixtures/expected/api-cs/v1-base/Sales.Domain.jsonl");
    run(["validate", source]);
    const bytes = readFileSync(source);
    const header: { repositoryId: string; commitSha: string; unit: { path: string } } =
      JSON.parse(bytes.toString("utf8").split("\n")[0]!);
    writeFileSync(join(directory, "native.jsonl"), bytes);
    writeFileSync(join(directory, "plan.json"), JSON.stringify({
      schema: "ce.collection.plan.v1", recipeSha256: hash("native-golden"),
      configSha256: hash("collector-smoke"), policySha256: hash("synthetic-content-rights"),
      repositories: [{
        repositoryId: header.repositoryId, providerId: "local:synthetic", producerId: "dogfood",
        tenantRef: "synthetic", rightsRef: "explicit-synthetic-content",
        commitSha: header.commitSha, treeSha: "b".repeat(40),
        permittedContentProfiles: ["native-content"]
      }],
      inputs: [{
        repositoryId: header.repositoryId, profile: "ce.v1", path: "native.jsonl",
        unitPath: header.unit.path, sha256: hash(bytes), byteLength: bytes.length,
        required: true, contentProfile: "native-content"
      }]
    }));
    const output = join(directory, "collection");
    run(["collect", "--plan", join(directory, "plan.json"), "--out", output]);
    run(["validate-collection", output]);
    assert.equal((await validateCollectionBundle(output)).ok, true);
    const first = readFileSync(join(output, "collection.json"), "utf8");
    run(["collect", "--plan", join(directory, "plan.json"), "--out", output]);
    assert.equal(readFileSync(join(output, "collection.json"), "utf8"), first);
    run(["collect", "--plan", join(directory, "plan.json"), "--out", output, "--unknown", "value"], 2);
    for (const key of ["accessToken", "gitAuthor"]) {
      const records = bytes.toString("utf8").trimEnd().split("\n")
        .map((line): Record<string, unknown> => JSON.parse(line));
      const node = records.find(record => record.kind === "node");
      assert.ok(node);
      node.props = { [key]: "synthetic-canary" };
      const changed = records.map(record => JSON.stringify(record)).join("\n") + "\n";
      const envelope: {
        contentSha256: string;
        content: { inputs: Array<{ artifactPath: string; byteLength: number; sha256: string }> };
      } = JSON.parse(first);
      const input = envelope.content.inputs[0]!;
      const artifact = join(output, input.artifactPath);
      writeFileSync(artifact, changed);
      run(["validate", artifact]);
      input.byteLength = Buffer.byteLength(changed);
      input.sha256 = hash(changed);
      envelope.contentSha256 = hash(canonicalContentBytes(envelope.content));
      writeFileSync(join(output, "collection.json"), JSON.stringify(envelope));

      assert.match(run(["validate-collection", output], 1), /credential or personal Git author data/);
      const result = await validateCollectionBundle(output);
      assert.equal(result.ok, false);
      assert.ok(result.problems.some(({ message }) => message.includes("credential or personal Git author data")));
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("Given Unicode separators and non-BMP metadata, independent runtimes agree on the content identity", async () => {
  const directory = mkdtempSync(join(tmpdir(), "ce-collector-unicode-"));
  try {
    const unitPath = "models/\u2028\u{1f600}+<workflow>.bpmn";
    const artifact = JSON.stringify({
      schema: "ce.models.v1", repositoryId: "sales", commitSha: "a".repeat(40),
      unitPath, sourceSha256: hash("fixture"), contentProfile: "structural",
      coverage: { status: "full", supported: [], limitations: [] },
      diagnostics: [], elements: [], relationships: []
    });
    writeFileSync(join(directory, "input.json"), artifact);
    writeFileSync(join(directory, "plan.json"), JSON.stringify({
      schema: "ce.collection.plan.v1", recipeSha256: hash("recipe"),
      configSha256: hash("config"), policySha256: hash("policy"),
      repositories: [{
        repositoryId: "sales", providerId: "local:synthetic", producerId: "dogfood",
        tenantRef: "synthetic", rightsRef: "explicit-synthetic-content",
        commitSha: "a".repeat(40), treeSha: "b".repeat(40), permittedContentProfiles: ["model-content"]
      }],
      inputs: [{
        repositoryId: "sales", profile: "ce.models.v1", path: "input.json",
        unitPath, sha256: hash(artifact), byteLength: Buffer.byteLength(artifact),
        required: true, contentProfile: "model-content"
      }]
    }));
    const output = join(directory, "collection");
    runCli(["collect", "--plan", join(directory, "plan.json"), "--out", output]);
    const result = await validateCollectionBundle(output);
    assert.equal(result.ok, true, JSON.stringify(result.problems));
    const manifest = join(output, "collection.json");
    const original = readFileSync(manifest, "utf8");
    for (const suffix of [".0", "e0"]) {
      writeFileSync(manifest, original.replace(/"byteLength":(\d+)/, `"byteLength":$1${suffix}`));
      runCli(["validate-collection", output]);
      assert.equal((await validateCollectionBundle(output)).ok, true);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
