import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { validateConfig, inventories, dockerArgs, outputDirectory, copySealed, historyInventory, historyCheck } from '../scripts/security-collect.mjs';
import { sha } from '../scripts/security-audit-lib.mjs';
const image = `scanner:1@sha256:${'a'.repeat(64)}`;
const config = () => ({ schemaVersion: 1, tools: Object.fromEntries(['trivy', 'gitleaks', 'semgrep'].map(name => [name, { image, platform: 'linux/arm64' }])), images: [{ id: 'image-node', reference: image, platform: 'linux/arm64' }] });
function temp(t) { const root = fs.mkdtempSync(path.join(os.tmpdir(), 'collector-')); t.after(() => fs.rmSync(root, { recursive: true, force: true })); return root; }
test('configuration rejects mutable tags, invalid platforms and duplicate images', () => {
  assert.equal(validateConfig(config()).images.length, 1);
  const tag = config(); tag.tools.trivy.image = 'trivy:latest'; assert.throws(() => validateConfig(tag), /unpinned/);
  const platform = config(); platform.tools.trivy.platform = 'unknown'; assert.throws(() => validateConfig(platform), /platform/);
  const duplicate = config(); duplicate.images.push(duplicate.images[0]); assert.throws(() => validateConfig(duplicate), /duplicate/);
});
test('scanner containers receive no Docker socket, credentials or network by default', () => {
  const args = dockerArgs({ ...config().tools.trivy, name: 'trivy' }, [{ source: '/tmp/sealed', target: '/source' }]);
  assert.equal(args[args.indexOf('--network') + 1], 'none');
  assert.ok(args.includes('--read-only')); assert.ok(args.includes('ALL')); assert.ok(args.includes('no-new-privileges'));
  assert.ok(args.includes('type=bind,source=/tmp/sealed,target=/source,readonly'));
  assert.ok(!args.join(' ').includes('docker.sock')); assert.ok(!args.join(' ').includes('TOKEN'));
  assert.throws(() => dockerArgs(config().tools.trivy, [{ source: '/tmp/bad,readonly=false', target: '/source' }]), /mount/);
});
test('inventory includes all lockfiles and test source, while labels unsupported configuration explicitly', () => {
  const inv = inventories(['package-lock.json', 'nested/packages.lock.json', 'tests/example.ts', '.github/workflows/ci.yml', 'Dockerfile', 'infra/app.tf', 'scripts/analyze'].map(path => ({ path })));
  assert.deepEqual(inv.dependencies, ['nested/packages.lock.json', 'package-lock.json']);
  assert.ok(inv.sast.includes('tests/example.ts')); assert.ok(inv.sast.includes('scripts/analyze'));
  assert.deepEqual(inv.misconfiguration, ['Dockerfile', 'infra/app.tf']);
  assert.ok(inv.unsupportedConfiguration.includes('.github/workflows/ci.yml'));
});
test('output rejects stale directory reuse, traversal and symbolic-link ancestors', t => {
  const root = temp(t); outputDirectory(root, '.sandbox-cache/new');
  assert.throws(() => outputDirectory(root, '.sandbox-cache/new'), /existing/);
  assert.throws(() => outputDirectory(root, '.sandbox-cache/../escape'), /inside/);
  fs.symlinkSync(os.tmpdir(), path.join(root, '.sandbox-cache/link'));
  assert.throws(() => outputDirectory(root, '.sandbox-cache/link/new'), /symlink/);
});
test('sealed source copy rejects changed bytes and symlink replacement', t => {
  const root = temp(t), destination = path.join(root, 'sealed'); fs.mkdirSync(destination);
  fs.writeFileSync(path.join(root, 'source.ts'), 'safe');
  const source = { files: [{ path: 'source.ts', sha256: sha('old') }] };
  assert.throws(() => copySealed(root, destination, source), /changed/);
  fs.unlinkSync(path.join(destination, 'source.ts')); fs.unlinkSync(path.join(root, 'source.ts'));
  fs.symlinkSync('/etc/passwd', path.join(root, 'source.ts'));
  assert.throws(() => copySealed(root, destination, source), /non-file/);
});
const gitRunner = (revision = 'a'.repeat(40), shallow = false) => (_command, args) => {
  const text = args.includes('--is-shallow-repository') ? String(shallow) : args.includes('for-each-ref') ? `refs/heads/stage ${revision}\n` : args.includes('--count') ? '3' : revision;
  return { status: 0, stdout: text, stderr: '' };
};
test('history inventory rejects shallow repositories', () => { assert.throws(() => historyInventory('/work', gitRunner(undefined, true)), /shallow/); });
test('fresh history check detects changed branch tips independently of the manifest', t => {
  const root = temp(t), history = JSON.stringify(historyInventory(root, gitRunner())); fs.writeFileSync(path.join(root, 'history.json'), history);
  fs.writeFileSync(path.join(root, 'manifest.json'), JSON.stringify({ history: { path: 'history.json', sha256: sha(history) } }));
  historyCheck(root, 'manifest.json', gitRunner());
  assert.throws(() => historyCheck(root, 'manifest.json', gitRunner('b'.repeat(40))), /changed/);
});
