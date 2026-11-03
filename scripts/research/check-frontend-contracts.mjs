#!/usr/bin/env node
import { lstatSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ACCEPTANCE_IDS = [
  'F01', 'F02', 'F03', 'F04', 'F05', 'F06', 'F07',
  'D01', 'D02', 'D03', 'D04', 'D05', 'D06',
  'Q01', 'Q02', 'Q03', 'Q04', 'G01', 'G02',
];
const POLICY_ACCEPTANCE = ACCEPTANCE_IDS;
const PHASES = ['specification', 'pseudocode', 'architecture', 'all'];
const text = value => typeof value === 'string' && value.trim().length > 0;

function readRegular(root, path, limit = 131072) {
  const file = join(root, path);
  const stat = lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > limit) {
    throw new Error(`${path} must be a regular file <=${limit} bytes`);
  }
  return readFileSync(file, 'utf8');
}

function containsCompletionClaim(value, path = '$') {
  const keys = new Set(['complete', 'completed', 'implemented', 'passed', 'satisfied', 'verified']);
  if (!value || typeof value !== 'object') return [];
  const errors = [];
  for (const [key, child] of Object.entries(value)) {
    if (keys.has(key.toLowerCase())) errors.push(`${path}.${key} is a self-attested status field`);
    errors.push(...containsCompletionClaim(child, `${path}.${key}`));
  }
  return errors;
}

/** Validate issue-checkbox traceability and concrete evidence obligations, not status assertions. */
export function validateSpecification(contract, specification) {
  const errors = [];
  const need = (condition, message) => { if (!condition) errors.push(message); };
  need(contract?.version === 1 && contract.issue === 42
    && contract.scope === 'bounded-language-neutral-research', 'issue/scope identity mismatch');
  need(/^[0-9a-f]{40}$/.test(contract?.baseline ?? ''), 'baseline must be a full source commit');

  const criteria = Array.isArray(contract?.acceptance) ? contract.acceptance : [];
  const ids = criteria.map(item => item?.id);
  need(ids.length === ACCEPTANCE_IDS.length && new Set(ids).size === ids.length
    && ACCEPTANCE_IDS.every(id => ids.includes(id)), 'acceptance trace must map all 19 issue obligations exactly once');
  for (const id of ACCEPTANCE_IDS) {
    const item = criteria.find(entry => entry?.id === id);
    need(item && text(item.title) && text(item.evidencePath) && text(item.evidenceHeading)
      && text(item.positiveCase) && text(item.negativeCase),
    `${id} needs named evidence and positive/negative obligations`);
  }
  const specIds = [...specification.matchAll(/^\|\s*([A-Z]\d{2})\s*\|/gm)].map(match => match[1]);
  need(specIds.length === ACCEPTANCE_IDS.length && ACCEPTANCE_IDS.every(id => specIds.includes(id)),
    'specification table must independently map every issue obligation');

  const requiredConstraints = [
    'No parser adoption or new-language choice without recorded demand.',
    'Do not modify schema/v1/record.schema.json or change the ce.v1 language vocabulary.',
    'Do not implement production repository discovery or add runtime dependencies.',
    'Do not execute analyzed repository build/package scripts or inherit credentials.',
    'Do not use undeclared network access or traverse outside an immutable source snapshot.',
    'Do not claim fixture conformance proves semantic resolution or all repository layouts.',
    'Keep #33 exchange-envelope, #12 contract-document evidence, #6 resolution, #37/#48 frozen-source boundary and #23 corpus limitation distinct.',
    'Issue 42 excludes DDD business-object, domain-event and integration-event grading.',
  ];
  for (const constraint of requiredConstraints) {
    need(Array.isArray(contract?.constraints) && contract.constraints.includes(constraint),
      `machine-readable constraint missing: ${constraint}`);
    need(specification.includes(constraint), `specification constraint missing: ${constraint}`);
  }
  errors.push(...containsCompletionClaim(contract));
  return errors;
}

