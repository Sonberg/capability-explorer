import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const policyFile = 'config/ci/required-plan.json';
const protectedInputs = [
  'Makefile', 'config/build/sandbox.mk', 'config/build/native.mk',
  'config/build/issue28.mk', 'config/build/platform.mk', 'config/build/container-engine.mk',
  'config/runtime/images.env', '.github/workflows/ci.yml', 'scripts/local-check',
  'scripts/sandbox', 'scripts/security-check', 'scripts/compile-fixtures.sh',
  'scripts/ci/check-frugal-ci', 'scripts/ci/check-frugal-ci.mjs'
];

export function commands(text) {
  return text.replace(/[ \t]*\\\r?\n[ \t]*/g, ' ').split(/\r?\n/)
    .map(line => line.trim()).filter(line => line && !line.startsWith('#'));
}

export function inspect(read, plan) {
  const inputs = Object.fromEntries(protectedInputs.map(path => [
    path, createHash('sha256').update(read(path)).digest('hex')
  ]));
  const workflow = read('.github/workflows/ci.yml');
  if ((workflow.match(/^\s*- run: make EXECUTION=sandbox ci$/gm) ?? []).length !== 1)
    throw new Error('hosted CI must invoke the canonical sandbox ci target once');
  if (!read('scripts/local-check').includes('-W .github/workflows/ci.yml -j ci --bind'))
    throw new Error('local CI must run the same workflow/job');
  if (!/^\.NOTPARALLEL:$/m.test(read('config/build/sandbox.mk')))
    throw new Error('Make must serialize shared restore/build/fixture outputs');
  const cachePaths = workflow.match(/          path: \|\n((?:            .+\n)+)/)?.[1]
    .trim().split('\n').map(line => line.trim());
  if (JSON.stringify(cachePaths) !== JSON.stringify([
    '.sandbox-cache/nuget', '.sandbox-cache/nuget-http', '.sandbox-cache/npm',
    '.sandbox-cache/trivy', '.sandbox-cache/tools'
  ])) throw new Error('only download/tool caches may be restored; no installed outputs or test receipts');
  for (const input of ['config/runtime/images.env', 'scripts/sandbox', 'config/build/sandbox.mk'])
    if (!workflow.includes(`'${input}'`)) throw new Error(`cache key omits ${input}`);
  const expanded = commands(plan);
  if (!expanded.includes('scripts/ci/check-frugal-ci'))
    throw new Error('full CI omits its drift checker');
  if (!expanded.some(command => command.includes('tests/frugal-ci.test.mjs tests/fixture-concurrency.test.mjs')))
    throw new Error('full CI omits drift/failure regression tests');
  if (expanded.some(command => /(?:^|\s)(?:make\s+-j|--jobs)(?:\s|=|\d)/.test(command)))
    throw new Error('unclassified Make parallelism');
  return { version: 1, inputs, commands: expanded };
}

export function verify(expected, actual) {
  if (expected.version !== 1) throw new Error('unknown required-plan version');
  for (const [path, hash] of Object.entries(actual.inputs))
    if (expected.inputs[path] !== hash) throw new Error(`unreviewed CI policy input: ${path}`);
  if (JSON.stringify(Object.keys(expected.inputs).sort()) !== JSON.stringify(Object.keys(actual.inputs).sort()))
    throw new Error('CI policy input inventory drift');
  if (JSON.stringify(expected.commands) !== JSON.stringify(actual.commands)) {
    const index = actual.commands.findIndex((command, i) => command !== expected.commands[i]);
    throw new Error(`expanded CI plan drift at command ${index < 0 ? actual.commands.length + 1 : index + 1}; review required-plan.json, do not drop required coverage`);
  }
}

function expand() {
  const result = spawnSync('make', [
    '--no-print-directory', '--dry-run', '--always-make', 'EXECUTION=sandbox', 'ci'
  ], {
    cwd: root, encoding: 'utf8', maxBuffer: 4 * 1024 * 1024,
    env: { ...process.env, MAKEFLAGS: '', MFLAGS: '', GNUMAKEFLAGS: '', MAKEOVERRIDES: '' }
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Make plan expansion failed: ${result.stderr}`);
  return result.stdout;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    if (args.length > 2 || (args.length && !['--record', '--plan'].includes(args[0])) ||
        (args[0] === '--record' && args.length !== 1) ||
        (args[0] === '--plan' && args.length !== 2))
      throw new Error('usage: check-frugal-ci.mjs [--record | --plan path]');
    const read = path => readFileSync(resolve(root, path), 'utf8');
    const actual = inspect(read, args[0] === '--plan' ? read(args[1]) : expand());
    if (args[0] === '--record') {
      writeFileSync(resolve(root, policyFile), `${JSON.stringify(actual, null, 2)}\n`);
      console.log('frugal-ci: recorded candidate plan; review every coverage/command/input change');
    } else {
      verify(JSON.parse(read(policyFile)), actual);
      console.log(`frugal-ci: ${actual.commands.length} canonical commands; local/hosted policy matches`);
    }
  } catch (error) {
    console.error(`frugal-ci: ${error.message}`);
    process.exitCode = 1;
  }
}
