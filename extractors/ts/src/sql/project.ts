import { readdirSync, readFileSync } from "node:fs";
import { basename, dirname, join, posix as path } from "node:path";
import type { Dialect } from "./lexer.ts";

/** One analysis unit of SQL: a database project (`.sqlproj`) or a folder of scripts / migrations. */
export interface SqlUnit {
  /** Repository-relative directory (`.` for the repository root). */
  dir: string;
  name: string;
  dialect: Dialect;
  /** Repository-relative script paths in processing order (migrations sort by path). */
  files: string[];
  projectFile?: string;
  migrations: boolean;
  /** Set when the name was not given by a project file; says where it came from. */
  nameSource?: string;
}

const SKIP_DIRS = new Set(["node_modules", ".git", "bin", "obj", "dist", "build", "out", "coverage", ".vs", ".idea"]);
/** Folder names that group scripts inside a database folder; the unit is the folder above them. */
const GROUPING = new Set([
  "migrations", "migration", "tables", "table", "functions", "function", "views", "view", "procedures", "procedure", "stored procedures",
  "storedprocedures", "sprocs", "types", "schemas", "schema", "triggers", "indexes", "seeds", "seed",
]);

const rel = (root: string, file: string) => file.slice(root.length + 1).split("\\").join("/");

function walk(root: string): { sql: string[]; sqlproj: string[]; other: string[] } {
  const found = { sql: [] as string[], sqlproj: [] as string[], other: [] as string[] };
  const go = (dir: string, depth: number) => {
    if (depth > 14) return;
    let entries: import("node:fs").Dirent[];
    try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))) {
      const full = join(dir, e.name);
      if (e.isDirectory()) { if (!SKIP_DIRS.has(e.name) && !e.name.startsWith(".")) go(full, depth + 1); }
      else if (e.isFile()) {
        const lower = e.name.toLowerCase();
        if (lower.endsWith(".sql")) found.sql.push(rel(root, full));
        else if (lower.endsWith(".sqlproj")) found.sqlproj.push(rel(root, full));
        else found.other.push(rel(root, full));
      }
    }
  };
  go(root, 0);
  return found;
}

const globToRegExp = (glob: string) =>
  new RegExp(
    "^" + glob.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*\*\/?/g, (m) => (m.endsWith("/") ? "\u0000" : "\u0001")).replace(/\*/g, "[^/]*")
      .replace(/\u0000/g, "(?:.*/)?").replace(/\u0001/g, ".*").replace(/\?/g, "[^/]") + "$", "i");

/** Reads the parts of a `.sqlproj` that decide which scripts belong to the model. The project is never built. */
function readProject(root: string, projectFile: string, sql: string[]): string[] {
  const text = readFileSync(join(root, projectFile), "utf8");
  const dir = dirname(projectFile) === "." ? "" : dirname(projectFile);
  const norm = (p: string) => path.normalize(path.join(dir, p.replace(/\\/g, "/")));
  const attr = (tag: string, name: string) => [...text.matchAll(new RegExp(`<${tag}\\s+[^>]*?${name}="([^"]+)"`, "gi"))].map((m) => m[1]!);
  const excluded = new Set([...attr("PostDeploy", "Include"), ...attr("PreDeploy", "Include")].map(norm));
  const sdkStyle = /<Project\s[^>]*Sdk="[^"]*Microsoft\.Build\.Sql/i.test(text) || /<Project\s[^>]*Sdk=/i.test(text);
  if (sdkStyle) {
    const removed = [...attr("Build", "Remove"), ...attr("Build", "Exclude")].map((g) => globToRegExp(norm(g)));
    return sql.filter((f) => (dir === "" || f.startsWith(dir + "/")) && !excluded.has(f) && !removed.some((r) => r.test(f)));
  }
  const listed = attr("Build", "Include").map(norm).filter((f) => f.toLowerCase().endsWith(".sql"));
  const known = new Set(sql);
  return listed.filter((f) => known.has(f) && !excluded.has(f));
}

