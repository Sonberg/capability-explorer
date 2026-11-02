#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const allowed = new Set([
  'PATH', 'HOME', 'TMPDIR', 'TMP', 'TEMP', 'LANG', 'LC_ALL', 'TZ', 'TERM',
  'DOTNET_ROOT', 'DOTNET_ROOT_ARM64', 'DOTNET_ROOT_X64', 'DOTNET_CLI_HOME',
  'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT',
  'ASPNETCORE_URLS', 'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT',
  'SSL_CERT_FILE', 'SSL_CERT_DIR', 'GIT_SSL_CAINFO',
  'CE_DATABASE', 'CE_FOUNDRY_API_KEY', 'CE_MODEL_ENDPOINT',
  'CE_CHAT_MODEL', 'CE_EMBEDDING_MODEL', 'EXPLORER_SOURCE',
]);

export function explorerEnvironment(source) {
  return Object.fromEntries(Object.entries(source).filter(([name]) =>
    allowed.has(name) || /^interpretation__[A-Za-z][A-Za-z0-9_]*$/.test(name)));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (process.argv.length < 3) {
    console.error('usage: node scripts/explorer-host.mjs <ce-explorer.dll> [options]');
    process.exitCode = 2;
  } else {
    const child = spawn('dotnet', process.argv.slice(2), {
      env: explorerEnvironment(process.env),
      stdio: 'inherit',
    });
    for (const signal of ['SIGINT', 'SIGTERM']) {
      process.on(signal, () => child.kill(signal));
    }
    child.on('error', error => {
      console.error(`Explorer could not start: ${error.code ?? error.name}`);
      process.exitCode = 1;
    });
    child.on('exit', (code, signal) => {
      process.exitCode = code ?? (signal === 'SIGINT' ? 130 : 143);
    });
  }
}
