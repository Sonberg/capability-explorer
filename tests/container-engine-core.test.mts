import { test, type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync, type SpawnSyncReturns } from 'node:child_process';

type EngineCall = { engine: string; args: string[]; selectors: Record<string, string | null> };
type CommandResult = SpawnSyncReturns<string>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item: unknown) => typeof item === 'string');
}
function isSelectors(value: unknown): value is Record<string, string | null> {
  return isRecord(value) && Object.values(value).every(item => item === null || typeof item === 'string');
}
function engineCall(line: string): EngineCall {
  const value: unknown = JSON.parse(line);
  assert.ok(isRecord(value), `invalid engine log: ${line}`);
  assert.equal(typeof value.engine, 'string', line);
  assert.ok(typeof value.engine === 'string' && isStringArray(value.args) && isSelectors(value.selectors), line);
  return { engine: value.engine, args: value.args, selectors: value.selectors };
}

// Test-only executables, never production aliases or runtime qualification evidence.
const source = process.env.CE_RUNTIME_TEST_SOURCE_ROOT ?? fileURLToPath(new URL('../', import.meta.url));
const selectors = ['CE_CONTAINER_ENGINE', 'CE_PODMAN_CONNECTION', 'DOCKER_HOST', 'DOCKER_CONTEXT',
  'CONTAINER_HOST', 'CONTAINER_CONNECTION', 'CONTAINER_SSHKEY', 'SANDBOX_SOURCES', 'SANDBOX_PASS_ENV',
  'SANDBOX_NETWORK', 'SANDBOX_EXTRA_CA_FILE', 'SANDBOX_NODE_IMAGE', 'SANDBOX_DOTNET_IMAGE', 'CE_ANALYSIS_RUNTIME'];
const mock = `#!${process.execPath}
const fs=require('node:fs');
const path=require('node:path');
const engine=path.basename(process.argv[1]), full=process.argv.slice(2), args=[...full];
fs.appendFileSync(process.env.TEST_ENGINE_LOG,JSON.stringify({engine,args:full,selectors:Object.fromEntries(
 ['DOCKER_HOST','DOCKER_CONTEXT','CONTAINER_HOST','CONTAINER_CONNECTION','CONTAINER_SSHKEY'].map(k=>[k,process.env[k]??null]))})+String.fromCharCode(10));
while(['--host','--connection','--url','--identity'].includes(args[0]))args.splice(0,2);
if(args[0]==='--remote=false')args.shift();
const op=args[0];
if(process.env.MOCK_HANGING_METADATA===engine&&op==='info'){
 fs.writeFileSync(process.env.TEST_ENGINE_LOG+'.pid',String(process.pid));
 fs.writeSync(1,Buffer.alloc(70000,81));setInterval(()=>{},1000);
}
if(process.env.MOCK_REPOINT_MACHINE==='1'&&op==='system'&&args[1]==='connection')
 fs.writeFileSync(process.env.TEST_ENGINE_LOG+'.repointed','changed named connection after discovery');
else if(process.env.MOCK_REPOINT_MACHINE==='1'&&full.includes('--connection'))process.exit(42);
if(process.env.MOCK_METADATA_FAIL===engine+':'+op){console.error('PRIVATE-METADATA-SENTINEL');process.exit(19);}
if(engine==='docker'){
 if(op==='context')console.log(args[1]==='show'?'fixture-context':(process.env.MOCK_DOCKER_ENDPOINT??'unix:///fixture/docker.sock'));
 else if(op==='version')console.log(process.env.MOCK_DOCKER_METADATA??'29.0.0|29.0.0|linux|arm64|Docker Engine');
 else if(op==='info')console.log(process.env.MOCK_DOCKER_ID??'fixture-daemon');
} else if(engine==='podman'){
 if(op==='machine'&&args[1]==='list')console.log(process.env.MOCK_PODMAN_MACHINES??'ce-validation|true');
 else if(op==='system'&&args[1]==='connection')console.log(process.env.MOCK_PODMAN_CONNECTION
   ??('ssh://core@127.0.0.1:34567/run/user/501/podman/podman.sock|true|'+process.env.TEST_PODMAN_IDENTITY));
 else if(op==='info')console.log(full.some(a=>a.includes('.Store.GraphRoot'))
   ?(process.env.MOCK_PODMAN_STORAGE??'/home/core/.local/share/containers/storage|fixture-machine')
   :(process.env.MOCK_PODMAN_METADATA??'true|arm64|6.1.4|linux'));
 else if(op==='version')console.log(process.env.MOCK_PODMAN_VERSION??'6.1.4|6.1.4');
}
if(op==='image'&&args[1]==='inspect')process.exit(Number(process.env.MOCK_IMAGE_MISSING??0));
if(op==='pull')process.exit(Number(process.env.MOCK_PULL_EXIT??0));
if(op==='run')process.exit(Number(process.env.MOCK_RUN_EXIT??0));
`;

