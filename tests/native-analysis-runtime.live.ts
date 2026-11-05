import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { chmodSync, cpSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, readlinkSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

type ObjectValue = Record<string, unknown>;
type CommandResult = { code: number | null; stdout: string; stderr: string };
type Artifact = { path: string; repositoryId: string; commitSha: string; sha256: string; bytes: number };
type RunEvidence = { runId: string; attemptId: string; capabilityId: string; manifestHash: string; acceptedInputsHash: string; artifacts: Artifact[] };

const root = resolve(fileURLToPath(new URL('../', import.meta.url)));
const cliTree = 'platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0';
const csharpTree = 'platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0';
const copiedInputs = ['scripts', 'config/runtime/images.env', 'schema', 'extractors/ts/src',
  'extractors/ts/package.json', 'extractors/ts/package-lock.json', 'extractors/ts/node_modules', cliTree, csharpTree];
const fixtureIds = ['api-cs', 'web-ts'];
const capabilityIds = ['native-runtime-single', 'native-runtime-batch'];
const digest = (bytes: string | Buffer): string => createHash('sha256').update(bytes).digest('hex');

function isObject(value: unknown): value is ObjectValue {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function object(value: unknown): ObjectValue {
  assert.ok(isObject(value), 'expected JSON object');
  return value;
}
function text(value: unknown): string {
  assert.ok(typeof value === 'string' && value.length > 0, 'expected nonempty string');
  return value;
}
function array(value: unknown): unknown[] {
  assert.ok(Array.isArray(value), 'expected JSON array');
  const items: unknown[] = value;
  return items;
}
function hash(value: unknown): string {
  const result = text(value);
  assert.match(result, /^[a-f0-9]{64}$/);
  return result;
}
function identity(value: unknown): string {
  const result = text(value);
  assert.match(result, /^[a-f0-9]{32}$/);
  return result;
}
function rejectLinks(path: string): void {
  for (let current = resolve(path); ; current = dirname(current)) {
    if (existsSync(current)) assert.ok(!lstatSync(current).isSymbolicLink(), 'symbolic-link ancestor rejected');
    else {
      try { lstatSync(current); assert.fail('dangling symbolic link rejected'); }
      catch (error: unknown) { assert.ok(object(error).code === 'ENOENT', 'unsafe path'); }
    }
    if (dirname(current) === current) break;
  }
}
function readJson(path: string): ObjectValue {
  rejectLinks(path);
  assert.ok(lstatSync(path).size <= 16 * 1024 * 1024, 'metadata exceeds limit');
  const value: unknown = JSON.parse(readFileSync(path, 'utf8'));
  return object(value);
}
function writeJson(path: string, value: unknown): void {
  rejectLinks(path);
  writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { mode: 0o600, flag: 'wx' });
}
function within(parent: string, path: string): string {
  const child = resolve(parent, path), rel = relative(parent, child);
  assert.ok(rel !== '' && rel !== '..' && !rel.startsWith('..' + sep) && !isAbsolute(rel), 'path escapes owned root');
  rejectLinks(child);
  return child;
}
function inventory(path: string, preserveFrom?: string): { sha256: string; files: number } {
  const entries: { path: string; mode: number; sha256?: string; link?: string }[] = [];
  function visit(current: string, source: string | undefined): void {
    const info = lstatSync(current);
    if (source !== undefined && !info.isSymbolicLink()) chmodSync(current, lstatSync(source).mode & 0o777);
    const mode = lstatSync(current).mode & 0o777;
    const name = relative(path, current).split(sep).join('/') || '.';
    if (info.isSymbolicLink()) {
      const link = readlinkSync(current);
      const target = resolve(dirname(current), link), rel = relative(path, target);
      assert.ok(!isAbsolute(link) && rel !== '..' && !rel.startsWith('..' + sep), 'input link escapes copied tree');
      entries.push({ path: name, mode, link });
    } else if (info.isDirectory()) {
      entries.push({ path: name, mode });
      for (const child of readdirSync(current).sort()) visit(join(current, child), source === undefined ? undefined : join(source, child));
    } else {
      assert.ok(info.isFile() && info.size <= 256 * 1024 * 1024, 'unsupported or oversized input');
      entries.push({ path: name, mode, sha256: digest(readFileSync(current)) });
    }
    assert.ok(entries.length <= 200000, 'input inventory exceeds limit');
  }
  visit(path, preserveFrom);
  return { sha256: digest(JSON.stringify(entries)), files: entries.filter(entry => entry.sha256 !== undefined).length };
}

async function qualify(): Promise<void> {
  assert.deepEqual(process.argv.slice(2), ['--live'], 'opt in explicitly: node --experimental-strip-types tests/native-analysis-runtime.live.ts --live');
  assert.ok(process.platform === 'darwin' || process.platform === 'linux', 'Unix host required');
  const requested = process.env.CE_CONTAINER_ENGINE ?? 'auto';
  assert.ok(['docker', 'podman', 'auto'].includes(requested), 'CE_CONTAINER_ENGINE must be docker, podman or auto');
  assert.equal(process.env.SANDBOX_DOTNET_IMAGE, undefined, 'image overrides need separate qualification');
  assert.equal(process.env.SANDBOX_NODE_IMAGE, undefined, 'image overrides need separate qualification');
  for (const repo of fixtureIds) {
    const git = join(root, 'fixtures/.repos', repo, '.git');
    rejectLinks(git);
    assert.ok(lstatSync(git).isDirectory(), `missing public fixture .git directory: ${repo}; prepare fixtures first`);
  }
  for (const path of [...copiedInputs, `${cliTree}/ce.dll`, `${csharpTree}/ce-cs.dll`,
    'extractors/ts/src/cli.ts', 'extractors/ts/node_modules/typescript/package.json',
    'scripts/lib/analysis-runtime.ts', 'scripts/fetch-npm-packages.mjs']) {
    rejectLinks(join(root, path));
    assert.ok(existsSync(join(root, path)), `missing prerequisite: ${path}; build/install separately`);
  }

  const probe = join(root, '.sandbox-cache/runtime-analysis-probes', randomUUID());
  rejectLinks(probe);
  mkdirSync(probe, { recursive: true, mode: 0o700 });
  const workspace = join(probe, 'workspace');
  mkdirSync(workspace, { mode: 0o700 });
  const feeds = join(probe, 'empty-host-feeds.json');
  writeJson(feeds, { feeds: [] });
  const env: NodeJS.ProcessEnv = { CE_CONTAINER_ENGINE: requested, GITHUB_AUTH: 'none', PACKAGES: '0',
    ANALYZE_JOBS: '1', ANALYZE_WORKERS: '2', INCLUDE_TESTS: '0', LOCKED: '0', CE_PACKAGE_FEEDS_FILE: feeds,
    GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: '/dev/null', GIT_TERMINAL_PROMPT: '0', GIT_OPTIONAL_LOCKS: '0',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1' };
  for (const key of ['PATH', 'HOME', 'TMPDIR', 'DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_CONFIG', 'CE_PODMAN_CONNECTION', 'SANDBOX_EXTRA_CA_FILE']) {
    if (process.env[key] !== undefined) env[key] = process.env[key];
  }
  assert.ok(env.HOME, 'HOME is required for Podman machine discovery');
  const commandEvidence: { name: string; code: number | null; elapsedMs: number; bounded: boolean }[] = [];
  let commandNumber = 0;
  async function command(name: string, executable: string, args: string[], timeoutMs = 60000, allowFailure = false, log = true): Promise<CommandResult> {
    const index = ++commandNumber, start = Date.now();
    return await new Promise<CommandResult>((accept, reject) => {
      const child = spawn(executable, args, { cwd: workspace, env, detached: true, stdio: ['ignore', 'pipe', 'pipe'] });
      let stdout = '', stderr = '', bytes = 0, bounded = false, settled = false;
      let force: ReturnType<typeof setTimeout> | undefined;
      let deadline: ReturnType<typeof setTimeout> | undefined;
      function signal(kind: NodeJS.Signals): void {
        if (child.pid !== undefined) {
          try { process.kill(-child.pid, kind); } catch {}
        }
      }
      function finish(code: number | null): void {
        if (settled) return;
        settled = true;
        if (bounded) signal('SIGKILL');
        clearTimeout(timer); clearTimeout(force); clearTimeout(deadline);
        child.stdout.destroy(); child.stderr.destroy(); child.unref();
        commandEvidence.push({ name, code, elapsedMs: Date.now() - start, bounded });
        if (log) writeJson(join(probe, `command-${index}.json`), { name, code, bounded, stdout, stderr });
        if (bounded || (code !== 0 && !allowFailure)) reject(new Error(`${name} ${bounded ? 'exceeded time/output bound' : `failed (exit ${code})`}; inspect isolated command evidence`));
        else accept({ code, stdout, stderr });
      }
      function abort(): void {
        if (bounded) return;
        bounded = true; signal('SIGTERM');
        force = setTimeout(() => signal('SIGKILL'), 2000);
        deadline = setTimeout(() => finish(null), 5000);
      }
      const timer = setTimeout(abort, timeoutMs);
      function capture(chunk: Buffer, error: boolean): void {
        const remaining = Math.max(0, 1024 * 1024 - bytes);
        bytes += chunk.length;
        if (error) stderr += chunk.subarray(0, remaining).toString('utf8');
        else stdout += chunk.subarray(0, remaining).toString('utf8');
        if (bytes > 1024 * 1024) abort();
      }
      child.stdout.on('data', (chunk: Buffer) => capture(chunk, false));
      child.stderr.on('data', (chunk: Buffer) => capture(chunk, true));
      child.once('error', () => finish(null));
      child.once('close', finish);
    });
  }

  const sourceHashes: Record<string, { sha256: string; files: number }> = {};
  const fixtureRevisions: Record<string, string> = {};
  let engine: 'docker' | 'podman' | undefined;
  let runtime: ObjectValue = {};
  const phases: { scenario: string; runs: RunEvidence[]; artifactCount: number }[] = [];
  const cleanup: { label: string; remaining?: number; verified: boolean }[] = [];
  let failure: string | undefined;
  const nodeVersion = process.version;
  let dotnetVersion = '';
  const journal = join(workspace, '.sandbox-cache/requests');
  const records = join(journal, 'runs');
  function runs(): ObjectValue[] {
    if (!existsSync(records)) return [];
    rejectLinks(records);
    return readdirSync(records).filter(name => /^[a-f0-9]{32}$/.test(name))
      .map(name => {
        const value = readJson(join(records, name, 'run.json'));
        assert.equal(identity(value.runId), name);
        return value;
      });
  }
  async function verifyRun(run: ObjectValue): Promise<RunEvidence> {
    const runId = identity(run.runId), attemptId = identity(run.attemptId);
    assert.equal(run.state, 'done');
    assert.equal(run.executionCompleted, true);
    assert.equal(run.cancellationRequested, false);
    assert.equal(run.bindingsPath, null);
    const settings = object(run.settings);
    assert.equal(settings.packages, false); assert.equal(settings.gitHubAuth, 'none');
    assert.equal(settings.extractionJobs, 1);
    assert.equal(settings.capabilityWorkers, 1);
    const manifestHash = hash(run.manifestHash), acceptedInputsHash = hash(run.acceptedInputsHash);
    const resolvedPath = join(records, runId, 'resolved.json');
    rejectLinks(resolvedPath);
    assert.equal(digest(readFileSync(resolvedPath)), manifestHash);
    const manifest = readJson(resolvedPath), inputs = object(manifest.inputs);
    assert.equal(manifest.acceptedInputsHash, acceptedInputsHash);
    assert.ok(!Object.keys(inputs).some(key => /^(package-cache|lock:|package-feed|nuget-coverage:|npm-coverage)/.test(key)), 'PACKAGES=0 must not prepare packages');
    const effects = array(run.effects).map(object);
    assert.equal(effects.length, fixtureIds.length);
    const artifacts: Artifact[] = [];
    for (const repo of fixtureIds) {
      const revision = text(inputs[`revision:${repo}`]);
      assert.match(revision, /^[a-f0-9]{40}$/);
      assert.equal(revision, fixtureRevisions[repo], 'analysis must use the exact public fixture revision');
      const directory = within(join(workspace, '.sandbox-cache/artifacts'), join(repo, revision));
      const effect = effects.find(item => item.destination === directory);
      assert.ok(effect, 'missing local publication effect');
      assert.equal(effect.surface, 'local-artifacts'); assert.equal(effect.state, 'confirmed'); hash(effect.evidenceHash);
      const selected = Object.entries(inputs).filter(([key]) => key.startsWith(`artifact:${repo}:`));
      assert.ok(selected.length > 0, 'each fixture must have canonical artifacts');
      for (const [key, expected] of selected) {
        const suffix = key.slice(`artifact:${repo}:`.length);
        assert.ok(suffix.endsWith('.jsonl'));
        const path = within(directory, suffix), bytes = readFileSync(path);
        assert.equal(digest(bytes), hash(expected));
        await command(`ce validate ${repo}/${suffix}`, 'dotnet', [join(workspace, cliTree, 'ce.dll'), 'validate', path]);
        artifacts.push({ path: relative(join(workspace, '.sandbox-cache/artifacts'), path).split(sep).join('/'),
          repositoryId: repo, commitSha: revision, sha256: digest(bytes), bytes: bytes.length });
      }
      const expectedTree = selected.map(([key]) => key.slice(`artifact:${repo}:`.length)).sort();
      const actualTree: string[] = [];
      function collect(current: string): void {
        rejectLinks(current);
        for (const name of readdirSync(current).sort()) {
          const entry = join(current, name); rejectLinks(entry);
          if (lstatSync(entry).isDirectory()) collect(entry);
          else actualTree.push(relative(directory, entry));
        }
      }
      collect(directory);
      assert.deepEqual(actualTree.sort(), expectedTree, 'canonical artifact set must exactly match the manifest');
    }
    return { runId, attemptId, capabilityId: text(run.capabilityId), manifestHash, acceptedInputsHash, artifacts };
  }
  try {
    for (const input of copiedInputs) {
      const source = join(root, input), destination = join(workspace, input);
      mkdirSync(dirname(destination), { recursive: true });
      sourceHashes[input] = inventory(source);
      cpSync(source, destination, { recursive: true, preserveTimestamps: true, verbatimSymlinks: true, errorOnExist: true, force: false });
      assert.deepEqual(inventory(destination, source), sourceHashes[input], `copy or mode mismatch: ${input}`);
    }
    sourceHashes['tests/native-analysis-runtime.live.ts'] = inventory(join(root, 'tests/native-analysis-runtime.live.ts'));
    const repositories: { id: string; location: string; kind: 'local'; ref: string }[] = [];
    for (const id of fixtureIds) {
      const location = join(root, 'fixtures/.repos', id);
      const revision = (await command(`fixture revision ${id}`, 'git', ['-c', 'core.fsmonitor=false', '-c', 'core.hooksPath=/dev/null', '-C', location,
        'rev-parse', '--verify', 'v1-base^{commit}'])).stdout.trim();
      assert.match(revision, /^[a-f0-9]{40}$/);
      fixtureRevisions[id] = revision;
      repositories.push({ id, location, kind: 'local', ref: 'v1-base' });
      sourceHashes[`fixture:${id}:v1-base`] = { sha256: digest(revision), files: 0 };
    }
    writeJson(join(workspace, 'capabilities.json'), { capabilities: capabilityIds.map(id => ({ id, name: id, repositories })) });
    dotnetVersion = (await command('dotnet version', 'dotnet', ['--version'])).stdout.trim();
    assert.match(dotnetVersion, /^10\./, 'host .NET 10 SDK/runtime required');
    const captured = (await command('capture analysis runtime', join(workspace, 'scripts/container-engine'), ['--analysis-snapshot'], 60000, false, false)).stdout.trim();
    const parsed: unknown = JSON.parse(captured), snapshot = object(parsed);
    assert.ok(snapshot.backend === 'docker' || snapshot.backend === 'podman');
    engine = snapshot.backend;
    if (requested !== 'auto') assert.equal(engine, requested);
    env.CE_CONTAINER_ENGINE = engine; env.CE_ANALYSIS_RUNTIME = captured;
    const initialFingerprint = hash(snapshot.fingerprint);
    const doctorResult = await command('engine identity', join(workspace, 'scripts/container-engine-doctor'), [], 60000, false, false);
    const doctorValue: unknown = JSON.parse(doctorResult.stdout), doctor = object(doctorValue);
    assert.equal(doctor.backend, engine); assert.equal(hash(doctor.contextSha256), initialFingerprint);
    runtime = { backend: engine, fingerprint: initialFingerprint, clientVersion: text(doctor.clientVersion),
      serverVersion: text(doctor.serverVersion) };
    const imageText = readFileSync(join(workspace, 'config/runtime/images.env'), 'utf8');
    const images: { image: string; cached: boolean }[] = [];
    for (const name of ['DOTNET_IMAGE', 'NODE_IMAGE']) {
      const line = imageText.split('\n').find(item => item.startsWith(name + '='));
      assert.ok(line, 'missing pinned image');
      let image = line.slice(name.length + 1);
      assert.match(image, /^[A-Za-z0-9./:_-]+@sha256:[a-f0-9]{64}$/);
      if (engine === 'podman' && !image.split('/')[0]?.includes('.')) image = `docker.io/library/${image}`;
      const inspected = await command(`inspect pinned ${name}`, join(workspace, 'scripts/container-engine'), ['image', 'inspect', image], 60000, true, false);
      images.push({ image, cached: inspected.code === 0 });
      if (inspected.code !== 0) console.warn(`${name} is not cached; the normal sandbox may pull its pinned digest (network required).`);
    }
    runtime.images = images;

    await command('single analysis', join(workspace, 'scripts/analyze'), [capabilityIds[0] ?? '', 'capabilities.json'], 15 * 60000);
    const singleRuns = runs();
    assert.equal(singleRuns.length, 1);
    const single = await verifyRun(object(singleRuns[0]));
    assert.equal(single.capabilityId, capabilityIds[0]);
    phases.push({ scenario: 'Given isolated public fixtures, when analyze runs, then execution and publication complete with valid canonical artifacts',
      runs: [single], artifactCount: single.artifacts.length });

    await command('aggregate analysis', join(workspace, 'scripts/analyze-all'), ['capabilities.json'], 20 * 60000);
    const allRuns = runs();
    assert.equal(allRuns.length, 3);
    const batch = [];
    for (const run of allRuns.filter(item => item.runId !== single.runId)) batch.push(await verifyRun(run));
    assert.equal(batch.length, 2);
    assert.deepEqual(batch.map(run => run.capabilityId).sort(), [...capabilityIds].sort());
    const receipt = readJson(join(workspace, '.sandbox-cache/analysis-receipt.json'));
    assert.equal(receipt.format, 'ce.analysis-replay.v1');
    assert.equal(receipt.bindingsHash, null); assert.equal(receipt.localCapabilitiesHash, null);
    assert.equal(receipt.capabilitiesHash, digest(readFileSync(join(workspace, 'capabilities.json'))));
    assert.equal(receipt.runtimeImagesHash, digest(readFileSync(join(workspace, 'config/runtime/images.env'))));
    const recordedRuns = array(receipt.runs).map(object);
    assert.deepEqual(recordedRuns.map(run => identity(run.runId)).sort(), batch.map(run => run.runId).sort());
    for (const run of batch) {
      const recorded = recordedRuns.find(item => item.runId === run.runId);
      assert.ok(recorded);
      assert.equal(recorded.attemptId, run.attemptId); assert.equal(recorded.manifestHash, run.manifestHash);
      assert.equal(recorded.acceptedInputsHash, run.acceptedInputsHash);
    }
    const expectedArtifacts = new Map(batch.flatMap(run => run.artifacts).map(artifact => [artifact.path, artifact]));
    const receiptArtifacts = array(receipt.artifacts).map(object);
    assert.equal(receiptArtifacts.length, expectedArtifacts.size);
    assert.equal(new Set(receiptArtifacts.map(item => text(item.path))).size, expectedArtifacts.size);
    for (const item of receiptArtifacts) {
      const expected = expectedArtifacts.get(text(item.path)); assert.ok(expected);
      assert.equal(item.sha256, expected.sha256); assert.equal(item.repositoryId, expected.repositoryId);
      assert.equal(item.commitSha, expected.commitSha);
    }
    phases.push({ scenario: 'Given a completed single run, when analyze-all runs, then new completed runs and an aggregate receipt bind the validated artifacts',
      runs: batch, artifactCount: expectedArtifacts.size });
    const finalSnapshot: unknown = JSON.parse((await command('verify frozen runtime', join(workspace, 'scripts/container-engine'), ['--analysis-snapshot'], 60000, false, false)).stdout);
    assert.equal(object(finalSnapshot).fingerprint, initialFingerprint);
    for (const input of copiedInputs) assert.deepEqual(inventory(join(root, input)), sourceHashes[input], `source changed during probe: ${input}`);
  } catch (error: unknown) {
    failure = error instanceof Error ? error.message : 'qualification failed';
  } finally {
    if (env.CE_ANALYSIS_RUNTIME !== undefined) {
      try {
        for (const run of runs()) {
          if (run.attemptId === null || run.attemptId === undefined) continue;
          const label = `ce-analyze=${digest(workspace).slice(0, 16)}-${identity(run.runId)}-${identity(run.attemptId)}`;
          try {
            const listed = await command('list exact owned containers', join(workspace, 'scripts/container-engine'), ['ps', '-q', '--filter', `label=${label}`]);
            const ids = listed.stdout.trim().split(/\s+/).filter(Boolean);
            assert.ok(ids.every(id => /^[a-f0-9]{12,64}$/.test(id)), 'invalid container IDs');
            if (ids.length > 0) await command('kill exact owned containers', join(workspace, 'scripts/container-engine'), ['kill', ...ids]);
            const remaining = await command('verify exact owned teardown', join(workspace, 'scripts/container-engine'), ['ps', '-q', '--filter', `label=${label}`]);
            const count = remaining.stdout.trim().split(/\s+/).filter(Boolean).length;
            cleanup.push({ label, remaining: count, verified: count === 0 });
            assert.equal(count, 0, 'owned containers remain; preserve evidence and inspect manually');
            if (ids.length > 0) failure ??= 'owned containers survived the analysis command; teardown was required';
          } catch { cleanup.push({ label, verified: false }); failure ??= 'exact owned teardown could not be verified; evidence retained'; }
        }
      } catch { failure ??= 'isolated journal unreadable; no speculative teardown attempted'; }
    }
    const summary = { schemaVersion: 1, passed: failure === undefined, scope: 'opt-in native public-fixture analysis; no packages, database or models',
      engine: engine ?? requested, versions: { node: nodeVersion, dotnet: dotnetVersion }, runtime,
      workspace: relative(root, workspace), retained: true, sourceHashes, fixtureRevisions, phases, commands: commandEvidence, cleanup,
      failure, limits: ['No package feed, package-fetch, database, model, nested-container or full-CI qualification',
        'Empty owned container labels do not resolve uncertain journal evidence; no recovery or reconciliation performed'] };
    writeJson(join(probe, 'qualification.json'), summary);
    if (engine !== undefined) {
      const output = join(root, `.sandbox-cache/runtime-analysis-${engine}.json`);
      rejectLinks(output);
      const temporary = join(probe, 'summary.json'); writeJson(temporary, summary); renameSync(temporary, output);
    }
    console.log(JSON.stringify({ passed: failure === undefined, engine: engine ?? requested, evidence: relative(root, probe) }));
  }
  if (failure !== undefined) throw new Error(failure);
}

qualify().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : 'native analysis qualification failed');
  process.exitCode = 1;
});