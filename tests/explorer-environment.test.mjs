import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { chmodSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { explorerEnvironment } from '../scripts/explorer-host.mjs';

test('Explorer retains database/model policy but not acquisition credentials or injected Git config', () => {
  const environment = explorerEnvironment({
    PATH: '/usr/bin', HOME: '/home/operator', CE_DATABASE: 'fixture-database',
    CE_FOUNDRY_API_KEY: 'fixture-model', interpretation__provider: 'recorded',
    GITHUB_TOKEN: 'fixture-acquisition', GH_TOKEN: 'fixture-acquisition',
    NUGET_PASS_ENV: 'CUSTOM_FEED_PASSWORD', CUSTOM_FEED_PASSWORD: 'fixture-acquisition',
    NPM_TOKEN: 'fixture-acquisition', CE_PACKAGE_FEEDS_FILE: '/host/private.json',
    GIT_CONFIG_COUNT: '1', GIT_CONFIG_KEY_0: 'http.extraheader',
    GIT_CONFIG_VALUE_0: 'fixture-acquisition', DOTNET_STARTUP_HOOKS: '/injected.dll',
    DOCKER_HOST: '/engine.sock', SANDBOX_PASS_ENV: 'GITHUB_TOKEN',
  });
  assert.deepEqual(environment, {
    PATH: '/usr/bin', HOME: '/home/operator', CE_DATABASE: 'fixture-database',
    CE_FOUNDRY_API_KEY: 'fixture-model', interpretation__provider: 'recorded',
  });
});

test('the supported native launcher actually starts its child without arbitrary host secrets', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ce-explorer-environment-'));
  try {
    const dotnet = join(directory, 'dotnet');
    writeFileSync(dotnet, '#!/bin/sh\nexec /usr/bin/env\n', { mode: 0o700 });
    chmodSync(dotnet, 0o700);
    const output = execFileSync(process.execPath, [
      fileURLToPath(new URL('../scripts/explorer-host.mjs', import.meta.url)), 'fixture.dll',
    ], {
      encoding: 'utf8',
      env: {
        PATH: directory, GITHUB_TOKEN: 'fixture-secret-must-not-arrive',
        CUSTOM_FEED_PASSWORD: 'fixture-secret-must-not-arrive',
        CE_DATABASE: 'fixture-preserved-database', CE_FOUNDRY_API_KEY: 'fixture-preserved-model',
      },
    });
    assert.ok(!output.includes('fixture-secret-must-not-arrive'));
    assert.ok(output.includes('CE_DATABASE=fixture-preserved-database'));
    assert.ok(output.includes('CE_FOUNDRY_API_KEY=fixture-preserved-model'));
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
