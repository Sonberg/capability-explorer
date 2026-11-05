import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { chmodSync, cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { canonicalContentBytes } from "../../src/collection/canonicalContent.ts";
import { validateCollectionBundle } from "../../src/collection/validator.ts";

const root = resolve(import.meta.dirname, "../../../..");
const cli = join(root, "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll");
const extractor = join(root, "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll");
const runner = join(root, "extractors/ts/test/dogfood/collection-runner.ts");
const digest = (bytes: string | Buffer): string => createHash("sha256").update(bytes).digest("hex");

function command(program: string, args: string[], expected = 0, cwd = root): string {
  const result = spawnSync(program, args, { cwd, encoding: "utf8", timeout: 60_000 });
  assert.ifError(result.error);
  assert.equal(result.status, expected, `${program} ${args.join(" ")}\n${result.stdout}\n${result.stderr}`);
  return result.stdout.trim();
}

test("Given sealed synthetic candidate source, real transport binds committed bytes and requires candidate-only opt-in", async t => {
  const directory = join(root, ".sandbox-cache/collection-dogfood", `run.${randomUUID().replaceAll("-", "")}`);
  mkdirSync(directory, { recursive: true });
  const source = join(directory, "sales/Orchestrator.cs");
  const candidatePath = join(directory, "candidates.json");
  const phase = (name: string, expected = 0) => command(process.execPath, [runner, name, directory], expected);
  const ce = (args: string[], expected = 0) => command("dotnet", [cli, ...args], expected);
  try {
    phase("prepare");
    const revision = command("git", ["rev-parse", "HEAD"], 0, join(directory, "sales"));
    const extract = (commit = revision) => command("dotnet", [extractor, "extract-candidates",
      "--source", source, "--repository", "sales", "--commit", commit, "--out", candidatePath]);
    extract();
    const original = readFileSync(candidatePath);
    const sourceBytes = readFileSync(source);
    const artifact = JSON.parse(original.toString("utf8"));
    assert.ok(sourceBytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf])));
    assert.equal(artifact.sourceSha256, digest(sourceBytes));
    assert.equal(artifact.commitSha, revision);
    assert.equal(artifact.unitPath, "Orchestrator.cs");
    const committed = spawnSync("git", ["show", `${revision}:${artifact.unitPath}`],
      { cwd: join(directory, "sales"), timeout: 60_000 });
    assert.ifError(committed.error);
    assert.equal(committed.status, 0);
    assert.deepEqual(sourceBytes, committed.stdout);
    phase("plan");
    const bundle = join(directory, "collection");
    ce(["collect", "--plan", join(directory, "plan.json"), "--out", bundle]);
    ce(["validate-collection", bundle]);
    let result = await validateCollectionBundle(bundle);
    assert.equal(result.ok, true, JSON.stringify(result.problems));
    const manifestBytes = readFileSync(join(bundle, "collection.json"));
    ce(["collect", "--plan", join(directory, "plan.json"), "--out", bundle]);
    assert.deepEqual(readFileSync(join(bundle, "collection.json")), manifestBytes);
    const manifest = JSON.parse(manifestBytes.toString("utf8"));
    const descriptor = manifest.content.inputs.find((item: { profile: string }) => item.profile === "ce.candidates.v1");
    assert.ok(descriptor);
    assert.equal(descriptor.sha256, digest(original));
    assert.deepEqual(readFileSync(join(bundle, descriptor.artifactPath)), original);
    assert.equal(manifest.content.repositories.find((repo: { repositoryId: string }) => repo.repositoryId === "sales").commitSha, revision);

    await t.test("Given only candidate permission revoked, required collection cannot complete", () => {
      const denied = JSON.parse(readFileSync(join(directory, "denied-plan.json"), "utf8"));
      assert.ok(denied.repositories.every((repo: { permittedContentProfiles: string[] }) =>
        repo.permittedContentProfiles.includes("model-content") && !repo.permittedContentProfiles.includes("candidate-content")));
      ce(["collect", "--plan", join(directory, "denied-plan.json"), "--out", join(directory, "denied")], 1);
    });

    for (const mutation of ["permission", "unrelated-profile", "revision", "path", "bytes"] as const) {
      await t.test(`Given resealed candidate transport ${mutation}, both independent validators refuse`, async () => {
        const changed = join(directory, `bundle-${mutation}`);
        cpSync(bundle, changed, { recursive: true });
        const envelope = JSON.parse(manifestBytes.toString("utf8"));
        const input = envelope.content.inputs.find((item: { profile: string }) => item.profile === "ce.candidates.v1");
        const repository = envelope.content.repositories.find((repo: { repositoryId: string }) => repo.repositoryId === "sales");
        if (mutation === "permission") repository.permittedContentProfiles = ["model-content"];
        else if (mutation === "unrelated-profile") input.contentProfile = "model-content";
        else if (mutation === "bytes") writeFileSync(join(changed, input.artifactPath), `${original.toString("utf8")} `);
        else {
          const value = JSON.parse(original.toString("utf8"));
          if (mutation === "revision") value.commitSha = "f".repeat(40);
          else value.unitPath = "Other.cs";
          const bytes = JSON.stringify(value);
          writeFileSync(join(changed, input.artifactPath), bytes);
          input.sha256 = digest(bytes);
          input.byteLength = Buffer.byteLength(bytes);
        }
        envelope.contentSha256 = digest(canonicalContentBytes(envelope.content));
        writeFileSync(join(changed, "collection.json"), JSON.stringify(envelope));
        ce(["validate-collection", changed], 1);
        result = await validateCollectionBundle(changed);
        assert.equal(result.ok, false, "resealing cannot repair candidate binding or grant permission");
        assert.match(result.problems.map(problem => problem.message).join("; "),
          mutation === "permission" || mutation === "unrelated-profile" ? /contentProfile/
            : mutation === "bytes" ? /bytes do not match/ : /identity/);
      });
    }

    for (const mutation of ["caller-revision", "uncommitted-source", "digest", "missing-path", "span-digest", "predicate", "review"] as const) {
      await t.test(`Given ${mutation}, synthetic source qualification refuses despite a caller-supplied commit string`, () => {
        try {
          if (mutation === "caller-revision") extract("f".repeat(40));
          else if (mutation === "uncommitted-source") {
            chmodSync(source, 0o644);
            writeFileSync(source, sourceBytes.toString("utf8").replace('"Gold"', '"Priority"'));
            extract();
          } else {
            const value = JSON.parse(original.toString("utf8"));
            if (mutation === "digest") value.sourceSha256 = "f".repeat(64);
            if (mutation === "missing-path") value.unitPath = "Missing.cs";
            if (mutation === "span-digest") value.candidates[0].body.source.sourceSha256 = "f".repeat(64);
            if (mutation === "predicate") {
              const walk = (node: { rawPredicate?: string; children: unknown[] }): typeof node[] =>
                [node, ...node.children.flatMap(child => walk(child as typeof node))];
              walk(value.candidates[0].body).find(node => node.rawPredicate)!.rawPredicate = "false";
            }
            if (mutation === "review") value.candidates[0].reviewStatus = "APPROVED";
            writeFileSync(candidatePath, JSON.stringify(value));
          }
          phase("plan", 1);
        } finally {
          chmodSync(source, 0o644);
          writeFileSync(source, sourceBytes);
          chmodSync(source, 0o444);
          writeFileSync(candidatePath, original);
        }
      });
    }
    console.log("CANDIDATE TRANSPORT PROOF", JSON.stringify({
      revision, tree: command("git", ["rev-parse", "HEAD^{tree}"], 0, join(directory, "sales")),
      unitPath: artifact.unitPath, sourceSha256: digest(sourceBytes), artifactSha256: digest(original),
      collectionSha256: manifest.contentSha256, extractorSha256: digest(readFileSync(extractor)),
      collectorSha256: digest(readFileSync(cli)), runnerSha256: digest(readFileSync(runner)),
      qualification: "sealed synthetic Git membership only; validators bind declared metadata and bytes, not authenticated Git rights"
    }));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
