import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { buildNpmrc, findPackageRoots, manifestSources, resolveHostNpmFeeds } from '../scripts/fetch-npm-packages.mjs';

test('host npm token references override only the configured registry credential', () => {
  const feeds = [
    { kind: 'npm', url: 'https://npm.pkg.github.com/', scope: '@hm-group', username: 'saved-user', secret: 'expired' },
    { kind: 'nuget', url: 'https://nuget.example.test/', secret: 'nuget-secret' },
    { kind: 'npm', url: 'https://other.example.test/', scope: '@other', secret: 'other-secret' },
  ];
  const resolved = resolveHostNpmFeeds(feeds, '//npm.pkg.github.com/:_authToken=${NPM_TOKEN}\n//unrelated.example.test/:_authToken=unrelated-secret\n', { NPM_TOKEN: 'synthetic-host-token' });
  assert.deepEqual(resolved.warnings, []);
  assert.equal(resolved.feeds[0].secret, 'synthetic-host-token');
  assert.equal(resolved.feeds[0].username, null);
  assert.deepEqual(resolved.feeds.slice(1), feeds.slice(1));
  const npmrc = buildNpmrc(resolved.feeds);
  assert.match(npmrc.contents, /:_authToken=\$\{CE_NPM_TOKEN_0\}/);
  assert.doesNotMatch(npmrc.contents, /synthetic-host-token|nuget-secret|unrelated-secret/);
});

test('missing host token variables retain saved settings and report only the variable name', () => {
  const feeds = [{ kind: 'npm', url: 'https://npm.pkg.github.com/', scope: '@hm-group', secret: 'saved-secret' }];
  const result = resolveHostNpmFeeds(feeds, '//npm.pkg.github.com/:_authToken=${NPM_TOKEN}\n', {});
  assert.deepEqual(result.feeds, feeds);
  assert.deepEqual(result.warnings, ['npm host credential: NPM_TOKEN is not set; using saved feed settings']);
  assert.doesNotMatch(result.warnings.join('\n'), /saved-secret/);
});

test('npm auth is bound to the registry path and secrets stay in environment references', () => {
  const { contents, credentials } = buildNpmrc([
    { kind: 'npm', name: 'scoped', url: 'https://packages.example.test/npm/', scope: '@company', secret: 'synthetic-token' },
    { kind: 'npm', name: 'basic', url: 'https://basic.example.test/repository/npm/', scope: '@internal', username: 'build', secret: 'synthetic-password' },
  ]);
  assert.match(contents, /@company:registry=https:\/\/packages\.example\.test\/npm\//);
  assert.match(contents, /\/\/packages\.example\.test\/npm\/:_authToken=\$\{CE_NPM_TOKEN_0\}/);
  assert.match(contents, /\/\/basic\.example\.test\/repository\/npm\/:_auth=\$\{CE_NPM_AUTH_1\}/);
  assert.doesNotMatch(contents, /synthetic-(?:token|password)/);
  assert.equal(credentials.CE_NPM_TOKEN_0, 'synthetic-token');
  assert.equal(credentials.CE_NPM_AUTH_1, Buffer.from('build:synthetic-password').toString('base64'));
});

test('npm registries reject duplicate scopes and multiple defaults', () => {
  assert.throws(() => buildNpmrc([
    { kind: 'npm', url: 'https://one.example.test/', scope: '@company' },
    { kind: 'npm', url: 'https://two.example.test/', scope: '@company' },
  ]), /duplicate npm scope/);
  assert.throws(() => buildNpmrc([
    { kind: 'npm', url: 'https://one.example.test/' },
    { kind: 'npm', url: 'https://two.example.test/' },
  ]), /Only one npm registry/);
  assert.throws(() => buildNpmrc([{ kind: 'npm', url: 'http://insecure.example.test/', scope: '@company' }]), /Invalid npm registry URL/);
});

test('package root discovery finds lockfiles but skips node_modules and git trees', () => {
  const root = mkdtempSync(join(tmpdir(), 'ce-npm-roots-'));
  try {
    for (const dir of ['app', 'app/node_modules/fake', 'app/.git/hidden']) {
      mkdirSync(join(root, dir), { recursive: true });
      writeFileSync(join(root, dir, 'package.json'), '{}');
      writeFileSync(join(root, dir, 'package-lock.json'), '{}');
    }
    assert.deepEqual(findPackageRoots(root), [join(root, 'app')]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('manifest source paths resolve relative to the analysis work directory', () => {
  const root = mkdtempSync(join(tmpdir(), 'ce-npm-manifest-'));
  try {
    const manifestPath = join(root, 'work', 'manifest.tsv');
    const paths = manifestSources(manifestPath, 'repo\tsha\tsrc/repo\n');
    assert.deepEqual(paths, [join(root, 'work', 'src/repo')]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
