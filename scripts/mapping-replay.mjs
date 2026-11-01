import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { canonical, checkAssertions, equalRuns, hash, inside, inventory, replayOutputPath, requireThat, verifyEvidence, verifyLockedFiles } from './mapping-replay-lib.mjs';

const root = process.cwd();
const input = 'tests/mapping-fixtures/source';
const json = (file) => JSON.parse(readFileSync(file, 'utf8'));
const save = (file, value) => writeFileSync(file, canonical(value) + '\n');
const safeOutput = (name) => replayOutputPath(root, name);
const sourcePaths = ['extractors/ts/src', 'extractors/ts/package.json', 'extractors/ts/package-lock.json',
  'schema', 'platform/src/CapabilityExplorer.Extractors.CSharp', 'platform/src/CapabilityExplorer.Contracts',
  'platform/Directory.Build.props', 'platform/Directory.Packages.props', 'platform/global.json'];

function sourceInventory() {
  const files = {};
  for (const path of sourcePaths) {
    if (!path.includes('/src/CapabilityExplorer.') && !['schema', 'extractors/ts/src'].includes(path)) {
      files[path] = hash(readFileSync(inside(root, path))); continue;
    }
    for (const [name, digest] of Object.entries(inventory(inside(root, path), new Set(['bin', 'obj'])))) files[`${path}/${name}`] = digest;
  }
  return files;
}

function prepare(name, commitHint) {
  requireThat(/^[0-9a-f]{40}$/.test(commitHint ?? ''), 'advisory commit label must be a 40-character SHA');
  const dest = safeOutput(name);
  requireThat(!existsSync(dest), 'bundle already exists; choose a new immutable bundle path');
  const binaries = inside(root, 'platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0');
  requireThat(existsSync(resolve(binaries, 'ce-cs.dll')), 'build the C# extractor in scripts/sandbox before packaging');
  requireThat(existsSync(inside(root, 'extractors/ts/node_modules/typescript/lib/typescript.js')), 'install extractor dependencies in scripts/sandbox first');
  const sources = sourceInventory();
  const lock = json(inside(root, 'extractors/ts/package-lock.json'));
  for (const dependency of ['typescript', 'ajv', 'ajv-formats']) {
    requireThat(json(inside(root, `extractors/ts/node_modules/${dependency}/package.json`)).version === lock.packages[`node_modules/${dependency}`].version, `installed ${dependency} differs from lock`);
  }
  mkdirSync(dest, { recursive: true });
  for (const path of ['extractors/ts/src', 'extractors/ts/package.json', 'extractors/ts/package-lock.json', 'schema']) {
    cpSync(inside(root, path), resolve(dest, 'ts', path), { recursive: true, dereference: true });
  }
  // Copy only the dependencies already installed from the fixed extractor lock. No target package is installed.
  cpSync(inside(root, 'extractors/ts/node_modules'), resolve(dest, 'ts/extractors/ts/node_modules'), {
    recursive: true, dereference: true, filter: (path) => !path.includes('/node_modules/.bin'),
  });
  cpSync(binaries, resolve(dest, 'csharp'), { recursive: true, dereference: true });
  requireThat(canonical(sourceInventory()) === canonical(sources), 'source changed during packaging');
  const files = inventory(dest);
  const laneId = (prefix) => hash(canonical(Object.fromEntries(Object.entries(files).filter(([p]) => p.startsWith(prefix)))));
  save(resolve(dest, 'build.json'), {
    format: 'mapping-replay-build.v1', commitHint, sourceSnapshotHash: hash(canonical(sources)), sourceFiles: sources,
    files, buildHash: hash(canonical(files)), tsBuild: laneId('ts/'), csharpBuild: laneId('csharp/'),
  });
  console.log(`sealed ${name}: ${hash(canonical(files))}`);
}

function bundle(name) {
  const path = inside(root, name), build = json(resolve(path, 'build.json'));
  requireThat(build.format === 'mapping-replay-build.v1' && /^[0-9a-f]{40}$/.test(build.commitHint)
    && build.sourceSnapshotHash === hash(canonical(build.sourceFiles)) && build.buildHash === hash(canonical(build.files)), 'invalid build identity');
  const actual = inventory(path, new Set(['build.json']));
  requireThat(canonical(actual) === canonical(build.files), `sealed build bytes differ: ${name}`);
  for (const [language, prefix] of [['ts', 'ts/'], ['csharp', 'csharp/']]) {
    const identity = hash(canonical(Object.fromEntries(Object.entries(actual).filter(([p]) => p.startsWith(prefix)))));
    requireThat(build[`${language}Build`] === identity, `invalid ${language} build identity`);
  }
  requireThat(build.files['ts/schema/v1/record.schema.json'] === hash(readFileSync(inside(root, 'schema/v1/record.schema.json'))), 'schema contract changed');
  requireThat(build.files['ts/extractors/ts/package-lock.json'] === hash(readFileSync(inside(root, 'extractors/ts/package-lock.json'))), 'extractor dependency lock changed');
  return build;
}

