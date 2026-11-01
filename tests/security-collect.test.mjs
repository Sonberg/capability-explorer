import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { validateConfig, inventories, parseVersion, trivyScanArgs, ensureImage, copyCacheSnapshot, recordAttempt, inventoryFindings, dockerArgs, outputDirectory, copySealed, historyInventory, historyCheck } from '../scripts/security-collect.mjs';
import { runtimeInventory } from '../scripts/security-collect-inventory.mjs';
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
  assert.ok(inv.sast.includes('tests/example.ts')); assert.ok(!inv.sast.includes('scripts/analyze')); assert.ok(inv.unsupportedSast.includes('scripts/analyze'));
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

function runtimeFixture(t) {
  const root = temp(t), cfg = config();
  for (const file of ['config/runtime/images.env', 'config/security/trivy-image', 'scripts/sandbox', 'scripts/local-check', 'Makefile', '.github/workflows/ci.yml', 'platform/tests/CapabilityExplorer.Store.Tests/ImporterTests.cs']) {
    fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), '');
  }
  fs.writeFileSync(path.join(root, 'config/runtime/images.env'), ['DOTNET', 'NODE', 'PG', 'OLLAMA', 'ACT', 'BROWSER', 'RYUK'].map(role => `${role}_IMAGE=${image}`).join('\n'));
  fs.writeFileSync(path.join(root, 'config/security/trivy-image'), image);
  return { root, cfg };
}
test('runtime inventory requires every runtime and scanner image independently of configured scan list', t => {
  const f = runtimeFixture(t); assert.equal(runtimeInventory(f.root, f.cfg).references.length, 1);
  const different = `node:2@sha256:${'b'.repeat(64)}`;
  fs.appendFileSync(path.join(f.root, 'config/runtime/images.env'), `\nEXTRA_IMAGE=${different}`);
  assert.throws(() => runtimeInventory(f.root, f.cfg), /omits runtime/);
});
test('missing runtime role, stale hardcoded image and changed scanner platform fail', t => {
  const f = runtimeFixture(t), registry = path.join(f.root, 'config/runtime/images.env'), original = fs.readFileSync(registry, 'utf8');
  fs.writeFileSync(registry, original.replace(/^RYUK_IMAGE=.*$/m, ''));
  assert.throws(() => runtimeInventory(f.root, f.cfg), /missing required runtime/);
  fs.writeFileSync(registry, original); fs.writeFileSync(path.join(f.root, 'Makefile'), `BROWSER=browser:2@sha256:${'b'.repeat(64)}`);
  assert.throws(() => runtimeInventory(f.root, f.cfg), /unregistered image/);
  fs.writeFileSync(path.join(f.root, 'Makefile'), ''); f.cfg.tools.semgrep.platform = 'linux/amd64';
  assert.throws(() => runtimeInventory(f.root, f.cfg), /scanner image platform/);
});
test('runtime env files require manual configuration review', () => {
  assert.ok(inventories([{ path: 'config/runtime/images.env' }]).unsupportedConfiguration.includes('config/runtime/images.env'));
});
test('split Make profiles retain image inventory and manual-review coverage', t => {
  const f=runtimeFixture(t), directory=path.join(f.root,'config/build');fs.mkdirSync(directory,{recursive:true});
  fs.writeFileSync(path.join(directory,'sandbox.mk'),`BROWSER=browser:2@sha256:${'b'.repeat(64)}`);
  assert.throws(()=>runtimeInventory(f.root,f.cfg),/unregistered image/);
  assert.ok(inventories([{path:'config/build/sandbox.mk'}]).unsupportedConfiguration.includes('config/build/sandbox.mk'));
});

test('actual Gitleaks v-prefixed output and Trivy/Semgrep versions parse without inventing version', () => {
  assert.equal(parseVersion('v8.30.1\n'), '8.30.1');
  assert.equal(parseVersion('Version: 0.76.1\n'), '0.76.1');
  assert.equal(parseVersion('1.183.0\n'), '1.183.0');
  assert.throws(() => parseVersion('unable to execute scanner'), /missing actual/);
});

