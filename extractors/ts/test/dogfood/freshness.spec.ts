import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

const root = resolve(import.meta.dirname, "../../../..");
const cli = join(root, "platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll");
const hash = (value: string | Buffer): string => createHash("sha256").update(value).digest("hex");

function command(program: string, args: string[], cwd: string): string {
  const result = spawnSync(program, args, { cwd, encoding: "utf8", timeout: 30_000 });
  assert.ifError(result.error);
  assert.equal(result.status, 0, `${program}: ${result.stderr}\n${result.stdout}`);
  return result.stdout.trim();
}

test("Given sealed Git input vectors, the installed CLI detects source and authority drift without clocks", () => {
  const directory = mkdtempSync(join(tmpdir(), "ce-freshness-dogfood-"));
  try {
    const repositories = ["sales", "policy"].map(repositoryId => {
      const location = join(directory, repositoryId);
      command("git", ["init", "--quiet", location], directory);
      const source = join(location, "model.txt");
      writeFileSync(source, `${repositoryId}: reviewed synthetic input\n`);
      command("git", ["add", "model.txt"], location);
      command("git", ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
        "commit", "--quiet", "-m", "Seal synthetic input"], location);
      chmodSync(source, 0o444);
      return {
        repositoryId,
        commitSha: command("git", ["rev-parse", "HEAD"], location),
        treeSha: command("git", ["rev-parse", "HEAD^{tree}"], location),
        inputsSha256: hash(readFileSync(source)),
        dependenciesSha256: hash("[]")
      };
    });

    const vector = {
      repositories, recipeSha256: hash("recipe:v1"), configurationSha256: hash("configuration:v1"),
      enrollmentSha256: hash("synthetic-local-enrollment"), rightsSha256: hash("metadata-only"),
      profilesSha256: hash("ce.collection.v1"), enumerationComplete: true
    };
    const previous = join(directory, "previous.json"), current = join(directory, "current.json");
    writeFileSync(previous, JSON.stringify(vector));
    const compare = (observed: typeof vector, expectedExit: number, status: string): string => {
      writeFileSync(current, JSON.stringify(observed));
      const result = spawnSync("dotnet", [cli, "collection-freshness", "--previous", previous, "--current", current],
        { cwd: root, encoding: "utf8", timeout: 30_000 });
      assert.ifError(result.error);
      assert.equal(result.status, expectedExit, result.stderr);
      assert.match(result.stdout, new RegExp(`"status":"${status}"`));
      return result.stdout.trim();
    };
    const unchanged = compare(vector, 0, "current");
    assert.equal(compare({ ...vector, repositories: [...repositories].reverse() }, 0, "current"), unchanged);
    const source = join(directory, "sales/model.txt");
    chmodSync(source, 0o644);
    writeFileSync(source, "sales: changed input\n");
    const changed = repositories.map(repo => repo.repositoryId === "sales"
      ? { ...repo, inputsSha256: hash(readFileSync(source)) } : repo);
    compare({ ...vector, repositories: changed }, 1, "stale");
    compare({ ...vector, rightsSha256: hash("rights:revoked") }, 1, "stale");
    compare({ ...vector, recipeSha256: hash("recipe:v2") }, 1, "stale");
    compare({ ...vector, enumerationComplete: false }, 1, "unknown");
    compare({ ...vector, repositories: repositories.slice(1) }, 1, "stale");
    writeFileSync(current, JSON.stringify(vector));
    const duplicate = spawnSync("dotnet", [cli, "collection-freshness", "--previous", previous,
      "--previous", previous, "--current", current], { cwd: root, encoding: "utf8", timeout: 30_000 });
    assert.ifError(duplicate.error);
    assert.equal(duplicate.status, 2, "duplicate collector options must not be silently overwritten");
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("Given an explicit observation policy, the actual CLI expires a receipt at its half-open deadline", () => {
  const directory = mkdtempSync(join(tmpdir(), "ce-freshness-clock-"));
  try {
    const vector = {
      repositories: [{
        repositoryId: "sales", commitSha: "a".repeat(40), treeSha: "b".repeat(40),
        inputsSha256: hash("source"), dependenciesSha256: hash("[]")
      }],
      recipeSha256: hash("recipe"), configurationSha256: hash("config"),
      enrollmentSha256: hash("enrollment"), rightsSha256: hash("rights"),
      profilesSha256: hash("profiles"), enumerationComplete: true
    };
    const path = join(directory, "vector.json"), policy = join(directory, "policy.json");
    writeFileSync(path, JSON.stringify(vector));
    const context = {
      observedAt: "2026-11-04T00:00:00Z", collectedAt: "2026-11-04T00:00:00Z",
      attemptId: "synthetic-attempt", policyId: "rights",
      policySha256: vector.rightsSha256, maxAgeSeconds: 86400
    };
    writeFileSync(policy, JSON.stringify(context));
    const compare = (asOf: string, expectedExit: number, status: string): string => {
      const result = spawnSync("dotnet", [cli, "collection-freshness", "--previous", path, "--current", path,
        "--policy", policy, "--as-of", asOf], { cwd: root, encoding: "utf8", timeout: 30_000 });
      assert.ifError(result.error);
      assert.equal(result.status, expectedExit, result.stderr);
      assert.equal((JSON.parse(result.stdout) as { status: string }).status, status);
      return result.stdout.trim();
    };
    const current = compare("2026-11-04T23:59:59Z", 0, "current");
    assert.equal(compare("2026-11-04T23:59:59Z", 0, "current"), current);
    assert.match(compare("2026-11-05T00:00:00Z", 1, "stale"), /observation-expired/);
    writeFileSync(policy, JSON.stringify({ ...context, maxAgeSeconds: null }));
    assert.match(compare("2026-11-04T23:59:59Z", 1, "unknown"), /no-freshness-policy/);
    writeFileSync(policy, JSON.stringify(context));
    assert.match(compare("2026-11-03T23:59:59Z", 1, "unknown"), /clock-inconsistent/);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