/** Cross-check ordered policy steps, failure paths, acceptance trace and complexity. */
export function validatePseudocode(policy, pseudocode) {
  const errors = [];
  const need = (condition, message) => { if (!condition) errors.push(message); };
  need(policy?.version === 1 && policy.scope === 'proposed-deterministic-discovery-policy'
    && policy.algorithm === 'discover(snapshot, registry, declaration, budgets)', 'pseudocode policy identity mismatch');

  const algorithmBlock = heading => pseudocode.split(heading)[1]?.match(/```[\s\S]*?\n([\s\S]*?)```/)?.[1] ?? '';
  const checkOrderedSteps = (heading, declaredValue, prefix, count) => {
    const documented = [...algorithmBlock(heading).matchAll(new RegExp('^\\s*(' + prefix + '\\d{2})\\s+(.+)$', 'gm'))]
      .map(match => ({ id: match[1], operation: match[2].trim() }));
    const declared = Array.isArray(declaredValue) ? declaredValue : [];
    need(declared.length === count && documented.length === count
      && declared.every((step, index) => step?.id === `${prefix}${String(index + 1).padStart(2, '0')}`
        && step.id === documented[index]?.id && step.operation === documented[index]?.operation && text(step.failure)),
    `${prefix}01-${prefix}${String(count).padStart(2, '0')} must be ordered, independently documented and carry explicit failure outcomes`);
    return new Set(declared.map(step => step?.id));
  };
  const layoutIds = checkOrderedSteps('## Deterministic bounded procedure', policy?.steps, 'P', 11);
  const adapterIds = checkOrderedSteps('## Adapter and compatibility procedure', policy?.adapterSteps, 'A', 11);
  const stepIds = new Set([...layoutIds, ...adapterIds]);
  const traces = Array.isArray(policy?.requirementTrace) ? policy.requirementTrace : [];
  need(traces.length === POLICY_ACCEPTANCE.length
    && new Set(traces.map(trace => trace?.acceptanceId)).size === traces.length
    && POLICY_ACCEPTANCE.every(id => traces.some(trace => trace?.acceptanceId === id)),
  'policy trace must cover every issue acceptance ID exactly once');
  for (const id of POLICY_ACCEPTANCE) {
    const trace = traces.find(entry => entry?.acceptanceId === id);
    need(Array.isArray(trace?.steps) && trace.steps.length > 0
      && new Set(trace.steps).size === trace.steps.length
      && trace.steps.every(step => stepIds.has(step)),
    `${id} must map to existing distinct policy steps`);
  }

  const complexity = policy?.complexity;
  for (const [key, expected] of Object.entries({
    traversalAndNormalization: 'O(D + F + B)',
    sorting: 'O((F + M + R) log(F + M + R))',
    referenceIndex: 'O(R)',
    memory: 'O(F + M + R)',
  })) {
    need(complexity?.[key] === expected && pseudocode.includes(expected), `complexity bound ${key} missing or undocumented`);
  }
  need(Array.isArray(complexity?.boundedBy)
    && ['depth', 'candidateMarkers', 'filesPerUnit', 'totalFiles', 'aggregateBytes'].every(item => complexity.boundedBy.includes(item)),
  'complexity must be bounded by every hard input budget');
  const lowerPseudocode = pseudocode.replace(/\s+/g, ' ').toLowerCase();
  const requiredSemantics = [
    'unitkey = "unit:v1/"', 'nearest ownership', 'same-owner deduplication',
    'equal-depth cross-unit overlap', 'missing-dependency', 'limit-exceeded',
    'marker-free repository', 'no source path is resolved through a symlink.',
    'timestamps', 'precision/recall',
  ];
  for (const phrase of requiredSemantics) need(lowerPseudocode.includes(phrase), `pseudocode semantics missing: ${phrase}`);
  for (const phrase of [
    'ce.v1', 'not-measured', 'false/missing links', 'rollback',
    'without language selection', 'never run repository scripts',
  ]) need(lowerPseudocode.includes(phrase), `adapter/qualification semantics missing: ${phrase}`);
  errors.push(...containsCompletionClaim(policy));
  return errors;
}

/** @param {string[]} argv @param {string} root */
export function run(argv, root) {
  if (argv.length !== 1 || !PHASES.includes(argv[0])) {
    throw new Error('usage: check-frontend-contracts.mjs specification|pseudocode|architecture|all');
  }
  const contract = JSON.parse(readRegular(root, 'config/research/frontend-contracts.json'));
  const specification = readRegular(root, 'docs/research/language-neutral-frontends-specification.md');
  const errors = validateSpecification(contract, specification);
  if (argv[0] !== 'specification') {
    const policy = JSON.parse(readRegular(root, 'config/research/frontend-layout-policy.json'));
    const pseudocode = readRegular(root, 'docs/research/language-neutral-frontends-pseudocode.md');
    errors.push(...validatePseudocode(policy, pseudocode));
  }
  if (errors.length) throw new Error(errors.join('\n'));
  return `${argv[0]}: ${ACCEPTANCE_IDS.length} issue obligations traced; constraints checked; no implementation or semantic-accuracy claim`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { process.stdout.write(run(process.argv.slice(2), fileURLToPath(new URL('../../', import.meta.url))) + '\n'); }
  catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