function inputs(manifestName) {
  const manifest = json(inside(root, manifestName));
  requireThat(manifest.format === 'mapping-replay-input.v1' && manifest.repositoryId === 'mapping-replay'
    && manifest.inputKind === 'synthetic' && /^[0-9a-f]{40}$/.test(manifest.commitSha), 'invalid synthetic input identity');
  verifyLockedFiles(inside(root, input), manifest.files);
  requireThat(manifest.inputHash === hash(canonical(manifest.files)) && manifest.commitSha === manifest.inputHash.slice(0, 40), 'synthetic commit must identify locked source bytes');
  requireThat(Array.isArray(manifest.assertions) && manifest.assertions.length > 0, 'nonzero named assertions required');
  requireThat(manifest.assertions.some((a) => a.baseline !== a.candidate)
    && Array.isArray(manifest.expectedChanges?.removed) && Array.isArray(manifest.expectedChanges?.added)
    && manifest.expectedChanges.removed.length + manifest.expectedChanges.added.length > 0, 'nonzero locked improvement/delta expectations required');
  return manifest;
}

async function artifacts(directory, manifest) {
  const { validateArtifact } = await import(pathToFileURL(inside(root, 'extractors/ts/src/contracts/validateArtifact.ts')));
  const all = [], identities = [], hashes = {};
  let evidence = 0;
  for (const language of ['ts', 'csharp']) {
    const dir = resolve(directory, language), files = readdirSync(dir).sort();
    requireThat(files.length > 0 && files.every((f) => f.endsWith('.jsonl')), `missing/invalid ${language} artifacts`);
    const units = new Set();
    for (const file of files) {
      const bytes = readFileSync(resolve(dir, file)), text = bytes.toString('utf8');
      const check = validateArtifact(text);
      requireThat(check.ok, `schema/structure failure: ${file}: ${canonical(check.problems)}`);
      const records = text.trimEnd().split('\n').map(JSON.parse), header = records[0], complete = records.at(-1);
      requireThat(!units.has(header.unit.path), `duplicate output unit: ${header.unit.path}`); units.add(header.unit.path);
      if (complete.coverage.unresolvedEdges !== undefined) requireThat(complete.coverage.unresolvedEdges === records.filter((r) => r.kind === 'edge' && r.resolution === 'unresolved').length, 'incorrect unresolved edge count');
      evidence += verifyEvidence(records, inside(root, input), manifest);
      identities.push({ language, unit: header.unit.path, configHash: header.configHash, inputHash: header.inputHash });
      all.push(...records); hashes[`${language}/${file}`] = hash(bytes);
    }
  }
  return { all, identities, hashes, evidence };
}

async function verify(baselineName, candidateName, manifestName, outputName) {
  const manifest = inputs(manifestName), baseline = bundle(baselineName), candidate = bundle(candidateName), out = safeOutput(outputName);
  requireThat(candidate.sourceSnapshotHash === hash(canonical(sourceInventory())), 'candidate source no longer matches checkout');
  const before = await artifacts(resolve(out, 'baseline/1'), manifest), beforeAgain = await artifacts(resolve(out, 'baseline/2'), manifest);
  const after = await artifacts(resolve(out, 'candidate/1'), manifest), afterAgain = await artifacts(resolve(out, 'candidate/2'), manifest);
  equalRuns(before.hashes, beforeAgain.hashes); equalRuns(after.hashes, afterAgain.hashes);
  requireThat(before.identities.length === after.identities.length, 'unit set changed unexpectedly');
  for (let i = 0; i < before.identities.length; i++) {
    const a = before.identities[i], b = after.identities[i];
    requireThat(a.language === b.language && a.unit === b.unit && a.inputHash === b.inputHash, 'input/unit identity changed');
    if (baseline[`${a.language}Build`] !== candidate[`${a.language}Build`]) requireThat(a.configHash !== b.configHash, 'changed extractor build collides with baseline configHash');
  }
  const delta = checkAssertions(before.all, after.all, manifest.assertions, manifest.expectedChanges);
  const report = {
    format: 'mapping-replay-report.v1', inputKind: 'synthetic', inputHash: manifest.inputHash,
    manifestHash: hash(readFileSync(inside(root, manifestName))),
    baseline: { commitHint: baseline.commitHint, sourceSnapshotHash: baseline.sourceSnapshotHash, buildHash: baseline.buildHash, artifacts: before.hashes, identities: before.identities },
    candidate: { commitHint: candidate.commitHint, sourceSnapshotHash: candidate.sourceSnapshotHash, buildHash: candidate.buildHash, artifacts: after.hashes, identities: after.identities },
    repeatBytesEqual: true, schemaValidated: true, evidenceVerified: { baseline: before.evidence, candidate: after.evidence }, ...delta,
    limitations: ["Commit hints are caller labels; sourceSnapshotHash and buildHash are authoritative.", "Installed dependencies are sealed by file hash; registry tarball integrity is not independently recomputed.", 'Synthetic source only; no private repository fidelity claim.', 'C# framework/package asset selection is covered by dedicated extractor tests, not this HTTP replay.'],
  };
  save(resolve(out, 'report.json'), report);
  console.log(`replay passed: ${delta.assertions.length} named assertions, ${delta.changedRecords} explained changed records, ${before.evidence + after.evidence} exact evidence spans`);
}

try {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'prepare' && args.length === 2) prepare(...args);
  else if (command === 'preflight' && args.length === 4) {
    const [baseline, candidate, manifestName, output] = args;
    const manifest = inputs(manifestName); bundle(baseline); bundle(candidate);
    const out = safeOutput(output);
    requireThat(!existsSync(out), 'output already exists; choose a fresh replay directory');
    mkdirSync(out, { recursive: true }); writeFileSync(resolve(out, 'commit.txt'), manifest.commitSha + '\n');
  } else if (command === 'verify' && args.length === 4) await verify(...args);
  else throw new Error('usage: mapping-replay.mjs prepare|preflight|verify (use scripts/mapping-replay)');
} catch (error) { console.error(`mapping replay failed: ${error.message}`); process.exitCode = 1; }
