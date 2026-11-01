import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, symlinkSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import ts from 'typescript';
import { sourceCompilerHost, sourceSystem } from '../src/extract/source-files.ts';
import { discoverUnits } from '../src/extract/project.ts';
import { extract } from '../src/extract/extractor.ts';

function setup(run: (root: string, base: string) => void) {
  const base = mkdtempSync(join(tmpdir(), 'ce-source-policy-'));
  const root = join(base, 'repo');
  mkdirSync(root);mkdirSync(join(base, 'repo-other'));
  writeFileSync(join(root, 'package.json'), '{"name":"boundary"}');
  writeFileSync(join(root, 'index.ts'), 'export const x = 1;');
  writeFileSync(join(base, 'repo-other/outside.ts'), 'export const external = "synthetic-outside";');
  symlinkSync(join(base, 'repo-other'), join(root, 'linked'));
  try { run(root, base); } finally { rmSync(base, {recursive:true, force:true}); }
}

test('compiler filesystem host rejects sibling-prefix and symlink paths including directory enumeration', () => setup((root, base) => {
  const host = sourceSystem(root, true);
  for (const file of [join(base, 'repo-other/outside.ts'), join(root, 'linked/outside.ts')]) {
    assert.equal(host.readFile(file), undefined);assert.equal(host.fileExists(file), false);
  }
  assert.deepEqual(host.readDirectory(root, ['.ts'], [], ['**/*']), [join(root, 'index.ts')]);
  assert.deepEqual(host.readDirectory(join(root, 'linked'), ['.ts'], [], ['**/*']), []);
  assert.equal(host.directoryExists!(join(root, 'linked')), false);
  assert.equal(host.readFile(join(root, 'index.ts')), 'export const x = 1;');
}));

test('only compiler host can read installed TypeScript builtin declarations', () => setup(root => {
  const builtin = ts.getDefaultLibFilePath({});
  assert.ok(sourceSystem(root, true).readFile(builtin));
  assert.equal(sourceSystem(root).readFile(builtin), undefined);
  assert.equal(sourceSystem(root, true).readFile(join(dirname(builtin), 'typescript.js')), undefined);
}));

test('tsconfig extends and sibling-prefix includes do not import external configuration or files', () => setup((root, base) => {
  writeFileSync(join(base, 'outside.json'), '{"compilerOptions":{"strict":true}}');
  writeFileSync(join(root, 'tsconfig.json'), '{"extends":"../outside.json","include":["**/*.ts","../repo-other/*.ts"]}');
  const units = discoverUnits(root);
  assert.equal(units.length, 1);assert.equal(units[0]!.options.strict, undefined);
  assert.ok(units[0]!.configErrors.length > 0);
  assert.deepEqual(units[0]!.rootNames, [join(root, 'index.ts')]);
}));

for (const reference of ['../repo-other/outside.ts', './linked/outside.ts']) {
  test(`blocked import and triple-slash references become diagnostic unknown: ${reference}`, () => setup(root => {
    writeFileSync(join(root, 'tsconfig.json'), '{"include":["index.ts"]}');
    writeFileSync(join(root, 'index.ts'), `/// <reference path="${reference}" />\nimport {external} from '${reference}';export const value = external;`);
    const result = extract({source:root, repository:'sample', commit:'0'.repeat(40)})[0]!.jsonl;
    assert.equal(result.includes('synthetic-outside'), false);
    assert.ok(result.includes('SOURCE_REFERENCE_UNAVAILABLE'));
  }));
}

test('compiler host directory expansion cannot enumerate symlink or sibling trees', () => setup((root, base) => {
  const host = sourceCompilerHost(root, {});
  assert.deepEqual(host.readDirectory!(join(base, 'repo-other'), ['.ts'], [], ['**/*']), []);
  assert.deepEqual(host.readDirectory!(join(root, 'linked'), ['.ts'], [], ['**/*']), []);
  assert.deepEqual(host.readDirectory!(root, ['.ts'], [], ['**/*']), [join(root, 'index.ts')]);
}));

test('symlink framework config cannot affect derived artifacts or fingerprint', () => setup((root, base) => {
  writeFileSync(join(root, 'tsconfig.json'), '{"include":["index.ts"]}');
  const outside = join(base, 'outside-config.js');
  writeFileSync(outside, 'synthetic outside version one');
  symlinkSync(outside, join(root, 'next.config.js'));
  const run = () => extract({source:root, repository:'sample', commit:'0'.repeat(40)})[0]!.jsonl;
  const first = run();
  writeFileSync(outside, 'synthetic outside version two');
  assert.equal(run(), first);
  assert.ok(first.includes('CONFIG_SOURCE_UNAVAILABLE'));
}));

test('contained framework config remains a fingerprint input', () => setup(root => {
  writeFileSync(join(root, 'tsconfig.json'), '{"include":["index.ts"]}');
  const config = join(root, 'next.config.js');
  writeFileSync(config, 'export default {poweredByHeader:false};');
  const run = () => extract({source:root, repository:'sample', commit:'0'.repeat(40)})[0]!.jsonl;
  const first = run();
  writeFileSync(config, 'export default {poweredByHeader:true};');
  assert.notEqual(run(), first);
  assert.equal(first.includes('CONFIG_SOURCE_UNAVAILABLE'), false);
}));
