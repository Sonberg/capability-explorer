import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, symlinkSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extract } from '../src/extract/extractor.ts';
import { validateArtifact } from '../src/contracts/validateArtifact.ts';

for (const mode of ['outside', 'sibling-prefix', 'file-link', 'directory-link', 'inside'] as const) {
  test(`imported JSON boundary: ${mode}`, () => {
    const base = mkdtempSync(join(tmpdir(), 'ce-json-boundary-'));
    const root = join(base, 'repo');
    mkdirSync(root);
    mkdirSync(join(base, 'repo-other'));
    const config = JSON.stringify({ db: 'postgres://synthetic-outside.example/orders' });
    writeFileSync(join(base, 'outside.json'), config);
    writeFileSync(join(base, 'repo-other/config.json'), config);
    writeFileSync(join(root, 'config.json'), config);
    symlinkSync(join(base, 'outside.json'), join(root, 'linked.json'));
    symlinkSync(join(base, 'repo-other'), join(root, 'linked-directory'));
    const specifier = { outside: '../outside.json', 'sibling-prefix': '../repo-other/config.json',
      'file-link': './linked.json', 'directory-link': './linked-directory/config.json', inside: './config.json' }[mode];
    try {
      writeFileSync(join(root, 'package.json'), '{"name":"json-boundary"}');
      writeFileSync(join(root, 'index.js'), `import config from '${specifier}';import pg from 'pg';const pool=new pg.Pool({connectionString:config.db});export function read(){return pool.query('SELECT id FROM orders');}`);
      const artifact = extract({ source: root, repository: 'sample', commit: '0'.repeat(40) })[0]!.jsonl;
      const records = artifact.trim().split('\n').map(line => JSON.parse(line));
      assert.equal(validateArtifact(artifact).ok, true);
      assert.equal(artifact.includes('synthetic-outside.example'), mode === 'inside');
      assert.equal(records.some(r => r.code === 'JSON_SOURCE_UNAVAILABLE'), mode !== 'inside');
      assert.equal(records.some(r => r.kind === 'evidence' && r.path?.startsWith('../')), false);
    } finally { rmSync(base, { recursive: true, force: true }); }
  });
}
