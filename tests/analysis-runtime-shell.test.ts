import { test, type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(new URL('../', import.meta.url));
function fixture(context: TestContext) {
  const dir = mkdtempSync(join(tmpdir(), 'ce-analysis-runtime-'));
  context.after(() => rmSync(dir, { recursive: true, force: true }));
  const root = join(dir, 'checkout with spaces'), bin = join(dir, 'bin'), log = join(dir, 'calls');
  mkdirSync(root); mkdirSync(bin);
  cpSync(join(source, 'scripts'), join(root, 'scripts'), { recursive: true });
  cpSync(join(source, 'config'), join(root, 'config'), { recursive: true });
  const identity = join(dir, 'machine key'); writeFileSync(identity, 'test-only');
  for (const utility of ['bash', 'dirname', 'mkdir', 'cat', 'mktemp', 'rm', 'wc', 'sleep', 'id', 'shasum', 'sha256sum', 'sed']) {
    const actual = ['/usr/bin', '/bin'].map(path => join(path, utility)).find(existsSync);
    if (actual) symlinkSync(actual, join(bin, utility));
  }
  symlinkSync(process.execPath, join(bin, 'node'));
  writeFileSync(join(bin, 'uname'), '#!/bin/sh\necho "${TEST_OS:-Linux}"\n', { mode: 0o755 });
  const mock = `#!${process.execPath}
const fs = require('node:fs');
const full = process.argv.slice(2), args = [...full];
fs.appendFileSync(process.env.TEST_LOG, JSON.stringify({args: full, selectors: ['DOCKER_HOST','DOCKER_CONTEXT','CONTAINER_HOST','CONTAINER_CONNECTION','CONTAINER_SSHKEY','CE_ANALYSIS_RUNTIME'].map(key => process.env[key] ?? null)})+'\\n');
while (['--host','--url','--identity'].includes(args[0])) args.splice(0,2);
if (args[0] === '--remote=false') args.shift();
const docker = process.argv[1].endsWith('/docker');
if (args[0] === 'context') console.log(args[1] === 'show' ? 'mutable-alias' : (process.env.TEST_ENDPOINT ?? 'unix:///original.sock'));
if (args[0] === 'machine') console.log('fixture|true');
if (args[0] === 'system') console.log('ssh://core@127.0.0.1:34567/run/user/501/podman/podman.sock|true|'+process.env.TEST_IDENTITY);
if (args[0] === 'version') console.log(docker ? '29.0.0|29.0.0|linux|arm64|Docker Engine' : '6.1.4|6.1.4');
if (args[0] === 'info') console.log(docker ? (process.env.TEST_ID ?? 'original-daemon') : (full.some(arg => arg.includes('.Store.GraphRoot')) ? (process.env.TEST_ID ?? '/storage|machine') : 'true|arm64|6.1.4|linux'));
`;
  for (const engine of ['docker', 'podman']) writeFileSync(join(bin, engine), mock, { mode: 0o755 });
  const env: NodeJS.ProcessEnv = { PATH: bin, HOME: dir, TEST_LOG: log, TEST_IDENTITY: identity };
  function run(script: string, args: string[], extra: NodeJS.ProcessEnv = {}) {
    return spawnSync('/bin/bash', [join(root, script), ...args], { cwd: root, env: { ...env, ...extra }, encoding: 'utf8', timeout: 30000 });
  }
  function calls(): { args: string[]; selectors: (string | null)[] }[] {
    return existsSync(log) ? readFileSync(log, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line)) : [];
  }
  return { root, bin, identity, run, calls, clear: () => writeFileSync(log, '') };
}
function snapshot(f: ReturnType<typeof fixture>, backend: string, os: string) {
  const result = f.run('scripts/container-engine', ['--analysis-snapshot'], { CE_CONTAINER_ENGINE: backend, TEST_OS: os });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /^\{"backend":/);
  const value: unknown = JSON.parse(result.stdout);
  assert.ok(typeof value === 'object' && value !== null && 'fingerprint' in value);
  assert.deepEqual(Object.keys(value).sort(), ['backend', 'executable', 'fingerprint', 'prefixArguments']);
  assert.match(String(value.fingerprint), /^[a-f0-9]{64}$/);
  assert.ok(!f.calls().some(call => call.args.includes('--analysis-snapshot')));
  return result.stdout.trim();
}

for (const [backend, os] of [['docker', 'Linux'], ['podman', 'Linux'], ['podman', 'Darwin']]) {
  test(`Given qualified ${backend} on ${os} when captured and restored then only its frozen command runs`, context => {
    const f = fixture(context), captured = snapshot(f, backend!, os!);
    f.clear();
    const result = f.run('scripts/container-engine', ['ps'], { CE_ANALYSIS_RUNTIME: captured, TEST_OS: os,
      CE_CONTAINER_ENGINE: 'invalid', TEST_ENDPOINT: 'unix:///repointed.sock', CE_PODMAN_CONNECTION: 'repointed',
      DOCKER_HOST: 'tcp://ambient', DOCKER_CONTEXT: 'ambient', CONTAINER_HOST: 'ssh://ambient', CONTAINER_CONNECTION: 'ambient', CONTAINER_SSHKEY: '/ambient' });
    assert.equal(result.status, 0, result.stderr);
    const expected: { executable: string; prefixArguments: string[] } = JSON.parse(captured);
    assert.equal(expected.executable, join(f.bin, backend!));
    assert.ok(f.calls().every(call => call.args.slice(0, expected.prefixArguments.length).join('\n') === expected.prefixArguments.join('\n')));
    assert.ok(f.calls().every(call => !call.args.includes('context') && !call.args.includes('machine') && !call.args.includes('system')));
    assert.ok(f.calls().every(call => call.selectors.slice(0, 5).every(value => value === null)));
    assert.equal(f.calls().at(-1)?.args.at(-1), 'ps');
    f.clear();
    const drift = f.run('scripts/container-engine', ['ps'], { CE_ANALYSIS_RUNTIME: captured, TEST_OS: os, TEST_ID: 'changed-identity' });
    assert.notEqual(drift.status, 0); assert.match(drift.stderr, /fingerprint|drift/i);
    assert.ok(!f.calls().some(call => call.args.includes('ps')));
  });
}

