import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sourceScript = path.join(projectRoot, 'scripts/compile-fixtures.sh');
const shell = process.env.BASH ?? 'bash';
const dotnetCases = [
  'api-cs@v1-base',
  'api-cs@v2-rename',
  'api-cs@v3-filter',
  'worker-cs@v1-base',
  'worker-cs@v2-broken',
  'forecast-cs@v1-base',
  'forecast-cs@v2-contract',
];
const nodeCases = ['web-ts@v1-base', 'pricing-node@v1-base', 'pricing-node@v2-move'];
const phases = ['restore-dotnet', 'restore-node', 'build-dotnet', 'build-node'];

const fakeTools = `import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
const args = process.argv.slice(3);
const name = process.argv[2];
const stateDir = process.env.FIXTURE_STATE;
const stateFile = path.join(stateDir, 'state.json');
const lockDir = path.join(stateDir, 'lock');
const waitCell = new Int32Array(new SharedArrayBuffer(4));
function locked(update) {
  while (true) {
    try { mkdirSync(lockDir); break; }
    catch (error) { if (error.code !== 'EEXIST') throw error; Atomics.wait(waitCell, 0, 0, 2); }
  }
  try {
    const state = existsSync(stateFile) ? JSON.parse(readFileSync(stateFile, 'utf8')) : { active: {}, max: {}, arrivals: {}, events: [] };
    update(state);
    writeFileSync(stateFile, JSON.stringify(state));
  } finally { rmSync(lockDir, { recursive: true, force: true }); }
}
const caseName = path.basename(process.cwd());
if (name === 'git') {
  const source = args.at(-2);
  const destination = args.at(-1);
  mkdirSync(path.dirname(destination), { recursive: true });
  cpSync(source, destination, { recursive: true });
  process.exit(0);
}
const operation = name === 'dotnet' ? args[0] : name === 'npm' ? 'restore' : 'build';
const key = (name === 'dotnet' ? 'dotnet-' : name === 'npm' ? 'node-' : 'node-') + operation;
const barrier = process.env.FIXTURE_BARRIER_TARGET;
locked(state => {
  state.active[key] = (state.active[key] ?? 0) + 1;
  state.max[key] = Math.max(state.max[key] ?? 0, state.active[key]);
  state.events.push({ type: 'start', key, caseName, home: process.env.HOME, cliHome: process.env.DOTNET_CLI_HOME });
  state.arrivals[key] = (state.arrivals[key] ?? 0) + 1;
  if (barrier && state.arrivals[key] >= Number(barrier)) writeFileSync(path.join(stateDir, key + '.ready'), key);
  if (barrier && !process.env.FIXTURE_HOLD && state.arrivals[key] >= Number(barrier)) writeFileSync(path.join(stateDir, key + '.open'), 'open');
});
if (barrier) while (!existsSync(path.join(stateDir, key + '.open'))) await delay(2);
let status = 0;
if (name === 'dotnet' && operation === 'restore' && caseName === 'worker-cs@v2-broken') status = 1;
if (name === 'dotnet' && operation === 'build' && caseName === 'worker-cs@v2-broken') {
  process.stderr.write("CS0234 The type or namespace name 'Shared' could not be found\\\\n");
  status = 1;
}
if (process.env.FIXTURE_FAIL_CASE === caseName && process.env.FIXTURE_FAIL_KEY === key) {
  process.stderr.write('synthetic worker failure\\\\n');
  status = 1;
}
locked(state => {
  state.active[key] -= 1;
  state.events.push({ type: 'end', key, caseName, status });
});
process.exit(status);
`;