function fixture(t: TestContext, engines: string[] = ['docker', 'podman']) {
  const dir = mkdtempSync(join(tmpdir(), 'ce-runtime-core-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const root = join(dir, 'checkout with spaces'), bin = join(dir, 'bin'), log = join(dir, 'engine.jsonl');
  mkdirSync(root); mkdirSync(bin);
  const identity = join(dir, 'synthetic machine identity');
  writeFileSync(identity, 'SYNTHETIC-KEY-MATERIAL-DO-NOT-PUBLISH', { mode: 0o600 });
  // No fallthrough to real engine CLIs installed on a developer or CI host.
  for (const utility of ['bash', 'sh', 'env', 'dirname', 'mkdir', 'cat', 'mktemp', 'rm', 'wc', 'sleep',
    'id', 'stat', 'shasum', 'sha256sum', 'sed', 'grep', 'sort', 'find', 'head', 'tail', 'tr', 'xargs', 'touch']) {
    const actual = ['/usr/bin', '/bin', '/usr/sbin'].map(dir => join(dir, utility)).find(existsSync);
    if (actual) symlinkSync(actual, join(bin, utility));
  }
  symlinkSync(process.execPath, join(bin, 'node'));
  cpSync(join(source, 'scripts'), join(root, 'scripts'), { recursive: true });
  assert.ok(existsSync(join(root, 'scripts/lib/analysis-runtime.ts')), 'copied scripts must include the runtime snapshot helper');
  cpSync(join(source, 'config'), join(root, 'config'), { recursive: true });
  cpSync(join(source, 'Makefile'), join(root, 'Makefile'));
  for (const engine of engines) writeFileSync(join(bin, engine), mock, { mode: 0o755 });
  writeFileSync(join(bin, 'uname'), '#!/bin/sh\nif [ "$1" = -m ]; then echo arm64; else echo "${MOCK_HOST_OS:-Linux}"; fi\n', { mode: 0o755 });
  // Prevent later-slice entrypoints from contacting real Git or restoring/building code.
  for (const cmd of ['gh', 'dotnet', 'curl']) writeFileSync(join(bin, cmd), '#!/bin/sh\nexit 91\n', { mode: 0o755 });
  writeFileSync(join(bin, 'git'), '#!/bin/sh\nif [ "$1" = rev-parse ] && [ "$2" = --show-toplevel ]; then printf "%s\\n" "$TEST_FIXTURE_ROOT"; else exit 91; fi\n', { mode: 0o755 });
  const env: NodeJS.ProcessEnv = { ...process.env, PATH: bin, TEST_ENGINE_LOG: log, TEST_FIXTURE_ROOT: root,
    TEST_PODMAN_IDENTITY: identity, MOCK_HOST_OS: 'Linux' };
  for (const key of [...selectors, 'MAKEFLAGS', 'MAKEOVERRIDES', 'MAKELEVEL', 'MFLAGS', ...Object.keys(env).filter(k => k.startsWith('MOCK_'))]) delete env[key];
  env.MOCK_HOST_OS = 'Linux';
  const calls = (): EngineCall[] => existsSync(log) ? readFileSync(log, 'utf8').trim().split('\n').filter(Boolean).map(engineCall) : [];
  function run(script: string, args: string[] = [], extra: NodeJS.ProcessEnv = {}, executable = '/bin/bash', timeout = 30000) {
    return spawnSync(executable, [join(root, script), ...args], { cwd: root, env: { ...env, ...extra }, encoding: 'utf8', timeout });
  }
  return { dir, root, bin, log, env, identity, run, calls };
}
const doctor = (result: CommandResult): Record<string, unknown> & { contextSha256: string } => {
  const value: unknown = JSON.parse(result.stdout);
  assert.ok(isRecord(value), resultError(result));
  for (const key of ['backend', 'qualification', 'clientVersion', 'serverVersion', 'connection', 'serverOs'])
    assert.equal(typeof value[key], 'string', `invalid doctor field ${key}: ${result.stdout}`);
  assert.ok(typeof value.contextSha256 === 'string', result.stdout);
  return { ...value, contextSha256: value.contextSha256 };
};
const operation = (call: EngineCall | undefined) => {
  assert.ok(call, 'expected an engine command in the fixture log');
  const args = [...call.args];
  while (['--host', '--connection', '--url', '--identity'].includes(args[0])) args.splice(0, 2);
  if (args[0] === '--remote=false') args.shift();
  return args;
};
const resultError = (result: CommandResult) => `status=${result.status} signal=${result.signal}\n${result.error?.message ?? ''}\n${result.stdout}\n${result.stderr}`;
function passed(result: CommandResult) { assert.equal(result.status, 0, resultError(result)); }
function refused(r: CommandResult, message: RegExp) {
  assert.ok(Number.isInteger(r.status) && r.status !== 0, resultError(r) || 'unexpected successful command');
  assert.match(resultError(r), message);
}

test('doctor defaults to Docker and records a probe-only backend/context identity', t => {
  const f = fixture(t), r = f.run('scripts/container-engine-doctor'); passed(r);
  const d = doctor(r);
  assert.equal(d.backend, 'docker'); assert.equal(d.qualification, 'probe-only');
  assert.equal(d.clientVersion, '29.0.0'); assert.equal(d.serverVersion, '29.0.0');
  assert.equal(d.connection, 'fixture-context'); assert.match(d.contextSha256, /^[a-f0-9]{64}$/);
  assert.ok(f.calls().every(c => c.engine === 'docker'));
});

test('auto selects Podman when Docker is not installed', t => {
  const f = fixture(t, ['podman']), r = f.run('scripts/container-engine-doctor'); passed(r);
  assert.equal(doctor(r).backend, 'podman');
  assert.ok(f.calls().every(call => call.engine === 'podman'));
});

test('Linux Podman works without a Docker executable and overrides ambient remote selectors', t => {
  const f = fixture(t, ['podman']);
  const r = f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: 'podman',
    CONTAINER_HOST: 'ssh://not-selected', CONTAINER_CONNECTION: 'not-selected', CONTAINER_SSHKEY: '/private/key',
    DOCKER_HOST: 'tcp://not-selected:2375', DOCKER_CONTEXT: 'not-selected' }); passed(r);
  const d = doctor(r); assert.equal(d.backend, 'podman'); assert.equal(d.rootless, true);
  assert.equal(d.connection, 'native-rootless'); assert.equal(d.serverOs, 'linux');
  for (const c of f.calls()) {
    assert.equal(c.engine, 'podman'); assert.ok(c.args.includes('--remote=false'));
    assert.ok(Object.values(c.selectors).every(value => value === null));
  }
});

