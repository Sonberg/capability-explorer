import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, statSync } from "node:fs";

assert.equal(process.argv.length, 3, "usage: node fixtures/classification/check-conformance.mjs <benchmark-report.json>");
const path = process.argv[2];
assert.ok(statSync(path).size <= 32 * 1024 * 1024, "Report must be at most 32 MiB");
const report = JSON.parse(readFileSync(path, "utf8"));
const artifactBytes = readFileSync(new URL("./synthetic-artifact.v1.json", import.meta.url));
const corpusBytes = readFileSync(new URL("./synthetic-corpus.v1.json", import.meta.url));
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
assert.equal(hash(artifactBytes), "a48a617509060866ee2da35e0eaadc51f9685640b9f9841139dc4f1079a6ab6b");
assert.equal(hash(corpusBytes), "9ec55b8362ea7c182d0e865de5817e6c6e7c919d6b3fbcaf3046fbbdbce60d16");
assert.equal(report.schema, "ce.classification-benchmark.v1");
assert.equal(report.artifactId, hash(artifactBytes));
assert.equal(report.corpusSha256, hash(corpusBytes));
assert.equal(report.scoreKind, "normalized-score");
const artifact = JSON.parse(artifactBytes);
const corpus = JSON.parse(corpusBytes);
assert.equal(report.quality.conformance.length, corpus.cases.length);
for (const [index, expected] of corpus.cases.entries()) {
  const actual = report.quality.conformance[index];
  assert.equal(actual.id, expected.id);
  assert.equal(actual.outcome, expected.outcome);
  assert.equal(actual.reason, expected.reason);
  assert.equal(actual.selectedClass, expected.selectedClass);
  if (expected.reason === "OutOfDistribution") {
    assert.deepEqual(actual.scores, []);
    continue;
  }
  const logits = artifact.weights.map((row, i) =>
    row.reduce((value, weight, j) => value + weight * expected.features[j], artifact.biases[i]));
  const shifted = logits.map(value => Math.exp(value - Math.max(...logits)));
  const denominator = shifted.reduce((sum, value) => sum + value, 0);
  const oracle = shifted.map(value => value / denominator);
  assert.equal(actual.scores.length, oracle.length);
  oracle.forEach((score, i) => assert.ok(Math.abs(actual.scores[i] - score) <= 1e-12,
    `${expected.id} class ${i} exceeds declared .NET/Node numeric tolerance`));
}
console.log(`Synthetic .NET/Node conformance passed ${corpus.cases.length} cases; not product parity or calibration.`);
