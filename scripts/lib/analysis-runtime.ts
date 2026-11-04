import { isAbsolute } from 'node:path';

type Snapshot = {
  backend: 'docker' | 'podman';
  executable: string;
  prefixArguments: string[];
  fingerprint: string;
};

function safeText(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= 2048 && !/[\x00-\x1f\x7f-\x9f\u2028\u2029]/u.test(value);
}

function validate(value: unknown): Snapshot {
  if (typeof value !== 'object' || value === null || Array.isArray(value)
    || Object.keys(value).sort().join(',') !== 'backend,executable,fingerprint,prefixArguments'
    || !('backend' in value) || (value.backend !== 'docker' && value.backend !== 'podman')
    || !('executable' in value) || !safeText(value.executable) || !isAbsolute(value.executable)
    || !('fingerprint' in value) || typeof value.fingerprint !== 'string' || !/^[a-f0-9]{64}$/.test(value.fingerprint)
    || !('prefixArguments' in value) || !Array.isArray(value.prefixArguments)) throw new Error('invalid snapshot');
  const prefixArguments: string[] = [];
  for (const argument of value.prefixArguments) {
    if (!safeText(argument)) throw new Error('invalid prefix argument');
    prefixArguments.push(argument);
  }
  const [flag, endpoint, identityFlag, identity] = prefixArguments;
  if (value.backend === 'docker') {
    if (prefixArguments.length !== 2 || flag !== '--host' || !endpoint?.startsWith('unix:///') || endpoint.length > 1024) throw new Error('invalid Docker endpoint');
  } else if (!(prefixArguments.length === 1 && flag === '--remote=false')) {
    if (prefixArguments.length !== 4 || flag !== '--url' || !endpoint
      || !/^ssh:\/\/[A-Za-z0-9._-]+@127\.0\.0\.1:[0-9]+\/run\/user\/[0-9]+\/podman\/podman\.sock$/.test(endpoint)
      || identityFlag !== '--identity' || !identity || !isAbsolute(identity) || identity.includes('|')) throw new Error('invalid Podman endpoint');
  }
  return { backend: value.backend, executable: value.executable, prefixArguments, fingerprint: value.fingerprint };
}

try {
  const [mode, backend, fingerprint, executable, ...prefixArguments] = process.argv.slice(2);
  if (mode === 'encode') {
    const snapshot = validate({ backend, executable, prefixArguments, fingerprint });
    const json = JSON.stringify(snapshot);
    if (Buffer.byteLength(json) > 16384) throw new Error('oversized snapshot');
    process.stdout.write(`${json}\n`);
  } else if (mode === 'decode') {
    const json = process.env.CE_ANALYSIS_RUNTIME;
    if (typeof json !== 'string' || Buffer.byteLength(json) > 16384) throw new Error('invalid snapshot');
    const parsed: unknown = JSON.parse(json);
    const snapshot = validate(parsed);
    process.stdout.write([snapshot.backend, snapshot.fingerprint, snapshot.executable, ...snapshot.prefixArguments].join('\n') + '\n');
  } else throw new Error('invalid operation');
} catch {
  process.stderr.write('container-engine: invalid analysis runtime snapshot\n');
  process.exitCode = 2;
}