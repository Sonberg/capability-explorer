import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { validateConfig, inventories, parseVersion, trivyScanArgs, ensureImage, copyCacheSnapshot, recordAttempt, inventoryFindings, dockerArgs, outputDirectory, copySealed, historyInventory, historyCheck, cosignVerifyArgs, validateCosignOutput, selectedIndexManifest, selectedChildConfig } from '../scripts/security-collect.mjs';
import { dependencySnapshot, runtimeInventory } from '../scripts/security-collect-inventory.mjs';
import { cosignPlatform, inspectSelectedImage, controlledRunner, acquisitionCacheBound, runOwnedContainer,
  standardDependencyDockerArgs } from '../scripts/lib/security-check.mjs';
import { sha } from '../scripts/security-audit-lib.mjs';
const image = `scanner:1@sha256:${'a'.repeat(64)}`;
const config = () => ({ schemaVersion: 1, tools: Object.fromEntries(['trivy', 'gitleaks', 'semgrep'].map(name => [name, { image, platform: 'linux/arm64' }])), images: [{ id: 'image-node', reference: image, platform: 'linux/arm64' }] });
function makeWritable(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory() && !entry.isSymbolicLink()) { makeWritable(file); fs.chmodSync(file, 0o700); }
    else if (!entry.isSymbolicLink()) fs.chmodSync(file, 0o600);
  }
  fs.chmodSync(directory, 0o700);
}
function temp(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'collector-'));
  t.after(() => { if (fs.existsSync(root)) { makeWritable(root); fs.rmSync(root, { recursive: true, force: true }); } });
  return root;
}
test('configuration rejects mutable tags, invalid platforms and duplicate images', () => {
  assert.equal(validateConfig(config()).images.length, 1);
  const tag = config(); tag.tools.trivy.image = 'trivy:latest'; assert.throws(() => validateConfig(tag), /unpinned/);
  const platform = config(); platform.tools.trivy.platform = 'unknown'; assert.throws(() => validateConfig(platform), /platform/);
  const duplicate = config(); duplicate.images.push(duplicate.images[0]); assert.throws(() => validateConfig(duplicate), /duplicate/);
});
test('Trivy image verification uses exact Cosign identity, issuer and immutable index digest', () => {
  assert.equal(cosignPlatform('linux', 'x64'), 'linux/amd64');
  assert.equal(cosignPlatform('linux', 'arm64'), 'linux/arm64');
  const reference = 'aquasec/trivy:0.76.1@sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c';
  const args = cosignVerifyArgs(reference, '/verified/cosign');
  assert.deepEqual(args.slice(1), ['verify', '--timeout', '60s', '--certificate-identity',
    'https://github.com/aquasecurity/trivy/.github/workflows/reusable-release.yaml@refs/tags/v0.76.1',
    '--certificate-oidc-issuer', 'https://token.actions.githubusercontent.com', reference]);
  const verified = { critical: { type: 'https://sigstore.dev/cosign/sign/v1',
    identity: { 'docker-reference': 'index.docker.io/aquasec/trivy@sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c' },
    image: { 'docker-manifest-digest': 'sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c' } } };
  assert.equal(validateCosignOutput(JSON.stringify([verified])).length, 1);
  assert.throws(() => validateCosignOutput(JSON.stringify([{ ...verified,
    critical: { ...verified.critical, image: { 'docker-manifest-digest': `sha256:${'a'.repeat(64)}` } } }])), /exact signed/);
  assert.throws(() => cosignVerifyArgs('aquasec/trivy:latest', '/verified/cosign'), /exact Trivy digest/);
});
test('containerd platform manifest identity binds the selected child without assuming image Id is a config digest', () => {
  const child = `sha256:${'c'.repeat(64)}`, index = `sha256:${'a'.repeat(64)}`;
  const image = { Id: child, Os: 'linux', Architecture: 'arm64',
    Descriptor: { mediaType: 'application/vnd.oci.image.manifest.v1+json', digest: child } };
  assert.equal(inspectSelectedImage(JSON.stringify(image), 'linux/arm64', child).childDigest, child);
  assert.throws(() => inspectSelectedImage(JSON.stringify({ ...image, Id: index }), 'linux/arm64', child), /selected index child/);
  assert.throws(() => inspectSelectedImage(JSON.stringify(image), 'linux/amd64', child), /platform mismatch/);
  assert.throws(() => inspectSelectedImage(JSON.stringify({ ...image, Descriptor: { ...image.Descriptor, digest: index } }),
    'linux/arm64', child), /selected index child/);
});
test('bounded command runner waits for termination on timeout and output overflow', async () => {
  for (const [source, options, expected] of [
    ['setInterval(() => {}, 1000)', { timeout: 100 }, /timed out/],
    ['process.stdout.write("x".repeat(4096)); setInterval(() => {}, 1000)', { maxBuffer: 1024 }, /output exceeded/],
  ]) {
    const controller = { children: new Set() }, run = controlledRunner(controller);
    await assert.rejects(run(process.execPath, ['-e', source], options), expected);
    assert.equal(controller.children.size, 0, 'terminated child must be reaped before cleanup begins');
  }
});
test('acquisition cache rejects oversized sparse data and symlinks', t => {
  const root = temp(t), file = path.join(root, 'oversized');
  const fd = fs.openSync(file, 'w'); fs.ftruncateSync(fd, 2 * 1024 * 1024 * 1024 + 1); fs.closeSync(fd);
  assert.throws(() => acquisitionCacheBound(root), /2GiB limit/);
  fs.unlinkSync(file); fs.symlinkSync('absent', file);
  assert.throws(() => acquisitionCacheBound(root), /symbolic link/);
});
test('standard launch boundary permits only isolated acquisition cache and read-only offline dependency mounts', t => {
  const root = temp(t), cache = path.join(root, 'cache'), source = path.join(root, 'source'), cid = path.join(root, 'scan.cid');
  const acquire = standardDependencyDockerArgs('acquire', [{ source: cache, target: '/cache', write: true }], cid);
  const scan = standardDependencyDockerArgs('scan', [{ source, target: '/work' }, { source: cache, target: '/cache' }], cid);
  for (const [args, network] of [[acquire, 'bridge'], [scan, 'none']]) {
    assert.equal(args[args.indexOf('--network') + 1], network);
    assert.match(args[args.indexOf('--user') + 1], /^[1-9]\d*:\d+$/, 'scanner must never run as root');
    for (const flag of ['--read-only', '--cap-drop', 'ALL', 'no-new-privileges', '--tmpfs']) assert.ok(args.includes(flag));
    for (const [flag, value] of [['--cpus', '2'], ['--memory', '2g'], ['--pids-limit', '512']])
      assert.equal(args[args.indexOf(flag) + 1], value);
  }
  assert.ok(acquire.includes(`type=bind,source=${cache},target=/cache`));
  assert.ok(!acquire.includes('/cache:rw,nosuid,size=1g,mode=1777'), 'acquisition cache must survive container exit');
  assert.equal(scan.filter(arg => arg.startsWith('type=bind,')).length, 2);
  assert.ok(scan.filter(arg => arg.startsWith('type=bind,')).every(arg => arg.endsWith(',readonly')));
  assert.throws(() => standardDependencyDockerArgs('acquire', [{ source, target: '/work' }], cid), /acquisition permits only/);
  assert.throws(() => standardDependencyDockerArgs('scan', [{ source: cache, target: '/cache', write: true }], cid), /only the isolated acquisition/);
});
test('platform selection binds the local Trivy child to exactly one verified index descriptor', () => {
  const index = JSON.stringify({ schemaVersion: 2, manifests: [
    { digest: `sha256:${'a'.repeat(64)}`, platform: { os: 'linux', architecture: 'amd64' } },
    { digest: `sha256:${'b'.repeat(64)}`, platform: { os: 'linux', architecture: 'arm64', variant: 'v8' } },
  ] });
  assert.equal(selectedIndexManifest(index, 'linux/arm64'), `sha256:${'b'.repeat(64)}`);
  assert.equal(selectedChildConfig(JSON.stringify({ schemaVersion: 2, config: { digest: `sha256:${'d'.repeat(64)}` } })),
    `sha256:${'d'.repeat(64)}`);
  assert.throws(() => selectedChildConfig(JSON.stringify({ schemaVersion: 2, config: {} })), /configuration digest/);
  assert.throws(() => selectedIndexManifest(index, 'linux/ppc64'), /does not contain/);
  assert.throws(() => selectedIndexManifest(JSON.stringify({ schemaVersion: 2, manifests: [
    { digest: `sha256:${'a'.repeat(64)}`, platform: { os: 'linux', architecture: 'arm64' } },
    { digest: `sha256:${'b'.repeat(64)}`, platform: { os: 'linux', architecture: 'arm64' } },
  ] }), 'linux/arm64'), /unambiguous/);
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
test('dependency snapshot includes untracked lockfiles and required companion manifests without private config', t => {
  const root = temp(t), files = [
    'package-lock.json', 'package.json', 'yarn.lock', 'bun.lock', 'apps/web/package.json', 'apps/web/pnpm-lock.yaml',
    'Pipfile.lock', 'pyproject.toml', 'poetry.lock', 'uv.lock', 'pylock.toml', 'requirements-dev.txt',
    'Cargo.lock', 'go.mod', 'go.sum', 'Gemfile.lock', 'composer.lock', 'vendor/composer/installed.json',
    'packages.lock.json', 'packages.config', 'Directory.Packages.props', 'legacy/Packages.props',
    'project.deps.json', 'pom.xml', 'gradle.lockfile', 'app.gradle.lockfile', 'service.sbt.lock',
    'conan.lock', 'mix.lock', 'pubspec.lock', 'Podfile', 'Podfile.lock', 'Package.resolved',
    'Manifest.toml', 'pnpm-workspace.yaml', '.gitignore',
    '.npmrc', 'config/private-settings.json',
  ];
  for (const relative of files) {
    const file = path.join(root, relative); fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, relative === '.gitignore' ? 'Cargo.lock\n' : relative);
  }
  fs.mkdirSync(path.join(root, 'node_modules/example'), { recursive: true });
  fs.writeFileSync(path.join(root, 'node_modules/example/package-lock.json'), 'excluded dependency tree');
  const source = dependencySnapshot(root);
  assert.ok(source.files.some(file => file.path === 'Cargo.lock'), 'filesystem discovery includes ignored/untracked dependency files');
  assert.ok(source.files.some(file => file.path === 'apps/web/package.json'), 'lockfile companion manifests remain sealed');
  assert.ok(!source.files.some(file => file.path === '.npmrc' || file.path === 'config/private-settings.json' ||
    file.path.startsWith('node_modules/')));
  assert.ok(source.targets.includes('apps/web/pnpm-lock.yaml'));
  assert.ok(source.targets.includes('Manifest.toml'));
  assert.ok(source.targets.includes('gradle.lockfile'));
  assert.ok(source.targets.includes('vendor/composer/installed.json'));
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


function runStandardGate(t, extraEnv = {}) {
  const root = temp(t), bin = path.join(root, 'bin'), project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const scriptRoot = path.join(root, 'checkout'), log = path.join(root, 'engine.jsonl'), tempDir = path.join(root, 'engine-tmp');
  fs.mkdirSync(path.join(scriptRoot, 'scripts/lib'), { recursive: true }); fs.mkdirSync(bin); fs.mkdirSync(tempDir);
  fs.mkdirSync(path.join(scriptRoot, 'config/security'), { recursive: true });
  fs.cpSync(path.join(project, 'scripts'), path.join(scriptRoot, 'scripts'), { recursive: true });
  fs.copyFileSync(path.join(project, 'config/security/trivy-image'), path.join(scriptRoot, 'config/security/trivy-image'));
  fs.writeFileSync(path.join(scriptRoot, 'package-lock.json'), JSON.stringify({ name: 'synthetic', lockfileVersion: 3, packages: { 'node_modules/example': { version: '1.0.0' } } }));
  fs.writeFileSync(path.join(bin, 'docker'), `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
fs.appendFileSync(process.env.TEST_ENGINE_LOG, JSON.stringify(args) + '\\n');
const operation = [...args];
while (operation[0] === '--host') operation.splice(0, 2);
if (operation[0] === 'context' && operation[1] === 'show') console.log('fixture-context');
else if (operation[0] === 'context' && operation[1] === 'inspect') console.log('unix:///fixture/docker.sock');
else if (operation[0] === 'version') console.log('29.0.0|29.0.0|linux|arm64|Docker Engine');
else if (operation[0] === 'info') console.log(operation[2] === '{{.OSType}}/{{.Architecture}}' ? 'linux/arm64' : 'fixture-daemon');
else if (operation[0] === 'image' && operation[1] === 'inspect') {
  if (process.env.TEST_IMAGE_MISSING === '1') process.exit(1);
  console.log(JSON.stringify({ Id: 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', Os: 'linux', Architecture: 'arm64', RepoDigests: ['aquasec/trivy@sha256:${'c'.repeat(64)}'] }));
} else if (operation[0] === 'manifest' && operation[1] === 'inspect') {
  process.stdout.write(operation[2].endsWith('sha256:${'c'.repeat(64)}')
    ? JSON.stringify({ schemaVersion: 2, config: { digest: process.env.TEST_CHILD_CONFIG ?? 'sha256:${'b'.repeat(64)}' } })
    : JSON.stringify({ schemaVersion: 2, manifests: [
    { digest: 'sha256:${'c'.repeat(64)}', platform: { os: 'linux', architecture: 'arm64', variant: 'v8' } },
  ] }));
} else if (operation[0] === 'pull') process.exit(Number(process.env.TEST_PULL_EXIT ?? 0));
else if (operation[0] === 'create') {
  const cidfile = operation[operation.indexOf('--cidfile') + 1], id = operation.includes('--download-db-only') ? 'a'.repeat(64) : 'b'.repeat(64);
  if (process.env.TEST_CREATE_EXIT) process.exit(Number(process.env.TEST_CREATE_EXIT));
  fs.writeFileSync(cidfile, process.env.TEST_BAD_CID === '1' ? 'invalid' : id);
  console.log(id);
} else if (operation[0] === 'start' && operation[1] === '-a') {
  if (process.env.TEST_CANCEL) {
    process.kill(process.ppid, process.env.TEST_CANCEL);
    setInterval(() => {}, 1000);
    return;
  }
  if (operation[2][0] === 'a') {
    if (Number(process.env.TEST_ACQUIRE_EXIT ?? 0) !== 0) process.exit(Number(process.env.TEST_ACQUIRE_EXIT));
    const calls = fs.readFileSync(process.env.TEST_ENGINE_LOG, 'utf8').trim().split('\\n').map(line => JSON.parse(line));
    const acquisition = calls.find(args => args.includes('--download-db-only'));
    const mount = acquisition.find(arg => arg.startsWith('type=bind,source=') && arg.endsWith('target=/cache'));
    const destination = path.join(mount.slice('type=bind,source='.length).split(',target=')[0], 'db');
    fs.mkdirSync(destination, { recursive: true });
    fs.writeFileSync(path.join(destination, 'trivy.db'), Buffer.from('synthetic opaque bbolt bytes'));
    const now = Date.now();
    fs.writeFileSync(path.join(destination, 'metadata.json'), JSON.stringify({ Version: 2, UpdatedAt: new Date(now - 60000).toISOString(),
      NextUpdate: new Date(now + 12 * 60 * 60 * 1000).toISOString(), DownloadedAt: new Date(now - 30000).toISOString() }));
    process.exit(0);
  }
  if (Number(process.env.TEST_RUN_EXIT ?? 0) !== 0) process.exit(Number(process.env.TEST_RUN_EXIT));
  if (process.env.TEST_MUTATE_DB === '1') {
    const calls = fs.readFileSync(process.env.TEST_ENGINE_LOG, 'utf8').trim().split('\\n').map(line => {
      const args = JSON.parse(line);
      while (args[0] === '--host') args.splice(0, 2);
      return args;
    });
    const scan = calls.find(args => args[0] === 'create' && args.includes('--offline-scan'));
    const mount = scan?.find(arg => arg.startsWith('type=bind,source=') && arg.endsWith('target=/cache,readonly'));
    if (mount) {
      const database = path.join(mount.slice('type=bind,source='.length).split(',target=')[0], 'db/trivy.db');
      fs.chmodSync(database, 0o600); fs.appendFileSync(database, 'mutation');
    }
  }
  process.stdout.write(JSON.stringify({ SchemaVersion: 2, Results: [{ Target: 'package-lock.json', Vulnerabilities: [] }] }));
} else if (operation[0] === 'rm' && operation[1] === '-f') process.exit(0);
`, { mode: 0o755 });
  const env = { ...process.env, ...extraEnv, PATH: `${bin}${path.delimiter}${process.env.PATH}`, TMPDIR: tempDir,
    CE_CONTAINER_ENGINE: 'docker', TEST_ENGINE_LOG: log };
  if (!env.COSIGN_BINARY) {
    env.COSIGN_BINARY = process.env.COSIGN_BINARY ?? '';
  }
  const result = spawnSync('/bin/bash', [path.join(scriptRoot, 'scripts/security-check')], { cwd: extraEnv.TEST_OUTSIDE_CWD === '1' ? root : scriptRoot, env, encoding: 'utf8', timeout: 45000 });
  const calls = fs.existsSync(log) ? fs.readFileSync(log, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line)) : [];
  const commands = calls.map(args => { const copy = [...args]; while (copy[0] === '--host') copy.splice(0, 2); return copy; });
  const outcome = { root: scriptRoot, status: result.status, stderr: result.stderr, stdout: result.stdout,
    creates: commands.filter(args => args[0] === 'create'), commands };
  makeWritable(root); fs.rmSync(root, { recursive: true, force: true });
  return outcome;
}

test('SEC104 Trivy database acquisition is source-free and resource bounded', { skip: !process.env.COSIGN_BINARY }, t => {
  const { root, creates, status, stderr, commands, stdout } = runStandardGate(t, { TEST_OUTSIDE_CWD: '1' });
  assert.equal(status, 0, `${stderr}\n${JSON.stringify(commands)}`);
  const acquisition = creates.find(args => args.includes('--download-db-only'));
  assert.ok(acquisition, 'standard gate must make database acquisition observable');
  assert.equal(acquisition[acquisition.indexOf('--network') + 1], 'bridge');
  assert.ok(acquisition.includes('--tmpfs'));
  const mounts = acquisition.filter(arg => arg.startsWith('type=bind,source='));
  assert.equal(mounts.length, 1, 'source-free acquisition receives only its isolated cache bind');
  assert.match(mounts[0], /\/acquisition-cache,target=\/cache$/);
  assert.ok(!mounts[0].endsWith(',readonly'));
  const cidfile = acquisition[acquisition.indexOf('--cidfile') + 1];
  assert.ok(acquisition.filter(arg => arg !== cidfile && arg !== mounts[0]).every(arg => !arg.includes(root)),
    'acquisition receives no checkout paths other than its cache and host-side container identity file');
  assert.ok(!commands.some(args => args[0] === 'cp'), 'database is retained by the isolated cache, not copied from an exited tmpfs');
  const receipt = JSON.parse(stdout);
  assert.ok(receipt.source.files.some(file => file.path === 'package-lock.json' && /^[a-f0-9]{64}$/.test(file.sha256)));
  assert.match(receipt.reportSha256, /^[a-f0-9]{64}$/);
  assert.equal(receipt.scanner.childDigest, `sha256:${'c'.repeat(64)}`);
  assert.equal(receipt.scanner.publisher.publisherSignatureVerified, true);
  assert.equal(receipt.database.publisherSignatureVerified, false);
  assert.equal(receipt.database.origin, 'ghcr.io/aquasecurity/trivy-db');
  assert.ok(acquisition.includes('--read-only')); assert.ok(acquisition.includes('--cap-drop'));
  assert.ok(acquisition.includes('--pids-limit')); assert.ok(acquisition.includes('--memory'));
  assert.ok(acquisition.includes('--cpus')); assert.ok(acquisition.includes('no-new-privileges'));
});

test('SEC104 standard dependency scan is offline, minimized, and preserves all-severity dev coverage', { skip: !process.env.COSIGN_BINARY }, t => {
  const { root, creates, status, stderr, commands } = runStandardGate(t);
  assert.equal(status, 0, `${stderr}\n${JSON.stringify(commands)}`);
  const scan = creates.find(args => args.includes('UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL'));
  assert.ok(scan, 'standard gate must invoke the dependency scan');
  assert.equal(scan[scan.indexOf('--network') + 1], 'none', 'dependency scanning must be offline');
  assert.ok(scan.includes('--include-dev-deps'), 'development dependencies remain in scope');
  assert.ok(scan.includes('--skip-db-update')); assert.ok(scan.includes('--offline-scan'));
  assert.equal(scan[scan.indexOf('--memory') + 1], '2g');
  assert.equal(scan[scan.indexOf('--cpus') + 1], '2');
  assert.equal(scan[scan.indexOf('--pids-limit') + 1], '512');
  assert.ok(scan.includes('--tmpfs')); assert.ok(scan.includes('/tmp:rw,nosuid,size=1g'));
  const mounts = scan.filter(arg => arg.startsWith('type=bind,source='));
  assert.equal(mounts.length, 2, 'scan receives only the sealed input and isolated database');
  assert.ok(mounts.every(mount => mount.endsWith(',readonly')));
  assert.ok(mounts.every(mount => !mount.includes(`${root},`)), 'scan must not mount the full checkout');
  assert.ok(mounts.some(mount => mount.endsWith('target=/work,readonly')));
  assert.ok(mounts.some(mount => mount.endsWith('target=/cache,readonly')));
});

test('SEC104 changed database bytes fail the post-scan identity check', { skip: !process.env.COSIGN_BINARY }, t => {
  const { status, stderr, commands } = runStandardGate(t, { TEST_MUTATE_DB: '1' });
  assert.notEqual(status, 0);
  assert.match(stderr, /database changed during offline scan/, JSON.stringify(commands));
});

test('SEC104 production launch helper owns cleanup before create and preserves primary failure', async t => {
  for (const stage of ['create', 'identity', 'start', 'timeout']) {
    const root = temp(t), calls = [], original = new Error(`expected ${stage}`); original.exitCode = stage === 'timeout' ? 124 : 19;
    const engine = async (args, _accepted, cleanup) => {
      calls.push({ args, cleanup });
      if (args[0] === 'create') {
        if (stage === 'create') throw original;
        fs.writeFileSync(args[args.indexOf('--cidfile') + 1], stage === 'identity' ? 'invalid' : 'a'.repeat(64));
      }
      if (args[0] === 'start') throw original;
      if (args[0] === 'rm' && stage === 'create') throw new Error('cleanup failure');
      return { status: 0 };
    };
    await assert.rejects(runOwnedContainer(engine, root, 'scan', ['fs'], [], 'linux/arm64'),
      error => stage === 'identity' ? /invalid container identity/.test(error.message) : error === original);
    const created = calls.find(call => call.args[0] === 'create').args;
    const removed = calls.find(call => call.args[0] === 'rm');
    assert.deepEqual(removed.args, ['rm', '-f', created[created.indexOf('--name') + 1]]);
    assert.equal(removed.cleanup, true);
  }
});
test('SEC104 rejects an unpinned verifier before contacting the engine', t => {
  const { status, stderr, commands } = runStandardGate(t, { COSIGN_BINARY: process.execPath });
  assert.notEqual(status, 0);
  assert.match(stderr, /hash-pinned Cosign|bounded executable/);
  assert.equal(commands.length, 0);
});
test('SEC104 production launch helper cleans up after real child cancellation', async t => {
  for (const [signal, expected] of [['SIGTERM', 143], ['SIGINT', 130]]) {
    const root = temp(t), calls = [], controller = { children: new Set() }, run = controlledRunner(controller);
    const engine = async (args, _accepted, cleanup) => {
      calls.push({ args, cleanup });
      if (args[0] === 'create') fs.writeFileSync(args[args.indexOf('--cidfile') + 1], 'a'.repeat(64));
      if (args[0] === 'start') {
        const timer = setTimeout(() => {
          controller.signal = signal;
          for (const child of controller.children) child.kill(signal);
        }, 100);
        try { return await run(process.execPath, ['-e', 'setInterval(() => {}, 1000)']); }
        finally { clearTimeout(timer); }
      }
      return { status: 0 };
    };
    await assert.rejects(runOwnedContainer(engine, root, 'scan', ['fs'], [], 'linux/arm64'), error => error.exitCode === expected);
    assert.equal(controller.children.size, 0);
    assert.ok(calls.some(call => call.args[0] === 'rm' && call.cleanup), `${signal}: missing owned-container cleanup`);
  }
});
