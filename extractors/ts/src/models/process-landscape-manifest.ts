import { createHash } from "node:crypto";
import { constants } from "node:fs";
import type { FileHandle } from "node:fs/promises";
import { lstat, open, realpath, stat as fsStat } from "node:fs/promises";
import { basename, dirname, join, resolve, sep } from "node:path";
import { ProcessLandscapeError } from "./process-landscape-types.ts";

const IS_WINDOWS = process.platform === "win32";

/**
 * `fs.constants.O_NOFOLLOW` is POSIX-only and is `undefined` on Windows. Bitwise-OR-ing a flag
 * set with `undefined` coerces it to 0 via ToInt32, which silently drops the flag rather than
 * failing: `O_RDONLY | undefined === O_RDONLY | 0 === O_RDONLY`. Computing the flag set explicitly
 * (instead of inline bitwise composition with the raw constant) makes that platform gap visible
 * and lets Windows rely on the explicit reparse-point checks below instead of a flag it cannot use.
 */
function noFollowReadFlags(): number {
  return constants.O_RDONLY | (IS_WINDOWS ? 0 : constants.O_NOFOLLOW);
}

async function openCheckedFile(path: string, raceCode: string, displayPath: string): Promise<FileHandle> {
  try {
    return await open(path, noFollowReadFlags());
  } catch (error) {
    // A checked path can disappear, lose a directory component, or become a symlink before open.
    // Permission, I/O, resource-exhaustion and programming failures are not evidence of that race.
    const errno = (error as NodeJS.ErrnoException | null)?.code;
    if (errno !== "ENOENT" && errno !== "ENOTDIR" && errno !== "ELOOP") throw error;
    const failure = new ProcessLandscapeError(
      raceCode, `Checked file path changed before it could be opened safely: ${displayPath} (${errno})`,
    );
    failure.cause = error;
    throw failure;
  }
}

/**
 * On Windows, a directory reparse point (junction) is not reliably reported by `lstat().isSymbolicLink()`
 * the way a POSIX symlink is: junctions carry the `IO_REPARSE_TAG_MOUNT_POINT` tag rather than
 * `IO_REPARSE_TAG_SYMLINK`, and relying on `isSymbolicLink()` alone to catch them is not a portable
 * invariant. This performs a name-independent identity check instead: a reparse point (symlink or
 * junction) resolves to a *different* filesystem object than the path name itself, so its
 * dev/ino pair (via `stat`, which follows reparse points) will differ from the un-followed `lstat`
 * of the same path -- or `stat` will fail outright for a dangling link/junction. A plain file or
 * directory's `stat` and `lstat` identity always agree. This check is Windows-only; POSIX symlinks
 * are already caught earlier by `lstat().isSymbolicLink()`.
 */
async function assertNoWindowsReparsePoint(path: string, lstatInfo: { dev: number; ino: number }, code: string, message: string): Promise<void> {
  if (!IS_WINDOWS) return;
  const followed = await fsStat(path).catch(() => undefined);
  if (!followed || followed.dev !== lstatInfo.dev || followed.ino !== lstatInfo.ino) {
    throw new ProcessLandscapeError(code, message);
  }
}

/** Fails closed when the platform cannot report a trustworthy file identity (e.g. dev/ino both zero). */
function assertReliableFileIdentity(info: { dev: number; ino: number }, relativePath: string): void {
  if (info.dev === 0 && info.ino === 0) {
    throw new ProcessLandscapeError(
      "windows-file-identity-unavailable",
      `File identity (device/inode) is unavailable for this path; refusing to read without a reliable identity: ${relativePath}`,
    );
  }
}

async function readHandleBounded(handle: FileHandle, maxBytes: number): Promise<Buffer> {
  const buffer = Buffer.alloc(maxBytes + 1);
  let length = 0;
  while (length < buffer.length) {
    const { bytesRead } = await handle.read(buffer, length, buffer.length - length);
    if (bytesRead === 0) break;
    length += bytesRead;
  }
  return buffer.subarray(0, length);
}

export const MANIFEST_SCHEMA = "ce.process-landscape-manifest.v1";

export type ManifestRole =
  | "bpmn" | "process-metadata" | "cross-process-events" | "object-lifecycle" | "phase-journey" | "navigator-data";

export interface ManifestEntry {
  path: string;
  role: ManifestRole;
}

