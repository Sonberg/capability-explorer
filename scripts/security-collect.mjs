import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { snapshot, sha, readJson, findings } from './security-audit-lib.mjs';
import { requireDockerRuntime } from './lib/docker-only.mjs';

const need = (ok, message) => { if (!ok) throw new Error(message); };
export { validateConfig, inventories } from './security-collect-inventory.mjs';
import { validateConfig, inventories, runtimeInventory } from './security-collect-inventory.mjs';
export function trivyScanArgs(mode) {
  need(['fs', 'image'].includes(mode), 'unsupported Trivy scan mode');
  const common = ['--cache-dir', '/cache', '--skip-db-update', '--skip-check-update', '--offline-scan', '--skip-version-check', '--secret-config', '', '--disable-telemetry', '--config', '', '--ignorefile', '/rules/empty-ignore', '--format', 'json', '--list-all-pkgs', '--severity', 'UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL', '--exit-code', '0'];
  return [mode, ...common, ...(mode === 'fs' ? ['--include-dev-deps'] : [])];
}
export function dockerArgs(tool, mounts, network = 'none') {
  const args = ['run', '--rm', '--pull=missing', '--platform', tool.platform, '--network', network, '--user', `${process.getuid?.() ?? 1000}:${process.getgid?.() ?? 1000}`, '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--pids-limit', '512', '--memory', '2g', '--cpus', '2', '--tmpfs', '/tmp:rw,nosuid,size=1g', '-e', 'HOME=/tmp', '-e', 'SEMGREP_SEND_METRICS=off', '-e', 'SEMGREP_ENABLE_VERSION_CHECK=0', '-e', 'GIT_CONFIG_NOSYSTEM=1', '-e', 'GIT_CONFIG_GLOBAL=/dev/null'];
  if (tool.name) { need(['trivy', 'gitleaks', 'semgrep'].includes(tool.name), 'invalid tool entrypoint'); args.push('--entrypoint', tool.name); }
  for (const mount of mounts) {
    need(path.isAbsolute(mount.source) && !mount.source.includes(',') && /^\/[a-z][a-z/-]*$/.test(mount.target), 'invalid container mount');
    args.push('--mount', `type=bind,source=${mount.source},target=${mount.target}${mount.write ? '' : ',readonly'}`);
  }
  return args;
}
export function outputDirectory(root, relative) {
  need(typeof relative === 'string' && relative.startsWith('.sandbox-cache/') && !relative.split(/[\\/]/).includes('..') && !path.isAbsolute(relative), 'output must be a new directory inside .sandbox-cache');
  const full = path.resolve(root, relative);
  let cursor = root;
  for (const part of path.relative(root, full).split(path.sep)) {
    cursor = path.join(cursor, part);
    if (fs.existsSync(cursor)) need(!fs.lstatSync(cursor).isSymbolicLink(), 'output ancestor symlink');
  }
  need(!fs.existsSync(full), 'refuse existing output directory: prevents stale report reuse');
  fs.mkdirSync(full, { recursive: true, mode: 0o700 }); return full;
}
export function copySealed(root, destination, source) {
  need(source.files.length <= 100_000, 'source file limit exceeded');
  let bytes = 0;
  for (const entry of source.files) {
    const input = path.resolve(root, entry.path), output = path.resolve(destination, entry.path);
    need(input.startsWith(root + path.sep) && output.startsWith(destination + path.sep), 'unsafe snapshot path');
    const stat = fs.lstatSync(input); need(stat.isFile() && !stat.isSymbolicLink(), 'source changed to non-file');
    bytes += stat.size; need(bytes <= 512 * 1024 * 1024, 'source byte limit exceeded');
    fs.mkdirSync(path.dirname(output), { recursive: true }); fs.copyFileSync(input, output, fs.constants.COPYFILE_EXCL);
    need(sha(fs.readFileSync(output)) === entry.sha256, 'source changed while sealing');
  }
}
export function ensureImage(reference, targetPlatform, run, record) {
  requireDockerRuntime('full audit image preparation');
  need(typeof reference === 'string' && /^[\w./:-]+@sha256:[a-f0-9]{64}$/.test(reference) && /^linux\/(arm64|amd64)$/.test(targetPlatform), 'image acquisition requires a digest pin and explicit platform');
  const invoke = (suffix, args) => { const result = run('docker', args); record(suffix, { command: 'docker', args, ...result }); return result; };
  let inspected = invoke('inspect-before', ['image', 'inspect', reference]);
  if (inspected.status !== 0) {
    const pulled = invoke('pull', ['pull', '--platform', targetPlatform, reference]);
    need(pulled.status === 0, 'pinned image acquisition failed');
    inspected = invoke('inspect-after', ['image', 'inspect', reference]);
  }
  need(inspected.status === 0, 'pinned image unavailable after bounded acquisition');
  const metadata = JSON.parse(inspected.stdout)[0];
  need(metadata && /^sha256:[a-f0-9]{64}$/.test(metadata.Id) && `${metadata.Os}/${metadata.Architecture}` === targetPlatform, 'acquired image identity/platform mismatch');
  return metadata;
}
export function copyCacheSnapshot(source, destination) {
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    need(!entry.isSymbolicLink(), 'cache snapshot rejects symlinks');
    const from = path.join(source, entry.name), to = path.join(destination, entry.name);
    if (entry.isDirectory()) { fs.mkdirSync(to, { recursive: true }); copyCacheSnapshot(from, to); }
    else { need(entry.isFile(), 'cache snapshot rejects non-files'); fs.copyFileSync(from, to, fs.constants.COPYFILE_FICLONE); }
  }
}
export function recordAttempt(manifest, id, action, save) {
  try { action(); return true; }
  catch (error) {
    manifest.collectionErrors.push({ id, error: error.message });
    const scan = manifest.scans.find(x => x.id === id);
    if (scan) { scan.completed = false; scan.error = error.message; }
    save(); return false;
  }
}
export function inventoryFindings(scan, raw) {
  // Retain usable findings from a partially parsed Semgrep report, explicitly
  // labelled partial. The original report is unchanged and the audit still fails.
  if (scan.tool.name === 'semgrep' && Array.isArray(raw.errors) && raw.errors.length && Array.isArray(raw.results))
    return findings(scan, { ...raw, errors: [] }).map(row => ({ ...row, partial: true }));
  try { return findings(scan, raw); }
  catch (error) {
    // A recorded failed scan is not a clean scan. Preserve its raw report and
    // collection error while allowing other scanners' findings to be finalized.
    if (scan.completed === false && typeof scan.error === 'string' && scan.error.length) return [];
    throw error;
  }
}
export function parseVersion(stdout) {
  const version = /^(?:Version:\s*)?v?(\d+\.\d+\.\d+)\s*$/m.exec(stdout)?.[1];
  need(version, 'missing actual scanner version'); return version;
}
const fileHash = async file => {
  const hash = crypto.createHash('sha256'); for await (const chunk of fs.createReadStream(file)) hash.update(chunk); return hash.digest('hex');
};
function runner(command, args, options = {}) {
  const result = spawnSync(command, args, { encoding: 'utf8', timeout: 20 * 60 * 1000, maxBuffer: 64 * 1024 * 1024, ...options });
  if (result.error) throw result.error;
  return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}
