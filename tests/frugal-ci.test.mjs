import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { commands, inspect, verify } from '../scripts/ci/check-frugal-ci.mjs';

const root = new URL('../', import.meta.url);
const read = path => readFileSync(new URL(path, root), 'utf8');
const expected = JSON.parse(read('config/ci/required-plan.json'));
const plan = expected.commands.join('\n');

test('canonical plan and policy inputs agree', () => {
  verify(expected, inspect(read, plan));
  assert.deepEqual(commands('one \\\n  two\n# note\n\nthree'), ['one two', 'three']);
});

for (const [label, mutate] of [
  ['dropped Store gate', lines => lines.filter(line => !line.includes('Store.Tests'))],
  ['narrowed test filter', lines => lines.map(line => line.replace('Profile!=HostDocker', 'Profile=Smoke'))],
  ['unclassified extra gate', lines => [...lines, 'echo unclassified']],
  ['skipped preparation', lines => lines.filter(line => !line.includes('dotnet restore --locked-mode'))],
  ['missing failure regression tests', lines => lines.filter(line => !line.includes('tests/frugal-ci.test.mjs'))]
]) {
  test(`rejects ${label}`, () => {
    assert.throws(() => verify(expected, inspect(read, mutate(expected.commands).join('\n'))));
  });
}

for (const [label, path, mutate] of [
  ['hosted target mismatch', '.github/workflows/ci.yml', text => text.replace('EXECUTION=sandbox ci', 'EXECUTION=sandbox test')],
  ['local workflow bypass', 'scripts/local-check', text => text.replace('-j ci --bind', '-j smoke --bind')],
  ['installed-output caching', '.github/workflows/ci.yml', text => text.replace('.sandbox-cache/npm\n', '.sandbox-cache/npm\n            node_modules\n')],
  ['cache key missing runtime identity', '.github/workflows/ci.yml', text => text.replace("'config/runtime/images.env', ", '')],
  ['unsafe Make parallelism', 'config/build/sandbox.mk', text => text.replace('.NOTPARALLEL:', '# .NOTPARALLEL:')],
  ['changed credential boundary', 'scripts/sandbox', text => text.replace('npm_config_ignore_scripts=true', 'npm_config_ignore_scripts=false')],
  ['lost download-cache preference', 'scripts/sandbox', text => text.replace('npm_config_prefer_offline=true', 'npm_config_prefer_offline=false')],
  ['changed fixture worker cap', 'scripts/compile-fixtures.sh', text => text.replace('1|2)', '1|2|8)')]
]) {
  test(`rejects ${label}`, () => {
    const changed = name => name === path ? mutate(read(name)) : read(name);
    assert.notEqual(changed(path), read(path), 'mutation must change real source');
    assert.throws(() => verify(expected, inspect(changed, plan)));
  });
}

test('reviewing plan does not allow unknown schema or input inventory', () => {
  const actual = inspect(read, plan);
  assert.throws(() => verify({ ...expected, version: 2 }, actual), /version/);
  assert.throws(() => verify({ ...expected, inputs: { ...expected.inputs, hidden: 'x' } }, actual), /inventory/);
});
