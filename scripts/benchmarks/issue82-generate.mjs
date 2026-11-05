#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { createHash } from 'node:crypto';
import { hash, root, sourceIdentity, safeOutput, DISK_RESERVE_GIB } from './issue28-generate.mjs';

const modules = 2400;
const core = 500;
const fanout = 24;
const repositoryId = 'dense82';
const commitSha = hash('issue82-dense-v1', 'sha1');
const extractor = { name: 'issue82-dense', version: '1' };
const modulePath = index => `src/module${String(index).padStart(4, '0')}.ts`;
const symbol = index => `ts|${repositoryId}|@dense82/core|${modulePath(index)}|f${index}#value`;

export function* records() {
  const wrap = (kind, data) => ({ schema: 'ce.v1', kind, ...data });
  yield wrap('header', {
    repositoryId, commitSha, language: 'typescript', unit: { path: 'tsconfig.json', name: '@dense82/core' },
    extractor, configHash: hash('issue82-dense-v1'), inputHash: hash(`${modules}|${core}|${fanout}`),
  });
  for (let index = 0; index < modules; index++) {
    yield wrap('evidence', {
      id: `ev:${index}`, repositoryId, commitSha, path: modulePath(index),
      span: { startLine: 1, startColumn: 1, endLine: 1, endColumn: 2 },
      snippetSha256: hash(`synthetic module ${index}`), extractor,
    });
    yield wrap('node', {
      id: `n:${index}`, symbolKey: symbol(index), nodeKind: 'Function',
      name: `Function ${String(index).padStart(4, '0')}`, signature: `function f${index}()`,
      evidence: [`ev:${index}`],
    });
  }
  let edges = 0;
  for (let index = 0; index < modules; index++) {
    const count = index < core ? fanout : 2;
    for (let offset = 1; offset <= count; offset++) {
      const target = index < core ? (index + offset) % core : (index * 17 + offset) % core;
      yield wrap('edge', {
        id: `e:${edges++}`, from: symbol(index), relation: 'CALLS', to: symbol(target),
        derivation: 'compiler', resolution: 'resolved', evidence: [`ev:${index}`],
      });
    }
  }
  yield wrap('complete', {
    status: 'full', counts: { node: modules, edge: edges, evidence: modules, calculation: 0, diagnostic: 0 },
    coverage: { filesTotal: modules, filesAnalyzed: modules, unresolvedEdges: 0 },
  });
}

export async function generate(out = '.sandbox-cache/issue28-bench/issue82-dense') {
  const output = safeOutput(out);
  const disk = fs.statfsSync(root);
  if (disk.bavail * disk.bsize < DISK_RESERVE_GIB * 1024 ** 3 + 32 * 1024 ** 2) {
    throw new Error('Need 10GiB disk reserve plus dense fixture budget');
  }
  fs.mkdirSync(output, { recursive: true });
  const artifact = path.join(output, 'dense82.jsonl');
  const sha = createHash('sha256');
  let bytes = 0;
  const digest = new Transform({
    transform(chunk, encoding, done) { sha.update(chunk); bytes += chunk.length; done(null, chunk); },
  });
  await pipeline(
    Readable.from((function* () { for (const record of records()) yield JSON.stringify(record) + '\n'; })()),
    digest, fs.createWriteStream(artifact, { flags: 'wx' }),
  );
  const manifest = {
    generator: 'issue82-dense-v1', generatorSha256: hash(fs.readFileSync(fileURLToPath(import.meta.url))),
    sourceSha: sourceIdentity(), repositoryId, commitSha,
    modules, core, fanout, edges: core * fanout + (modules - core) * 2,
    artifact: 'dense82.jsonl', artifactSha256: sha.digest('hex'), bytes,
    route: '#/architecture?level=repository&focus=dense82',
    baselineQuery: '/api/architecture?level=repository&focus=dense82&nodeBudget=2000',
    expectedModuleMapping: 'comp:ts|dense82|@dense82/core|src/module0000.ts|(module)#namespace',
    limitations: ['Synthetic compiler-shaped facts, not extracted application source', 'CALLS only; kind removal exercised with call'],
  };
  fs.writeFileSync(path.join(output, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
  return manifest;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length > 3) throw new Error('Usage: issue82-generate.mjs [output-under-issue28-bench]');
    console.log(JSON.stringify(await generate(process.argv[2])));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