export interface ProcessLandscapeManifest {
  schema: typeof MANIFEST_SCHEMA;
  manifestVersion: 1;
  repositoryId: string;
  entries: ManifestEntry[];
}

const SINGLETON_ROLES = new Set<ManifestRole>(["process-metadata", "cross-process-events", "object-lifecycle", "phase-journey", "navigator-data"]);
const ROLE_EXTENSIONS: Record<ManifestRole, RegExp> = {
  bpmn: /\.(?:bpmn|bpmn20\.xml)$/i,
  "process-metadata": /\.json$/i,
  "cross-process-events": /\.json$/i,
  "object-lifecycle": /\.json$/i,
  "phase-journey": /\.json$/i,
  "navigator-data": /\.js$/i,
};

export const MANIFEST_LIMITS = {
  maxEntries: 512,
  maxPathLength: 240,
  maxPathDepth: 16,
  maxBpmnBytes: 4 * 1024 * 1024,
  maxSidecarBytes: 2 * 1024 * 1024,
  // The bounded navigator-data bundle is a single generated static-navigator `.js` file. The bound
  // must stay comfortably above real-world bundle sizes while remaining an explicit, finite cap
  // rather than an unbounded read.
  maxNavigatorDataBytes: 16 * 1024 * 1024,
  maxTotalBytes: 96 * 1024 * 1024,
} as const;

export function sha256Hex(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}