test('container command wrapper pins Podman service operations to the validated context', t => {
  const f = fixture(t), r = f.run('scripts/container-engine', ['ps', '-aq', '--filter', 'name=ce-postgres'], {
    CE_CONTAINER_ENGINE: 'podman', CONTAINER_HOST: 'ssh://not-selected', CONTAINER_CONNECTION: 'not-selected',
    DOCKER_HOST: 'tcp://not-selected:2375', DOCKER_CONTEXT: 'not-selected' }); passed(r);
  const calls = f.calls();
  assert.equal(operation(calls.at(-1))[0], 'ps');
  assert.ok(calls.every(call => call.engine === 'podman'));
  assert.ok(calls.filter(call => operation(call)[0] !== 'system').every(call => call.args.includes('--remote=false')));
  assert.ok(calls.every(call => Object.values(call.selectors).every(value => value === null)));
});

test('macOS freezes endpoint and identity after discovering the explicitly selected machine', t => {
  const f = fixture(t), r = f.run('scripts/container-engine-doctor', [], {
    CE_CONTAINER_ENGINE: 'podman', CE_PODMAN_CONNECTION: 'ce-validation', MOCK_HOST_OS: 'Darwin', MOCK_REPOINT_MACHINE: '1',
    DOCKER_HOST: 'tcp://not-selected:2375', DOCKER_CONTEXT: 'not-selected', CONTAINER_HOST: 'ssh://not-selected',
    CONTAINER_CONNECTION: 'changed-default', CONTAINER_SSHKEY: '/not-selected/key' }); passed(r);
  assert.equal(doctor(r).connection, 'ce-validation');
  const calls = f.calls(); assert.ok(calls.every(c => c.engine === 'podman'));
  assert.equal(calls.filter(c => operation(c)[0] === 'system').length, 1);
  for (const c of calls.filter(c => operation(c)[0] !== 'system')) {
    assert.deepEqual(c.args.slice(0, 4), ['--url', 'ssh://core@127.0.0.1:34567/run/user/501/podman/podman.sock', '--identity', f.identity]);
    assert.ok(!c.args.includes('--connection'));
  }
  for (const c of calls) assert.ok(Object.values(c.selectors).every(value => value === null));
  assert.doesNotMatch(r.stdout, /SYNTHETIC-KEY-MATERIAL|synthetic machine identity/);
});

test('macOS auto-detects its sole running Podman machine', t => {
  const f = fixture(t), r = f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Darwin' });
  passed(r); assert.equal(doctor(r).connection, 'ce-validation');
  const calls = f.calls();
  assert.equal(calls.filter(call => operation(call)[0] === 'machine').length, 1);
  assert.ok(calls.filter(call => operation(call)[0] !== 'machine' && operation(call)[0] !== 'system')
    .every(call => call.args.slice(0, 4).includes('--url') && call.args.includes('--identity')));
});

test('macOS requires an explicit connection when multiple Podman machines are running', t => {
  const f = fixture(t), r = f.run('scripts/container-engine-doctor', [], {
    CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Darwin', MOCK_PODMAN_MACHINES: 'first|true\nsecond|true' });
  refused(r, /multiple Podman machines are running/);
  assert.equal(f.calls().filter(call => operation(call)[0] === 'system').length, 0);
});

for (const kind of ['missing', 'relative', 'directory', 'symlink', 'control', 'extra-field'] as const)
  test(`macOS refuses ${kind} SSH identity metadata before server commands`, t => {
    const f = fixture(t);
    const alias = join(f.dir, 'linked identity'); symlinkSync(f.identity, alias);
    const identity = { missing: join(f.dir, 'missing'), relative: 'relative-key', directory: f.root,
      symlink: alias, control: `${f.identity}\t`, 'extra-field': `${f.identity}|extra` }[kind];
    const r = f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: 'podman',
      CE_PODMAN_CONNECTION: 'ce', MOCK_HOST_OS: 'Darwin',
      MOCK_PODMAN_CONNECTION: `ssh://core@127.0.0.1:34567/run/user/501/podman/podman.sock|true|${identity}` });
    refused(r, /identity|connection metadata/i);
    assert.ok(f.calls().every(c => operation(c)[0] === 'system'));
  });

