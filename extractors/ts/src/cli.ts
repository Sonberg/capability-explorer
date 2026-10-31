#!/usr/bin/env node
import { readFileSync } from "node:fs";
import { validateArtifact } from "./contracts/validateArtifact.ts";

const [command, ...files] = process.argv.slice(2);
if (command !== "validate" || files.length === 0) {
  console.error("usage: ce-ts validate <artifact.jsonl...>");
  process.exit(2);
}
let failed = 0;
for (const file of files) {
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
