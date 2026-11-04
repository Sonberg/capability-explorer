#!/usr/bin/env node
import { validateCollectionBundle } from "./validator.ts";

const [command, ...args] = process.argv.slice(2);
if (command !== "validate" || args.length !== 1 || args[0]!.startsWith("-")) {
  console.error("usage: node src/collection/cli.ts validate <collection-directory>");
  process.exit(2);
}

process.once("SIGINT", () => process.exit(130));
const report = await validateCollectionBundle(args[0]!);
if (report.ok) {
  console.log(`VALID ${report.contentSha256}`);
  process.exit(0);
}

for (const problem of report.problems) console.error(`INVALID ${problem.message}`);
process.exit(1);
