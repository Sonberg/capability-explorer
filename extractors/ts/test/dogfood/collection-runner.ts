import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  chmodSync, cpSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, realpathSync,
  rmSync, writeFileSync
} from "node:fs";
import { basename, dirname, join, relative, resolve } from "node:path";

const root = resolve(import.meta.dirname, "../../../..");
const directory = resolve(root, process.argv[3] ?? "");
assert.equal(dirname(directory), join(root, ".sandbox-cache/collection-dogfood"));
assert.match(basename(directory), /^run\.[a-zA-Z0-9]+$/);
assert.equal(realpathSync(dirname(directory)), dirname(directory));
assert.ok(!lstatSync(directory).isSymbolicLink());
const hash = (value: string | Buffer): string => createHash("sha256").update(value).digest("hex");
const json = (file: string): string => readFileSync(join(directory, file), "utf8");
const write = (file: string, value: unknown): void => writeFileSync(join(directory, file), JSON.stringify(value));

interface Repository {
  repositoryId: string; providerId: string; producerId: string; tenantRef: string; rightsRef: string;
  commitSha: string; treeSha: string; permittedContentProfiles: string[];
}
interface Artifact {
  schema: string; repositoryId: string; commitSha: string; unitPath: string;
  coverage: { status: string }; elements?: unknown[]; candidates?: unknown[];
}
interface Input {
  repositoryId: string; profile: string; path: string; unitPath: string; sha256: string;
  byteLength: number; required: boolean; contentProfile: string;
}
interface Plan {
  schema: string; recipeSha256: string; configSha256: string; policySha256: string;
  repositories: Repository[]; inputs: Input[];
}
interface Descriptor {
  contentSha256: string;
  content: { repositories: Repository[]; inputs: { artifactPath: string; status: string }[] };
}

function command(program: string, args: string[], cwd = root): string {
  const result = spawnSync(program, args, { cwd, encoding: "utf8", timeout: 60_000 });
  assert.ifError(result.error);
  assert.equal(result.status, 0, `${program}: ${result.stderr}\n${result.stdout}`);
  return result.stdout.trim();
}
function git(id: string, ...args: string[]): string {
  return command("git", args, join(directory, id));
}
function seal(id: string): void {
  git(id, "add", ".");
  git(id, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
    "commit", "--quiet", "-m", "Seal synthetic model evidence");
  for (const name of readdirSync(join(directory, id)).filter(name => name !== ".git"))
    chmodSync(join(directory, id, name), 0o444);
}
function repository(id: string): Repository {
  return {
    repositoryId: id, providerId: `local:${id}`, producerId: "dogfood", tenantRef: "synthetic",
    rightsRef: "synthetic-explicit-content", commitSha: git(id, "rev-parse", "HEAD"),
    treeSha: git(id, "rev-parse", "HEAD^{tree}"), permittedContentProfiles: ["model-content", "candidate-content"]
  };
}
function extractModel(id: string, source: string, out: string): void {
  command(process.execPath, [join(root, "extractors/ts/src/models/cli.ts"),
    "--source", join(directory, id, source), "--repository", id, "--commit", git(id, "rev-parse", "HEAD"),
    "--out", join(directory, out), "--content-profile", "model-content"]);
}
function input(file: string, profile: string, id: string, contentProfile: string): Input {
  const bytes = readFileSync(join(directory, file));
  const artifact: Artifact = JSON.parse(bytes.toString("utf8"));
  assert.equal(artifact.schema, profile);
  assert.equal(artifact.repositoryId, id);
  assert.equal(artifact.commitSha, git(id, "rev-parse", "HEAD"));
  assert.ok(artifact.unitPath && !artifact.unitPath.startsWith("/"));
  assert.ok(["full", "partial"].includes(artifact.coverage.status));
  return {
    repositoryId: id, profile, path: file, unitPath: artifact.unitPath,
    sha256: hash(bytes), byteLength: bytes.length, required: true, contentProfile
  };
}
function sourceRecipe(): string {
  const files: string[] = [];
  function visit(path: string): void {
    for (const item of readdirSync(path, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, "en"))) {
      const file = join(path, item.name);
      if (item.isDirectory()) visit(file);
      else if (item.isFile() && item.name.endsWith(".ts")) files.push(file);
    }
  }
  visit(join(root, "extractors/ts/src/models"));
  visit(join(root, "extractors/ts/src/collection"));
  files.push(join(root, "extractors/ts/package-lock.json"), join(root, "config/runtime/images.env"),
    join(root, "platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll"),
    join(root, "platform/src/CapabilityExplorer.Contracts/bin/Debug/net10.0/CapabilityExplorer.Contracts.dll"));
  return hash(JSON.stringify(files.sort().map(file => [relative(root, file), hash(readFileSync(file))])));
}
function validateTypescript(bundle: string): void {
  command(process.execPath, [join(root, "extractors/ts/src/collection/cli.ts"), "validate", join(directory, bundle)]);
}
function descriptor(bundle: string): Descriptor {
  return JSON.parse(json(`${bundle}/collection.json`));
}
function verifyPrivacy(bundle: string): void {
  const manifest = descriptor(bundle);
  assert.equal(manifest.content.repositories.length, 2);
  assert.equal(manifest.content.inputs.length, 3);
  for (const item of manifest.content.inputs) {
    assert.equal(item.status, "included");
    const artifact: Artifact = JSON.parse(json(`${bundle}/${item.artifactPath}`));
    const text = JSON.stringify(artifact);
    assert.doesNotMatch(text, /fixture@example\.invalid|<\?xml|<bpmn:definitions|<definitions/);
    assert.ok(!text.includes(directory), "local absolute workspace must not be transported");
    if (artifact.schema === "ce.models.v1") assert.ok((artifact.elements?.length ?? 0) > 0);
    if (artifact.schema === "ce.candidates.v1") assert.ok((artifact.candidates?.length ?? 0) > 0);
  }
}

