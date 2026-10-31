import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { validateArtifact } from "../src/contracts/validateArtifact.ts";

const expected = fileURLToPath(new URL("../../../fixtures/expected", import.meta.url));
const goldenFiles = (readdirSync(expected, { recursive: true }) as string[]).filter((f) => f.endsWith(".jsonl")).sort();
const read = (f: string) => readFileSync(join(expected, f), "utf8");
const lines = (f: string) => read(f).trimEnd().split("\n");
const sample = "api-cs/v1-base/Sales.Api.jsonl";

test("golden fixtures exist", () => {
  assert.ok(goldenFiles.length >= 10, `found ${goldenFiles.length} golden files`);
});

for (const file of goldenFiles) {
  test(`golden artifact is valid: ${file}`, () => {
    const result = validateArtifact(read(file));
    assert.deepEqual(result.problems, []);
  });
}

test("broken worker revision is partial with diagnostics, not empty", () => {
  const result = validateArtifact(read("worker-cs/v2-broken/Sales.Worker.jsonl"));
  assert.equal(result.complete?.status, "partial");
  assert.deepEqual(result.complete?.coverage.incompletePaths, ["src/Sales.Worker/Calculations/CurrencyNormalizer.cs"]);
  const base = validateArtifact(read("worker-cs/v1-base/Sales.Worker.jsonl"));
  assert.ok(result.complete!.counts.node > base.complete!.counts.node, "broken revision keeps every base symbol");
});

test("truncated artifact (no complete record) is rejected", () => {
  const result = validateArtifact(lines(sample).slice(0, -1).join("\n"));
  assert.equal(result.ok, false);
  assert.match(result.problems[0]!.message, /truncated/);
});

test("count mismatch is rejected", () => {
  const all = lines(sample);
  const withoutOneNode = all.filter((l, i) => !(i === all.findIndex((x) => x.includes('"kind":"node"'))));
  const result = validateArtifact(withoutOneNode.join("\n"));
  assert.equal(result.ok, false);
});

test("dangling evidence reference is rejected", () => {
  const all = lines(sample).map((l) => l.replace(/"evidence":\["ev:[0-9a-f]{16}"/, '"evidence":["ev:0000000000000000"'));
  const result = validateArtifact(all.join("\n"));
  assert.ok(result.problems.some((p) => p.message.includes("unknown evidence reference ev:0000000000000000")));
});

test("evidence from another commit is rejected", () => {
  const all = lines(sample);
  const i = all.findIndex((l) => l.includes('"kind":"evidence"'));
  all[i] = all[i]!.replace(/"commitSha":"[0-9a-f]{40}"/, `"commitSha":"${"0".repeat(40)}"`);
  const result = validateArtifact(all.join("\n"));
  assert.ok(result.problems.some((p) => p.message.includes("evidence commitSha differs")));
});

test("schema violations are reported per line", () => {
  const all = lines(sample);
  const i = all.findIndex((l) => l.includes('"kind":"edge"'));
  all[i] = all[i]!.replace(/"derivation":"[a-z]+"/, '"derivation":"llm"');
  const result = validateArtifact(all.join("\n"));
  assert.equal(result.ok, false);
  assert.equal(result.problems[0]!.line, i + 1);
});

test("unresolved edges must say why", () => {
  const all = lines(sample);
  const i = all.findIndex((l) => l.includes('"resolution":"unresolved"'));
  all[i] = all[i]!.replace(/,"unresolvedReason":"[a-z-]+"/, "");
  assert.equal(validateArtifact(all.join("\n")).ok, false);
});
