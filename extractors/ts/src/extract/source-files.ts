import { lstatSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import ts from 'typescript';

/** Selected checkout is immutable during extraction; reject symlink components before reading. */
export function containedSource(root: string, file: string): boolean {
  const base = resolve(root), target = resolve(file), rel = relative(base, target);
  if (isAbsolute(rel) || rel === '..' || rel.startsWith('..' + sep)) return false;
  try {
    let current = base;
    if (lstatSync(current).isSymbolicLink()) return false;
    for (const part of rel.split(sep).filter(Boolean)) {
      current = resolve(current, part);
      if (lstatSync(current).isSymbolicLink()) return false;
    }
    return true;
  } catch { return false; }
}

export function readSource(root: string, file: string): string | undefined {
  if (!containedSource(root, file)) return undefined;
  try { return readFileSync(file, 'utf8'); } catch { return undefined; }
}

// Pinned TypeScript exposes its own glob matcher. Preserve tsconfig include/exclude semantics
// while supplying a directory enumerator that never follows repository symlinks.
type Matcher = (path: string, extensions: readonly string[] | undefined, excludes: readonly string[] | undefined,
  includes: readonly string[] | undefined, caseSensitive: boolean, cwd: string, depth: number | undefined,
  entries: (path: string) => { files: string[]; directories: string[] }, realpath: (path: string) => string) => string[];
const matchFiles = (ts as unknown as { matchFiles: Matcher }).matchFiles;

export function sourceSystem(root: string, builtins = false): ts.System {
  const lib = dirname(ts.getDefaultLibFilePath({}));
  const allowed = (file: string) => containedSource(root, file) || (builtins &&
    dirname(resolve(file)) === lib && /^lib(?:\.[a-z0-9.-]+)?\.d\.ts$/i.test(file.split(sep).at(-1)!) && containedSource(lib, file));
  const entries = (dir: string) => {
    const result: { files: string[]; directories: string[] } = { files: [], directories: [] };
    if (!containedSource(root, dir)) return result;
    for (const item of readdirSync(dir, { withFileTypes: true })) {
      if (item.isFile()) result.files.push(item.name);
      else if (item.isDirectory()) result.directories.push(item.name);
    }
    return result;
  };
  return { ...ts.sys, getCurrentDirectory: () => root,
    readFile: file => allowed(file) ? ts.sys.readFile(file) : undefined,
    fileExists: file => allowed(file) && ts.sys.fileExists(file),
    directoryExists: dir => containedSource(root, dir) && ts.sys.directoryExists(dir),
    getDirectories: dir => entries(dir).directories,
    realpath: file => resolve(file),
    readDirectory: (dir, extensions, excludes, includes, depth) => matchFiles(dir, extensions, excludes, includes,
      ts.sys.useCaseSensitiveFileNames, root, depth, entries, file => resolve(file)),
  };
}
