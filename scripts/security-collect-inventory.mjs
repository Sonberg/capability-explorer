import path from 'node:path';
import fs from 'node:fs';
const need = (ok, message) => { if (!ok) throw new Error(message); };
const pin = value => typeof value === 'string' && /^[\w./:-]+@sha256:[a-f0-9]{64}$/.test(value);
const platform = value => /^linux\/(arm64|amd64)$/.test(value ?? '');
export function validateConfig(config) {
  need(config.schemaVersion === 1 && config.tools && Array.isArray(config.images) && config.images.length, 'missing collector configuration/image inventory');
  need(Object.keys(config.tools).sort().join(',') === 'gitleaks,semgrep,trivy', 'unexpected scanner tools');
  for (const name of ['trivy', 'gitleaks', 'semgrep']) {
    const tool = config.tools[name]; need(pin(tool?.image) && platform(tool?.platform), `unpinned tool/platform: ${name}`);
  }
  const seen = new Set();
  for (const image of config.images) {
    need(/^image-[a-z0-9-]+$/.test(image.id ?? '') && !seen.has(image.id) && pin(image.reference) && platform(image.platform), 'invalid/duplicate image inventory'); seen.add(image.id);
  }
  return config;
}
export function inventories(files) {
  const names = files.map(x => x.path).sort();
  return {
    source: names,
    dependencies: names.filter(x => /(?:^|\/)(?:package-lock\.json|npm-shrinkwrap\.json|packages\.lock\.json|Directory\.Packages\.props|yarn\.lock|pnpm-lock\.yaml|Cargo\.lock|go\.sum|requirements[^/]*\.txt|poetry\.lock|Gemfile\.lock|composer\.lock)$/.test(x)),
    sast: names.filter(x => /\.(?:cs|[cm]?js|jsx|ts|tsx|sh|bash)$/.test(x) || x.startsWith('scripts/') && !path.basename(x).includes('.')),
    // This declared first profile deliberately supports Dockerfile/Terraform only.
    // YAML semantics cannot be classified by suffix; all other config requires manual review.
    misconfiguration: names.filter(x => /(?:^|\/)(?:Dockerfile(?:\.[^/]*)?|[^/]+\.tf)$/.test(x)),
    unsupportedConfiguration: names.filter(x => /\.(?:json|ya?ml|toml|props|targets|xml|config|env|sh)$/.test(x) || x.startsWith('scripts/') || x === 'Makefile'),
  };
}

// A reviewed central registry is authoritative; do not infer image dependencies
// from arbitrary source strings. Fixed launch sites are checked for stale literal pins.
export function runtimeInventory(root, config) {
  const registryFile = path.join(root, 'config/runtime/images.env');
  const registry = new Map();
  for (const raw of fs.readFileSync(registryFile, 'utf8').split(/\r?\n/)) {
    const line = raw.trim(); if (!line || line.startsWith('#')) continue;
    const match = /^([A-Z][A-Z0-9_]*_IMAGE)=(\S+)$/.exec(line);
    need(match && pin(match[2]) && !registry.has(match[1]), 'runtime image registry must contain unique literal digest-pinned assignments');
    registry.set(match[1], match[2]);
  }
  for (const role of ['DOTNET_IMAGE', 'NODE_IMAGE', 'PG_IMAGE', 'OLLAMA_IMAGE', 'ACT_IMAGE', 'BROWSER_IMAGE', 'RYUK_IMAGE'])
    need(registry.has(role), `missing required runtime image role: ${role}`);
  const required = new Set([...registry.values(), ...Object.values(config.tools).map(tool => tool.image)]);
  for (const reference of required) need(config.images.some(image => image.reference === reference), `image inventory omits runtime/scanner reference: ${reference}`);
  for (const tool of Object.values(config.tools)) need(config.images.some(image => image.reference === tool.image && image.platform === tool.platform), 'scanner image platform absent from vulnerability inventory');
  const launchSites = ['scripts/sandbox', 'scripts/local-check', 'Makefile', '.github/workflows/ci.yml', 'platform/tests/CapabilityExplorer.Store.Tests/ImporterTests.cs'];
  for (const relative of launchSites) {
    const file = path.join(root, relative); need(fs.existsSync(file), `missing required image launch site: ${relative}`);
    const content = fs.readFileSync(file, 'utf8');
    for (const match of content.matchAll(/[A-Za-z0-9][A-Za-z0-9._/:+-]*@sha256:[a-f0-9]{64}/g))
      need(required.has(match[0]), `unregistered image at ${relative}: ${match[0]}`);
  }
  const trivy = fs.readFileSync(path.join(root, 'config/security/trivy-image'), 'utf8').trim();
  need(trivy === config.tools.trivy.image, 'legacy Trivy gate image differs from collector');
  return { roles: Object.fromEntries(registry), references: [...required].sort() };
}