export function historyInventory(root, run = runner) {
  const git = args => { const r = run('git', ['-C', root, ...args]); need(r.status === 0, 'Git history inventory failed'); return r.stdout.trim(); };
  need(git(['rev-parse', '--is-shallow-repository']) === 'false', 'shallow history cannot satisfy audit');
  const refs = git(['for-each-ref', '--format=%(refname) %(objectname)']).split('\n').filter(Boolean).map(x => { const [name, sha] = x.split(' '); return { name, sha }; });
  refs.push({ name: 'HEAD', sha: git(['rev-parse', 'HEAD']) }); refs.sort((a, b) => a.name.localeCompare(b.name, 'en'));
  return { refs, commitCount: Number(git(['rev-list', '--all', 'HEAD', '--count'])), shallow: false };
}
export function historyCheck(root, manifestPath, run = runner) {
  const manifest = readJson(root, manifestPath), prior = readJson(root, manifest.history.path);
  need(sha(fs.readFileSync(path.join(root, manifest.history.path))) === manifest.history.sha256, 'history evidence hash mismatch');
  need(JSON.stringify(historyInventory(root, run)) === JSON.stringify(prior), 'reachable Git history changed since collection');
}
export async function collect(root, relative, ledgerPath, manualPath, run = runner) {
  requireDockerRuntime('full source/history/image security audit collection');
  root = fs.realpathSync(root);
  const config = validateConfig(readJson(root, 'config/security/collector.json'));
  const runtimeImages = runtimeInventory(root, config);
  const out = outputDirectory(root, relative), sourceDir = path.join(out, 'source'), reportsDir = path.join(out, 'reports'), rulesDir = path.join(out, 'rules'), cacheDir = path.join(out, 'cache');
  for (const directory of [sourceDir, reportsDir, rulesDir, cacheDir]) fs.mkdirSync(directory);
  const artifact = file => ({ path: path.relative(root, file).split(path.sep).join('/'), sha256: sha(fs.readFileSync(file)) });
  const write = (name, data) => { const file = path.join(out, name); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, typeof data === 'string' ? data : JSON.stringify(data, null, 2) + '\n'); return artifact(file); };
  const source = snapshot(root), inventory = inventories(source.files); copySealed(root, sourceDir, source);
  const history = historyInventory(root, run);
  const manifest = { schemaVersion: 1, source: write('source.json', source), history: write('history.json', history), inventory: write('inventory.json', inventory), runtimeImages: write('runtime-images.json', runtimeImages), images: config.images, scans: [], collectionErrors: [], collectionCompleted: false };
  write('manifest.json', manifest); // An interrupted run remains visibly incomplete.
  const checked = (command, args, log, accepted = [0]) => {
    const result = run(command, args); const receipt = write(log, JSON.stringify({ command, args, exitCode: result.status, stdout: result.stdout, stderr: result.stderr }, null, 2));
    need(accepted.includes(result.status), `${command} failed (${result.status}); inspect ${log}`); return { ...result, receipt };
  };
  const tools = {};
  for (const [name, tool] of Object.entries(config.tools)) {
    const metadata = ensureImage(tool.image, tool.platform, run, (suffix, result) => write(`reports/${name}-${suffix}.json`, result));
    const args = dockerArgs({ ...tool, name }, []);
    const version = checked('docker', [...args, tool.image, ...(name === 'gitleaks' ? ['version'] : ['--version'])], `reports/${name}-version.json`);
    const parsed = parseVersion(version.stdout);
    tools[name] = { name, ...tool, version: parsed, imageId: metadata.Id };
  }
  // Shared download cache avoids re-fetching a fresh DB on retries. Every run
  // receives its own byte-bound snapshot after the scanner's normal update check.
  const sharedCache = path.join(root, '.sandbox-cache/security-trivy-cache');
  fs.mkdirSync(sharedCache, { recursive: true, mode: 0o700 });
  need(!fs.lstatSync(sharedCache).isSymbolicLink(), 'shared scanner cache symlink');
  const priorCache = path.join(root, '.sandbox-cache/trivy');
  if (!fs.existsSync(path.join(sharedCache, 'db/metadata.json')) && fs.existsSync(priorCache)) copyCacheSnapshot(priorCache, sharedCache);
  // Only the pinned Trivy image can reach the network here, without source/history mounts.
  checked('docker', [...dockerArgs(tools.trivy, [{ source: sharedCache, target: '/cache', write: true }], 'bridge'), tools.trivy.image, 'image', '--download-db-only', '--cache-dir', '/cache', '--disable-telemetry'], 'reports/database-download.log');
  // Download built-in checks separately, without exposing repository bytes to a networked scanner.
  checked('docker', [...dockerArgs(tools.trivy, [{ source: sharedCache, target: '/cache', write: true }], 'bridge'), tools.trivy.image, 'fs', '--scanners', 'misconfig', '--cache-dir', '/cache', '--disable-telemetry', '--config', '', '--ignorefile', '', '/tmp'], 'reports/checks-download.log');
  copyCacheSnapshot(sharedCache, cacheDir);
  const databasePath = path.join(cacheDir, 'db/trivy.db'), dbMetadata = JSON.parse(fs.readFileSync(path.join(cacheDir, 'db/metadata.json')));
  const database = { sha256: await fileHash(databasePath), version: String(dbMetadata.Version), updatedAt: dbMetadata.UpdatedAt };
  write('database.json', { ...database, metadata: dbMetadata });
  const checkFiles = [];
  const walkChecks = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      const file = path.join(directory, entry.name), relative = path.relative(cacheDir, file);
      if (relative === 'db' || relative === 'java-db') continue;
      need(!entry.isSymbolicLink(), 'checks cache symlink');
      if (entry.isDirectory()) walkChecks(file);
      else if (entry.isFile()) checkFiles.push({ path: relative, sha256: sha(fs.readFileSync(file)) });
    }
  };
  walkChecks(cacheDir); need(checkFiles.length > 0, 'missing downloaded misconfiguration rules');
  const checksBundle = write('checks-bundle.json', checkFiles);
  fs.copyFileSync(path.join(root, 'config/security/semgrep-rules.yaml'), path.join(rulesDir, 'semgrep.yaml'));
  fs.writeFileSync(path.join(rulesDir, 'gitleaks.toml'), '[extend]\nuseDefault = true\n'); fs.writeFileSync(path.join(rulesDir, 'empty-ignore'), '');
  const mounts = [{ source: sourceDir, target: '/source' }, { source: reportsDir, target: '/reports', write: true }, { source: rulesDir, target: '/rules' }, { source: cacheDir, target: '/cache', write: true }];
  const addScan = (id, name, arguments_, targets, extra = {}) => {
    const tool = tools[name], args = [...dockerArgs(tool, mounts), '-w', '/source', tool.image, ...arguments_];
    const result = checked('docker', args, `reports/${id}.log`, name === 'trivy' ? [0] : [0, 1]);
    const file = path.join(reportsDir, `${id}.json`); need(fs.existsSync(file), `${id}: scanner did not create report`);
    const raw = JSON.parse(fs.readFileSync(file));
    const scan = { id, tool, rules: write(`rules/${id}-invocation.json`, { arguments: arguments_, sourceRules: name === 'semgrep' ? artifact(path.join(rulesDir, 'semgrep.yaml')) : name === 'gitleaks' ? artifact(path.join(rulesDir, 'gitleaks.toml')) : { image: tool.image, checksBundle }, disabledInputSuppression: true }), targets: write(`reports/${id}-targets.json`, targets), coverage: [id], skipped: 0, sourceSha256: source.sha256, completedAt: new Date().toISOString(), completed: true, exitCode: result.status, executionLog: result.receipt, report: artifact(file), ...extra };
    manifest.scans.push(scan); write('manifest.json', manifest);
    findings(scan, raw); // Reject malformed or partial scanner formats immediately.
    if (name === 'semgrep') need(targets.every(x => raw.paths.scanned.includes(x)), 'Semgrep omitted required targets');
    if (name === 'gitleaks') {
      need(/scanned\s+~?[\d.]+\s+bytes/.test(result.stderr) && !/\b(?:ERR|FTL|fatal)\b/.test(result.stderr), `${id}: scanner completion missing or errors reported`);
      need(!/\b(?:skipping|skipped)\b/i.test(result.stderr), `${id}: scanner reports skipped coverage`);
    }
    write('manifest.json', manifest);
  };
  const attemptScan = (id, ...args) => recordAttempt(manifest, id, () => addScan(id, ...args), () => write('manifest.json', manifest));

  need(inventory.dependencies.length && inventory.sast.length, 'required dependency/SAST scope is empty');
  attemptScan('dependencies', 'trivy', [...trivyScanArgs('fs'), '--scanners', 'vuln', '--output', '/reports/dependencies.json', '.'], inventory.dependencies, { database, severities: ['UNKNOWN', 'LOW', 'MEDIUM', 'HIGH', 'CRITICAL'], includeDevDependencies: true });
  attemptScan('fs-secrets', 'trivy', [...trivyScanArgs('fs'), '--scanners', 'secret', '--output', '/reports/fs-secrets.json', '.'], inventory.source, { coverageReceipt: { kind: 'sealed-tree-invocation', fileCount: inventory.source.length, sourceSha256: source.sha256 } });
  attemptScan('fs-misconfig', 'trivy', [...trivyScanArgs('fs'), '--scanners', 'misconfig', '--misconfig-scanners', 'dockerfile,terraform', '--include-non-failures', '--output', '/reports/fs-misconfig.json', '.'], inventory.misconfiguration, { applicability: inventory.misconfiguration.length ? 'applicable' : 'not-applicable', coverageReceipt: { kind: 'sealed-tree-invocation', fileCount: inventory.source.length, sourceSha256: source.sha256 }, unsupportedTargets: inventory.unsupportedConfiguration });
  const leak = ['--config', '/rules/gitleaks.toml', '--gitleaks-ignore-path', '/rules/empty-ignore', '--ignore-gitleaks-allow', '--redact=100', '--no-banner', '--no-color', '--report-format', 'json', '--timeout', '600'];
  attemptScan('source-secrets', 'gitleaks', ['dir', ...leak, '--report-path', '/reports/source-secrets.json', '.'], inventory.source);
  const historyDir = path.join(out, 'history.git');
  checked('git', ['clone', '--mirror', '--no-hardlinks', root, historyDir], 'reports/history-clone.log');
  need(JSON.stringify(historyInventory(root, run)) === JSON.stringify(history), 'history changed during snapshot');
  need(JSON.stringify(historyInventory(historyDir, run)) === JSON.stringify(history), 'sealed history refs or commits differ');
  mounts.push({ source: historyDir, target: '/history' });
  attemptScan('history-secrets', 'gitleaks', ['git', ...leak, '--log-opts=--all --full-history', '--report-path', '/reports/history-secrets.json', '/history'], history.refs.map(x => `${x.name}@${x.sha}`), { historySha256: manifest.history.sha256 });
  const semgrepRoot = path.join(out, 'semgrep-source'); fs.mkdirSync(semgrepRoot); copySealed(root, semgrepRoot, source);
  // Only the SAST copy changes suppression controls; secret scans saw original bytes.
  for (const entry of source.files) if (path.basename(entry.path) === '.semgrepignore') fs.unlinkSync(path.join(semgrepRoot, entry.path));
  fs.writeFileSync(path.join(semgrepRoot, '.semgrepignore'), '');
  mounts[0] = { source: semgrepRoot, target: '/source' };
  attemptScan('sast', 'semgrep', ['scan', '--config', '/rules/semgrep.yaml', '--metrics', 'off', '--disable-version-check', '--disable-nosem', '--no-git-ignore', '--max-target-bytes', '0', '--json', '--output', '/reports/sast.json', ...inventory.sast], inventory.sast);
  mounts[0] = { source: sourceDir, target: '/source' };
  for (const image of config.images) {
    try {
    const info = ensureImage(image.reference, image.platform, run, (suffix, result) => write(`reports/${image.id}-${suffix}.json`, result));
    const archiveDirectory = path.join(root, '.sandbox-cache/security-image-archives'); fs.mkdirSync(archiveDirectory, { recursive: true });
    need(!fs.lstatSync(archiveDirectory).isSymbolicLink(), 'archive cache symlink');
    const archiveName = `${image.reference.split('@sha256:')[1]}-${image.platform.replace('/', '-')}.tar`;
    const archive = path.join(archiveDirectory, archiveName), identityFile = `${archive}.json`;
    if (fs.existsSync(archive) && fs.existsSync(identityFile)) {
      const cached = JSON.parse(fs.readFileSync(identityFile));
      need(cached.imageId === info.Id && cached.sha256 === await fileHash(archive), 'image archive cache identity mismatch');
      write(`reports/${image.id}-export.log`, { reused: true, ...cached });
    } else {
      checked('docker', ['image', 'save', '--output', archive, image.reference], `reports/${image.id}-export.log`);
      fs.writeFileSync(identityFile, JSON.stringify({ imageId: info.Id, sha256: await fileHash(archive), reference: image.reference, platform: image.platform }));
    }
    const archiveHash = await fileHash(archive);
    mounts.push({ source: archiveDirectory, target: '/archives' });
    attemptScan(image.id, 'trivy', [...trivyScanArgs('image'), '--scanners', 'vuln', '--input', `/archives/${archiveName}`, '--output', `/reports/${image.id}.json`], [image.reference], { targetImage: image.reference, targetPlatform: image.platform, archiveSha256: archiveHash, database, severities: ['UNKNOWN', 'LOW', 'MEDIUM', 'HIGH', 'CRITICAL'], packageScope: 'installed-os-and-language-packages' });
    mounts.pop(); // Reuse exact image bytes on later DB snapshots; no daemon socket reaches scanners.
    } catch (error) {
      if (mounts.at(-1)?.target === '/archives') mounts.pop();
      manifest.collectionErrors.push({ id: image.id, error: error.message }); write('manifest.json', manifest);
    }
  }
  need(snapshot(root).sha256 === source.sha256, 'source changed during collection');
  need(JSON.stringify(historyInventory(root, run)) === JSON.stringify(history), 'history changed during collection');
  need(await fileHash(databasePath) === database.sha256, 'database changed during scan');
  manifest.collectionCompleted = manifest.collectionErrors.length === 0;
  if (ledgerPath) { readJson(root, ledgerPath); manifest.ledger = artifact(path.join(root, ledgerPath)); }
  if (manualPath) { readJson(root, manualPath); manifest.manualReview = artifact(path.join(root, manualPath)); }
  write('manifest.json', manifest);
  const all = manifest.scans.flatMap(scan => inventoryFindings(scan, readJson(root, scan.report.path)));
  write('findings.json', all);
  return { manifest: `${relative}/manifest.json`, findings: all.length, collectionErrors: manifest.collectionErrors, triageAttached: Boolean(ledgerPath && manualPath) };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [command, ...args] = process.argv.slice(2);
    if (command === 'history-check' && args.length === 1) historyCheck(process.cwd(), args[0]);
    else if (command === 'run' && [1, 3].includes(args.length)) { const result = await collect(process.cwd(), args[0], args[1], args[2]); console.log(JSON.stringify(result, null, 2)); if (result.collectionErrors.length) process.exitCode = 1; }
    else throw new Error('usage: security-collect run <new-cache-directory> [ledger.json manual-review.json] | history-check <manifest.json>');
  } catch (error) { console.error(`security-collect: ${error.message}`); process.exitCode = 1; }
}
