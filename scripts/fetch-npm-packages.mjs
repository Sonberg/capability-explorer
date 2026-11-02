import { chmodSync, existsSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { homedir } from "node:os";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const manifestPath = process.argv[2] && resolve(root, process.argv[2]);

export function resolveHostNpmFeeds(feeds, contents, env = process.env) {
  const tokens = new Map();
  for (const line of contents.split(/\r?\n/)) {
    const match = line.match(/^\s*(\/\/[^\s=]+:_authToken)\s*=\s*(.*?)\s*$/);
    if (match) tokens.set(match[1], match[2].replace(/^(["'])(.*)\1$/, "$2"));
  }
  const warnings = new Set();
  const resolved = feeds.map(feed => {
    if (feed.kind !== "npm") return feed;
    const registry = new URL(feed.url);
    const path = registry.pathname.endsWith("/") ? registry.pathname : registry.pathname + "/";
    const configured = tokens.get(`//${registry.host}${path}:_authToken`);
    if (!configured) return feed;
    let missing = false;
    const secret = configured.replace(/\$\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (_, name) => {
      if (!env[name]) {
        warnings.add(`npm host credential: ${name} is not set; using saved feed settings`);
        missing = true;
      }
      return env[name] || "";
    });
    if (missing || !secret || /\$\{|[\r\n\0]/.test(secret)) return feed;
    return { ...feed, username: null, secret };
  });
  return { feeds: resolved, warnings: [...warnings] };
}

export function buildNpmrc(feeds, envPrefix = "CE_NPM") {
  const credentials = {};
  const lines = [];
  let tokenIndex = 0;
  const scopes = new Set();
  let hasDefault = false;

  for (const feed of feeds.filter(item => item.kind === "npm")) {
    const registry = new URL(feed.url);
    if (registry.protocol !== "https:" || registry.username || registry.password || registry.search || registry.hash)
      throw new Error("Invalid npm registry URL in saved settings");
    if (!registry.pathname.endsWith("/")) registry.pathname += "/";
    const scopeList = (feed.scope || "").split(",").map(scope => scope.trim().toLowerCase()).filter(Boolean);
    if (scopeList.length === 0) {
      if (hasDefault) throw new Error("Only one npm registry can be the default registry");
      hasDefault = true;
      lines.push(`registry=${registry.href}`);
    } else {
      for (const scope of scopeList) {
        if (!/^@[a-z0-9][a-z0-9._-]{0,63}$/.test(scope) || scopes.has(scope)) throw new Error("Invalid or duplicate npm scope");
        scopes.add(scope);
        lines.push(`${scope}:registry=${registry.href}`);
      }
    }
    if (feed.secret) {
      const authPath = `//${registry.host}${registry.pathname}:`;
      if (feed.username) {
        const key = `${envPrefix}_AUTH_${tokenIndex}`;
        credentials[key] = Buffer.from(`${feed.username}:${feed.secret}`).toString("base64");
        lines.push(`${authPath}_auth=\${${key}}`);
      } else {
        const key = `${envPrefix}_TOKEN_${tokenIndex}`;
        credentials[key] = feed.secret;
        lines.push(`${authPath}_authToken=\${${key}}`);
      }
      lines.push(`${authPath}always-auth=true`);
      tokenIndex++;
    }
  }

  return { contents: lines.join("\n") + "\n", credentials };
}

export function findPackageRoots(source) {
  const roots = [];
  const visit = dir => {
    let entries;
    try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return; }
    const names = new Set(entries.filter(entry => entry.isFile()).map(entry => entry.name));
    if (names.has("package.json") && (names.has("package-lock.json") || names.has("npm-shrinkwrap.json"))) roots.push(dir);
    for (const entry of entries) {
      if (entry.isDirectory() && entry.name !== "node_modules" && entry.name !== ".git") visit(resolve(dir, entry.name));
    }
  };
  visit(source);
  return roots;
}

export function manifestSources(manifestPath, contents) {
  return contents.trim().split(/\r?\n/).filter(Boolean)
    .map(row => row.split("\t")[2]).filter(Boolean)
    .map(source => resolve(dirname(manifestPath), source));
}

async function main() {
  const configured = JSON.parse(process.env.CE_PACKAGE_FEEDS || "{\"feeds\":[]}");
  if (process.argv[2] === "--resolve-host-feeds") {
    const path = process.env.npm_config_userconfig || process.env.NPM_CONFIG_USERCONFIG || resolve(homedir(), ".npmrc");
    const resolved = resolveHostNpmFeeds(configured.feeds || [], existsSync(path) ? readFileSync(path, "utf8") : "");
    for (const warning of resolved.warnings) console.error(warning);
    process.stdout.write(JSON.stringify({ feeds: resolved.feeds }));
    return;
  }
  if (!manifestPath) throw new Error("usage: fetch-npm-packages.mjs <manifest.tsv>");
  const { contents, credentials } = buildNpmrc(configured.feeds || [], `CE_NPM_${randomUUID().replaceAll("-", "").toUpperCase()}`);
  const manifest = readFileSync(manifestPath, "utf8");
  const sources = manifestSources(manifestPath, manifest);
  const targets = [...new Set(sources.flatMap(source => findPackageRoots(source)))].sort();
  const configPath = `/tmp/ce-npmrc-${process.pid}`;
  writeFileSync(configPath, contents, { mode: 0o600 });
  chmodSync(configPath, 0o600);

  let installed = 0;
  let failed = 0;
  try {
    for (const dir of targets) {
      const npmEnv = { ...process.env, ...credentials, npm_config_ignore_scripts: "true" };
      delete npmEnv.CE_PACKAGE_FEEDS;
      const result = spawnSync("npm", ["ci", "--ignore-scripts", "--no-audit", "--fund=false", "--userconfig", configPath], {
        cwd: dir,
        stdio: "inherit",
        timeout: 15 * 60 * 1000,
        env: npmEnv,
      });
      if (result.error || result.status !== 0) {
        failed++;
        console.error(`npm dependencies unavailable for ${relative(root, dir)}${result.status === null ? ` (${result.error?.name || "spawn error"})` : ` (exit ${result.status})`}`);
      } else {
        installed++;
      }
    }
  } finally {
    rmSync(configPath, { force: true });
  }

  console.log(`npm packages: ${installed} lockfile-backed project(s) installed, ${failed} unavailable; lifecycle scripts disabled`);
}

if (resolve(process.argv[1] || "") === fileURLToPath(import.meta.url)) await main();