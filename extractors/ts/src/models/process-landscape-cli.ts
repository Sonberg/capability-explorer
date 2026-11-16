#!/usr/bin/env node
import { lstat, rename, unlink, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { basename, dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { parseArgs } from "node:util";
import { parseStrictJson } from "../collection/strictJson.ts";
import { buildProcessLandscapeSnapshot } from "./process-landscape.ts";
import { readBoundedFile, validateManifest } from "./process-landscape-manifest.ts";
import { ProcessLandscapeError } from "./process-landscape-types.ts";

const usage = "usage: node src/models/process-landscape-cli.ts --manifest <manifest.json> --base <dir> --out <file.json> [--content-profile model-content]";
const maxManifestBytes = 1_048_576;

async function readManifestBytes(path: string): Promise<Buffer> {
  return readBoundedFile(path, maxManifestBytes);
}

async function main(): Promise<number> {
  let values: ReturnType<typeof parseArgs>["values"];
  try {
    ({ values } = parseArgs({
      options: {
        manifest: { type: "string" },
        base: { type: "string" },
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
  const { manifest, base, out } = values;
  if (typeof manifest !== "string" || typeof base !== "string" || typeof out !== "string") {
    console.error(usage);
    return 2;
  }
  const contentProfile = values["content-profile"];
  if (contentProfile !== undefined && contentProfile !== "structural" && contentProfile !== "model-content") {
    console.error("content profile must be structural or model-content");
    return 2;
  }

  const manifestPath = resolve(manifest);
  const outputPath = resolve(out);
  try {
    const manifestBytes = await readManifestBytes(manifestPath);
    const manifestText = new TextDecoder("utf-8", { fatal: true }).decode(manifestBytes);
    const manifestJson = parseStrictJson(manifestText);
    const baseResolved = resolve(base);

    if (outputPath === manifestPath) throw new ProcessLandscapeError("invalid-output-path", "Output file must not overwrite the manifest");
    const parsedManifest = validateManifest(manifestJson);
    for (const entry of parsedManifest.entries) {
      if (outputPath === resolve(baseResolved, entry.path)) {
        throw new ProcessLandscapeError("invalid-output-path", `Output file must not overwrite an admitted manifest input: ${entry.path}`);
      }
    }

    const snapshot = await buildProcessLandscapeSnapshot(baseResolved, manifestJson, {
      ...(contentProfile ? { contentProfile: contentProfile as "structural" | "model-content" } : {}),
    });

    const existing = await lstat(outputPath).catch((error: NodeJS.ErrnoException) => {
      if (error.code !== "ENOENT") throw error;
      return undefined;
    });
    if (existing?.isSymbolicLink()) throw new ProcessLandscapeError("invalid-output-path", "Output must not be a symlink");

    const payload = Buffer.from(`${JSON.stringify(snapshot, null, 2)}\n`);
    const temporary = join(dirname(outputPath), `.${basename(outputPath)}.${randomUUID()}.tmp`);
    try {
      await writeFile(temporary, payload, { flag: "wx", mode: 0o600 });
      await rename(temporary, outputPath);
    } finally {
      await unlink(temporary).catch((error: NodeJS.ErrnoException) => { if (error.code !== "ENOENT") throw error; });
    }
    console.log(`${snapshot.coverage.status} ${snapshot.snapshotId}: ${snapshot.processes.length} processes, ${snapshot.diagnostics.length} snapshot diagnostics`);
    return 0;
  } catch (error) {
    const code = error instanceof ProcessLandscapeError ? `${error.code}: ` : "";
    console.error(`${code}${error instanceof Error ? error.message : "Process landscape extraction failed"}`);
    return 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exitCode = await main();
}