function createHarness(label) {
  const base = path.join(projectRoot, 'tests', `.fixture-concurrency-${process.pid}-${label}-${randomUUID()}`);
  const bin = path.join(base, 'bin');
  const scriptRoot = path.join(base, 'repo');
  const fixtureRoot = path.join(scriptRoot, 'fixtures');
  const stateDir = path.join(base, 'state');
  const toolsPath = path.join(base, 'fake-tools.mjs');
  const tscTool = path.join(base, 'tsc');
  mkdirSync(bin, { recursive: true });
  mkdirSync(path.join(fixtureRoot, '.repos'), { recursive: true });
  mkdirSync(stateDir);
  mkdirSync(path.join(scriptRoot, 'scripts'), { recursive: true });
  mkdirSync(path.join(scriptRoot, 'platform'));
  cpSync(sourceScript, path.join(scriptRoot, 'scripts/compile-fixtures.sh'));
  writeFileSync(toolsPath, fakeTools, { mode: 0o755 });
  writeFileSync(tscTool, `#!/bin/sh\nexec node "${toolsPath}" tsc "$@"\n`, { mode: 0o755 });
  for (const name of ['git', 'dotnet', 'npm']) {
    const wrapper = path.join(bin, name);
    writeFileSync(wrapper, `#!/bin/sh\nexec node "${toolsPath}" ${name} "$@"\n`, { mode: 0o755 });
  }
  for (const repo of ['api-cs', 'worker-cs', 'forecast-cs', 'web-ts', 'pricing-node']) {
    const fixture = path.join(fixtureRoot, '.repos', repo);
    mkdirSync(path.join(fixture, 'node_modules', '.bin'), { recursive: true });
    symlinkSync(tscTool, path.join(fixture, 'node_modules', '.bin', 'tsc'));
  }
  return {
    base,
    bin,
    script: path.join(scriptRoot, 'scripts/compile-fixtures.sh'),
    stateDir,
    env: {
      ...process.env,
      PATH: `${bin}${path.delimiter}${process.env.PATH}`,
      FIXTURE_STATE: stateDir,
    },
    state() {
      return existsSync(path.join(stateDir, 'state.json'))
        ? JSON.parse(readFileSync(path.join(stateDir, 'state.json'), 'utf8'))
        : { active: {}, max: {}, arrivals: {}, events: [] };
    },
    cleanup() { rmSync(base, { recursive: true, force: true }); },
  };
}

function runPhase(harness, phase, jobs, extraEnv = {}) {
  return spawnSync(shell, [harness.script, phase, String(jobs)], {
    cwd: harness.base,
    env: { ...harness.env, ...extraEnv },
    encoding: 'utf8',
  });
}

async function waitForFile(file, child) {
  const deadline = Date.now() + 10_000;
  while (!existsSync(file)) {
    if (child.exitCode !== null) throw new Error(`child exited before barrier: ${child.exitCode}`);
    if (Date.now() > deadline) throw new Error('timed out waiting for deterministic fixture barrier');
    await delay(5);
  }
}

