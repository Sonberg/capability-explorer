#!/usr/bin/env node
import { resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { commit, list, need, parseJson, readBounded, readEvidence, text } from './evidence.mjs';
import { validateTool } from './tool-gates.mjs';

/** Historical arithmetic is separate from current measured assessment.
 * @param {any} register @returns {number} */
export function validateRegister(register) {
  need(register?.schemaVersion === 1 && commit(register.baseline), 'invalid research register');
  const weights = register.weights;
  need(weights && Object.keys(weights).sort().join(',') === 'C,D,F,I,M,S', 'wrong rating dimensions');
  need(Object.values(weights).every(w => Number.isFinite(w) && w >= 0)
    && Object.values(weights).reduce((a, b) => a + b, 0) === 100, 'weights must total 100');
  const modes = list(register.historicalModes, 'historical modes');
  const names = new Set();
  for (const m of modes) {
    need(text(m.mode) && !names.has(m.mode) && text(m.role), 'duplicate/invalid historical mode'); names.add(m.mode);
    need(Object.keys(m.historicalRatings ?? {}).sort().join(',') === Object.keys(weights).sort().join(','), 'missing historical rating');
    let total = 0;
    for (const [key, weight] of Object.entries(weights)) {
      const score = m.historicalRatings[key];
      need(Number.isFinite(score) && score >= 0 && score <= 5, 'rating outside 0..5'); total += weight * score / 5;
    }
    need(Math.abs(total - m.historicalDeskScore) < 1e-8, 'incorrect historical total');
    need(text(m.rationale) && text(m.confidence) && text(m.runtimeStatus), 'historical evidence class missing');
    if (m.currentAssessment !== undefined) {
      const c = m.currentAssessment;
      need(c && c.assessmentId !== register.historicalAssessmentDate && text(c.assessmentId), 'current assessment identity missing');
      need(c.role === m.role && c.inheritsHistoricalEvidence === false, 'cross-role or inherited current rank');
      for (const key of Object.keys(weights)) {
        const rating = c.ratings?.[key];
        need(rating && (rating.value === null || (Number.isFinite(rating.value) && rating.value >= 0 && rating.value <= 5)), 'invalid current rating');
        need(rating.value === null || (text(rating.reference) && text(rating.evidenceClass)), 'uncited current rating');
      }
    }
  }
  return modes.length;
}

/** Check exact declared files and hand-labelled anchors without executing input.
 * @param {any} contract @param {string} root @returns {number} */
export function validateCorpus(contract, root) {
  need(contract?.schemaVersion === 1 && contract.scope === 'foundation' && contract.issueComplete === false, 'foundation scope invalid');
  need(commit(contract.baseline), 'corpus baseline missing');
  const files = list(contract.files, 'sealed files', 3);
  need(new Set(files.map(f => f.path)).size === files.length, 'duplicate corpus path');
  const bytes = new Map(files.map(ref => [ref.path, readEvidence(root, ref)]));
  const oracle = parseJson(readEvidence(root, contract.oracle));
  need(oracle.schemaVersion === 1 && list(oracle.files, 'oracle files').length === files.length, 'oracle coverage mismatch');
  const seen = new Set();
  for (const f of oracle.files) {
    need(bytes.has(f.path) && !seen.has(f.path) && text(f.language), 'oracle file mismatch'); seen.add(f.path);
    need(Number.isSafeInteger(f.anchorLine) && f.anchorLine > 0 && text(f.anchor), 'invalid oracle anchor');
    need(bytes.get(f.path).toString('utf8').split('\n')[f.anchorLine - 1] === f.anchor, 'source anchor mismatch');
  }
  return files.length;
}

/** @param {string[]} argv @param {string} root @returns {string} */
export function run(argv, root) {
  if (argv.length === 1 && argv[0] === 'foundation') {
    const register = parseJson(readBounded(root, 'docs/research/code-insights-research-register.json'));
    const contract = parseJson(readBounded(root, 'config/research/foundation.json'));
    const modes = validateRegister(register), files = validateCorpus(contract, root);
    return `foundation: ${modes} historical scores and ${files} sealed source anchors valid; no tool/runtime/research completion claim`;
  }
  if (argv[0] === 'tool') {
    need(argv.length === 5 && argv[1] === '--id' && argv[3] === '--stage', 'usage: tool --id <id> --stage <desk|trial|decision|pr>');
    const id = argv[2], stage = argv[4];
    need(/^[a-z][a-z0-9-]{0,63}$/.test(id), 'invalid tool id');
    const path = `docs/research/code-insights/tools/${id}/dossier.json`;
    const d = parseJson(readBounded(root, path));
    need(d?.proposal?.id === id, 'dossier id mismatch');
    return JSON.stringify(validateTool(d, stage, root));
  }
  if (argv.length === 1 && ['specification', 'pseudocode', 'architecture', 'refinement', 'completion', 'all'].includes(argv[0])) {
    throw new Error(`${argv[0]}: full research phase evidence/executor not delivered by the tool-free foundation; gate remains incomplete`);
  }
  throw new Error('usage: check-code-insights.mjs foundation | tool --id <id> --stage <stage> | <research-phase>');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const root = fileURLToPath(new URL('../../', import.meta.url));
    process.stdout.write(`${run(process.argv.slice(2), root)}\n`);
  } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
