import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { inventories } from '../scripts/security-collect-inventory.mjs';
import { sha, snapshot, validate, findings, safeFile, validateTrivyDatabase, validateDependencyReport } from '../scripts/security-audit-lib.mjs';
const image = `scanner:1@sha256:${'a'.repeat(64)}`;
function fixture(t, vulnerability = false) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'security-audit-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, 'config/security'), { recursive: true });
  fs.copyFileSync('config/security/audit-scope.json', path.join(root, 'config/security/audit-scope.json'));
  fs.writeFileSync(path.join(root, 'source.ts'), 'export const x = 1;');
  fs.writeFileSync(path.join(root, 'package-lock.json'), JSON.stringify({ name: 'fixture', lockfileVersion: 3, packages: { 'node_modules/example': { version: '1' } } }));
  fs.writeFileSync(path.join(root, 'Dockerfile'), 'FROM scratch\n');
  fs.writeFileSync(path.join(root, 'config/security/collector.json'), JSON.stringify({ schemaVersion: 1, tools: Object.fromEntries(['trivy', 'gitleaks', 'semgrep'].map(name => [name, { image, platform: 'linux/arm64' }])), images: [{ id: 'image-runtime', reference: image, platform: 'linux/arm64' }] }));
  fs.mkdirSync(path.join(root, 'config/runtime'), { recursive: true });
  fs.writeFileSync(path.join(root, 'config/runtime/images.env'), ['DOTNET', 'NODE', 'PG', 'OLLAMA', 'ACT', 'BROWSER', 'RYUK'].map(role => `${role}_IMAGE=${image}`).join('\n'));
  fs.writeFileSync(path.join(root, 'config/security/trivy-image'), image);
  for (const relative of ['scripts/sandbox', 'scripts/local-check', 'Makefile', '.github/workflows/ci.yml', 'platform/tests/CapabilityExplorer.Store.Tests/ImporterTests.cs']) {
    fs.mkdirSync(path.dirname(path.join(root, relative)), { recursive: true }); fs.writeFileSync(path.join(root, relative), '');
  }
  const write = (name, value) => {
    const relative = `.sandbox-cache/${name}`;
    fs.mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
    const data = typeof value === 'string' ? value : JSON.stringify(value);
    fs.writeFileSync(path.join(root, relative), data);
    return { path: relative, sha256: sha(data) };
  };
  const source = snapshot(root), now = Date.now();
  const history = write('history.json', { refs: [{ name: 'refs/heads/stage', sha: 'a'.repeat(40) }], commitCount: 3, shallow: false });
  const manifest = { schemaVersion: 1, collectionCompleted: true, source: write('source.json', source), history, images: [{ id: 'image-runtime', reference: image, platform: 'linux/arm64' }], scans: [] };
  for (const [id, tool] of Object.entries({ dependencies: 'trivy', 'fs-secrets': 'trivy', 'fs-misconfig': 'trivy', 'history-secrets': 'gitleaks', 'source-secrets': 'gitleaks', sast: 'semgrep', 'image-runtime': 'trivy' })) {
    const raw = tool === 'trivy' ? { SchemaVersion: 2, Results: [{ Target: id === 'dependencies' ? 'package-lock.json' : 'source.ts', ...(vulnerability && id === 'dependencies' ? { Vulnerabilities: [{ VulnerabilityID: 'CVE-TEST', PkgName: 'example', InstalledVersion: '1', Severity: 'LOW' }] } : {}) }] }
      : tool === 'semgrep' ? { results: [], errors: [], paths: { scanned: inventories(source.files).sast } } : [];
    manifest.scans.push({ id, tool: { name: tool, version: '1.0.0', image, platform: 'linux/arm64' }, rules: write(`${id}-rules.txt`, 'reviewed-rules'), targets: write(`${id}-targets.json`, id === 'image-runtime' ? [image] : id === 'dependencies' ? ['package-lock.json'] : id === 'fs-misconfig' ? ['Dockerfile'] : id === 'sast' ? inventories(source.files).sast : id === 'history-secrets' ? [`refs/heads/stage@${'a'.repeat(40)}`] : source.files.map(x => x.path)), coverage: [id], skipped: 0, sourceSha256: source.sha256, completedAt: new Date(now).toISOString(), exitCode: 0, completed: true, executionLog: write(`${id}-log.txt`, 'scanner completed: 1 target'), report: write(`${id}-report.json`, raw), database: { sha256: 'b'.repeat(64), version: '2', updatedAt: new Date(now).toISOString() }, severities: ['UNKNOWN', 'LOW', 'MEDIUM', 'HIGH', 'CRITICAL'], includeDevDependencies: id === 'dependencies' ? true : undefined, packageScope: id === 'image-runtime' ? 'installed-os-and-language-packages' : undefined, historySha256: history.sha256, targetImage: image, targetPlatform: 'linux/arm64' });
  }
  manifest.manualReview = write('manual.json', { completed: true, reviewer: 'human-reviewer', limitations: ['Synthetic test only'], findings: [], sourceSha256: source.sha256, configurationReviewedTargets: inventories(source.files).unsupportedConfiguration });
  const current = manifest.scans.flatMap(scan => findings(scan, JSON.parse(fs.readFileSync(path.join(root, scan.report.path)))));
  const ledger = { schemaVersion: 1, findings: current.map(x => ({ ...x, disposition: 'confirmed-open', owner: 'reviewer', rationale: 'Synthetic regression example' })) };
  manifest.ledger = write('ledger.json', ledger);
  return { root, manifest, ledger, write, now };
}
test('complete baseline is collection success, not a vulnerability pass', t => {
  const f = fixture(t, true);
  assert.equal(validate(f.root, f.manifest, 'baseline', f.now).confirmedOpen, 1);
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /unresolved confirmed/);
});
test('complete clean collection passes verification', t => {
  const f = fixture(t); assert.equal(validate(f.root, f.manifest, 'verify', f.now).scans, 7);
});
test('Trivy bbolt database identity requires schema 2, acquisition metadata and a current update window', t => {
  const f = fixture(t), directory = path.join(f.root, 'database'); fs.mkdirSync(directory);
  const database = path.join(directory, 'trivy.db'), metadata = path.join(directory, 'metadata.json');
  fs.writeFileSync(database, Buffer.from('synthetic opaque bbolt bytes'));
  const now = Date.now(), valid = { Version: 2, UpdatedAt: new Date(now - 60_000).toISOString(),
    NextUpdate: new Date(now + 12 * 60 * 60 * 1000).toISOString(), DownloadedAt: new Date(now - 30_000).toISOString() };
  fs.writeFileSync(metadata, JSON.stringify(valid));
  const readFile = fs.readFileSync;
  t.mock.method(fs, 'readFileSync', (file, ...args) => {
    assert.notEqual(file, database, 'database hashing must not allocate the whole database');
    return readFile(file, ...args);
  });
  const identity = validateTrivyDatabase(directory, now);
  assert.equal(identity.downloadedAt, valid.DownloadedAt);
  assert.equal(identity.sha256, sha(Buffer.from('synthetic opaque bbolt bytes')));
  for (const invalid of [
    { ...valid, UpdatedAt: new Date(now + 1).toISOString() },
    { ...valid, NextUpdate: valid.UpdatedAt },
    { ...valid, NextUpdate: new Date(now - 1).toISOString() },
    { ...valid, UpdatedAt: new Date(now - 24 * 60 * 60 * 1000).toISOString() },
    { ...valid, DownloadedAt: undefined },
    { ...valid, DownloadedAt: new Date(now + 1).toISOString() },
    { ...valid, DownloadedAt: new Date(now - 60_001).toISOString() },
    { ...valid, Version: 999 },
  ]) {
    fs.writeFileSync(metadata, JSON.stringify(invalid));
    assert.throws(() => validateTrivyDatabase(directory, now), /timestamp|schema version/);
  }
  fs.writeFileSync(metadata, JSON.stringify(valid));
  const fd = fs.openSync(database, 'w');
  fs.ftruncateSync(fd, 2 * 1024 * 1024 * 1024 + 1); fs.closeSync(fd);
  assert.throws(() => validateTrivyDatabase(directory, now), /exceeds size limit/);
});
test('dependency report covers lockfile targets while companion manifests stay sealed inputs', t => {
  const f = fixture(t);
  const source = { files: [{ path: 'package-lock.json' }, { path: 'package.json' }], targets: ['package-lock.json'] };
  const report = { SchemaVersion: 2, Results: [{ Target: 'package-lock.json', Vulnerabilities: [
    { VulnerabilityID: 'CVE-UNKNOWN', PkgName: 'example', InstalledVersion: '1', Severity: 'UNKNOWN' },
    { VulnerabilityID: 'CVE-CRITICAL', PkgName: 'example', InstalledVersion: '1', Severity: 'CRITICAL' },
  ] }] };
  assert.equal(validateDependencyReport(f.root, report, source).findings.length, 2);
  assert.throws(() => validateDependencyReport(f.root, { SchemaVersion: 2, Results: [] }, source), /omitted inventoried input/);
  report.Results[0].Vulnerabilities[1].Severity = 'UNSPECIFIED';
  assert.throws(() => validateDependencyReport(f.root, report, source), /unsupported severity/);
});
for (const [name, mutate, error] of [
  ['missing scanner', f => f.manifest.scans.pop(), /missing/],
  ['duplicate scanner', f => f.manifest.scans[1] = f.manifest.scans[0], /duplicate/],
  ['source edit', f => fs.writeFileSync(path.join(f.root, 'source.ts'), 'changed'), /source mismatch/],
  ['new untracked source', f => fs.writeFileSync(path.join(f.root, 'untracked.ts'), 'new'), /source mismatch/],
  ['stale scan', f => f.manifest.scans[0].completedAt = new Date(f.now - 73 * 3600000).toISOString(), /stale/],
  ['stale database', f => f.manifest.scans[0].database.updatedAt = new Date(f.now - 25 * 3600000).toISOString(), /stale/],
  ['database missing', f => delete f.manifest.scans[0].database, /database/],
  ['scanner crash', f => f.manifest.scans[0].exitCode = 2, /failed/],
  ['incomplete scanner', f => f.manifest.scans[0].completed = false, /failed/],
  ['skipped target', f => f.manifest.scans[0].skipped = 1, /incomplete/],
  ['empty target inventory', f => f.manifest.scans[0].targets = f.write('targets-empty.json', []), /empty/],
  ['lower severity excluded', f => f.manifest.scans[0].severities = ['HIGH', 'CRITICAL'], /severity/],
  ['image scope missing', f => delete f.manifest.scans.find(x => x.id === 'image-runtime').packageScope, /dependency/],
  ['dev dependencies excluded', f => f.manifest.scans[0].includeDevDependencies = false, /dependency/],
  ['raw report tampering', f => fs.appendFileSync(path.join(f.root, f.manifest.scans[0].report.path), ' '), /digest mismatch/],
  ['empty Trivy result coverage', f => f.manifest.scans[0].report = f.write('empty-trivy.json', { SchemaVersion: 2, Results: [] }), /empty Trivy/],
  ['Semgrep errors', f => f.manifest.scans[5].report = f.write('error-semgrep.json', { results: [], errors: [{ message: 'parse failed' }], paths: { scanned: ['source.ts'] } }), /Semgrep/],
  ['image identity mismatch', f => f.manifest.scans[6].targetImage = 'different', /image target/],
  ['shallow history', f => f.manifest.history = f.write('shallow.json', { refs: [{ name: 'main', sha: 'a'.repeat(40) }], commitCount: 2, shallow: true }), /incomplete history/]
]) test(`rejects ${name}`, t => { const f = fixture(t); mutate(f); assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), error); });
test('untriaged finding blocks baseline', t => {
  const f = fixture(t, true); f.manifest.ledger = f.write('empty-ledger.json', { schemaVersion: 1, findings: [] });
  assert.throws(() => validate(f.root, f.manifest, 'baseline', f.now), /untriaged/);
});
test('blanket allowlist is rejected', t => {
  const f = fixture(t, true); f.ledger.findings[0].allowlist = '*'; f.manifest.ledger = f.write('allowlist.json', f.ledger);
  assert.throws(() => validate(f.root, f.manifest, 'baseline', f.now), /blanket/);
});
test('false positive requires independent evidence', t => {
  const f = fixture(t, true); Object.assign(f.ledger.findings[0], { disposition: 'false-positive', reviewer: 'reviewer' }); f.manifest.ledger = f.write('fp.json', f.ledger);
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /independent/);
  Object.assign(f.ledger.findings[0], { reviewer: 'independent', evidence: f.write('review.txt', 'Documented exact synthetic false positive') }); f.manifest.ledger = f.write('fp.json', f.ledger);
  assert.equal(validate(f.root, f.manifest, 'verify', f.now).currentFindings, 1);
});
test('fixed-but-still-present finding fails', t => {
  const f = fixture(t, true); Object.assign(f.ledger.findings[0], { disposition: 'fixed', fixCommit: 'a'.repeat(40), retest: { ...f.write('retest.txt', 'passed'), sourceSha256: f.manifest.scans[0].sourceSha256 } }); f.manifest.ledger = f.write('fixed.json', f.ledger);
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /persists/);
});
test('source symlinks and evidence traversal fail', t => {
  const f = fixture(t); fs.symlinkSync('/etc/passwd', path.join(f.root, 'link'));
  assert.throws(() => snapshot(f.root), /symlink/);
  assert.throws(() => safeFile(f.root, '../outside'), /unsafe/);
});
test('scope cannot hide arbitrary source folders', t => {
  const f = fixture(t), file = path.join(f.root, 'config/security/audit-scope.json');
  const p = JSON.parse(fs.readFileSync(file)); p.excludedPaths.push('src'); fs.writeFileSync(file, JSON.stringify(p));
  assert.throws(() => snapshot(f.root), /unreviewed source exclusion/);
});
test('invalid raw Trivy finding array is rejected rather than discarded', t => {
  const f = fixture(t); f.manifest.scans[0].report = f.write('invalid.json', { SchemaVersion: 2, Results: [{ Target: 'package-lock.json', Vulnerabilities: {} }] });
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /finding array/);
});
test('manual review finding blocks clean scanner verification until fixed and retested', t => {
  const f = fixture(t), finding = { id: sha('manual-B01'), externalId: 'B01', location: 'source.ts:1', severity: 'HIGH' };
  f.manifest.manualReview = f.write('manual.json', { completed: true, reviewer: 'security-reviewer', limitations: [], findings: [finding], sourceSha256: f.manifest.scans[0].sourceSha256, configurationReviewedTargets: inventories(snapshot(f.root).files).unsupportedConfiguration });
  f.ledger.findings.push({ ...finding, disposition: 'confirmed-open', owner: 'developer', rationale: 'Boundary reproduction' });
  f.manifest.ledger = f.write('ledger.json', f.ledger);
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /unresolved confirmed/);
  Object.assign(f.ledger.findings[0], { disposition: 'fixed', fixCommit: 'c'.repeat(40), retest: { ...f.write('manual-retest.txt', 'Regression passed'), sourceSha256: f.manifest.scans[0].sourceSha256 } });
  f.manifest.ledger = f.write('ledger.json', f.ledger);
  assert.equal(validate(f.root, f.manifest, 'verify', f.now).confirmedOpen, 0);
});
test('a full source hash cannot conceal a missing lockfile target', t => {
  const f = fixture(t); f.manifest.scans[0].targets = f.write('wrong-targets.json', ['source.ts']);
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /discovered target/);
});
test('raw vulnerability report must cover every nonempty discovered lockfile', t => {
  const f = fixture(t); f.manifest.scans[0].report = f.write('wrong-report.json', { SchemaVersion: 2, Results: [{ Target: 'source.ts' }] });
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /omitted discovered lockfile/);
});
test('manifest cannot silently remove a configured image or swap scanner identity', t => {
  const f = fixture(t); f.manifest.images = [];
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /missing image/);
  f.manifest.images = [{ id: 'image-runtime', reference: image, platform: 'linux/arm64' }];
  f.manifest.scans[0].tool.image = `other:2@sha256:${'c'.repeat(64)}`;
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /configured tool/);
});
test('real empty Trivy secret results require sealed execution evidence', t => {
  const f = fixture(t), scan = f.manifest.scans[1], source = JSON.parse(fs.readFileSync(path.join(f.root, f.manifest.source.path)));
  scan.report = f.write('clean-secret.json', { SchemaVersion: 2, ArtifactName: '.', ArtifactType: 'filesystem' });
  scan.coverageReceipt = { kind: 'sealed-tree-invocation', fileCount: source.files.length, sourceSha256: source.sha256 };
  scan.executionLog = f.write('secret-execution.json', { command: 'docker', exitCode: 0, args: ['run', '--network', 'none', '--scanners', 'secret'], stderr: 'Secret scanning is enabled' });
  assert.equal(validate(f.root, f.manifest, 'verify', f.now).confirmedOpen, 0);
  scan.coverageReceipt.fileCount = 0;
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /empty Trivy/);
});
test('presence of a Dockerfile cannot waive manual review of unsupported configuration', t => {
  const f = fixture(t), manual = JSON.parse(fs.readFileSync(path.join(f.root, f.manifest.manualReview.path)));
  manual.configurationReviewedTargets = [];
  f.manifest.manualReview = f.write('manual-incomplete.json', manual);
  assert.throws(() => validate(f.root, f.manifest, 'verify', f.now), /unsupported-configuration coverage/);
});