test('fixture phases use a bounded, failure-aggregating worker pool', async () => {
  const outputs = [];
  const serial = createHarness('serial');
  const concurrent = createHarness('concurrent');
  try {
    for (const harness of [serial, concurrent]) {
      const jobs = harness === serial ? 1 : 2;
      const barrier = jobs === 2 ? { FIXTURE_BARRIER_TARGET: '2' } : {};
      for (const phase of phases) {
        const result = jobs === 2 && phase === 'build-node'
          ? spawnSync(shell, [harness.script, phase], {
            cwd: harness.base,
            env: { ...harness.env, ...barrier },
            encoding: 'utf8',
          })
          : runPhase(harness, phase, jobs, barrier);
        assert.equal(result.status, 0, `${phase} (${jobs} workers): ${result.stderr}\n${result.stdout}`);
        outputs.push(result.stdout);
        const state = harness.state();
        const dotnet = phase.endsWith('dotnet');
        const expected = dotnet ? dotnetCases : nodeCases;
        const operation = `${dotnet ? 'dotnet' : 'node'}-${phase.startsWith('restore') ? 'restore' : 'build'}`;
        const starts = state.events.filter(event => event.type === 'start' && event.key === operation);
        const ends = state.events.filter(event => event.type === 'end' && event.key === operation);
        assert.deepEqual(starts.map(event => event.caseName).sort(), [...expected].sort(), phase);
        assert.deepEqual(ends.map(event => event.caseName).sort(), [...expected].sort(), `${phase} waited for every child`);
        if (dotnet) {
          assert.equal(new Set(starts.map(event => event.home)).size, expected.length, 'each .NET case isolates first-use mutex state');
          for (const event of starts) {
            assert.equal(event.home, event.cliHome);
            assert.equal(path.basename(event.home), '.dotnet-home');
            assert.equal(path.basename(path.dirname(event.home)), event.caseName);
          }
        }
        assert.ok(state.max[operation] <= jobs, `${phase} exceeded its ${jobs}-worker limit`);
        if (jobs === 2) assert.equal(state.max[operation], 2, `${phase} barrier proved actual overlap`);
      }
    }

    assert.match(outputs.join('\n'), /PASS worker-cs@v2-broken expected broken-project restore failure/);
    assert.match(outputs.join('\n'), /PASS worker-cs@v2-broken fails as intended \(CS0234\.\*'Shared'\)/);
    const beforeInvalid = serial.state().events.length;
    for (const invalid of ['0', '3', '01', '1.5', 'two', '']) {
      const result = spawnSync(shell, [serial.script, 'build-dotnet', invalid], {
        cwd: serial.base,
        env: serial.env,
        encoding: 'utf8',
      });
      assert.equal(result.status, 2, `invalid jobs=${JSON.stringify(invalid)}`);
      assert.equal(serial.state().events.length, beforeInvalid, 'invalid jobs have no tool effects');
    }
    const extraArg = spawnSync(shell, [serial.script, 'build-dotnet', '1', 'extra'], {
      cwd: serial.base,
      env: serial.env,
      encoding: 'utf8',
    });
    assert.equal(extraArg.status, 2);
    assert.equal(serial.state().events.length, beforeInvalid, 'invalid arguments have no tool effects');

    const failing = createHarness('failure');
    try {
      const child = spawn(shell, [failing.script, 'build-dotnet', '2'], {
        cwd: failing.base,
        env: {
          ...failing.env,
          FIXTURE_BARRIER_TARGET: '2',
          FIXTURE_HOLD: '1',
          FIXTURE_FAIL_CASE: 'api-cs@v1-base',
          FIXTURE_FAIL_KEY: 'dotnet-build',
        },
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      let output = '';
      child.stdout.setEncoding('utf8').on('data', chunk => { output += chunk; });
      child.stderr.setEncoding('utf8').on('data', chunk => { output += chunk; });
      const closed = new Promise(resolve => child.on('close', (code, signal) => resolve({ code, signal })));
      await waitForFile(path.join(failing.stateDir, 'dotnet-build.ready'), child);
      assert.equal(child.exitCode, null, 'workers are still blocked at the barrier');
      writeFileSync(path.join(failing.stateDir, 'dotnet-build.open'), 'release');
      const result = await closed;
      assert.notEqual(result.code, 0, output);
      assert.match(output, /FAIL api-cs@v1-base expected to compile/);
      const state = failing.state();
      assert.deepEqual(state.events.filter(event => event.type === 'start').map(event => event.caseName).sort(), dotnetCases.slice().sort());
      assert.deepEqual(state.events.filter(event => event.type === 'end').map(event => event.caseName).sort(), dotnetCases.slice().sort());
    } finally {
      failing.cleanup();
    }

    const interrupted = createHarness('interrupted');
    try {
      const child = spawn(shell, [interrupted.script, 'build-dotnet', '2'], {
        cwd: interrupted.base,
        env: { ...interrupted.env, FIXTURE_BARRIER_TARGET: '2', FIXTURE_HOLD: '1' },
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      let output = '';
      child.stdout.setEncoding('utf8').on('data', chunk => { output += chunk; });
      child.stderr.setEncoding('utf8').on('data', chunk => { output += chunk; });
      const closed = new Promise(resolve => child.on('close', (code, signal) => resolve({ code, signal })));
      await waitForFile(path.join(interrupted.stateDir, 'dotnet-build.ready'), child);
      child.kill('SIGTERM');
      await new Promise((resolve, reject) => {
        const deadline = Date.now() + 10_000;
        const observe = () => {
          if (output.includes('received interrupt')) resolve();
          else if (child.exitCode !== null) reject(new Error(`interrupted worker exited early: ${output}`));
          else if (Date.now() > deadline) reject(new Error('signal handler did not run'));
          else setTimeout(observe, 5);
        };
        observe();
      });
      writeFileSync(path.join(interrupted.stateDir, 'dotnet-build.open'), 'release');
      const result = await closed;
      assert.notEqual(result.code, 0, output);
      assert.match(output, /FAIL build-dotnet interrupted/);
      const state = interrupted.state();
      assert.equal(state.events.filter(event => event.type === 'start').length, 2, 'interruption starts no later cases');
      assert.equal(state.events.filter(event => event.type === 'end').length, 2, 'interrupted parent waits for every launched worker');
    } finally {
      interrupted.cleanup();
    }
  } finally {
    serial.cleanup();
    concurrent.cleanup();
  }
});
