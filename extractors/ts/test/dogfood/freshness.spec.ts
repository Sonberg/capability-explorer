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
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
