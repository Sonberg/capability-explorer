import fs from 'node:fs';
import path from 'node:path';
import { policy, snapshot, validate, findings, readJson } from './security-audit-lib.mjs';
const [command, file] = process.argv.slice(2), root = process.cwd();
try {
  if (command === 'snapshot' && file) {
    if (path.isAbsolute(file) || file.split(/[\\/]/).includes('..') || !file.startsWith('.sandbox-cache/')) throw new Error('snapshot output must be under .sandbox-cache');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify(snapshot(root), null, 2) + '\n');
    console.log(`Source snapshot: ${file}`);
  } else if (command === 'plan-check') {
    const p = policy(root), source = snapshot(root, p);
    console.log(JSON.stringify({ requiredCoverage: p.requiredCoverage, files: source.files.length, exclusions: [...p.excludedDirectoryNames, ...p.excludedPaths] }, null, 2));
  } else if (command === 'findings' && file) {
    const manifest = readJson(root, file);
    // Inventory only: no pass verdict, triage or evidence validation is implied.
    console.log(JSON.stringify(manifest.scans.flatMap(scan => findings(scan, readJson(root, scan.report.path))), null, 2));
  } else if (['baseline', 'verify'].includes(command) && file) {
    console.log(JSON.stringify(validate(root, readJson(root, file), command), null, 2));
  } else throw new Error('usage: security-audit snapshot <cache-file> | plan-check | findings|baseline|verify <manifest>');
} catch (error) {
  console.error(`security-audit: ${error.message}`); process.exitCode = 1;
}
