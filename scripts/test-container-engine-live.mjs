#!/usr/bin/env node
// Opt-in core probe against a real engine. No daemon socket, models or private data.
import { createServer } from 'node:http';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync, statSync, rmSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../', import.meta.url));
const engine = process.env.CE_CONTAINER_ENGINE ?? 'docker';
assert.ok(['docker', 'podman'].includes(engine), 'explicit supported engine required');
const receiptPath = join(root, `.sandbox-cache/runtime-core-${engine}.json`);
rmSync(receiptPath, { force: true });
const parent = resolve(process.env.CE_RUNTIME_PROBE_ROOT || '/private/tmp/ce-podman-validation');
const id = `core probe ${randomUUID()}`;
const source = join(parent, id);
mkdirSync(source, { recursive: true, mode: 0o700 });
const bytes = 'synthetic sealed evidence 7\n';
const digest = x => createHash('sha256').update(x).digest('hex');
writeFileSync(join(source, 'input.txt'), bytes, { mode: 0o600 });
const cache = `.sandbox-cache/runtime-probes/${id}`;
mkdirSync(join(root, cache), { recursive: true });
let hits = 0;
const server = createServer((req, res) => { hits++; res.end('controlled-local-canary'); });
await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
const host = engine === 'podman' ? 'host.containers.internal' : 'host.docker.internal';
const url = `http://${host}:${server.address().port}/canary`;

async function command(path, args, env = {}) {
  return await new Promise((resolve, reject) => {
    const child = spawn(path, args, { cwd: root, env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '', stderr = '';
    const timer = setTimeout(() => { child.kill('SIGTERM'); reject(new Error('live core probe timed out')); }, 60000);
    child.stdout.on('data', b => { stdout += b; if (stdout.length > 1024 * 1024) child.kill('SIGTERM'); });
    child.stderr.on('data', b => { stderr += b; if (stderr.length > 1024 * 1024) child.kill('SIGTERM'); });
    child.once('error', e => { clearTimeout(timer); reject(e); });
    child.once('close', code => { clearTimeout(timer); code === 0 ? resolve({ stdout, stderr }) : reject(new Error(`probe command failed (${code}): ${stderr.slice(-1000)}`)); });
  });
}

const probe = `
const assert=require('node:assert/strict'),fs=require('node:fs'),http=require('node:http'),os=require('node:os'),crypto=require('node:crypto');
const source=process.argv[1],cache=process.argv[2],url=process.argv[3],offline=process.argv[4]==='offline';
assert.notEqual(process.getuid(),0);
assert.equal(fs.readFileSync(source+'/input.txt','utf8'),${JSON.stringify(bytes)});
assert.throws(()=>fs.writeFileSync(source+'/input.txt','changed'),{code:'EROFS'});
assert.throws(()=>fs.writeFileSync('/rootfs-probe','changed'),{code:'EROFS'});
assert(!fs.existsSync('/var/run/docker.sock'));assert(!fs.existsSync('/run/podman/podman.sock'));
const status=fs.readFileSync('/proc/self/status','utf8');
assert.match(status,/CapEff:\\s+0+\\n/);assert.match(status,/NoNewPrivs:\\s+1\\n/);
fs.writeFileSync(cache+'/cache.txt','owned-cache');
assert.equal(process.env.CE_LIVE_SENTINEL,offline?undefined:'synthetic-only');
if(offline)assert.deepEqual(Object.keys(os.networkInterfaces()),['lo']);
let connected=false;
const request=http.get(url,res=>{let body='';res.on('data',b=>body+=b);res.on('end',()=>{connected=body==='controlled-local-canary';done();});});
request.setTimeout(1500,()=>request.destroy(new Error('expected offline timeout')));
request.on('error',()=>done());
let finished=false;function done(){if(finished)return;finished=true;assert.equal(connected,!offline);
console.log(JSON.stringify({inputSha256:crypto.createHash('sha256').update(fs.readFileSync(source+'/input.txt')).digest('hex'),uid:process.getuid(),offline,connected,capabilitiesDropped:true,noNewPrivileges:true,sourceReadOnly:true,noEngineSocket:true}));}
`;

try {
  const doctor = JSON.parse((await command(join(root, 'scripts/container-engine-doctor'), [])).stdout);
  assert.equal(doctor.backend, engine);
  const env = { SANDBOX_SOURCES: source, SANDBOX_PASS_ENV: 'CE_LIVE_SENTINEL', CE_LIVE_SENTINEL: 'synthetic-only' };
  const online = JSON.parse((await command(join(root, 'scripts/sandbox'), ['node', 'node', '-e', probe, source, `/work/${cache}`, url, 'online'], env)).stdout.trim());
  assert.equal(hits, 1, 'positive network control did not reach owned local server');
  const offline = JSON.parse((await command(join(root, 'scripts/sandbox'), ['--offline', 'node', 'node', '-e', probe, source, `/work/${cache}`, url, 'offline'], { ...env, SANDBOX_NETWORK: 'host' })).stdout.trim());
  assert.equal(hits, 1, 'offline container reached local control');
  assert.equal(digest(readFileSync(join(source, 'input.txt'))), digest(bytes));
  assert.equal(statSync(join(root, cache, 'cache.txt')).uid, process.getuid());
  const finalDoctor = JSON.parse((await command(join(root, 'scripts/container-engine-doctor'), [])).stdout);
  assert.equal(finalDoctor.contextSha256, doctor.contextSha256, 'context changed during live probe');
  const inputs = ['scripts/lib/container-engine.sh', 'scripts/container-engine-doctor', 'scripts/sandbox', 'config/runtime/images.env', 'scripts/test-container-engine-live.mjs'];
  const hashes = Object.fromEntries(inputs.map(path => [path, digest(readFileSync(join(root, path)))]));
  const receipt = { schemaVersion: 1, scope: 'real core sandbox probe only', doctor, sourceHashes: hashes,
    sourceHash: digest(JSON.stringify(hashes)), online, offline, inputUnchanged: true, cacheOwnedByCaller: true,
    limits: ['not full pipeline or ce.v1 parity', 'no nested act/Testcontainers or full audit qualification', 'controlled local networking only'] };
  const path = receiptPath;
  writeFileSync(path, JSON.stringify(receipt, null, 2) + '\n', { mode: 0o600 });
  console.log(JSON.stringify({ passed: true, engine, receipt: path }));
} finally {
  await new Promise(resolve => server.close(resolve));
  rmSync(source, { recursive: true, force: true });
}