function isSafeRelativePath(value: unknown): value is string {
  return typeof value === "string"
    && value.length > 0
    && value.length <= MANIFEST_LIMITS.maxPathLength
    && !value.startsWith("/")
    && !/^[a-zA-Z]:[\\/]/.test(value)
    && !value.startsWith("\\\\")
    && !value.includes("\\")
    && !value.includes(":")
    && !/[\u0000-\u001f\u007f]/.test(value)
    && (() => {
      const parts = value.split("/");
      return parts.length <= MANIFEST_LIMITS.maxPathDepth
        && parts.every((part) => part.length > 0 && part !== "." && part !== "..");
    })();
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Validates the explicit manifest shape and rejects unsupported roles, extensions, and duplicate paths. */
export function validateManifest(value: unknown): ProcessLandscapeManifest {
  if (!isPlainObject(value)) throw new ProcessLandscapeError("invalid-manifest", "Manifest must be a JSON object");
  if (value.schema !== MANIFEST_SCHEMA) throw new ProcessLandscapeError("invalid-manifest-schema", `Manifest schema must be ${MANIFEST_SCHEMA}`);
  if (value.manifestVersion !== 1) throw new ProcessLandscapeError("invalid-manifest-version", "Manifest version must be 1");
  const repositoryId = value.repositoryId;
  if (typeof repositoryId !== "string" || !repositoryId.trim() || /[\u0000-\u001f]/.test(repositoryId)) {
    throw new ProcessLandscapeError("invalid-repository", "Manifest repositoryId must be non-empty and contain no control characters");
  }
  const entries = value.entries;
  if (!Array.isArray(entries) || entries.length === 0) {
    throw new ProcessLandscapeError("invalid-manifest-entries", "Manifest must declare at least one entry");
  }
  if (entries.length > MANIFEST_LIMITS.maxEntries) {
    throw new ProcessLandscapeError("manifest-entry-limit", `Manifest exceeds the entry limit (${MANIFEST_LIMITS.maxEntries})`);
  }

  const seenPaths = new Set<string>();
  const seenSingletons = new Set<ManifestRole>();
  const result: ManifestEntry[] = [];
  for (const raw of entries) {
    if (!isPlainObject(raw)) throw new ProcessLandscapeError("invalid-manifest-entry", "Manifest entry must be a JSON object");
    const { path, role } = raw;
    if (!isSafeRelativePath(path)) {
      throw new ProcessLandscapeError("invalid-manifest-path", "Manifest entry path must be a safe repository-relative path");
    }
    if (typeof role !== "string" || !(role in ROLE_EXTENSIONS)) {
      throw new ProcessLandscapeError("unsupported-manifest-role", `Unsupported manifest role: ${String(role)}`);
    }
    const typedRole = role as ManifestRole;
    if (!ROLE_EXTENSIONS[typedRole].test(path)) {
      throw new ProcessLandscapeError("unsupported-manifest-extension", `Entry extension is not supported for role ${typedRole}`);
    }
    if (seenPaths.has(path)) throw new ProcessLandscapeError("duplicate-manifest-path", `Duplicate manifest path: ${path}`);
    seenPaths.add(path);
    if (SINGLETON_ROLES.has(typedRole)) {
      if (seenSingletons.has(typedRole)) throw new ProcessLandscapeError("duplicate-manifest-role", `Manifest role ${typedRole} must appear at most once`);
      seenSingletons.add(typedRole);
    }
    result.push({ path, role: typedRole });
  }

  return { schema: MANIFEST_SCHEMA, manifestVersion: 1, repositoryId, entries: result };
}

/**
 * Resolves a manifest-declared relative path under baseDir, rejecting any symlink path component
 * (including the final file) and reading the file as raw bytes bounded by maxBytes.
 *
 * Race-resistance invariant: a check-then-open on path strings is inherently vulnerable to a
 * parent-component symlink swap between the per-component `lstat` walk and the final `open`
 * (TOCTOU), because `O_NOFOLLOW` only blocks the final path component from being a symlink, not
 * intermediate directories. To close that gap without relying on a platform-specific `/proc`
 * facility, this function proves containment by filesystem object identity rather than by path
 * string: after opening, it independently resolves the fully symlink-free (canonical) directory
 * that currently backs the requested path via `realpath`, confirms that canonical directory is
 * inside `baseDir`, and then compares the open descriptor's `(device, inode)` pair (`fstat`)
 * against a fresh `lstat` of that canonical, in-root location. A file outside the base directory
 * cannot share both device and inode with a file the filesystem reports as living inside it
 * without literally being that object, so this comparison holds regardless of any symlink swap
 * timing and is portable across platforms that expose POSIX-style device/inode numbers.
 *
 * Windows invariant: `O_NOFOLLOW` does not exist on Windows (`noFollowReadFlags` omits it there
 * rather than silently coercing it to 0), and a directory junction is not reliably reported by
 * `lstat().isSymbolicLink()`. Each path component is additionally checked with
 * `assertNoWindowsReparsePoint`, which compares the followed (`stat`) and unfollowed (`lstat`)
 * identity of the same path name; a junction resolves to a different object and is rejected before
 * any read. If the platform cannot report a trustworthy device/inode pair at all (both zero),
 * `assertReliableFileIdentity` fails closed with an explicit `windows-file-identity-unavailable`
 * error instead of proceeding on an unverifiable identity.
 */
export async function readManifestFile(baseDir: string, relativePath: string, maxBytes: number): Promise<Buffer> {
  const root = await realpath(resolve(baseDir)).catch((error: NodeJS.ErrnoException) => {
    throw new ProcessLandscapeError("manifest-base-missing", `Manifest base directory does not exist: ${error.code ?? "error"}`);
  });
  const segments = relativePath.split("/");
  let current = root;
  for (const [index, segment] of segments.entries()) {
    current = join(current, segment);
    const info = await lstat(current).catch((error: NodeJS.ErrnoException) => {
      throw new ProcessLandscapeError("manifest-path-missing", `Manifest path does not exist: ${relativePath} (${error.code ?? "error"})`);
    });
    if (info.isSymbolicLink()) {
      throw new ProcessLandscapeError("manifest-path-symlink", `Manifest path contains a symbolic link component: ${relativePath}`);
    }
    const isLast = index === segments.length - 1;
    if (isLast && !info.isFile()) throw new ProcessLandscapeError("manifest-path-not-file", `Manifest path is not a regular file: ${relativePath}`);
    if (!isLast && !info.isDirectory()) throw new ProcessLandscapeError("manifest-path-not-directory", `Manifest path segment is not a directory: ${relativePath}`);
    await assertNoWindowsReparsePoint(
      current, info, "manifest-path-junction",
      `Manifest path contains a reparse-point (junction) component that cannot be verified as a plain ${isLast ? "file" : "directory"}: ${relativePath}`,
    );
  }
  if (!current.startsWith(`${root}${sep}`) && current !== root) {
    throw new ProcessLandscapeError("manifest-path-escape", `Manifest path escapes the base directory: ${relativePath}`);
  }

  const handle = await openCheckedFile(current, "manifest-path-race-detected", relativePath);
  try {
    const stat = await handle.stat();
    if (!stat.isFile() || stat.size > maxBytes) {
      throw new ProcessLandscapeError("manifest-file-size-limit", `Manifest file exceeds its byte limit: ${relativePath}`);
    }
    assertReliableFileIdentity(stat, relativePath);

    const canonicalDir = await realpath(dirname(current)).catch((error: NodeJS.ErrnoException) => {
      throw new ProcessLandscapeError("manifest-path-missing", `Manifest path parent does not exist: ${relativePath} (${error.code ?? "error"})`);
    });
    if (canonicalDir !== root && !canonicalDir.startsWith(`${root}${sep}`)) {
      throw new ProcessLandscapeError("manifest-path-escape", `Manifest path resolves outside the base directory: ${relativePath}`);
    }
    const canonicalStat = await lstat(join(canonicalDir, basename(current))).catch((error: NodeJS.ErrnoException) => {
      throw new ProcessLandscapeError("manifest-path-missing", `Manifest path identity could not be reverified: ${relativePath} (${error.code ?? "error"})`);
    });
    if (canonicalStat.dev !== stat.dev || canonicalStat.ino !== stat.ino) {
      throw new ProcessLandscapeError("manifest-path-race-detected", `Manifest path identity changed during access, refusing to read: ${relativePath}`);
    }
    const bytes = await readHandleBounded(handle, maxBytes);
    if (bytes.length > maxBytes) throw new ProcessLandscapeError("manifest-file-size-limit", `Manifest file exceeds its byte limit: ${relativePath}`);
    return bytes;
  } finally {
    await handle.close();
  }
}

/**
 * Reads a single, already-resolved absolute file path as raw bytes bounded by maxBytes, rejecting a
 * symlinked path (POSIX) and a reparse-point/junction path (Windows) before ever returning bytes, with
 * the same post-open device/inode identity reverification as {@link readManifestFile}. Used for
 * top-level single-path inputs (such as a CLI-supplied manifest file) that are not resolved through a
 * multi-segment relative walk under a base directory.
 */
export async function readBoundedFile(absolutePath: string, maxBytes: number): Promise<Buffer> {
  const info = await lstat(absolutePath).catch((error: NodeJS.ErrnoException) => {
    throw new ProcessLandscapeError("bounded-file-missing", `File does not exist: ${absolutePath} (${error.code ?? "error"})`);
  });
  if (info.isSymbolicLink()) throw new ProcessLandscapeError("bounded-file-symlink", `File must not be a symbolic link: ${absolutePath}`);
  if (!info.isFile()) throw new ProcessLandscapeError("bounded-file-not-file", `File must be a regular file: ${absolutePath}`);
  await assertNoWindowsReparsePoint(absolutePath, info, "bounded-file-junction", `File is a reparse-point (junction) that cannot be verified as a plain file: ${absolutePath}`);

  const handle = await openCheckedFile(absolutePath, "bounded-file-race-detected", absolutePath);
  try {
    const stat = await handle.stat();
    if (!stat.isFile() || stat.size < 1 || stat.size > maxBytes) {
      throw new ProcessLandscapeError("bounded-file-size-limit", `File must be a non-empty regular file within the byte limit: ${absolutePath}`);
    }
    assertReliableFileIdentity(stat, absolutePath);
    const reverified = await lstat(absolutePath).catch((error: NodeJS.ErrnoException) => {
      throw new ProcessLandscapeError("bounded-file-missing", `File identity could not be reverified: ${absolutePath} (${error.code ?? "error"})`);
    });
    if (reverified.dev !== stat.dev || reverified.ino !== stat.ino) {
      throw new ProcessLandscapeError("bounded-file-race-detected", `File identity changed during access, refusing to read: ${absolutePath}`);
    }
    const bytes = await readHandleBounded(handle, maxBytes);
    if (bytes.length > maxBytes) throw new ProcessLandscapeError("bounded-file-size-limit", `File exceeds its byte limit: ${absolutePath}`);
    return bytes;
  } finally {
    await handle.close();
  }
}

export function decodeUtf8Strict(bytes: Uint8Array, relativePath: string): string {
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    throw new ProcessLandscapeError("invalid-source-bytes", `Manifest file is not valid UTF-8: ${relativePath}`);
  }
}
