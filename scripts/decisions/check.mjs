import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { readJSON, specification, pseudocode } from './contracts.mjs';
import { auditSchema } from './schema.mjs';
import { loadLedger } from './ledger.mjs';
import { projections, outputViews } from './project.mjs';
import { canonical } from './semantics.mjs';
import { sha256, localPath } from './source.mjs';

const codeRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
try {
  const [mode = 'all', ...flags] = process.argv.slice(2), options = {};
  if (!['specification', 'pseudocode', 'architecture', 'all', 'check', 'generate'].includes(mode))
    throw new Error(`unknown mode: ${mode}`);
  for (let i = 0; i < flags.length; i++) {
    const flag = flags[i];
    if (!['--root', '--config', '--output', '--dry-run'].includes(flag) || Object.hasOwn(options, flag))
      throw new Error(`unknown/duplicate option: ${flag}`);
    if (flag === '--dry-run') options[flag] = true;
    else {
      if (!flags[i + 1] || flags[i + 1].startsWith('--')) throw new Error(`missing value: ${flag}`);
      options[flag] = flags[++i];
    }
  }
  if (options['--dry-run'] && mode !== 'generate') throw new Error('--dry-run only applies to generate');
  if (options['--output'] && !['generate', 'check'].includes(mode)) throw new Error('--output only applies to generate/check');
  const root = resolve(options['--root'] ?? process.cwd());
  const configPath = options['--config'] ?? 'config/decisions/project.json';
  if (['all', 'specification'].includes(mode))
    specification(readJSON(resolve(codeRoot, 'config/decisions/requirements.json')));
  if (['all', 'pseudocode'].includes(mode))
    pseudocode(readJSON(resolve(codeRoot, 'config/decisions/examples.json')));
  if (['all', 'architecture', 'generate', 'check'].includes(mode)) {
    auditSchema();
    const config = readJSON(localPath(root, configPath)), ledger = loadLedger(root, configPath);
    if (['generate', 'check'].includes(mode)) {
      const output = options['--output'] ?? config.output;
      const views = projections(ledger, config, root, output);
      if (options['--dry-run']) console.log(canonical(Object.fromEntries(
        Object.entries(views).map(([name, bytes]) => [name, { bytes: Buffer.byteLength(bytes), sha256: sha256(bytes) }])
      )).trimEnd());
      else outputViews(root, output, views, mode);
    } else console.log(`decision-ledger: ${ledger.records.length} records, ${ledger.edges.length} asserted edges, ${ledger.diagnostics.length} explicit diagnostics`);
  }
  if (mode === 'all') {
    const result = spawnSync(process.execPath, ['--test', 'tests/decision-ledger.test.mjs'], { cwd: codeRoot, stdio: 'inherit' });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(`focused tests failed: ${result.status}`);
  }
  console.log(`decision-ledger: ${mode} PASS`);
} catch (error) {
  console.error(`decision-ledger: ${error.message}`);
  process.exitCode = 1;
}
