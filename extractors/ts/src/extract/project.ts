import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import ts from "typescript";

/** A TypeScript/JavaScript unit: one tsconfig.json (or a package.json with plain JavaScript and no tsconfig). */
export interface UnitConfig {
  /** Absolute path of the config file the unit is named after. */
  configFile: string;
  /** Directory of the nearest package.json at or above the config: module paths in keys are relative to it. */
  packageRoot: string;
  packageName: string;
  packageJson?: string;
  options: ts.CompilerOptions;
  rootNames: string[];
  configErrors: string[];
}

const SKIP_DIRS = new Set(["node_modules", ".git", "bin", "obj", "dist", "build", "out", "coverage", ".next", ".turbo"]);

export const posix = (p: string) => p.split(sep).join("/");

export function isTestPath(path: string): boolean {
  const p = posix(path);
  return p.split("/").some((s) => /^(?:test|tests|__tests__)$/i.test(s)) || /^(?:.*\.)?(?:test|spec)\.[cm]?[jt]sx?$/i.test(p.split("/").at(-1) ?? "");
}

function walk(dir: string, found: (file: string) => void, depth = 0) {
  if (depth > 12) return;
  let entries: import("node:fs").Dirent[];
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const e of entries) {
    if (e.isDirectory()) {
      if (!SKIP_DIRS.has(e.name) && !e.name.startsWith(".")) walk(join(dir, e.name), found, depth + 1);
    } else if (e.isFile()) {
      found(join(dir, e.name));
    }
  }
}

function nearestPackage(start: string, root: string): { dir: string; name: string; file?: string } {
  for (let dir = start; ; dir = dirname(dir)) {
    const file = join(dir, "package.json");
    if (existsSync(file)) {
      try {
        const name = (JSON.parse(readFileSync(file, "utf8")) as { name?: unknown }).name;
        if (typeof name === "string" && name.length > 0) return { dir, name, file };
      } catch {
        /* unreadable package.json: keep looking upwards */
      }
    }
    if (dir === root || dirname(dir) === dir || !dir.startsWith(root)) break;
  }
  const rel = posix(relative(root, start));
  return { dir: start, name: rel || "(root)" };
}

/**
 * Finds units without running anything from the repository: tsconfig.json files are parsed with the compiler's
 * own JSON reader, `extends` to packages that are not installed degrade to a config error. Nothing is installed.
 */
export function discoverUnits(root: string, includeTests = true): UnitConfig[] {
  const tsconfigs: string[] = [];
  const packages: string[] = [];
  walk(root, (f) => {
    if (f.endsWith(`${sep}tsconfig.json`)) tsconfigs.push(f);
    else if (f.endsWith(`${sep}package.json`)) packages.push(f);
  });
  tsconfigs.sort();
  const units: UnitConfig[] = [];
  for (const configFile of tsconfigs) {
    const read = ts.readConfigFile(configFile, (p) => ts.sys.readFile(p));
    const errors: string[] = [];
    if (read.error) errors.push(ts.flattenDiagnosticMessageText(read.error.messageText, "\n"));
    const parsed = ts.parseJsonConfigFileContent(read.config ?? {}, ts.sys, dirname(configFile), undefined, configFile);
    for (const e of parsed.errors) {
      // "No inputs were found" is normal for solution-style configs that only list references.
      if (e.code !== 18003) errors.push(ts.flattenDiagnosticMessageText(e.messageText, "\n"));
    }
    const rootNames = parsed.fileNames.filter((f) => resolve(f).startsWith(root) && !f.includes(`${sep}node_modules${sep}`) && !f.includes("/node_modules/")
      && (includeTests || !isTestPath(relative(root, f))));
    if (rootNames.length === 0) continue;
    const pkg = nearestPackage(dirname(configFile), root);
    units.push({
      configFile, packageRoot: pkg.dir, packageName: pkg.name, packageJson: pkg.file,
      options: { ...parsed.options, noEmit: true, skipLibCheck: true },
      rootNames, configErrors: errors,
    });
  }

  // Plain JavaScript packages without any tsconfig above them.
  const covered = (dir: string) => units.some((u) => dir.startsWith(dirname(u.configFile)));
  for (const file of packages.sort()) {
    const dir = dirname(file);
    if (covered(dir)) continue;
    const sources: string[] = [];
    walk(dir, (f) => {
      if (/\.(m|c)?jsx?$/.test(f) && !f.endsWith(".min.js")) sources.push(f);
    });
    // Nested packages are their own units.
    const own = sources.filter((s) => nearestPackage(dirname(s), root).dir === dir && (includeTests || !isTestPath(relative(root, s))));
    if (own.length === 0) continue;
    const pkg = nearestPackage(dir, root);
    units.push({
      configFile: file, packageRoot: pkg.dir, packageName: pkg.name, packageJson: pkg.file,
      options: { allowJs: true, checkJs: true, noEmit: true, skipLibCheck: true, target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.NodeNext, moduleResolution: ts.ModuleResolutionKind.NodeNext, resolveJsonModule: true },
      rootNames: own, configErrors: [],
    });
  }
  return units;
}

export function isFile(p: string) {
  try {
    return statSync(p).isFile();
  } catch {
    return false;
  }
}