test('Given an offline snapshot sandbox when requests exists then it mounts the journal and keeps the snapshot host-only', context => {
  const f = fixture(context), captured = snapshot(f, 'podman', 'Linux');
  mkdirSync(join(f.root, '.sandbox-cache/requests'), { recursive: true }); f.clear();
  const result = f.run('scripts/sandbox', ['--offline', 'node', 'true'], { CE_ANALYSIS_RUNTIME: captured, SANDBOX_PASS_ENV: 'CE_ANALYSIS_RUNTIME' });
  assert.equal(result.status, 0, result.stderr);
  const args = f.calls().find(call => call.args.includes('run'))?.args; assert.ok(args);
  assert.ok(args.includes(`${f.root}/.sandbox-cache/requests:/work/.sandbox-cache/requests`));
  for (const arg of ['--read-only', '--userns', '--network', 'none']) assert.ok(args.includes(arg));
  const user = args[args.indexOf('--user') + 1];
  assert.ok(typeof user === 'string');
  const [uid, gid] = user.split(':');
  assert.equal(args[args.indexOf('--userns') + 1], `keep-id:uid=${uid},gid=${gid}`);
  assert.ok(!args.some(arg => arg.includes('CE_ANALYSIS_RUNTIME')));
  assert.ok(f.calls().every(call => call.selectors[5] === null));
});

for (const dangling of [false, true]) test(`Given a ${dangling ? 'dangling' : 'directory'} journal symlink when sandbox starts then no container runs`, context => {
  const f = fixture(context); mkdirSync(join(f.root, '.sandbox-cache'));
  symlinkSync(dangling ? join(f.root, 'missing') : f.root, join(f.root, '.sandbox-cache/requests'));
  const result = f.run('scripts/sandbox', ['--offline', 'node', 'true']);
  assert.notEqual(result.status, 0); assert.match(result.stderr, /journal.*symbolic link/i);
  assert.ok(!f.calls().some(call => call.args.includes('run')));
});

test('Given a Podman snapshot when Docker socket mode is requested then it refuses before probes', context => {
  const f = fixture(context), captured = snapshot(f, 'podman', 'Linux'); f.clear();
  const result = f.run('scripts/sandbox', ['--docker', 'node', 'true'], { CE_ANALYSIS_RUNTIME: captured, CE_CONTAINER_ENGINE: 'docker' });
  assert.notEqual(result.status, 0); assert.match(result.stderr, /not qualified/);
  assert.deepEqual(f.calls(), []);
});

test('Given a snapshot when online forwarding names it then the host-only value is refused', context => {
  const f = fixture(context), captured = snapshot(f, 'docker', 'Linux'); f.clear();
  const result = f.run('scripts/sandbox', ['node', 'true'], { CE_ANALYSIS_RUNTIME: captured, SANDBOX_PASS_ENV: 'CE_ANALYSIS_RUNTIME' });
  assert.notEqual(result.status, 0); assert.match(result.stderr, /host-only/);
  assert.ok(!f.calls().some(call => call.args.includes('run')));
});

test('Given a snapshot whose executable disappeared when restored then PATH engines are not fallback candidates', context => {
  const f = fixture(context), captured = snapshot(f, 'docker', 'Linux'); f.clear();
  rmSync(join(f.bin, 'docker'));
  const result = f.run('scripts/container-engine', ['ps'], { CE_ANALYSIS_RUNTIME: captured, CE_CONTAINER_ENGINE: 'podman' });
  assert.notEqual(result.status, 0); assert.match(result.stderr, /captured runtime executable is missing/);
  assert.deepEqual(f.calls(), []);
});

test('Given malformed snapshots when restored then validation refuses before engine execution', context => {
  const f = fixture(context);
  const base = { backend: 'docker', executable: join(f.bin, 'docker'), prefixArguments: ['--host', 'unix:///original.sock'], fingerprint: 'a'.repeat(64) };
  const invalid = ['', '{', 'x'.repeat(17000), ...[
    { ...base, extra: true }, { ...base, backend: 'containerd' }, { ...base, fingerprint: 'A'.repeat(64) },
    { ...base, executable: 'docker' }, { ...base, prefixArguments: ['--context', 'mutable'] },
    { ...base, prefixArguments: ['--host', 'tcp://external:2375'] },
    { ...base, prefixArguments: ['--host', 'unix:///socket\t'] },
    { ...base, executable: base.executable + '\n' },
    { ...base, prefixArguments: ['--host', 'unix:///socket', 'run'] },
    { ...base, backend: 'podman', prefixArguments: ['--connection', 'mutable'] },
  ].map(value => JSON.stringify(value))];
  for (const captured of invalid) {
    const result = f.run('scripts/container-engine', ['ps'], { CE_ANALYSIS_RUNTIME: captured });
    assert.notEqual(result.status, 0); assert.match(result.stderr, /invalid analysis runtime snapshot/);
  }
  assert.deepEqual(f.calls(), []);
});