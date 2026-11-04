#!/usr/bin/env node
import { constants } from "node:fs";
import { lstat, open, rename, unlink, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { basename, dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { parseArgs } from "node:util";
import { extractModelXml } from "./extract.ts";
import { DEFAULT_XML_LIMITS, ModelXmlError } from "./xml.ts";
import { createModelSource } from "./source.ts";

const usage = "usage: node src/models/cli.ts --source <file.bpmn|file.dmn> --repository <id> --commit <40-hex-sha> --out <file.json> [--content-profile model-content [--source-out <source.json>]]";

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
        "source-out": { type: "string" },
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
  const sourceOut = values["source-out"];
  if (sourceOut !== undefined && (typeof sourceOut !== "string" || contentProfile !== "model-content")) {
    console.error("--source-out requires explicit --content-profile model-content");
    return 2;
  }
  if (typeof contentProfile !== "undefined" && typeof contentProfile !== "string") {
    console.error("content profile must be structural or model-content");
    return 2;
  }
  if (contentProfile !== undefined && contentProfile !== "model-content" && contentProfile !== "structural") {
    console.error("content profile must be structural or model-content");
    return 2;
  }
  const sourcePath = resolve(source);
  if (!/\.(?:bpmn|bpmn20\.xml|dmn)$/i.test(basename(sourcePath))) {
    console.error("source file must end in .bpmn, .bpmn20.xml, or .dmn");
    return 2;
  }
  try {
    const input = await open(sourcePath, constants.O_RDONLY | constants.O_NOFOLLOW);
    let bytes: Buffer;
    let sourceIdentity: { dev: number; ino: number };
    try {
      const info = await input.stat();
      if (!info.isFile() || info.size > DEFAULT_XML_LIMITS.maxBytes) {
        throw new ModelXmlError("xml-byte-limit", "Source must be a regular file within the XML byte limit");
      }
      sourceIdentity = info;
      const buffer = Buffer.alloc(DEFAULT_XML_LIMITS.maxBytes + 1);
      let length = 0;
      while (length < buffer.length) {
        const { bytesRead } = await input.read(buffer, length, buffer.length - length);
        if (bytesRead === 0) break;
        length += bytesRead;
      }
      if (length > DEFAULT_XML_LIMITS.maxBytes) throw new ModelXmlError("xml-byte-limit", "XML byte limit exceeded");
      bytes = buffer.subarray(0, length);
    } finally { await input.close(); }
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
    const modelBytes = Buffer.from(`${JSON.stringify(evidence, null, 2)}\n`);
    const attachment = typeof sourceOut === "string" ? createModelSource(bytes, modelBytes) : undefined;
    if (typeof sourceOut === "string" && [sourcePath, outputPath].includes(resolve(sourceOut))) {
      throw new ModelXmlError("invalid-output-path", "Source attachment must not overwrite source or companion");
    }
    if (outputPath === sourcePath) throw new ModelXmlError("invalid-output-path", "Output file must not overwrite the model source");
    const output = await lstat(outputPath).catch((error: NodeJS.ErrnoException) => {
      if (error.code !== "ENOENT") throw error;
      return undefined;
    });
    if (output?.isSymbolicLink() || (output?.dev === sourceIdentity.dev && output.ino === sourceIdentity.ino)) {
      throw new ModelXmlError("invalid-output-path", "Output must not be a symlink or an alias of the source");
    }
    const temporary = join(dirname(outputPath), `.${basename(outputPath)}.${randomUUID()}.tmp`);
    let attachmentCreated = false;
    try {
      await writeFile(temporary, modelBytes, { flag: "wx", mode: 0o600 });
      if (attachment && typeof sourceOut === "string") {
        await writeFile(resolve(sourceOut), `${JSON.stringify(attachment, null, 2)}\n`, { flag: "wx", mode: 0o600 });
        attachmentCreated = true;
      }
      await rename(temporary, outputPath);
    } catch (error) {
      if (attachmentCreated && typeof sourceOut === "string") await unlink(resolve(sourceOut));
      throw error;
    } finally {
      await unlink(temporary).catch((error: NodeJS.ErrnoException) => { if (error.code !== "ENOENT") throw error; });
    }
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
