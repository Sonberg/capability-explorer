#!/usr/bin/env node
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { parseArgs } from "node:util";
import { validateArtifact } from "./contracts/validateArtifact.ts";
import { extract } from "./extract/extractor.ts";

const [command, ...rest] = process.argv.slice(2);

if (command === "validate" && rest.length > 0) {
  let failed = 0;
  for (const file of rest) {
    const result = validateArtifact(readFileSync(file, "utf8"));
    if (result.ok) {
      console.log(`ok   ${file}`);
    } else {
      failed++;
      console.log(`FAIL ${file}`);
      for (const p of result.problems) console.log(`     line ${p.line}: ${p.message}`);
    }
  }
  process.exit(failed ? 1 : 0);
}

if (command === "extract") {
  // Parses the sources with the TypeScript compiler API. Nothing in the analysed repository is installed,
  // built or executed; packages it imports are simply absent.
  const { values } = parseArgs({ args: rest, options: { source: { type: "string" }, repository: { type: "string" }, commit: { type: "string" }, out: { type: "string" } } });
  if (!values.source || !values.repository || !values.commit || !values.out) {
    console.error("usage: ce-ts extract --source <dir> --repository <id> --commit <sha> --out <dir>");
    process.exit(2);
  }
  const results = extract({ source: values.source, repository: values.repository, commit: values.commit });
  mkdirSync(values.out, { recursive: true });
  let failed = 0;
  for (const r of results) {
    const file = join(values.out, `${r.unitName.replace(/[^A-Za-z0-9._-]/g, "_")}.jsonl`);
    const check = validateArtifact(r.jsonl);
    if (!check.ok) {
      failed++;
      console.error(`INVALID ${r.unitPath}: ${check.problems.slice(0, 5).map((p) => `line ${p.line}: ${p.message}`).join("; ")}`);
      writeFileSync(`${file}.invalid`, r.jsonl);
      continue;
    }
    writeFileSync(file, r.jsonl);
    const c = check.complete!;
    console.log(`${c.status.padEnd(7)} ${r.unitPath}: ${c.counts.node} nodes, ${c.counts.edge} edges, ${c.counts.diagnostic} diagnostics`);
  }
  if (results.length === 0) console.log("no TypeScript/JavaScript units found");
  process.exit(failed ? 1 : 0);
}

console.error("usage:\n  ce-ts validate <artifact.jsonl...>\n  ce-ts extract --source <dir> --repository <id> --commit <sha> --out <dir>");
process.exit(2);