for (const [name, env, message, beforeCli] of [
  ['unknown engine', { CE_CONTAINER_ENGINE: 'containerd' }, /must be docker or podman/, true],
  ['empty explicit engine', { CE_CONTAINER_ENGINE: '' }, /must be docker or podman/, true],
  ['engine shell fragment', { CE_CONTAINER_ENGINE: 'podman; touch /tmp/never-execute' }, /must be docker or podman/, true],
  ['Linux remote request', { CE_CONTAINER_ENGINE: 'podman', CE_PODMAN_CONNECTION: 'remote' }, /native rootless/, true],
  ['missing mac machine', { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Darwin', MOCK_PODMAN_MACHINES: '' }, /no Podman machine is running/, false],
  ['unsafe mac connection name', { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Darwin', CE_PODMAN_CONNECTION: 'bad";exit' }, /invalid CE_PODMAN_CONNECTION/, true],
  ['external mac SSH host', { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Darwin', CE_PODMAN_CONNECTION: 'ce', MOCK_PODMAN_CONNECTION: 'ssh://core@external:34567/run/user/501/podman/podman.sock|true' }, /loopback rootless/, false],
  ['rootful mac socket', { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Darwin', CE_PODMAN_CONNECTION: 'ce', MOCK_PODMAN_CONNECTION: 'ssh://core@127.0.0.1:34567/run/podman/podman.sock|true' }, /loopback rootless/, false],
  ['nonmachine mac connection', { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Darwin', CE_PODMAN_CONNECTION: 'ce', MOCK_PODMAN_CONNECTION: 'ssh://core@127.0.0.1:34567/run/user/501/podman/podman.sock|false' }, /loopback rootless/, false],
  ['rootful Podman', { CE_CONTAINER_ENGINE: 'podman', MOCK_PODMAN_METADATA: 'false|arm64|6.1.4|linux' }, /rootful|non-Podman/, false],
  ['inconsistent Podman server', { CE_CONTAINER_ENGINE: 'podman', MOCK_PODMAN_VERSION: '6.1.4|6.0.0' }, /inconsistent Podman/, false],
  ['wrong server OS', { CE_CONTAINER_ENGINE: 'podman', MOCK_PODMAN_METADATA: 'true|arm64|6.1.4|darwin' }, /server platform/, false],
  ['invalid Docker version text', { MOCK_DOCKER_METADATA: '29"unsafe|29.0.0|linux|arm64|Docker Engine' }, /invalid runtime version/, false],
  ['Docker context pointing to Podman', { CE_CONTAINER_ENGINE: 'docker', MOCK_DOCKER_METADATA: '29.0.0|6.1.4|linux|arm64|Podman Engine' }, /did not identify a Docker/, false],
  ['remote Docker endpoint', { DOCKER_HOST: 'tcp://external:2375' }, /local Unix/, true],
  ['unsupported OS', { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: 'Windows_NT' }, /Linux or macOS/, true],
] satisfies [string, NodeJS.ProcessEnv, RegExp, boolean][]) test(`doctor rejects ${name}`, t => {
  const f = fixture(t), r = f.run('scripts/container-engine-doctor', [], env); refused(r, message);
  if (beforeCli) assert.equal(f.calls().length, 0);
  if (name === 'missing mac machine') assert.deepEqual(f.calls().map(call => operation(call)[0]), ['machine']);
});

test('missing Podman never invokes the available Docker CLI', t => {
  const f = fixture(t, ['docker']); refused(f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: 'podman' }), /selected podman executable is missing/);
  assert.equal(f.calls().length, 0);
});

test('failed metadata probe is bounded/generic and never leaks raw stderr or falls back', t => {
  const f = fixture(t), r = f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: 'podman', MOCK_METADATA_FAIL: 'podman:info' });
  refused(r, /metadata probe failed/); assert.doesNotMatch(resultError(r), /PRIVATE-METADATA-SENTINEL/);
  assert.ok(f.calls().every(c => c.engine === 'podman'));
});

test('oversized metadata is rejected without publishing its raw bytes', t => {
  const f = fixture(t), r = f.run('scripts/container-engine-doctor', [], { MOCK_DOCKER_METADATA: 'SENTINEL'.repeat(3000) });
  refused(r, /metadata (output too large|probe exceeded time\/output limit)/);
  assert.doesNotMatch(resultError(r), /SENTINELSENTINEL/);
});

for (const engine of ['docker', 'podman']) test(`${engine} bounded metadata capture terminates its actual hanging CLI child`, t => {
  const f = fixture(t);
  let pid: number | undefined;
  try {
    const r = f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: engine, MOCK_HANGING_METADATA: engine });
    refused(r, /metadata probe exceeded time\/output limit/);
    const childPid = Number(readFileSync(f.log + '.pid', 'utf8')); pid = childPid;
    assert.ok(Number.isInteger(childPid) && childPid > 1);
    assert.throws(() => process.kill(childPid, 0), { code: 'ESRCH' });
  } finally {
    if (pid) { try { process.kill(pid, 'SIGKILL'); } catch (error) { if (!isRecord(error) || error.code !== 'ESRCH') throw error; } }
  }
});

test('fingerprints change with Docker daemon and macOS connection endpoint identity', t => {
  const f = fixture(t);
  const a = f.run('scripts/container-engine-doctor'), b = f.run('scripts/container-engine-doctor', [], { MOCK_DOCKER_ID: 'another-daemon' });
  passed(a); passed(b); assert.notEqual(doctor(a).contextSha256, doctor(b).contextSha256);
  const env = { CE_CONTAINER_ENGINE: 'podman', CE_PODMAN_CONNECTION: 'ce', MOCK_HOST_OS: 'Darwin' };
  const c = f.run('scripts/container-engine-doctor', [], env), d = f.run('scripts/container-engine-doctor', [], {
    ...env, MOCK_PODMAN_CONNECTION: `ssh://core@127.0.0.1:45678/run/user/501/podman/podman.sock|true|${f.identity}` });
  passed(c); passed(d); assert.notEqual(doctor(c).contextSha256, doctor(d).contextSha256);
});

test('native Podman fingerprints distinguish local storage contexts without exposing their paths', t => {
  const f = fixture(t), a = f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: 'podman' });
  const b = f.run('scripts/container-engine-doctor', [], { CE_CONTAINER_ENGINE: 'podman', MOCK_PODMAN_STORAGE: '/other/private/storage|fixture-machine' });
  passed(a); passed(b); assert.notEqual(doctor(a).contextSha256, doctor(b).contextSha256);
  assert.doesNotMatch(b.stdout, /other\/private\/storage/);
});

test('Podman socket/Testcontainers mode refuses before any engine probe', t => {
  const f = fixture(t);
  refused(f.run('scripts/sandbox', ['--docker', 'node', 'true'], { CE_CONTAINER_ENGINE: 'podman' }), /not qualified|unsupported|not supported/i);
  assert.equal(f.calls().length, 0);
});

for (const engine of ['docker', 'podman']) test(`${engine} offline sandbox preserves hardening, literal arguments and read-only source mounts`, t => {
  const f = fixture(t), sealed = join(f.dir, 'sealed source'); mkdirSync(sealed);
  const r = f.run('scripts/sandbox', ['--offline', 'node', 'printf', 'argument with spaces; $(no-shell)'], {
    CE_CONTAINER_ENGINE: engine, SANDBOX_SOURCES: sealed, SANDBOX_PASS_ENV: 'TEST_PRIVATE_TOKEN', TEST_PRIVATE_TOKEN: 'not-forwarded' }); passed(r);
  const run = f.calls().find(c => operation(c)[0] === 'run'); assert.ok(run);
  const args = operation(run);
  for (const flag of ['--read-only', '--cap-drop', '--security-opt', '--pids-limit', '--user']) assert.ok(args.includes(flag), flag);
  assert.equal(args[args.indexOf('--network') + 1], 'none');
  assert.ok(args.includes(`${sealed}:${sealed}:ro`)); assert.ok(args.includes(`${f.root}:/work`));
  assert.ok(args.includes('argument with spaces; $(no-shell)'));
  assert.ok(!args.join('\n').includes('TEST_PRIVATE_TOKEN')); assert.ok(!args.join('\n').includes('not-forwarded'));
  assert.ok(!args.join('\n').includes('docker.sock')); assert.ok(!args.join('\n').includes('podman.sock'));
  assert.ok(f.calls().every(c => c.engine === engine));
  const expected = engine === 'podman' ? 'docker.io/library/node:' : 'node:';
  assert.ok(args.some(a => a.startsWith(expected) && a.includes('@sha256:')));
});

test('sandbox rejects incompatible offline published ports before run', t => {
  const f = fixture(t);
  refused(f.run('scripts/sandbox', ['--offline', '--port', '5999', 'node', 'true'], { CE_CONTAINER_ENGINE: 'podman' }), /cannot be combined/);
  assert.ok(!f.calls().some(c => operation(c)[0] === 'run'));
});

for (const os of ['Linux', 'Darwin']) test(`Podman ${os} maps the requested sandbox uid and gid explicitly`, t => {
  const f = fixture(t);
  passed(f.run('scripts/sandbox', ['--offline', 'node', 'true'], { CE_CONTAINER_ENGINE: 'podman', MOCK_HOST_OS: os }));
  const args = operation(f.calls().find(call => operation(call)[0] === 'run'));
  const user = args[args.indexOf('--user') + 1];
  assert.ok(typeof user === 'string');
  assert.match(user, /^[0-9]+:[0-9]+$/);
  const [uid, gid] = user.split(':');
  assert.equal(args[args.indexOf('--userns') + 1], `keep-id:uid=${uid},gid=${gid}`,
    'the Podman VM identity must map to the host uid/gid requested by the sandbox');
});

for (const engine of ['docker', 'podman']) test(`${engine} frozen extraction mounts source and runtime trees read-only`, t => {
  const f = fixture(t);
  const captured = f.run('scripts/container-engine', ['--analysis-snapshot'], { CE_CONTAINER_ENGINE: engine });
  passed(captured);
  const source = join(f.root, '.sandbox-cache/requests/runs/test/attempt/src/repo');
  mkdirSync(source, { recursive: true });
  const runtimeTrees = ['extractors/ts', 'platform/src/CapabilityExplorer.Extractors.CSharp/bin', 'platform/src/CapabilityExplorer.Cli/bin'];
  for (const tree of runtimeTrees) mkdirSync(join(f.root, tree), { recursive: true });
  const result = f.run('scripts/sandbox', ['--offline', 'node', 'true'], {
    CE_ANALYSIS_RUNTIME: captured.stdout.trim(), SANDBOX_SOURCES: source,
  });
  passed(result);
  const args = operation(f.calls().find(call => operation(call)[0] === 'run'));
  assert.ok(args.includes(`${source}:/work/.sandbox-cache/requests/runs/test/attempt/src/repo:ro`), 'the parser source must override the writable journal mount');
  for (const tree of runtimeTrees) assert.ok(args.includes(`${join(f.root, tree)}:/work/${tree}:ro`), tree);
});

test('Explorer sandbox mounts only the public feed directory and masks legacy secrets', t => {
  const f = fixture(t);
  passed(f.run('scripts/sandbox', ['--feed-catalog', 'dotnet', 'true'], { CE_CONTAINER_ENGINE: 'docker' }));
  const args = operation(f.calls().find(c => operation(c)[0] === 'run'));
  assert.ok(args.includes('/work/.sandbox-cache/package-feeds:rw,noexec,nosuid,size=1m'));
  assert.ok(args.includes(`${f.root}/.sandbox-cache/package-feed-catalog:/work/.sandbox-cache/package-feed-catalog:ro`));
  assert.ok(!args.some(a => a.endsWith('/package-feeds:/work/.sandbox-cache/package-feeds')));
  const refusedLegacy = f.run('scripts/sandbox', ['--package-settings', 'dotnet', 'true']);
  refused(refusedLegacy, /migrate credentials on the host/);
});

test('Explorer public feed mount rejects a symbolic-link directory', t => {
  const f = fixture(t);
  mkdirSync(join(f.root, '.sandbox-cache'), { recursive: true });
  symlinkSync(f.dir, join(f.root, '.sandbox-cache/package-feed-catalog'));
  refused(f.run('scripts/sandbox', ['--feed-catalog', 'dotnet', 'true']), /symbolic link/);
  assert.ok(!f.calls().some(c => operation(c)[0] === 'run'));
});

for (const engine of ['docker', 'podman']) test(`${engine} scanner preserves pinned policy and propagates findings failure`, t => {
  const f = fixture(t), r = f.run('scripts/security-check', [], { CE_CONTAINER_ENGINE: engine, MOCK_RUN_EXIT: '7' });
  assert.equal(r.status, 7, resultError(r));
  const args = operation(f.calls().find(c => operation(c)[0] === 'run'));
  assert.ok(args.includes(`${f.root}:/work:ro`)); assert.ok(args.includes('--include-dev-deps'));
  assert.ok(args.includes('UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL')); assert.ok(args.includes('--disable-telemetry'));
  assert.ok(args.some(a => a.includes('aquasec/trivy:') && /@sha256:[a-f0-9]{64}$/.test(a)));
  for (const flag of ['--config', '--ignorefile']) assert.equal(args[args.indexOf(flag) + 1], '');
  assert.ok(!args.join('\n').includes('.sock')); assert.ok(f.calls().every(c => c.engine === engine));
});

test('missing scanner image is acquired using only selected Podman; pull failure prevents run', t => {
  const f = fixture(t), r = f.run('scripts/security-check', [], { CE_CONTAINER_ENGINE: 'podman', MOCK_IMAGE_MISSING: '1', MOCK_PULL_EXIT: '17' });
  assert.equal(r.status, 17, resultError(r));
  const ops = f.calls().map(operation); assert.ok(ops.some(a => a[0] === 'pull'));
  assert.ok(!ops.some(a => a[0] === 'run')); assert.ok(f.calls().every(c => c.engine === 'podman'));
});

test('scanner rejects a mutable image before any engine call', t => {
  const f = fixture(t); writeFileSync(join(f.root, 'config/security/trivy-image'), 'aquasec/trivy:latest\n');
  refused(f.run('scripts/security-check', [], { CE_CONTAINER_ENGINE: 'podman' }), /Invalid pinned Trivy image/);
  assert.equal(f.calls().length, 0);
});

test('scanner mounts an explicit certificate with spaces read-only without forwarding secret content', t => {
  const f = fixture(t), ca = join(f.dir, 'operator certificate.pem');
  writeFileSync(ca, 'SYNTHETIC-CERTIFICATE-CONTENT', { mode: 0o600 });
  const r = f.run('scripts/security-check', [], { CE_CONTAINER_ENGINE: 'podman',
    MOCK_HOST_OS: 'Darwin', CE_PODMAN_CONNECTION: 'ce-validation', MOCK_REPOINT_MACHINE: '1',
    SANDBOX_EXTRA_CA_FILE: ca, TEST_PRIVATE_TOKEN: 'NEVER-FORWARD-THIS' }); passed(r);
  const call = f.calls().find(c => operation(c)[0] === 'run'); assert.ok(call);
  const args = operation(call);
  assert.deepEqual(call.args.slice(0, 4), ['--url', 'ssh://core@127.0.0.1:34567/run/user/501/podman/podman.sock', '--identity', f.identity]);
  assert.ok(!call.args.includes('--connection'));
  assert.ok(args.includes(`${ca}:/etc/sandbox/extra-ca.crt:ro`));
  assert.ok(args.includes('SSL_CERT_FILE=/etc/sandbox/extra-ca.crt'));
  assert.ok(!args.join('\n').includes('SYNTHETIC-CERTIFICATE-CONTENT'));
  assert.ok(!args.join('\n').includes('TEST_PRIVATE_TOKEN')); assert.ok(!args.join('\n').includes('NEVER-FORWARD-THIS'));
});

for (const kind of ['relative', 'missing', 'newline', 'colon'] as const) test(`scanner rejects ${kind} certificate path before run`, t => {
  const f = fixture(t), paths = { relative: 'ca.pem', missing: join(f.dir, 'missing.pem'),
    newline: join(f.dir, 'bad\ncertificate.pem'), colon: join(f.dir, 'bad:certificate.pem') };
  if (kind !== 'missing') writeFileSync(kind === 'relative' ? join(f.root, paths[kind]) : paths[kind], 'test-only certificate');
  const r = f.run('scripts/security-check', [], { CE_CONTAINER_ENGINE: 'podman', SANDBOX_EXTRA_CA_FILE: paths[kind] });
  refused(r, /SANDBOX_EXTRA_CA_FILE/); assert.ok(!f.calls().some(c => operation(c)[0] === 'run'));
});

for (const [script, args, command, expectedArgs] of [
  ['scripts/analyze-worker', ['capabilities with spaces.json'], 'analysis-worker', ['--capabilities', 'capabilities with spaces.json']],
  ['scripts/analyze', ['fixture-capability', 'capabilities with spaces.json'], 'analysis-run', ['--capability', 'fixture-capability', '--capabilities', 'capabilities with spaces.json']],
  ['scripts/analyze-all', ['capabilities with spaces.json'], 'analysis-all', ['--capabilities', 'capabilities with spaces.json']],
] satisfies [string, string[], string, string[]][]) test(`Given a native engine when ${script} launches then the host CLI receives that engine without fallback`, t => {
  for (const engine of ['docker', 'podman']) {
    const f = fixture(t), cli = join(f.root, 'platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll');
    mkdirSync(join(cli, '..'), { recursive: true }); writeFileSync(cli, 'test-only dummy CLI');
    const log = join(f.dir, 'host-cli.jsonl');
    writeFileSync(join(f.bin, 'dotnet'), `#!${process.execPath}
const fs = require('node:fs');
fs.appendFileSync(process.env.TEST_HOST_CLI_LOG, JSON.stringify({ args: process.argv.slice(2), engine: process.env.CE_CONTAINER_ENGINE })+'\\n');
process.exit(Number(process.env.TEST_HOST_CLI_EXIT ?? 0));
`, { mode: 0o755 });
    const env = { CE_CONTAINER_ENGINE: engine, TEST_HOST_CLI_LOG: log };
    const launched = f.run(script, args, env, '/bin/bash', 5000); passed(launched);
    const expected = { args: [cli, command, ...expectedArgs], engine };
    const recorded: unknown = JSON.parse(readFileSync(log, 'utf8').trim());
    assert.deepEqual(recorded, expected, resultError(launched));
    const failed = f.run(script, args, { ...env, TEST_HOST_CLI_EXIT: '17' }, '/bin/bash', 5000);
    assert.equal(failed.status, 17, resultError(failed));
    const calls: unknown[] = readFileSync(log, 'utf8').trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(calls, [expected, expected], 'launcher must propagate host failure without retrying another engine');
    rmSync(join(f.bin, engine));
    const missing = f.run(script, args, { ...env, TEST_HOST_CLI_EXIT: '17' }, '/bin/bash', 5000);
    assert.equal(missing.status, 17, resultError(missing));
    const afterMissing: unknown[] = readFileSync(log, 'utf8').trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(afterMissing, [expected, expected, expected], 'missing engine must reach host qualification without falling back to the available engine');
    refused(f.run(script, args, { ...env, CE_CONTAINER_ENGINE: 'containerd' }, '/bin/bash', 5000), /must be docker or podman/);
    assert.equal(readFileSync(log, 'utf8').trim().split('\n').length, 3, 'invalid engine must stop before the host CLI');
    assert.deepEqual(f.calls(), [], 'launcher must not probe or run an engine before the host CLI owns qualification');
  }
});

for (const script of ['scripts/local-check', 'scripts/pr-check', 'scripts/security-collect']) test(`${script} rejects the unqualified Podman legacy path`, t => {
  const f = fixture(t), r = f.run(script, [], { CE_CONTAINER_ENGINE: 'podman' }, '/bin/bash', 1500);
  refused(r, /not qualified|unsupported|not supported/i); assert.equal(f.calls().length, 0);
});

for (const [script, expression] of [
  ['scripts/security-collect.mjs', 'await m.collect(process.cwd(), ".sandbox-cache/not-created")'],
  ['scripts/benchmarks/issue28-explain.mjs', 'm.explain({})'],
  ['scripts/benchmarks/issue28-http.mjs', 'await m.benchmark({})'],
]) test(`${script} exported runtime operation rejects unqualified Podman before engine use`, t => {
  const f = fixture(t), url = pathToFileURL(join(f.root, script)).href;
  const code = `const m=await import(${JSON.stringify(url)}); ${expression};`;
  const r = spawnSync(process.execPath, ['--input-type=module', '-e', code], {
    cwd: f.root, env: { ...f.env, CE_CONTAINER_ENGINE: 'podman' }, encoding: 'utf8', timeout: 5000 });
  refused(r, /not qualified|unsupported|not supported/i); assert.equal(f.calls().length, 0);
  assert.ok(!existsSync(join(f.root, '.sandbox-cache/not-created')));
});

const make = process.platform === 'darwin' && existsSync('/Library/Developer/CommandLineTools/usr/bin/make')
  ? '/Library/Developer/CommandLineTools/usr/bin/make' : '/usr/bin/make';

for (const engine of ['docker', 'podman']) test(`native make dev starts the host worker with ${engine}`, t => {
  const f = fixture(t, [engine]);
  const ready = join(f.dir, 'worker-started');
  symlinkSync('/usr/bin/awk', join(f.bin, 'awk'));
  const cli = join(f.root, 'platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0');
  mkdirSync(cli, { recursive: true });
  writeFileSync(join(cli, 'ce.dll'), 'test-only CLI placeholder');
  writeFileSync(join(f.bin, 'dotnet'), '#!/bin/sh\nprintf "%s" "$CE_CONTAINER_ENGINE" > "$TEST_WORKER_READY"\n', { mode: 0o755 });
  const wait = join(f.dir, 'wait-worker.cjs');
  writeFileSync(wait, `const fs=require('node:fs');const file=process.env.TEST_WORKER_READY;
if(fs.existsSync(file))process.exit(0);
const watcher=fs.watch(require('node:path').dirname(file),()=>{if(fs.existsSync(file)){watcher.close();clearTimeout(timeout);}});
const timeout=setTimeout(()=>{watcher.close();console.error('development worker never started');process.exitCode=1;},2000);
`);
  const overrides = join(f.dir, 'dev-overrides.mk');
  writeFileSync(overrides, `explore:\n\t@node "${wait}"\n`);
  const result = spawnSync(make, ['-f', 'Makefile', '-f', overrides, '--no-print-directory',
    '-o', 'dev-data', '-o', 'build', '-o', 'fixtures', '-o', 'model-fixtures', 'EXECUTION=native',
    `MAKE=${make} -f Makefile -f ${overrides} -o dev-data -o build -o fixtures -o model-fixtures`, 'dev'], {
    cwd: f.root, env: { ...f.env, CE_CONTAINER_ENGINE: engine, TEST_WORKER_READY: ready }, encoding: 'utf8', timeout: 10000,
  });
  passed(result);
  assert.equal(readFileSync(ready, 'utf8'), engine, 'both development profiles must start the same host worker');
  assert.equal(f.calls().length, 0, 'stubbed development startup must not start a container');
});

for (const goal of ['postgres', 'ollama', 'dev-down', 'ci']) test(`Make ${goal} refuses the partial Podman profile before engine use`, t => {
  const f = fixture(t), r = spawnSync(make, ['EXECUTION=sandbox', goal], {
    cwd: f.root, env: { ...f.env, CE_CONTAINER_ENGINE: 'podman' }, encoding: 'utf8', timeout: 10000 });
  refused(r, /not qualified|unsupported|not supported/i); assert.equal(f.calls().length, 0);
});

test('Make permits native service and analysis goals with Podman but keeps sandbox dev refused', t => {
  const f = fixture(t), env = { ...f.env, CE_CONTAINER_ENGINE: 'podman' };
  writeFileSync(join(f.root, 'podman-goals.mk'), [
    'include config/build/container-engine.mk',
    '.PHONY: dev dev-data explore postgres ollama dev-down analyze analyze-all analyze-worker',
    'dev dev-data explore postgres ollama dev-down analyze analyze-all analyze-worker:',
    '\t@true',
    ''
  ].join('\n'));
  for (const goal of ['postgres', 'ollama', 'dev-down', 'dev', 'explore', 'analyze', 'analyze-all', 'analyze-worker']) {
    const r = spawnSync(make, ['-f', 'podman-goals.mk', '-n', 'EXECUTION=native', goal], {
      cwd: f.root, env, encoding: 'utf8', timeout: 10000 });
    passed(r);
  }
  const sandbox = spawnSync(make, ['EXECUTION=sandbox', 'dev'], { cwd: f.root, env, encoding: 'utf8', timeout: 10000 });
  refused(sandbox, /not qualified|unsupported|not supported/i);
  assert.equal(f.calls().length, 0);
});

for (const engine of ['%', 'docker podman', '']) test(`Make rejects invalid literal engine ${JSON.stringify(engine)}`, t => {
  const f = fixture(t), r = spawnSync(make, ['-n', 'help'], {
    cwd: f.root, env: { ...f.env, CE_CONTAINER_ENGINE: engine }, encoding: 'utf8', timeout: 5000 });
  refused(r, /must be auto, docker or podman/); assert.equal(f.calls().length, 0);
});

test('Make auto-selection fails clearly when neither engine is installed', t => {
  const f = fixture(t, []), r = spawnSync(make, ['postgres'], {
    cwd: f.root, env: f.env, encoding: 'utf8', timeout: 5000 });
  refused(r, /found neither Docker nor Podman/); assert.equal(f.calls().length, 0);
});

test('native development works without an engine while sandbox build still refuses', t => {
  const f = fixture(t, []);
  writeFileSync(join(f.root, 'engine-free-goals.mk'), [
    'include config/build/container-engine.mk',
    '.PHONY: build frontend backend restore restore-dotnet restore-ts restore-ui ui',
    'build frontend backend restore restore-dotnet restore-ts restore-ui ui:',
    '\t@true',
    ''
  ].join('\n'));
  for (const goal of ['build', 'frontend', 'backend', 'restore', 'restore-dotnet', 'restore-ts', 'restore-ui', 'ui']) {
    passed(spawnSync(make, ['-f', 'engine-free-goals.mk', '-n', 'EXECUTION=native', goal], {
      cwd: f.root, env: f.env, encoding: 'utf8', timeout: 5000 }));
  }
  refused(spawnSync(make, ['-f', 'engine-free-goals.mk', 'EXECUTION=sandbox', 'build'], {
    cwd: f.root, env: f.env, encoding: 'utf8', timeout: 5000 }), /found neither Docker nor Podman/);
  assert.equal(f.calls().length, 0);
});

test('bare Make dry-run selects help without doctor or engine commands', t => {
  const f = fixture(t), r = spawnSync(make, ['-n'], {
    cwd: f.root, env: { ...f.env, CE_CONTAINER_ENGINE: 'podman' }, encoding: 'utf8', timeout: 5000 });
  passed(r); assert.match(r.stdout, /make local-check/);
  assert.doesNotMatch(r.stdout, /scripts\/container-engine-doctor|docker info|podman info/);
  assert.equal(f.calls().length, 0);
});
