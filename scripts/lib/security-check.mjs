import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { sha, copySealed, validateTrivyDatabase, validateDependencyReport } from '../security-audit-lib.mjs';
import { dependencySnapshot } from '../security-collect-inventory.mjs';

const need = (ok, message) => { if (!ok) throw new Error(message); };
const scannerUid = process.getuid?.() || 1000, scannerGid = process.getgid?.() || 1000;
function ownScannerPath(file) {
  if (process.getuid?.() !== 0) return;
  const stat = fs.lstatSync(file);
  need(!stat.isSymbolicLink(), 'scanner-owned path must not be a symbolic link');
  fs.chownSync(file, scannerUid, scannerGid);
  if (stat.isDirectory()) for (const entry of fs.readdirSync(file)) ownScannerPath(path.join(file, entry));
}
const publisher = Object.freeze({
  reference: 'aquasec/trivy:0.76.1@sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c',
  digest: 'sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c',
  issuer: 'https://token.actions.githubusercontent.com',
  identity: 'https://github.com/aquasecurity/trivy/.github/workflows/reusable-release.yaml@refs/tags/v0.76.1',
  verifier: 'Cosign v3.1.4',
});
const cosignHashes = Object.freeze({
  'darwin/arm64': '9f9b8cd557cf2e5b3df3373a00baa005a86b02391e3e06dd0ce37b3a1533d3a4',
  'linux/amd64': '424e2c97d0b45fd7a6b608a87f090f35633f4c23ece5f092d48bba11aaf4bbfa',
  'linux/arm64': 'ade83b7aac62e131cacfe96d958948b2150cc9efec46a570a2649b8e27dedad2',
});
export function cosignPlatform(os = process.platform, architecture = process.arch) {
  return `${os}/${architecture === 'x64' ? 'amd64' : architecture}`;
}
export function validateVerifiedTrivyReference(reference) {
  need(reference === publisher.reference, 'Trivy image does not match the approved exact digest');
  return { ...publisher, publisherSignatureVerified: false, benignContentProven: false };
}
export function cosignVerifyArgs(reference, binary) {
  need(reference === publisher.reference && path.isAbsolute(binary),
    'Cosign verification requires the approved exact Trivy digest and absolute verifier path');
  return [binary, 'verify', '--timeout', '60s', '--certificate-identity', publisher.identity,
    '--certificate-oidc-issuer', publisher.issuer, reference];
}
export function validateCosignOutput(output) {
  const verified = JSON.parse(output);
  need(Array.isArray(verified) && verified.length > 0 && verified.every(item =>
    item?.critical?.type === 'https://sigstore.dev/cosign/sign/v1' &&
    item.critical.image?.['docker-manifest-digest'] === publisher.digest &&
    item.critical.identity?.['docker-reference'] === `index.docker.io/aquasec/trivy@${publisher.digest}`),
  'Cosign verification output does not bind the exact signed Trivy index digest');
  return verified;
}
async function verifyPublisher(reference, run, certificate) {
  validateVerifiedTrivyReference(reference);
  const platform = cosignPlatform(), expectedHash = cosignHashes[platform], binary = process.env.COSIGN_BINARY;
  need(expectedHash && typeof binary === 'string' && path.isAbsolute(binary),
    `set COSIGN_BINARY to the externally provisioned Cosign v3.1.4 binary for ${platform}; supported binaries are hash-pinned`);
  const stat = fs.statSync(binary), hash = crypto.createHash('sha256');
  need(stat.isFile() && stat.size <= 150 * 1024 * 1024 && (stat.mode & 0o111) !== 0,
    'COSIGN_BINARY is not a bounded executable file');
  for await (const chunk of fs.createReadStream(binary)) hash.update(chunk);
  need(hash.digest('hex') === expectedHash, `COSIGN_BINARY is not the hash-pinned Cosign v3.1.4 release binary for ${platform}`);
  const env = Object.fromEntries(['PATH', 'HOME', 'TMPDIR', 'HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY',
    'http_proxy', 'https_proxy', 'no_proxy'].filter(key => process.env[key]).map(key => [key, process.env[key]]));
  if (certificate) env.SSL_CERT_FILE = certificate;
  const result = await run(binary, cosignVerifyArgs(reference, binary).slice(1),
    { timeout: 60_000, maxBuffer: 8 * 1024 * 1024, env });
  need(result.status === 0, `Cosign rejected the Trivy image signature (${result.status})`);
  validateCosignOutput(result.stdout);
  return { ...publisher, publisherSignatureVerified: true, benignContentProven: false, verifierSha256: expectedHash };
}
export function controlledRunner(controller) {
  return (command, args, options = {}) => new Promise((resolve, reject) => {
    const { timeout = 1200_000, maxBuffer = 64 * 1024 * 1024, cleanup = false, guard, ...spawnOptions } = options;
    if (controller.signal && !cleanup) return reject(new Error(`security check cancelled by ${controller.signal}`));
    const child = spawn(command, args, { ...spawnOptions, stdio: ['ignore', 'pipe', 'pipe'] });
    controller.children.add(child);
    let stdout = '', stderr = '', bytes = 0, failure, killTimer, settled = false;
    const terminate = message => {
      failure ??= new Error(message); failure.exitCode = 124;
      child.kill('SIGTERM');
      killTimer ??= setTimeout(() => child.kill('SIGKILL'), 1500);
    };
    const timer = setTimeout(() => terminate(`security check command timed out: ${args[0]}`), timeout);
    const guardTimer = guard && setInterval(() => {
      try { guard(); } catch (error) { terminate(error.message); }
    }, 250);
    const finish = (error, status, signal) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer); clearTimeout(killTimer); clearInterval(guardTimer); controller.children.delete(child);
      if (controller.signal && !cleanup) {
        error = new Error(`security check cancelled by ${controller.signal}`);
        error.exitCode = controller.signal === 'SIGINT' ? 130 : 143;
      }
      if (error || failure) reject(error ?? failure);
      else resolve({ status: Number.isInteger(status) ? status : 1, signal, stdout, stderr });
    };
    child.stdout.on('data', chunk => {
      bytes += chunk.length;
      if (bytes > maxBuffer) terminate('security check command output exceeded limit');
      else stdout += chunk.toString('utf8');
    });
    child.stderr.on('data', chunk => {
      bytes += chunk.length;
      if (bytes > maxBuffer) terminate('security check command output exceeded limit');
      else stderr += chunk.toString('utf8');
    });
    child.once('error', error => finish(error));
    child.once('close', (status, signal) => finish(undefined, status, signal));
  });
}
export function standardDependencyDockerArgs(phase, mounts = [], cidfile) {
  need(['acquire', 'scan'].includes(phase), 'unsupported standard Trivy phase');
  need(typeof cidfile === 'string' && path.isAbsolute(cidfile) && !cidfile.includes(','),
    'container identity file must be an absolute safe path');
  const args = ['--cidfile', cidfile, '--pull=never', '--network', phase === 'acquire' ? 'bridge' : 'none',
    '--user', `${scannerUid}:${scannerGid}`, '--read-only',
    '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--pids-limit', '512',
    '--memory', '2g', '--cpus', '2', '--tmpfs', '/tmp:rw,nosuid,size=1g', '-e', 'HOME=/tmp', '--entrypoint', 'trivy'];
  for (const mount of mounts) {
    need(path.isAbsolute(mount.source) && !mount.source.includes(',') &&
      ['/cache', '/work', '/etc/sandbox/extra-ca.crt'].includes(mount.target), 'invalid standard Trivy mount');
    need(phase !== 'acquire' || mount.target === '/cache' || mount.target === '/etc/sandbox/extra-ca.crt',
      'acquisition permits only its isolated cache and explicit certificate');
    need(!mount.write || (phase === 'acquire' && mount.target === '/cache'), 'only the isolated acquisition cache may be writable');
    args.push('--mount', `type=bind,source=${mount.source},target=${mount.target}${mount.write ? '' : ',readonly'}`);
    if (phase === 'acquire' && mount.target === '/etc/sandbox/extra-ca.crt') args.push('-e', 'SSL_CERT_FILE=/etc/sandbox/extra-ca.crt');
  }
  if (phase === 'scan') args.push('-w', '/work');
  return args;
}
export function selectedIndexManifest(output, platform) {
  const index = JSON.parse(output), [os, architecture] = platform.split('/');
  need(index?.schemaVersion === 2 && Array.isArray(index.manifests),
    'Trivy digest did not resolve to a multi-platform OCI/Docker image index');
  const matches = index.manifests.filter(item => item.platform?.os === os && item.platform?.architecture === architecture &&
    (!item.platform.variant || item.platform.variant === (architecture === 'arm64' ? 'v8' : undefined)));
  need(matches.length === 1 && /^sha256:[a-f0-9]{64}$/.test(matches[0].digest),
    `Trivy index does not contain one unambiguous ${platform} child manifest`);
  return matches[0].digest;
}
export function selectedChildConfig(output) {
  const child = JSON.parse(output);
  need(child?.schemaVersion === 2 && /^sha256:[a-f0-9]{64}$/.test(child.config?.digest),
    'selected Trivy child manifest has no valid image configuration digest');
  return child.config.digest;
}
export function inspectSelectedImage(output, platform, childDigest, configDigest) {
  const parsed = JSON.parse(output), image = Array.isArray(parsed) ? parsed[0] : parsed;
  need(image && `${image.Os}/${image.Architecture}` === platform, 'selected local Trivy image platform mismatch');
  const descriptor = image.Descriptor;
  if (descriptor) need(descriptor.mediaType === 'application/vnd.oci.image.manifest.v1+json' ||
    descriptor.mediaType === 'application/vnd.docker.distribution.manifest.v2+json', 'local selected image is not a platform manifest');
  need(descriptor ? descriptor.digest === childDigest && image.Id === childDigest :
    Boolean(configDigest) && image.Id === configDigest, 'local Trivy image does not match the selected index child manifest');
  return { imageId: image.Id, platform, childDigest, identityBasis: descriptor ? 'selected-platform-manifest-descriptor' : 'child-config-digest' };
}
function certificateMount() {
  const source = process.env.SANDBOX_EXTRA_CA_FILE;
  if (!source) return [];
  need(path.isAbsolute(source) && !/[\u0000-\u001f\u007f:,]/.test(source) &&
    fs.existsSync(source) && fs.statSync(source).isFile(), 'SANDBOX_EXTRA_CA_FILE must be a readable absolute certificate file');
  fs.accessSync(source, fs.constants.R_OK);
  return [{ source, target: '/etc/sandbox/extra-ca.crt' }];
}
function createRunDirectory(root) {
  const parent = path.join(root, '.sandbox-cache'), runs = path.join(parent, 'trivy-runs');
  for (const directory of [parent, runs]) {
    if (fs.existsSync(directory)) need(fs.lstatSync(directory).isDirectory(), `unsafe Trivy run directory: ${directory}`);
    else fs.mkdirSync(directory, { mode: 0o700 });
  }
  return fs.mkdtempSync(path.join(runs, 'run-'));
}
function removeRunDirectory(directory) {
  const stat = fs.lstatSync(directory);
  if (stat.isSymbolicLink()) { fs.unlinkSync(directory); return; }
  fs.chmodSync(directory, stat.isDirectory() ? 0o700 : 0o600);
  if (stat.isDirectory()) for (const entry of fs.readdirSync(directory)) removeRunDirectory(path.join(directory, entry));
  fs.rmSync(directory, { recursive: true });
}
export function acquisitionCacheBound(directory) {
  let bytes = 0, entries = 0;
  const walk = current => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      need(++entries <= 4096 && !entry.isSymbolicLink(), 'acquisition cache entry limit or symbolic link');
      const file = path.join(current, entry.name);
      if (entry.isDirectory()) walk(file);
      else {
        need(entry.isFile(), 'acquisition cache contains non-file');
        bytes += fs.lstatSync(file).size;
        need(bytes <= 2 * 1024 * 1024 * 1024, 'acquisition cache exceeds 2GiB limit');
      }
    }
  };
  walk(directory);
  const available = fs.statfsSync(directory);
  need(available.bavail * available.bsize >= 1024 * 1024 * 1024,
    'acquisition cache exhausted the 1GiB free-space reserve');
}
export async function runOwnedContainer(engine, directory, phase, command, mounts, platform) {
  const name = `ce-trivy-${process.pid}-${crypto.randomBytes(12).toString('hex')}`, cidfile = path.join(directory, `${phase}.cid`);
  let failure;
  try {
    await engine(['create', '--name', name, '--platform', platform, ...standardDependencyDockerArgs(phase, mounts, cidfile),
      publisher.reference, ...command]);
    const id = fs.readFileSync(cidfile, 'utf8').trim();
    need(/^[a-f0-9]{12,64}$/.test(id), 'container engine returned an invalid container identity');
    const guard = phase === 'acquire' ? () => acquisitionCacheBound(mounts.find(mount => mount.target === '/cache').source) : undefined;
    const result = await engine(['start', '-a', id], phase === 'scan' ? [0, 1] : [0], false, guard);
    guard?.(); return result;
  } catch (error) { failure = error; throw error; }
  finally {
    try { await engine(['rm', '-f', name], [0], true); }
    catch (error) { if (!failure) throw error; failure.message += `; cleanup also failed: ${error.message}`; }
  }
}
export async function runStandardSecurityCheck(root, injectedRun) {
  root = fs.realpathSync(root);
  const startedAt = Date.now(), controller = { signal: undefined, children: new Set() };
  let directory, killTimer, runtime;
  const cancel = signal => {
    if (controller.signal) return;
    controller.signal = signal;
    const children = [...controller.children];
    for (const child of children) child.kill('SIGTERM');
    killTimer = setTimeout(() => { for (const child of children) child.kill('SIGKILL'); }, 1500);
    killTimer.unref();
  };
  const onTerm = () => cancel('SIGTERM'), onInt = () => cancel('SIGINT');
  process.on('SIGTERM', onTerm); process.on('SIGINT', onInt);
  const run = injectedRun ?? controlledRunner(controller);
  const engine = async (args, accepted = [0], cleanup = false, guard) => {
    const remaining = 1200_000 - (Date.now() - startedAt);
    need(cleanup || (remaining > 0 && !controller.signal), 'Trivy security check cancelled or exceeded 20-minute limit');
    const result = await run(path.join(root, 'scripts/container-engine'), args, {
      cwd: root, timeout: cleanup ? 10_000 : remaining, cleanup, guard,
      env: { ...process.env, ...(runtime ? { CE_ANALYSIS_RUNTIME: JSON.stringify(runtime) } : {}),
        TMPDIR: path.join(directory, 'tmp') },
    });
    if (!accepted.includes(result.status)) {
      const error = new Error(`container engine command failed (${result.status}): ${args[0]}; ${result.stderr.trim()}`);
      error.exitCode = result.status > 0 && result.status < 126 ? result.status : 1; throw error;
    }
    return result;
  };
  const launch = (...args) => runOwnedContainer(engine, directory, ...args);
  try {
    const certificate = certificateMount(), reference = fs.readFileSync(path.join(root, 'config/security/trivy-image'), 'utf8').trim();
    const verifiedPublisher = await verifyPublisher(reference, run, certificate[0]?.source);
    directory = createRunDirectory(root); fs.mkdirSync(path.join(directory, 'tmp'), { mode: 0o700 });
    runtime = JSON.parse((await engine(['--analysis-snapshot'])).stdout);
    need(['docker', 'podman'].includes(runtime.backend) && /^[a-f0-9]{64}$/.test(runtime.fingerprint),
      'container engine returned an invalid frozen runtime identity');
    const reported = (await engine(['info', '--format', '{{.OSType}}/{{.Architecture}}'])).stdout.trim();
    const platform = reported.replace(/x86_64$/, 'amd64').replace(/aarch64$/, 'arm64');
    need(/^linux\/(?:amd64|arm64)$/.test(platform), `unsupported container engine platform: ${reported}`);
    const childDigest = selectedIndexManifest((await engine(['manifest', 'inspect', reference])).stdout, platform);
    const inspectArgs = ['image', 'inspect', ...(runtime.backend === 'docker' ? ['--platform', platform] : []),
      '--format', '{{json .}}', reference];
    let inspected = await engine(inspectArgs, [0, 1]);
    if (inspected.status !== 0) {
      await engine(['pull', '--platform', platform, reference]); inspected = await engine(inspectArgs);
    }
    const parsed = JSON.parse(inspected.stdout), local = Array.isArray(parsed) ? parsed[0] : parsed;
    let configDigest;
    if (!local?.Descriptor) configDigest = selectedChildConfig((await engine(['manifest', 'inspect',
      `${reference.slice(0, reference.indexOf('@'))}@${childDigest}`])).stdout);
    const image = inspectSelectedImage(inspected.stdout, platform, childDigest, configDigest);
    const inventory = () => dependencySnapshot(root, { deadline: startedAt + 1200_000 });
    const source = inventory(), sourceDirectory = path.join(directory, 'source');
    fs.mkdirSync(sourceDirectory, { mode: 0o700 }); copySealed(root, sourceDirectory, source);
    ownScannerPath(sourceDirectory);
    for (const file of source.files) {
      const sealed = path.join(sourceDirectory, file.path);
      fs.chmodSync(sealed, 0o400); ownScannerPath(sealed);
    }
    need(inventory().sha256 === source.sha256 && dependencySnapshot(sourceDirectory).sha256 === source.sha256,
      'dependency inputs changed while creating the sealed scan snapshot');
    const acquisitionCache = path.join(directory, 'acquisition-cache');
    fs.mkdirSync(acquisitionCache, { mode: 0o700 }); ownScannerPath(acquisitionCache);
    acquisitionCacheBound(acquisitionCache);
    await launch('acquire', ['image', '--download-db-only', '--db-repository', 'ghcr.io/aquasecurity/trivy-db',
      '--cache-dir', '/cache', '--disable-telemetry', '--timeout', '20m'],
    [{ source: acquisitionCache, target: '/cache', write: true }, ...certificate], platform);
    const database = validateTrivyDatabase(path.join(acquisitionCache, 'db'));
    const scanCache = path.join(directory, 'cache'), dbDirectory = path.join(scanCache, 'db');
    fs.mkdirSync(dbDirectory, { recursive: true, mode: 0o700 });
    for (const file of ['trivy.db', 'metadata.json']) {
      fs.renameSync(path.join(acquisitionCache, 'db', file), path.join(dbDirectory, file));
      fs.chmodSync(path.join(dbDirectory, file), 0o400); ownScannerPath(path.join(dbDirectory, file));
    }
    removeRunDirectory(acquisitionCache);
    need(JSON.stringify(validateTrivyDatabase(dbDirectory)) === JSON.stringify(database), 'database changed while sealing');
    fs.chmodSync(dbDirectory, 0o500); fs.chmodSync(scanCache, 0o500);
    ownScannerPath(dbDirectory); ownScannerPath(scanCache);
    const scan = await launch('scan', ['fs', '--scanners', 'vuln', '--cache-dir', '/cache', '--skip-db-update', '--skip-check-update',
      '--offline-scan', '--skip-version-check', '--disable-telemetry', '--config', '', '--ignorefile', '', '--format', 'json',
      '--severity', 'UNKNOWN,LOW,MEDIUM,HIGH,CRITICAL', '--exit-code', '1', '--include-dev-deps', '--timeout', '20m', '.'],
    [{ source: sourceDirectory, target: '/work' }, { source: scanCache, target: '/cache' }], platform);
    const coverage = validateDependencyReport(root, JSON.parse(scan.stdout), source);
    need(!/(?:^|\n)(?:.*\b(?:ERROR|FATAL)\b)/.test(scan.stderr), 'Trivy reported a database or scan error');
    need(inventory().sha256 === source.sha256 && dependencySnapshot(sourceDirectory).sha256 === source.sha256,
      'dependency inputs changed during offline scan');
    need((scan.status === 1) === (coverage.findings.length > 0), 'Trivy exit status and validated all-severity finding set disagree');
    need(JSON.stringify(validateTrivyDatabase(dbDirectory)) === JSON.stringify(database), 'Trivy database changed during offline scan');
    need(!controller.signal, `Trivy security check cancelled by ${controller.signal}`);
    console.log(JSON.stringify({
      source, scanner: { reference, ...image, publisher: verifiedPublisher },
      runtime: { backend: runtime.backend, fingerprint: runtime.fingerprint },
      database: { ...database, origin: 'ghcr.io/aquasecurity/trivy-db', publisherSignatureVerified: false,
        trustBasis: 'TLS registry acquisition by verified pinned Trivy; metadata freshness and sealed byte identity; no database publisher signature verification' },
      reportSha256: sha(scan.stdout), scannedTargets: coverage.targets, findingCount: coverage.findings.length,
      sourceFreeAcquisition: true, offlineScan: true,
    }));
    return coverage.findings.length ? 1 : 0;
  } catch (error) {
    if (controller.signal) error.exitCode = controller.signal === 'SIGINT' ? 130 : 143;
    throw error;
  } finally {
    clearTimeout(killTimer);
    if (directory && fs.existsSync(directory)) removeRunDirectory(directory);
    process.removeListener('SIGTERM', onTerm); process.removeListener('SIGINT', onInt);
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(fs.realpathSync(process.argv[1])).href) {
  try {
    need(process.argv.length === 3 && path.isAbsolute(process.argv[2]), 'usage: security-check.mjs <absolute-checkout-root>');
    process.exitCode = await runStandardSecurityCheck(process.argv[2]);
  } catch (error) { console.error(`security-check: ${error.message}`); process.exitCode = error.exitCode ?? 1; }
}
