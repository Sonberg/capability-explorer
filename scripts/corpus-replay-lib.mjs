import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { canonical, hash, inside, inventory, requireThat, semanticRecord } from './mapping-replay-lib.mjs';
export const readJson = (path) => JSON.parse(readFileSync(path, 'utf8'));
const digest = (v) => typeof v === 'string' && /^[a-f0-9]{64}$/.test(v);
export function safePath(root, name) {
  requireThat(typeof name === 'string' && /^[A-Za-z0-9._/-]+$/.test(name), 'invalid bounded relative path');
  return inside(root, name);
}
export function verifySource(root, repo) {
  requireThat(/^[a-z0-9][a-z0-9._-]{0,99}$/.test(repo.id) && /^[a-f0-9]{40}$/.test(repo.commit), 'invalid repository identity');
  const source = safePath(root, repo.source), checkout = safePath(root, repo.checkout);
  const git = (...args) => execFileSync('git', ['--no-replace-objects', '-C', checkout, ...args], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  requireThat(git('rev-parse', '--verify', 'HEAD').trim() === repo.commit, 'stale checkout SHA');
  const entries = git('ls-tree', '-r', '-z', repo.commit).split('\0').filter(Boolean), files = {};
  requireThat(entries.length > 0, 'empty Git source tree');
  for (const entry of entries) {
    const match = /^(100644|100755) blob ([a-f0-9]{40})\t(.+)$/.exec(entry);
    requireThat(match, 'source contains unsupported symlink/submodule/tree entry');
    const [, , object, path] = match;
    // Hash raw bytes, never execute repository programs, hooks or filters.
    const bytes = execFileSync('git', ['--no-replace-objects', '-C', checkout, 'cat-file', 'blob', object], { maxBuffer: 64 * 1024 * 1024 });
    files[path] = hash(bytes);
  }
  requireThat(canonical(files) === canonical(repo.files) && canonical(inventory(source)) === canonical(files), 'export differs from locked Git commit');
  requireThat(repo.inputHash === hash(canonical(files)), 'invalid source input hash');
  return files;
}
export function verifyBundle(root, spec) {
  const path = safePath(root, spec.path), build = readJson(resolve(path, 'build.json'));
  requireThat(build.format === 'mapping-replay-build.v1' && digest(spec.buildHash) && spec.buildHash === build.buildHash, 'unbound build identity');
  const files = inventory(path, new Set(['build.json']));
  requireThat(hash(canonical(files)) === spec.buildHash && canonical(files) === canonical(build.files), 'unsealed build dependencies');
  requireThat(build.sourceFiles && Object.keys(build.sourceFiles).length > 0 && build.sourceSnapshotHash === hash(canonical(build.sourceFiles)), 'invalid build source snapshot');
  for (const required of ['csharp/ce-cs.dll', 'ts/extractors/ts/src/cli.ts', 'ts/extractors/ts/package-lock.json', 'ts/schema/v1/record.schema.json']) {
    requireThat(digest(files[required]), 'missing required build input');
  }
  requireThat(files['ts/schema/v1/record.schema.json'] === hash(readFileSync(safePath(root, 'schema/v1/record.schema.json'))), 'schema mismatch');
  return build;
}
export function validatePlan(root, plan, { verifySources = verifySource, verifyBuild = verifyBundle } = {}) {
  requireThat(plan.format === 'corpus-replay-plan.v1', 'unsupported corpus plan');
  requireThat(Object.keys(plan).every((k) => ['format','output','images','builds','repositories'].includes(k)), 'unknown plan field');
  const output = safePath(root, plan.output);
  requireThat(output.startsWith(resolve(root, '.sandbox-cache/corpus-replay') + '/'), 'output outside private corpus cache');
  requireThat(plan.images && ['node','dotnet'].every((p) => /^sha256:[a-f0-9]{64}$/.test(plan.images[p])), 'exact local image digests required');
  requireThat(plan.builds && Object.keys(plan.builds).sort().join(',') === 'baseline,candidate', 'baseline and candidate required');
  const builds = Object.fromEntries(Object.entries(plan.builds).map(([lane, spec]) => [lane, verifyBuild(root, spec)]));
  requireThat(Array.isArray(plan.repositories) && plan.repositories.length > 0 && plan.repositories.length <= 100, 'bounded nonempty repositories required');
  const ids = new Set();
  for (const repo of plan.repositories) {
    requireThat(Object.keys(repo).every((k) => ['id','commit','source','checkout','files','inputHash','commands','dependencies'].includes(k)), 'unknown repository field');
    requireThat(!ids.has(repo.id), 'duplicate repository'); ids.add(repo.id);
    requireThat(Array.isArray(repo.commands) && repo.commands.length > 0 && new Set(repo.commands).size === repo.commands.length
      && repo.commands.every((c) => ['csharp','ts','sql'].includes(c)), 'unknown or duplicate extraction command');
    verifySources(root, repo);
    if (repo.commands.includes('csharp')) {
      const deps = repo.dependencies;
      requireThat(deps && deps.packages && deps.lock && digest(deps.lockHash) && digest(deps.packagesHash), 'sealed C# dependencies and lock required');
      requireThat(hash(readFileSync(safePath(root, deps.lock))) === deps.lockHash, 'package lock changed');
      const lock = readJson(safePath(root, deps.lock));
      requireThat(lock.version === 1 && lock.projects && Object.keys(lock.projects).length > 0, 'invalid or empty package lock');
      const projects = Object.keys(repo.files).filter((p) => p.endsWith('.csproj')).sort();
      requireThat(canonical(Object.keys(lock.projects).sort()) === canonical(projects), 'package lock project coverage differs');
      for (const packages of Object.values(lock.projects)) {
        requireThat(Array.isArray(packages), 'invalid locked package list');
        const seen = new Set();
        for (const pkg of packages) {
          requireThat(typeof pkg.id === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(pkg.id)
            && typeof pkg.version === 'string' && /^[0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?$/.test(pkg.version), 'invalid exact package identity');
          requireThat(!seen.has(pkg.id.toLowerCase()), 'duplicate locked package'); seen.add(pkg.id.toLowerCase());
          requireThat(existsSync(safePath(root, `${deps.packages}/${pkg.id.toLowerCase()}/${pkg.version.toLowerCase()}`)), 'missing locked package folder');
        }
      }
      requireThat(hash(canonical(inventory(safePath(root, deps.packages)))) === deps.packagesHash, 'unsealed package dependencies');
    }
  }
  // Candidate build must describe current tracked extractor inputs, not a caller's stale label.
  for (const [path, expected] of Object.entries(builds.candidate.sourceFiles)) {
    requireThat(existsSync(safePath(root, path)) && hash(readFileSync(safePath(root, path))) === expected, 'stale candidate source snapshot');
  }
  for (const prefix of ['extractors/ts/src/', 'schema/', 'platform/src/CapabilityExplorer.Extractors.CSharp/', 'platform/src/CapabilityExplorer.Contracts/']) {
    const expected = Object.fromEntries(Object.entries(builds.candidate.sourceFiles).filter(([p]) => p.startsWith(prefix)).map(([p,v]) => [p.slice(prefix.length),v]));
    if (Object.keys(expected).length) requireThat(canonical(inventory(safePath(root,prefix.slice(0,-1)),new Set(['bin','obj']))) === canonical(expected), 'candidate source file set changed');
  }
  return { output, builds, planHash: hash(canonical(plan)) };
}
export function semanticDelta(before, after) {
  const records = (input) => input.filter((r) => ['node','edge','diagnostic','calculation','complete'].includes(r.kind)).map((r) => canonical(semanticRecord(r)));
  const subtract = (a, b) => { const counts = new Map(); for (const s of b) counts.set(s, (counts.get(s) ?? 0) + 1); return a.filter((s) => { const n = counts.get(s) ?? 0; if (n) counts.set(s,n-1); return !n; }); };
  const first = records(before), second = records(after);
  return { removed: subtract(first,second).map(JSON.parse), added: subtract(second,first).map(JSON.parse) };
}
