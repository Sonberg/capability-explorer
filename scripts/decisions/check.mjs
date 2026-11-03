import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readJSON, specification, pseudocode } from './contracts.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
try {
  const [mode = 'all'] = process.argv.slice(2);
  if (!['specification', 'pseudocode'].includes(mode)) throw new Error(`mode not implemented yet: ${mode}`);
  if (mode === 'specification') specification(readJSON(resolve(root, 'config/decisions/requirements.json')));
  else pseudocode(readJSON(resolve(root, 'config/decisions/examples.json')));
  console.log(`decision-ledger: ${mode} PASS`);
} catch (error) {
  console.error(`decision-ledger: ${error.message}`);
  process.exitCode = 1;
}