export function detectDialect(texts: string[]): Dialect {
  let tsql = 0;
  let pg = 0;
  for (const t of texts) {
    if (/^\s*GO\s*$/im.test(t)) tsql += 3;
    tsql += (t.match(/\b(nvarchar|varchar\(max\)|uniqueidentifier|datetime2|IDENTITY\s*\(|SET\s+NOCOUNT|\bTOP\s*\(?\d|\[dbo\]|\bdbo\.)/gi) ?? []).length;
    if (/@\w+\s+\w+/.test(t) && /\bBEGIN\b/i.test(t) && /\bDECLARE\b/i.test(t)) tsql += 2;
    pg += (t.match(/(\$\$|\bLANGUAGE\s+(sql|plpgsql)\b|\bserial\b|\bbigserial\b|\btimestamptz\b|\bCREATE\s+EXTENSION\b|::\w+|\bRETURNS\s+(SETOF|TRIGGER)\b|\bON\s+CONFLICT\b|\bILIKE\b|\bCREATE\s+SCHEMA\s+(IF\s+NOT\s+EXISTS\s+)?\w+\s*;)/gi) ?? []).length;
  }
  return tsql > pg ? "tsql" : "postgresql";
}

function packageName(root: string, other: string[], dir: string): string | undefined {
  for (let d = dir; ; d = d === "." ? "." : dirname(d)) {
    const candidate = d === "." ? "package.json" : `${d}/package.json`;
    if (other.includes(candidate)) {
      try {
        const name = (JSON.parse(readFileSync(join(root, candidate), "utf8")) as { name?: unknown }).name;
        if (typeof name === "string" && name) return name.replace(/^@[^/]+\//, "");
      } catch { /* an unreadable package.json simply does not name the unit */ }
    }
    if (d === ".") return undefined;
  }
}

function connectionDatabase(root: string, other: string[]): string | undefined {
  const names = new Set<string>();
  for (const f of other) {
    if (!/(^|\/)(appsettings[^/]*\.json|web\.config|app\.config|[^/]*\.config)$/i.test(f)) continue;
    let text: string;
    try { text = readFileSync(join(root, f), "utf8"); } catch { continue; }
    for (const m of text.matchAll(/(?:Initial Catalog|Database)\s*=\s*([^;"'\\\s]+)/gi)) names.add(m[1]!);
  }
  return names.size === 1 ? [...names][0] : undefined;
}

/** Finds the SQL units of a source tree: `.sqlproj` projects first, then folders of loose scripts. */
export function discoverSqlUnits(root: string): SqlUnit[] {
  const { sql, sqlproj, other } = walk(root);
  const units: SqlUnit[] = [];
  const claimed = new Set<string>();

  for (const projectFile of sqlproj) {
    const dir = dirname(projectFile) === "." ? "." : dirname(projectFile);
    const files = readProject(root, projectFile, sql).filter((f) => !claimed.has(f));
    for (const f of files) claimed.add(f);
    units.push({ dir, name: basename(projectFile).replace(/\.sqlproj$/i, ""), dialect: "tsql", files: files.sort(), projectFile, migrations: false });
  }

  const loose = sql.filter((f) => !claimed.has(f));
  const groups = new Map<string, string[]>();
  for (const f of loose) {
    let dir = dirname(f);
    while (dir !== "." && GROUPING.has(basename(dir).toLowerCase())) dir = dirname(dir);
    groups.set(dir, [...(groups.get(dir) ?? []), f]);
  }
  for (const [dir, files] of [...groups].sort(([a], [b]) => (a < b ? -1 : 1))) {
    const texts = files.map((f) => readFileSync(join(root, f), "utf8"));
    const dialect = detectDialect(texts);
    const migrations = dialect === "postgresql" && files.some((f) => /(^|\/)migrations?\//i.test(f));
    let name: string | undefined;
    let nameSource = "";
    if (dialect === "tsql") {
      name = connectionDatabase(root, other);
      nameSource = "the database named in the repository's connection strings";
      if (!name) {
        const sln = other.find((f) => f.toLowerCase().endsWith(".sln"));
        if (sln) { name = `${basename(sln).replace(/\.sln$/i, "")}Db`; nameSource = "the solution name"; }
      }
    } else {
      const pkg = packageName(root, other, dir);
      if (pkg) { name = `${pkg}-db`; nameSource = "the name of the enclosing package"; }
    }
    if (!name) { name = dir === "." ? "database" : basename(dir); nameSource = "the folder name"; }
    units.push({ dir, name, dialect, files: files.sort(), migrations, nameSource });
  }
  return units.filter((u) => u.files.length > 0).sort((a, b) => (a.dir < b.dir ? -1 : a.dir > b.dir ? 1 : 0));
}
