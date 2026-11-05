#!/usr/bin/env node
import { createHash } from "node:crypto";
import { chmod, copyFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const root = resolve(fileURLToPath(new URL("..", import.meta.url)));
const cacheRoot = join(root, ".sandbox-cache");
const fixtures = [
  "decision-rich.dmn",
  "decision.dmn",
  "order.bpmn",
  "workflow.bpmn",
];
const recipeFiles = [
  "scripts/prepare-model-fixtures.mjs",
  "extractors/ts/src/models/cli.ts",
  "extractors/ts/src/models/extract.ts",
  "extractors/ts/src/models/extract-semantics.ts",
  "extractors/ts/src/models/xml.ts",
  "extractors/ts/src/models/source.ts",
  "schema/models/support.json",
  "extractors/ts/package-lock.json",
];
const sha256 = value => createHash("sha256").update(value).digest("hex");

async function digestFiles(paths) {
  const parts = [];
  for (const path of paths) parts.push(Buffer.from(`${path}\0`), await readFile(join(root, path)), Buffer.from("\0"));
  return sha256(Buffer.concat(parts));
}

async function recipeDigest() {
  return digestFiles(recipeFiles);
}

async function collectionKey() {
  return (await digestFiles([...recipeFiles, ...fixtures.map(name => `fixtures/models/${name}`)])).slice(0, 20);
}

const fixtureCache = join(cacheRoot, "model-fixtures");
function validateOutput(path) {
  const output = resolve(root, path);
  const inside = relative(fixtureCache, output);
  if (!inside || inside === ".." || inside.startsWith(`..${sep}`) ||
      inside === "staging" || inside.startsWith(`staging${sep}`)) {
    throw new Error("model fixture collection output must be outside staging and under .sandbox-cache/model-fixtures");
  }
  return output;
}

const args = process.argv.slice(2);
if (args.length === 1 && args[0] === "--collection-key") {
  console.log(await collectionKey());
  process.exit(0);
}
if (args.length === 2 && args[0] === "--check-output") {
  validateOutput(args[1]);
  process.exit(0);
}
if (args.length !== 0) {
  throw new Error("usage: node scripts/prepare-model-fixtures.mjs [--collection-key | --check-output <path>]");
}
const stage = join(fixtureCache, "staging");
const repo = join(stage, "source");
const inputsDir = join(stage, "inputs");
await mkdir(cacheRoot, { recursive: true });

function command(program, args, cwd = root, extraEnv = {}) {
  const result = spawnSync(program, args, {
    cwd,
    encoding: "utf8",
    env: { ...process.env, ...extraEnv },
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`${program} ${args.join(" ")} failed (${result.status}): ${result.stderr || result.stdout}`);
  }
  return result.stdout.trim();
}

try {
  await rm(stage, { recursive: true, force: true });
  await mkdir(repo, { recursive: true });
  await mkdir(inputsDir, { recursive: true });
  for (const name of fixtures) {
    const destination = join(repo, name);
    await copyFile(join(root, "fixtures", "models", name), destination);
    await chmod(destination, 0o644);
  }

  const gitEnv = {
    GIT_CONFIG_NOSYSTEM: "1",
    GIT_CONFIG_GLOBAL: "/dev/null",
    GIT_AUTHOR_NAME: "Capability Explorer synthetic fixture",
    GIT_AUTHOR_EMAIL: "fixture@example.invalid",
    GIT_AUTHOR_DATE: "2020-01-01T00:00:00Z",
    GIT_COMMITTER_NAME: "Capability Explorer synthetic fixture",
    GIT_COMMITTER_EMAIL: "fixture@example.invalid",
    GIT_COMMITTER_DATE: "2020-01-01T00:00:00Z",
  };
  command("git", ["init", "--quiet", repo], root, gitEnv);
  command("git", ["-C", repo, "config", "core.autocrlf", "false"], root, gitEnv);
  command("git", ["-C", repo, "add", "--", ...fixtures], root, gitEnv);
  command("git", ["-C", repo, "-c", "user.name=Capability Explorer synthetic fixture",
    "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false",
    "commit", "--quiet", "-m", "Seal synthetic BPMN and DMN fixtures"], root, gitEnv);
  const commit = command("git", ["-C", repo, "rev-parse", "HEAD"]);
  const tree = command("git", ["-C", repo, "rev-parse", "HEAD^{tree}"]);

  const inputs = [];
  for (const name of fixtures) {
    const model = join(inputsDir, `${name}.json`);
    const source = join(inputsDir, `${name}.source.json`);
    command(process.execPath, [
      "--experimental-strip-types",
      join(root, "extractors/ts/src/models/cli.ts"),
      "--source", join(repo, name),
      "--repository", "synthetic-model-fixtures",
      "--commit", commit,
      "--out", model,
      "--content-profile", "model-content",
      "--source-out", source,
    ]);
    for (const [path, profile] of [
      [model, "ce.models.v1"],
      [source, "ce.model-source.v1"],
    ]) {
      const bytes = await readFile(path);
      const evidence = JSON.parse(bytes.toString("utf8"));
      if (evidence.schema !== profile) throw new Error(`unexpected fixture product profile: ${path}`);
      inputs.push({
        repositoryId: "synthetic-model-fixtures",
        profile,
        path: `inputs/${path.slice(inputsDir.length + 1)}`,
        unitPath: profile === "ce.models.v1"
          ? evidence.unitPath
          : JSON.parse((await readFile(model)).toString("utf8")).unitPath,
        sha256: sha256(bytes),
        byteLength: bytes.length,
        required: true,
        contentProfile: "model-content",
      });
    }
  }

  const plan = {
    schema: "ce.collection.plan.v1",
    recipeSha256: await recipeDigest(),
    configSha256: sha256(Buffer.from("synthetic-fixtures:bounded-model-extraction:v1")),
    policySha256: sha256(Buffer.from("synthetic-fixtures:explicit-local-model-content-consent:v1")),
    repositories: [{
      repositoryId: "synthetic-model-fixtures",
      providerId: "local:synthetic",
      producerId: "model-fixture-autoload",
      tenantRef: "synthetic",
      rightsRef: "explicit-local-fixture-model-content-consent",
      commitSha: commit,
      treeSha: tree,
      permittedContentProfiles: ["model-content"],
    }],
    inputs,
  };
  await writeFile(join(stage, "plan.json"), `${JSON.stringify(plan, null, 2)}\n`, { flag: "wx" });
  console.log(`Prepared ${fixtures.length} synthetic BPMN/DMN model products and collection plan`);
} catch (error) {
  await rm(stage, { recursive: true, force: true });
  throw error;
}