test('only exact generated memory files are excluded and runtime writes do not invalidate source', t => {
  const f = fixture(t), dir = path.join(f.root, '.swarm'); fs.mkdirSync(dir);
  for (const name of ['config.json', 'worker.ts', 'unknown.db']) fs.writeFileSync(path.join(dir, name), 'source');
  const before = snapshot(f.root);
  for (const name of ['agentdb-memory.db', 'memory.db']) for (const suffix of ['', '.ruvector', '-wal', '-shm'])
    fs.writeFileSync(path.join(dir, name + suffix), 'runtime state');
  assert.equal(snapshot(f.root).sha256, before.sha256);
  fs.writeFileSync(path.join(dir, 'agentdb-memory.db'), 'updated runtime state');
  assert.equal(snapshot(f.root).sha256, before.sha256);
  for (const name of ['config.json', 'worker.ts', 'unknown.db']) assert.ok(before.files.some(x => x.path === `.swarm/${name}`));
  fs.writeFileSync(path.join(dir, 'config.json'), 'changed config');
  assert.notEqual(snapshot(f.root).sha256, before.sha256);
});
test('generated exclusions cannot hide directories, symlinks or arbitrary .swarm files', t => {
  const f = fixture(t), dir = path.join(f.root, '.swarm'); fs.mkdirSync(dir);
  const generated = path.join(dir, 'memory.db'); fs.mkdirSync(generated);
  assert.throws(() => snapshot(f.root), /regular file/);
  fs.rmdirSync(generated); fs.symlinkSync(path.join(f.root, 'source.ts'), generated);
  assert.throws(() => snapshot(f.root), /regular file/);
  fs.unlinkSync(generated);
  const file = path.join(f.root, 'config/security/audit-scope.json'), policy = JSON.parse(fs.readFileSync(file));
  policy.excludedGeneratedFiles.push('.swarm/config.json'); fs.writeFileSync(file, JSON.stringify(policy));
  assert.throws(() => snapshot(f.root), /unreviewed generated/);
});

test('only the reviewed LikeC4 build directory is excluded from source identity', t => {
  const f = fixture(t), before = snapshot(f.root);
  fs.mkdirSync(path.join(f.root, 'spikes/likec4/dist/assets'), { recursive: true });
  fs.writeFileSync(path.join(f.root, 'spikes/likec4/dist/index.html'), 'generated');
  fs.writeFileSync(path.join(f.root, 'spikes/likec4/dist/assets/bundle.js'), 'generated');
  assert.equal(snapshot(f.root).sha256, before.sha256);
  for (const name of ['other/dist/source.js', 'spikes/likec4/src/source.ts']) {
    fs.mkdirSync(path.dirname(path.join(f.root, name)), { recursive: true });
    fs.writeFileSync(path.join(f.root, name), 'source');
    assert.ok(snapshot(f.root).files.some(x => x.path === name));
  }
});
