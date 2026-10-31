// Generates golden JSONL artifacts from facts/*.mjs against the locked fixture revisions.
//   node fixtures/golden/build.mjs           regenerate fixtures/expected
//   node fixtures/golden/build.mjs --check   fail if fixtures/expected is out of date
// Requires fixtures/.repos (scripts/build-fixtures.sh). No network, no dependencies.
import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { FixtureRepo } from "./lib.mjs";
import apiCs from "./facts/api-cs.mjs";
import workerCs from "./facts/worker-cs.mjs";
import webTs from "./facts/web-ts.mjs";
import forecastCs from "./facts/forecast-cs.mjs";
import pricingNode from "./facts/pricing-node.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const lock = JSON.parse(readFileSync(join(root, "fixtures/revisions.lock.json"), "utf8"));
const outDir = join(root, "fixtures/expected");
const check = process.argv.includes("--check");
const facts = { "api-cs": apiCs, "worker-cs": workerCs, "web-ts": webTs, "forecast-cs": forecastCs, "pricing-node": pricingNode };

const generated = new Map();
for (const [repoId, tags] of Object.entries(lock)) {
  for (const [tag, sha] of Object.entries(tags)) {
    const repo = new FixtureRepo(join(root, "fixtures/.repos", repoId), repoId, tag, sha);
    for (const artifact of facts[repoId](repo, tag)) {
      const file = join(repoId, tag, `${artifact.unit.name.replace(/[^A-Za-z0-9._-]/g, "_")}.jsonl`);
      generated.set(file, artifact.toJsonl());
    }
  }
}

const existing = new Map();
const walk = (dir, rel = "") => {
  if (!existsSync(dir)) return;
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const r = join(rel, e.name);
    if (e.isDirectory()) walk(join(dir, e.name), r);
    else if (e.name.endsWith(".jsonl")) existing.set(r, readFileSync(join(dir, e.name), "utf8"));
  }
};
walk(outDir);

if (check) {
  const problems = [];
  for (const [f, c] of generated) if (existing.get(f) !== c) problems.push(`${existing.has(f) ? "stale" : "missing"}: ${f}`);
  for (const f of existing.keys()) if (!generated.has(f)) problems.push(`unexpected: ${f}`);
  if (problems.length) {
    console.error(`golden files out of date (run node fixtures/golden/build.mjs):\n  ${problems.join("\n  ")}`);
    process.exit(1);
  }
  console.log(`golden files up to date (${generated.size} artifacts)`);
} else {
  rmSync(outDir, { recursive: true, force: true });
  for (const [f, c] of generated) {
    mkdirSync(dirname(join(outDir, f)), { recursive: true });
    writeFileSync(join(outDir, f), c);
  }
  console.log(`wrote ${generated.size} golden artifacts to fixtures/expected`);
}
