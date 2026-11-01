import path from 'node:path';
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
    unsupportedConfiguration: names.filter(x => /\.(?:json|ya?ml|toml|props|targets|xml|config|sh)$/.test(x) || x.startsWith('scripts/') || x === 'Makefile'),
  };
}
