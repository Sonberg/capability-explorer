import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readJSON, specification } from './contracts.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
try {
  const [mode = 'all'] = process.argv.slice(2);
  if (mode !== 'specification') throw new Error(`mode not implemented yet: ${mode}`);
  specification(readJSON(resolve(root, 'config/decisions/requirements.json')));
  console.log('decision-ledger: specification PASS (seven obligations; lead-only lifecycle boundary)');
} catch (error) {
  console.error(`decision-ledger: ${error.message}`);
  process.exitCode = 1;
}
