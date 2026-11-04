#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { basename, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { parseArgs } from "node:util";
import { extractModelXml } from "./extract.ts";
import { ModelXmlError } from "./xml.ts";

const usage = "usage: node src/models/cli.ts --source <file.bpmn|file.dmn> --repository <id> --commit <40-hex-sha> --out <file.json> [--content-profile model-content]";

async function main(): Promise<number> {
  let values: ReturnType<typeof parseArgs>["values"];
  try {
    ({ values } = parseArgs({
      options: {
        source: { type: "string" },
        repository: { type: "string" },
        commit: { type: "string" },
        out: { type: "string" },
        "content-profile": { type: "string" },
      },
      strict: true,
    }));
  } catch (error) {
    console.error(error instanceof Error ? error.message : "Invalid command-line options");
    console.error(usage);
    return 2;
  }
  const { source, repository, commit, out } = values;
  if (
    typeof source !== "string"
    || typeof repository !== "string"
    || typeof commit !== "string"
    || typeof out !== "string"
  ) {
    console.error(usage);
    return 2;
  }
  const contentProfile = values["content-profile"];
  if (typeof contentProfile !== "undefined" && typeof contentProfile !== "string") {
    console.error("content profile must be structural or model-content");
    return 2;
  }
  if (contentProfile && contentProfile !== "model-content" && contentProfile !== "structural") {
    console.error("content profile must be structural or model-content");
    return 2;
  }
  const sourcePath = resolve(source);
  if (!/\.(?:bpmn|bpmn20\.xml|dmn)$/i.test(basename(sourcePath))) {
    console.error("source file must end in .bpmn, .bpmn20.xml, or .dmn");
    return 2;
  }
  try {
    const bytes = await readFile(sourcePath);
    const xml = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    const unitPath = source.startsWith("/") || /^[a-zA-Z]:[\\/]/.test(source)
      ? basename(sourcePath)
      : source.replaceAll("\\", "/");
    const evidence = await extractModelXml(xml, {
      repositoryId: repository,
      commitSha: commit,
      unitPath,
      ...(contentProfile ? { contentProfile: contentProfile as "structural" | "model-content" } : {}),
    }, bytes);
    const outputPath = resolve(out);
    if (outputPath === sourcePath) throw new ModelXmlError("invalid-output-path", "Output file must not overwrite the model source");
    await writeFile(outputPath, `${JSON.stringify(evidence, null, 2)}\n`);
    console.log(`${evidence.coverage.status} ${evidence.unitPath}: ${evidence.elements.length} elements, ${evidence.relationships.length} relationships, ${evidence.diagnostics.length} diagnostics`);
    return 0;
  } catch (error) {
    const code = error instanceof ModelXmlError ? `${error.code}: ` : "";
    console.error(`${code}${error instanceof Error ? error.message : "Model extraction failed"}`);
    return 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exitCode = await main();
}