switch (process.argv[2]) {
  case "prepare": {
    for (const id of ["sales", "policy"]) {
      mkdirSync(join(directory, id));
      command("git", ["init", "--quiet", join(directory, id)]);
    }
    writeFileSync(join(directory, "sales/Orchestrator.cs"), `
using System;
public static class Pricing {
  public static string Classify(int amount, bool member) {
    if (amount < 0) return "Invalid";
    if (member && amount >= 100) return "Gold";
    return "None";
  }
}
`);
    writeFileSync(join(directory, "sales/Review.bpmn"), `<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
 id="ReviewDefinitions" targetNamespace="urn:ce:dogfood:sales">
 <bpmn:process id="ReviewProcess" isExecutable="false">
  <bpmn:startEvent id="Start"/><bpmn:task id="Review" name="Review"/>
  <bpmn:endEvent id="End"/>
  <bpmn:sequenceFlow id="BeginReview" sourceRef="Start" targetRef="Review"/>
  <bpmn:sequenceFlow id="FinishReview" sourceRef="Review" targetRef="End"/>
 </bpmn:process>
</bpmn:definitions>`);
    writeFileSync(join(directory, "policy/Tier.dmn"), `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/"
 id="TierDefinitions" name="Tier policy" namespace="urn:ce:dogfood:policy">
 <decision id="Tier" name="Tier"><decisionTable id="TierTable" hitPolicy="FIRST">
  <input id="Amount"><inputExpression id="AmountExpression" typeRef="number"><text>amount</text></inputExpression></input>
  <output id="Result" name="tier" typeRef="string"/>
  <rule id="Negative"><inputEntry id="NegativeInput"><text>&lt; 0</text></inputEntry><outputEntry id="NegativeOutput"><text>"Invalid"</text></outputEntry></rule>
  <rule id="High"><inputEntry id="HighInput"><text>&gt;= 100</text></inputEntry><outputEntry id="HighOutput"><text>"Gold"</text></outputEntry></rule>
  <rule id="Other"><inputEntry id="OtherInput"><text>-</text></inputEntry><outputEntry id="OtherOutput"><text>"None"</text></outputEntry></rule>
 </decisionTable></decision>
</definitions>`);
    seal("sales"); seal("policy");
    extractModel("sales", "Review.bpmn", "sales-model.json");
    extractModel("policy", "Tier.dmn", "policy-model.json");
    break;
  }
  case "plan": {
    const plan: Plan = {
      schema: "ce.collection.plan.v1", recipeSha256: sourceRecipe(),
      configSha256: hash("dogfood:supported-models-and-candidates:required"),
      policySha256: hash("synthetic:explicit-model-and-candidate-content"),
      repositories: ["sales", "policy"].map(repository),
      inputs: [input("sales-model.json", "ce.models.v1", "sales", "model-content"),
        input("policy-model.json", "ce.models.v1", "policy", "model-content"),
        input("candidates.json", "ce.candidates.v1", "sales", "candidate-content")]
    };
    write("plan.json", plan);
    write("denied-plan.json", { ...plan, repositories: plan.repositories.map(repo => ({ ...repo, permittedContentProfiles: [] })) });
    const vector = {
      repositories: plan.repositories.map(repo => ({
        repositoryId: repo.repositoryId, commitSha: repo.commitSha, treeSha: repo.treeSha,
        inputsSha256: hash(JSON.stringify(plan.inputs.filter(item => item.repositoryId === repo.repositoryId))),
        dependenciesSha256: hash("synthetic:only-pinned-recipe-and-BCL")
      })),
      recipeSha256: plan.recipeSha256, configurationSha256: plan.configSha256,
      enrollmentSha256: hash(JSON.stringify(plan.repositories.map(repo => [repo.repositoryId, repo.providerId, repo.producerId, repo.tenantRef]))),
      rightsSha256: plan.policySha256, profilesSha256: hash("ce.models.v1+ce.candidates.v1"),
      enumerationComplete: true
    };
    write("vector.json", vector);
    write("vector-rights.json", { ...vector, rightsSha256: hash("rights:revoked") });
    write("vector-recipe.json", { ...vector, recipeSha256: hash("recipe:changed") });
    write("vector-incomplete.json", { ...vector, enumerationComplete: false });
    break;
  }
  case "observation-policy": {
    const attempt: { attemptId: string; observedAt: string } = JSON.parse(json("collection/attempt.json"));
    const vector: { rightsSha256: string } = JSON.parse(json("vector.json"));
    const current: { inputVectorSha256: string } = JSON.parse(json("current.stdout"));
    write("observation-policy.json", {
      observedAt: attempt.observedAt, collectedAt: attempt.observedAt,
      attemptId: attempt.attemptId, sourceEffectiveAt: null, publishedAt: null,
      policyId: "rights", policySha256: vector.rightsSha256, maxAgeSeconds: 60,
      lastSuccessful: {
        attemptId: attempt.attemptId, collectedAt: attempt.observedAt,
        inputVectorSha256: current.inputVectorSha256, bundleSha256: descriptor("collection").contentSha256
      }
    });
    const observed = Date.parse(attempt.observedAt);
    writeFileSync(join(directory, "as-of-current"), new Date(observed + 1000).toISOString());
    writeFileSync(join(directory, "as-of-expired"), new Date(observed + 61_000).toISOString());
    break;
  }
  case "repeat-and-mutate": {
    validateTypescript("collection");
    verifyPrivacy("collection");
    write("original-identity.json", descriptor("collection").contentSha256);
    const original: Plan = JSON.parse(json("plan.json"));
    const source = join(directory, "policy/Tier.dmn");
    chmodSync(source, 0o644);
    writeFileSync(source, readFileSync(source, "utf8").replace('"Gold"', '"Priority"'));
    seal("policy");
    extractModel("policy", "Tier.dmn", "policy-changed.json");
    const changed: Plan = {
      ...original, repositories: ["sales", "policy"].map(repository),
      inputs: original.inputs.map(item => item.repositoryId === "policy"
        ? input("policy-changed.json", "ce.models.v1", "policy", "model-content") : item)
    };
    write("changed-plan.json", changed);
    const vector = JSON.parse(json("vector.json"));
    write("vector-source.json", {
      ...vector, repositories: vector.repositories.map((repo: { repositoryId: string }) =>
        repo.repositoryId === "policy" ? { ...repo, commitSha: git("policy", "rev-parse", "HEAD"),
          treeSha: git("policy", "rev-parse", "HEAD^{tree}"), inputsSha256: hash(readFileSync(source)) } : repo)
    });
    break;
  }
  case "check-and-tamper": {
    validateTypescript("changed-collection");
    assert.notEqual(descriptor("collection").contentSha256, descriptor("changed-collection").contentSha256);
    assert.equal(descriptor("collection").contentSha256, JSON.parse(json("original-identity.json")));
    cpSync(join(directory, "collection"), join(directory, "tampered"), { recursive: true });
    const item = descriptor("tampered").content.inputs[0]!;
    const path = join(directory, "tampered", item.artifactPath);
    writeFileSync(path, `${readFileSync(path, "utf8")}\n`);
    const rejected = spawnSync(process.execPath,
      [join(root, "extractors/ts/src/collection/cli.ts"), "validate", join(directory, "tampered")],
      { cwd: root, encoding: "utf8", timeout: 60_000 });
    assert.ifError(rejected.error);
    assert.equal(rejected.status, 1, `${rejected.stdout}\n${rejected.stderr}`);
    break;
  }
  case "final": {
    assert.ok(!existsSync(join(directory, "denied/collection.json")));
    assert.match(json("current.stdout"), /"status":"current"/);
    assert.match(json("policy-current.stdout"), /"status":"current"/);
    assert.match(json("policy-expired.stdout"), /"status":"stale"/);
    assert.match(json("policy-expired.stdout"), /observation-expired/);
    const observation: { lastSuccessful: { bundleSha256: string } } = JSON.parse(json("policy-current.stdout"));
    assert.equal(observation.lastSuccessful.bundleSha256, descriptor("collection").contentSha256);
    for (const change of ["source", "rights", "recipe"])
      assert.match(json(`freshness-${change}.stdout`), /"status":"stale"/);
    assert.match(json("freshness-incomplete.stdout"), /"status":"unknown"/);
    assert.match(json("duplicate-option.stderr"), /duplicate option/);
    assert.ok(json("tampered.stdout").length + json("tampered.stderr").length > 0);
    assert.ok(json("denied.stdout").length + json("denied.stderr").length > 0);
    validateTypescript("collection");
    validateTypescript("changed-collection");
    console.log("DOGFOOD PASS: 2 sealed Git repositories, actual model/code/collector CLIs, .NET+TS consumers, repeat/mutate/deny/tamper/freshness/expiry/last-success.");
    break;
  }
  case "cleanup":
    rmSync(directory, { recursive: true, force: true });
    break;
  default: throw new Error("Unknown dogfood stage");
}
