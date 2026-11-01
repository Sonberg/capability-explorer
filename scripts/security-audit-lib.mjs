import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { inventories, validateConfig, runtimeInventory } from './security-collect-inventory.mjs';

export const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const fail = message => { throw new Error(message); };
const need = (condition, message) => { if (!condition) fail(message); };
const nonempty = value => typeof value === 'string' && value.trim().length > 0;
const hash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const digest = value => typeof value === 'string' && /@sha256:[a-f0-9]{64}$/.test(value);
export function safeFile(root, relative) {
  need(nonempty(relative) && !path.isAbsolute(relative) && !relative.split(/[\\/]/).includes('..'), `unsafe path: ${relative}`);
  const base = fs.realpathSync(root), file = fs.realpathSync(path.resolve(base, relative));
  need(file.startsWith(base + path.sep) && fs.statSync(file).isFile(), `path escapes root or not file: ${relative}`);
  return file;
}
export const readJson = (root, file) => JSON.parse(fs.readFileSync(safeFile(root, file), 'utf8'));
export function policy(root) {
  const p = readJson(root, 'config/security/audit-scope.json');
  need(p.schemaVersion === 1 && nonempty(p.exclusionReason), 'invalid audit scope');
  const allowedDirectories = ['.git', '.sandbox-cache', 'node_modules', 'bin', 'obj', 'TestResults'];
  const allowedPaths = ['fixtures/.repos', 'fixtures/.work', 'platform/src/CapabilityExplorer.Explorer/wwwroot'];
  need(Array.isArray(p.excludedDirectoryNames) && p.excludedDirectoryNames.every(x => allowedDirectories.includes(x)) &&
    Array.isArray(p.excludedPaths) && p.excludedPaths.every(x => allowedPaths.includes(x)), 'unreviewed source exclusion');
  need(p.maximumScanAgeHours > 0 && p.maximumScanAgeHours <= 72 && p.maximumDatabaseAgeHours > 0 && p.maximumDatabaseAgeHours <= 24, 'unsafe freshness policy');
  for (const [id, tool] of Object.entries({ dependencies: 'trivy', 'fs-secrets': 'trivy', 'fs-misconfig': 'trivy', 'history-secrets': 'gitleaks', 'source-secrets': 'gitleaks', sast: 'semgrep' }))
    need(p.requiredCoverage?.[id] === tool, `required coverage removed: ${id}`);
  return p;
}
export function snapshot(root, p = policy(root)) {
  const files = [];
  function walk(dir, prefix = '') {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      const name = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (p.excludedDirectoryNames.includes(entry.name) && (entry.isDirectory() || entry.name === '.git')) continue;
      if (p.excludedPaths.some(x => name === x || name.startsWith(`${x}/`))) continue;
      need(!entry.isSymbolicLink(), `source symlink requires explicit review: ${name}`);
      if (entry.isDirectory()) walk(path.join(dir, entry.name), name);
      else if (entry.isFile()) files.push({ path: name, sha256: sha(fs.readFileSync(path.join(dir, entry.name))) });
      else fail(`unsupported source entry: ${name}`);
    }
  }
  walk(root);
  need(files.length > 0, 'empty source');
  return { schemaVersion: 1, sha256: sha(JSON.stringify(files)), files };
}
function artifact(root, item, label) {
  need(item && hash(item.sha256), `missing ${label} digest`);
  const file = safeFile(root, item.path);
  need(sha(fs.readFileSync(file)) === item.sha256, `${label} digest mismatch`);
  return file;
}
const freshness = (value, maximum, now, label) => {
  const timestamp = Date.parse(value);
  need(Number.isFinite(timestamp) && timestamp <= now + 300000 && now - timestamp <= maximum * 3600000, `${label} stale or invalid`);
};
export function findings(scan, raw) {
  let rows;
  if (scan.tool.name === 'trivy') {
    const emptySupported = ['fs-secrets', 'fs-misconfig'].includes(scan.id) && scan.coverageReceipt?.kind === 'sealed-tree-invocation' &&
      typeof raw.ArtifactName === 'string' && raw.ArtifactType === 'filesystem';
    need(raw.SchemaVersion === 2 && (Array.isArray(raw.Results) || (emptySupported && raw.Results === undefined)), `${scan.id}: invalid Trivy JSON`);
    const results = raw.Results ?? [];
    need(!raw.Errors?.length, `${scan.id}: Trivy scan errors`);
    for (const result of results) {
      need(nonempty(result.Target), `${scan.id}: missing Trivy target`);
      for (const field of ['Vulnerabilities', 'Secrets', 'Misconfigurations'])
        need(result[field] === undefined || Array.isArray(result[field]), `${scan.id}: invalid Trivy finding array`);
    }
    rows = results.flatMap(result => [
      ...(result.Vulnerabilities ?? []).map(x => ({ rule: x.VulnerabilityID, location: `${result.Target}:${x.PkgName}@${x.InstalledVersion}`, severity: x.Severity })),
      ...(result.Secrets ?? []).map(x => ({ rule: x.RuleID, location: `${result.Target}:${x.StartLine}`, severity: x.Severity })),
      ...(result.Misconfigurations ?? []).filter(x => x.Status !== 'PASS').map(x => ({ rule: x.ID, location: `${result.Target}:${x.CauseMetadata?.StartLine ?? 0}`, severity: x.Severity }))
    ]);
  } else if (scan.tool.name === 'gitleaks') {
    need(Array.isArray(raw), `${scan.id}: invalid Gitleaks JSON`);
    rows = raw.map(x => ({ rule: x.RuleID, location: `${x.File}:${x.StartLine}:${x.Commit ?? ''}`, severity: 'UNKNOWN' }));
  } else if (scan.tool.name === 'semgrep') {
    need(Array.isArray(raw.results) && Array.isArray(raw.errors) && raw.errors.length === 0, `${scan.id}: invalid/error Semgrep JSON`);
    need(Array.isArray(raw.paths?.scanned) && raw.paths.scanned.length > 0, `${scan.id}: no Semgrep targets`);
    need(!raw.paths.skipped?.length, `${scan.id}: skipped Semgrep targets`);
    rows = raw.results.map(x => ({ rule: x.check_id, location: `${x.path}:${x.start?.line}`, severity: x.extra?.severity ?? 'UNKNOWN' }));
  } else fail(`unsupported scanner: ${scan.tool.name}`);
  return rows.map(row => {
    need(nonempty(row.rule) && nonempty(row.location), `${scan.id}: malformed finding`);
    return { id: sha(JSON.stringify([scan.id, row.rule, row.location])), scanId: scan.id, ...row };
  });
}
export function validate(root, manifest, mode, now = Date.now()) {
  const p = policy(root);
  need(manifest.schemaVersion === 1 && Array.isArray(manifest.scans) && Array.isArray(manifest.images) && manifest.images.length > 0, 'invalid manifest or missing image inventory');
  need(['baseline', 'verify'].includes(mode), 'unknown audit mode');
  need(manifest.collectionCompleted === true, 'incomplete collection');
  const config = validateConfig(readJson(root, 'config/security/collector.json'));
  runtimeInventory(root, config);
  need(JSON.stringify(manifest.images) === JSON.stringify(config.images), 'configured image inventory mismatch');
  const current = snapshot(root, p), inventory = inventories(current.files);
  const saved = JSON.parse(fs.readFileSync(artifact(root, manifest.source, 'source snapshot'), 'utf8'));
  need(saved.sha256 === current.sha256 && JSON.stringify(saved.files) === JSON.stringify(current.files), 'source mismatch');
  const history = JSON.parse(fs.readFileSync(artifact(root, manifest.history, 'history inventory'), 'utf8'));
  need(Array.isArray(history.refs) && history.refs.length > 0 && history.refs.every(x => nonempty(x.name) && /^[a-f0-9]{40,64}$/.test(x.sha)) && Number.isInteger(history.commitCount) && history.commitCount > 0 && history.shallow === false, 'incomplete history inventory');
  const expected = new Map(Object.entries(p.requiredCoverage));
  for (const image of manifest.images) {
    need(digest(image.reference) && /^(linux)\/(amd64|arm64)$/.test(image.platform), 'invalid image identity');
    need(!expected.has(image.id), 'duplicate image ID');
    expected.set(image.id, 'trivy');
  }
  need(manifest.scans.length === expected.size, 'missing or duplicate scan coverage');
  const all = [], seen = new Set();
  for (const scan of manifest.scans) {
    need(!seen.has(scan.id) && expected.get(scan.id) === scan.tool?.name, `unexpected/duplicate scanner: ${scan.id}`); seen.add(scan.id);
    need(nonempty(scan.tool.version) && digest(scan.tool.image) && /^(linux)\/(amd64|arm64)$/.test(scan.tool.platform), `${scan.id}: missing tool identity`);
    artifact(root, scan.rules, `${scan.id} rules`);
    const targets = JSON.parse(fs.readFileSync(artifact(root, scan.targets, `${scan.id} targets`), 'utf8'));
    const notApplicable = scan.id === 'fs-misconfig' && inventory.misconfiguration.length === 0 && scan.applicability === 'not-applicable';
    need(Array.isArray(targets) && (targets.length > 0 || notApplicable) && targets.every(nonempty) && new Set(targets).size === targets.length, `${scan.id}: empty/invalid target inventory`);
    const expectedTargets = scan.id === 'dependencies' ? inventory.dependencies : scan.id === 'sast' ? inventory.sast :
      ['fs-secrets', 'source-secrets'].includes(scan.id) ? inventory.source : scan.id === 'fs-misconfig' ? inventory.misconfiguration :
      scan.id === 'history-secrets' ? history.refs.map(x => `${x.name}@${x.sha}`) : [manifest.images.find(x => x.id === scan.id)?.reference];
    need(JSON.stringify([...targets].sort()) === JSON.stringify([...expectedTargets].sort()), `${scan.id}: discovered target inventory mismatch`);
    const configured = config.tools[scan.tool.name];
    need(scan.tool.image === configured.image && scan.tool.platform === configured.platform, `${scan.id}: configured tool identity mismatch`);
    need(Array.isArray(scan.coverage) && scan.coverage.includes(scan.id) && scan.skipped === 0, `${scan.id}: incomplete coverage`);
    need(scan.sourceSha256 === saved.sha256, `${scan.id}: stale source identity`);
    freshness(scan.completedAt, p.maximumScanAgeHours, now, `${scan.id} scan`);
    need([0, 1].includes(scan.exitCode) && scan.completed === true, `${scan.id}: scanner failed`);
    artifact(root, scan.executionLog, `${scan.id} execution log`);
    if (scan.id === 'dependencies' || manifest.images.some(x => x.id === scan.id)) {
      need(scan.database && hash(scan.database.sha256) && nonempty(scan.database.version), `${scan.id}: missing database identity`);
      freshness(scan.database.updatedAt, p.maximumDatabaseAgeHours, now, `${scan.id} database`);
      need(scan.severities?.join(',') === 'UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL' && (scan.id === 'dependencies' ? scan.includeDevDependencies === true : scan.packageScope === 'installed-os-and-language-packages'), `${scan.id}: incomplete severity/dependency coverage`);
    }
    if (scan.id === 'history-secrets') need(scan.historySha256 === manifest.history.sha256, 'history scan mismatch');
    const image = manifest.images.find(x => x.id === scan.id);
    if (image) need(scan.targetImage === image.reference && scan.targetPlatform === image.platform && targets.includes(image.reference), `${scan.id}: image target mismatch`);
    const raw = JSON.parse(fs.readFileSync(artifact(root, scan.report, `${scan.id} report`), 'utf8'));
    const found = findings(scan, raw);
    if (scan.tool.name === 'trivy' && !image && !(raw.Results?.length)) {
      const covered = scan.coverageReceipt;
      need(['fs-secrets', 'fs-misconfig'].includes(scan.id) && covered?.kind === 'sealed-tree-invocation', `${scan.id}: empty Trivy coverage`);
      const log = JSON.parse(fs.readFileSync(artifact(root, scan.executionLog, `${scan.id} execution log`), 'utf8'));
      need(['fs-secrets', 'fs-misconfig'].includes(scan.id) && covered?.kind === 'sealed-tree-invocation' &&
        covered.fileCount === inventory.source.length && covered.sourceSha256 === saved.sha256 &&
        log.command === 'docker' && log.exitCode === 0 && Array.isArray(log.args) && log.args.includes('--network') &&
        log.args[log.args.indexOf('--network') + 1] === 'none' && log.args.includes('--scanners') &&
        log.args[log.args.indexOf('--scanners') + 1] === (scan.id === 'fs-secrets' ? 'secret' : 'misconfig'), `${scan.id}: empty Trivy coverage`);
      if (scan.id === 'fs-misconfig') need(notApplicable && /Detected config files[^\n]*num=0/.test(log.stderr), 'misconfig zero-target evidence missing');
    }
    if (scan.id === 'dependencies') {
      const scanned = new Set((raw.Results ?? []).map(x => x.Target));
      need(targets.every(x => {
        if (scanned.has(x)) return true;
        // NuGet emits dependency-free project locks; Trivy legitimately omits these.
        if (!x.endsWith('/packages.lock.json') && x !== 'packages.lock.json') return false;
        const lock = readJson(root, x);
        return lock.version === 2 && lock.dependencies && Object.keys(lock.dependencies).length > 0 &&
          Object.values(lock.dependencies).every(group => group && typeof group === 'object' && Object.keys(group).length === 0);
      }), 'dependency report omitted discovered lockfile');
    }
    if (scan.tool.name === 'semgrep') need(targets.every(x => raw.paths.scanned.includes(x)), `${scan.id}: missing scanned target`);
    all.push(...found);
  }
  const manual = JSON.parse(fs.readFileSync(artifact(root, manifest.manualReview, 'manual review'), 'utf8'));
  need(manual.completed === true && nonempty(manual.reviewer) && Array.isArray(manual.limitations) && Array.isArray(manual.findings), 'incomplete manual review');
  need(
    JSON.stringify([...(manual.configurationReviewedTargets ?? [])].sort()) === JSON.stringify(inventory.unsupportedConfiguration) &&
    manual.sourceSha256 === saved.sha256, 'manual unsupported-configuration coverage missing');
  for (const finding of manual.findings) {
    need(hash(finding.id) && nonempty(finding.externalId) && nonempty(finding.location) && nonempty(finding.severity), 'invalid manual finding');
    all.push({ ...finding, scanId: 'manual' });
  }
  const ledger = JSON.parse(fs.readFileSync(artifact(root, manifest.ledger, 'finding ledger'), 'utf8'));
  need(ledger.schemaVersion === 1 && Array.isArray(ledger.findings), 'invalid findings ledger');
  const entries = new Map();
  for (const entry of ledger.findings) {
    need(hash(entry.id) && !entries.has(entry.id), 'duplicate/invalid ledger ID'); entries.set(entry.id, entry);
    need(['confirmed-open', 'fixed', 'false-positive'].includes(entry.disposition) && nonempty(entry.owner) && nonempty(entry.rationale), `untriaged finding: ${entry.id}`);
    need(!entry.suppression && !entry.allowlist && !entry.pattern, 'blanket suppression prohibited');
    if (entry.disposition === 'false-positive') {
      need(nonempty(entry.reviewer) && entry.reviewer !== entry.owner, `independent false-positive review required: ${entry.id}`);
      artifact(root, entry.evidence, `false-positive ${entry.id}`);
    }
    if (entry.disposition === 'fixed') {
      need(/^[a-f0-9]{40,64}$/.test(entry.fixCommit), `missing fix commit: ${entry.id}`);
      artifact(root, entry.retest, `retest ${entry.id}`);
      need(entry.retest.sourceSha256 === saved.sha256 && !all.some(x => x.id === entry.id && x.scanId !== 'manual'), `fixed finding persists or stale retest: ${entry.id}`);
    }
    if (mode === 'verify') need(entry.disposition !== 'confirmed-open', `unresolved confirmed finding: ${entry.id}`);
  }
  for (const finding of all) need(entries.has(finding.id), `untriaged finding: ${finding.id}`);
  if (mode === 'verify') need(all.every(x => entries.get(x.id).disposition === 'false-positive' || (x.scanId === 'manual' && entries.get(x.id).disposition === 'fixed')), 'unresolved current findings');
  return { mode, sourceSha256: saved.sha256, scans: seen.size, currentFindings: all.length, confirmedOpen: [...entries.values()].filter(x => x.disposition === 'confirmed-open').length, findings: all };
}