test('a failed scan is retained while independent scans still run, without marking completion', () => {
  const manifest = { scans: [{ id: 'sast', completed: true }], collectionErrors: [], collectionCompleted: false };
  let saved = 0, independentRan = false;
  assert.equal(recordAttempt(manifest, 'sast', () => { throw new Error('partial parser'); }, () => saved++), false);
  assert.equal(recordAttempt(manifest, 'image-node', () => { independentRan = true; }, () => saved++), true);
  assert.equal(independentRan, true); assert.equal(manifest.scans[0].completed, false);
  assert.equal(manifest.collectionCompleted, false); assert.equal(manifest.collectionErrors.length, 1); assert.equal(saved, 1);
});
test('partial Semgrep findings retain their partial label and original raw errors', () => {
  const raw = { errors: [{ type: 'PartialParsing' }], results: [{ check_id: 'sink', path: 'app.cs', start: { line: 1 }, extra: { severity: 'WARNING' } }], paths: { scanned: ['app.cs'] } };
  const rows = inventoryFindings({ id: 'sast', tool: { name: 'semgrep' } }, raw);
  assert.equal(rows[0].partial, true); assert.equal(raw.errors.length, 1);
});

test('missing cached pinned image is acquired once then its actual identity/platform verified', () => {
  const calls = [], receipts = [];
  const run = (command, args) => { calls.push([command, ...args]); return { status: calls.length === 1 ? 1 : 0, stdout: args[0] === 'image' && calls.length > 1 ? JSON.stringify([{ Id: `sha256:${'b'.repeat(64)}`, Os: 'linux', Architecture: 'arm64' }]) : '', stderr: '' }; };
  assert.equal(ensureImage(image, 'linux/arm64', run, (...receipt) => receipts.push(receipt)).Architecture, 'arm64');
  assert.equal(calls.filter(x => x[1] === 'pull').length, 1); assert.equal(receipts.length, 3);
  assert.deepEqual(calls[1], ['docker', 'pull', '--platform', 'linux/arm64', image]);
  assert.throws(() => ensureImage('scanner:latest', 'linux/arm64', run, () => {}), /digest pin/);
});
test('image acquisition is bounded and rejects wrong-platform cache contents', () => {
  let attempts = 0;
  assert.throws(() => ensureImage(image, 'linux/arm64', () => { attempts++; return { status: 1, stdout: '', stderr: 'unavailable' }; }, () => {}), /acquisition failed/);
  assert.equal(attempts, 2);
  assert.throws(() => ensureImage(image, 'linux/arm64', () => ({ status: 0, stdout: JSON.stringify([{ Id: `sha256:${'b'.repeat(64)}`, Os: 'linux', Architecture: 'amd64' }]) }), () => {}), /platform mismatch/);
  assert.ok(dockerArgs(config().tools.trivy, []).includes('--pull=missing'));
});
test('per-run database snapshot preserves bytes without writable sharing and rejects symlinks', t => {
  const root = temp(t), source = path.join(root, 'source'), target = path.join(root, 'snapshot');
  fs.mkdirSync(source); fs.mkdirSync(target); fs.writeFileSync(path.join(source, 'db'), 'original');
  copyCacheSnapshot(source, target); fs.writeFileSync(path.join(source, 'db'), 'changed');
  assert.equal(fs.readFileSync(path.join(target, 'db'), 'utf8'), 'original');
  fs.symlinkSync('/etc/passwd', path.join(source, 'link'));
  assert.throws(() => copyCacheSnapshot(source, target), /symlink/);
});

test('filesystem dev dependency flag is never sent to image scanner', () => {
  assert.ok(trivyScanArgs('fs').includes('--include-dev-deps'));
  assert.ok(!trivyScanArgs('image').includes('--include-dev-deps'));
  assert.equal(trivyScanArgs('image')[0], 'image');
  assert.ok(trivyScanArgs('image').includes('--list-all-pkgs'));
  assert.throws(() => trivyScanArgs('unknown'), /unsupported/);
});

test('unsupported image inventory preserves explicit failure while finalizing other findings', () => {
  const raw = { SchemaVersion: 2, ArtifactType: 'container_image', Metadata: {} };
  const scan = { id: 'image-ryuk', tool: { name: 'trivy' }, completed: true };
  const manifest = { scans: [scan], collectionErrors: [], collectionCompleted: false };
  assert.equal(recordAttempt(manifest, scan.id, () => inventoryFindings(scan, raw), () => {}), false);
  assert.match(scan.error, /unsupported image package inventory coverage/);
  assert.equal(scan.completed, false);
  assert.deepEqual(inventoryFindings(scan, raw), []);
  assert.equal(manifest.collectionErrors.length, 1);
  assert.equal(manifest.collectionCompleted, false);
  assert.equal(raw.Results, undefined);
  assert.throws(() => inventoryFindings({ ...scan, completed: true }, raw), /unsupported image/);
  assert.throws(() => inventoryFindings({ ...scan, error: '' }, raw), /unsupported image/);
});
