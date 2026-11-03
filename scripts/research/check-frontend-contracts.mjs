#!/usr/bin/env node
import { lstatSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ACCEPTANCE_IDS = [
  'F01', 'F02', 'F03', 'F04', 'F05', 'F06', 'F07',
  'D01', 'D02', 'D03', 'D04', 'D05', 'D06',
  'Q01', 'Q02', 'Q03', 'Q04', 'G01', 'G02',
];
const POLICY_ACCEPTANCE = ACCEPTANCE_IDS;
const COMPATIBILITY_IDS = ['C01', 'C02', 'C03', 'C04', 'C05', 'C06', 'C07', 'C08'];
const POSITIVE_LAYOUTS = [
  'root-project', 'nested-project', 'mixed-language-monorepo', 'multiple-markers',
  'standalone-source', 'overlap-dedupe', 'missing-dependency', 'nested-repository-boundary',
  'deep-glob-ordinal-order', 'explicit-exclude-wins',
];
const NEGATIVE_LAYOUTS = [
  'unknown-language', 'unknown-capability', 'marker-free-ambiguity',
  'conflicting-owner', 'conflicting-override', 'budget-exceeded', 'path-traversal', 'symlink-escape',
  'per-unit-file-budget', 'total-file-budget',
];
const PHASES = ['specification', 'pseudocode', 'architecture', 'all'];
const text = value => typeof value === 'string' && value.trim().length > 0;
const compareUtf8 = (left, right) => Buffer.compare(Buffer.from(left, 'utf8'), Buffer.from(right, 'utf8'));

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
    need(item && text(item.title) && text(item.evidencePath) && text(item.evidenceAnchor)
      && ['markdown-section', 'json-path', 'matrix-cases', 'fixture-cases', 'test-names'].includes(item.evidenceType)
      && text(item.positiveCase) && text(item.negativeCase),
    `${id} needs typed evidence anchors and positive/negative obligations`);
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

function validateSchemaDraft(schema, expected) {
  const errors = [];
  const need = (condition, message) => { if (!condition) errors.push(message); };
  need(schema?.$schema === 'https://json-schema.org/draft/2020-12/schema'
    && schema?.$id === expected.id, `${expected.name} schema identity/dialect mismatch`);
  need(schema?.type === 'object' && schema.additionalProperties === false,
    `${expected.name} root must be a strict object`);
  const properties = schema?.properties ?? {};
  need(expected.required.every(field => (schema?.required ?? []).includes(field)
    && Object.hasOwn(properties, field)), `${expected.name} root required fields incomplete`);
  function strictObjects(node, path = '$') {
    if (!node || typeof node !== 'object') return;
    if (node.type === 'object') {
      need(node.additionalProperties === false, `${expected.name} ${path} must reject unknown properties`);
      need(Array.isArray(node.required) && node.required.every(field => Object.hasOwn(node.properties ?? {}, field)),
        `${expected.name} ${path} required properties must exist`);
    }
    if (Array.isArray(node)) node.forEach((value, index) => strictObjects(value, `${path}[${index}]`));
    else for (const [key, value] of Object.entries(node)) strictObjects(value, `${path}.${key}`);
  }
  strictObjects(schema);
  for (const [path, type] of Object.entries(expected.fields)) {
    const value = path.split('.').reduce((cursor, key) => cursor?.[key], schema);
    need(value?.type === type || (Array.isArray(value?.type) && value.type.includes(type)),
      `${expected.name} field ${path} must have type ${type}`);
  }
  for (const [path, fields] of Object.entries(expected.requiredPaths ?? {})) {
    const required = path.split('.').reduce((cursor, key) => cursor?.[key], schema);
    need(Array.isArray(required) && fields.every(field => required.includes(field)),
      `${expected.name} ${path} required fields incomplete`);
  }
  for (const [path, value] of Object.entries(expected.constants ?? {})) {
    const found = path.split('.').reduce((cursor, key) => cursor?.[key], schema);
    need(found?.const === value, `${expected.name} ${path} must be ${value}`);
  }
  errors.push(...containsCompletionClaim(schema));
  return errors;
}

/** Verify source-bound decision claims and strict schema/compatibility declarations. */
export function validateFixtureCatalogue(scenarios, expected) {
  const errors = [];
  const need = (condition, message) => { if (!condition) errors.push(message); };
  const positives = Array.isArray(scenarios?.positive) ? scenarios.positive : [];
  const negatives = Array.isArray(scenarios?.negative) ? scenarios.negative : [];
  const expectedPositives = Array.isArray(expected?.positive) ? expected.positive : [];
  const expectedNegatives = Array.isArray(expected?.negative) ? expected.negative : [];
  for (const [label, rows, ids] of [
    ['input positive', positives, POSITIVE_LAYOUTS],
    ['input negative', negatives, NEGATIVE_LAYOUTS],
    ['expected positive', expectedPositives, POSITIVE_LAYOUTS],
    ['expected negative', expectedNegatives, NEGATIVE_LAYOUTS],
  ]) {
    const found = rows.map(row => row?.id ?? row?.scenarioId);
    need(found.length === ids.length && new Set(found).size === found.length
      && ids.every(id => found.includes(id)), `${label} inventories must independently cover the required case IDs`);
  }
  need(expected?.version === 1 && expected.status === 'independently-authored-before-reference-oracle',
    'expected inventories must remain a separately authored pre-oracle fixture artifact');
  need(scenarios?.version === 1 && scenarios.status === 'synthetic-policy-inputs-not-production-layouts',
    'fixture inputs must remain explicitly synthetic policy cases');
  const inputIds = new Set([...positives, ...negatives].map(row => row.id));
  for (const item of [...expectedPositives, ...expectedNegatives]) {
    need(inputIds.has(item.scenarioId), `expected inventory has no independent input case: ${item.scenarioId}`);
    need(text(item.status) && Array.isArray(item.units) && Array.isArray(item.diagnostics),
      `${item.scenarioId} expected outcome needs a status, unit list and diagnostics`);
    const units = item.units ?? [];
    const keys = units.map(unit => unit.unitKey);
    need(keys.every(text) && JSON.stringify(keys) === JSON.stringify([...keys].sort(compareUtf8))
      && new Set(keys).size === keys.length, `${item.scenarioId} unit identities must be unique and ordered`);
    for (const unit of units) {
      need(Array.isArray(unit.files) && JSON.stringify(unit.files) === JSON.stringify([...unit.files].sort(compareUtf8))
        && new Set(unit.files).size === unit.files.length,
      `${item.scenarioId} file inventory must be independently ordered and deduplicated`);
      need(Array.isArray(unit.references), `${item.scenarioId} references must be explicit`);
    }
  }
  for (const item of expectedNegatives) {
    need(item.status !== 'complete' && item.units?.length === 0 && item.diagnostics?.length > 0,
      `${item.scenarioId} negative case cannot be successful or hide its failure`);
  }
  const standalone = expectedPositives.find(item => item.scenarioId === 'standalone-source');
  need(standalone?.status === 'ambiguous' && standalone.units?.length === 0,
    'standalone source must remain explicitly ambiguous');
  const overlap = expectedPositives.find(item => item.scenarioId === 'overlap-dedupe');
  need(overlap?.units?.length === 1 && overlap.units[0].files.length === new Set(overlap.units[0].files).size
    && overlap.diagnostics?.includes('same-owner-path-deduplicated'),
  'overlap fixture must preserve one owner and explicitly deduplicate repeated paths');
  const missing = expectedPositives.find(item => item.scenarioId === 'missing-dependency');
  need(missing?.status === 'partial'
    && missing.units?.some(unit => unit.references?.some(ref => ref.status === 'missing-dependency')),
  'missing dependency must remain an explicit partial linked result');
  errors.push(...containsCompletionClaim(scenarios), ...containsCompletionClaim(expected));
  return errors;
}

function validateEvidenceAnchors(contract, root, includeTestNames) {
  const errors = [];
  for (const item of contract.acceptance) {
    try {
      const content = readRegular(root, item.evidencePath, 262144);
      if (item.evidenceType === 'markdown-section') {
        const heading = `## ${item.evidenceAnchor}`;
        if (!content.includes(heading)) errors.push(`${item.id} evidence section missing: ${heading}`);
      } else if (item.evidenceType === 'json-path') {
        const value = JSON.parse(content);
        for (const path of item.evidenceAnchor.split('|')) {
          const target = path.split('.').reduce((cursor, key) => cursor?.[key], value);
          if (target === undefined) errors.push(`${item.id} JSON evidence path missing: ${path}`);
        }
      } else if (item.evidenceType === 'matrix-cases') {
        const ids = new Set(JSON.parse(content).cases.map(row => row.id));
        for (const id of item.evidenceAnchor.split('|')) if (!ids.has(id)) errors.push(`${item.id} compatibility case missing: ${id}`);
      } else if (item.evidenceType === 'fixture-cases') {
        const fixture = JSON.parse(content);
        const ids = new Set([...fixture.positive, ...fixture.negative].map(row => row.scenarioId));
        for (const id of item.evidenceAnchor.split('|')) if (!ids.has(id)) errors.push(`${item.id} expected fixture missing: ${id}`);
      } else if (item.evidenceType === 'test-names' && includeTestNames) {
        for (const name of item.evidenceAnchor.split('|')) if (!content.includes(`'${name}'`) && !content.includes(`"${name}"`)) {
          errors.push(`${item.id} focused test name missing: ${name}`);
        }
      }
    } catch (error) {
      errors.push(`${item.id} evidence unreadable: ${error.message}`);
    }
  }
  return errors;
}

export function validateArchitecture(contract, report, adr, adapterSchema, discoverySchema, compatibility, scenarios, expectedInventories) {
  const errors = [];
  const need = (condition, message) => { if (!condition) errors.push(message); };
  const reportText = report.replace(/\s+/g, ' ');
  need(report.includes(contract.baseline), 'architecture report must bind the exact checked source snapshot');
  for (const heading of [
    '## Exact-source observations', '## Discovery alternatives', '## Producer comparison',
    '## Compatibility matrix', '## Safety, reproducibility and future pilot',
    '## Unknowns, dissent and non-claims', '## Primary references checked 2026-11-03',
  ]) need(report.includes(heading), `architecture report section missing: ${heading}`);
  for (const citation of [
    'schema/v1/record.schema.json#L61-L79',
    'platform/src/CapabilityExplorer.Contracts/Records.cs#L6-L21',
    'extractors/ts/src/extract/project.ts#L19-L43',
    'CapabilityExplorer.Extractors.CSharp/ProjectModel.cs#L56-L68',
    'Definitions.cs#L11-L18',
    'docs/adr/0020-platform-exchange-contracts.md#L1-L23',
    'docs/adr/0013-integration-contract-standards.md#L85-L102',
    'docs/adr/0023-host-acquisition-boundary.md#L61-L65',
  ]) need(report.includes(citation), `exact-source citation missing: ${citation}`);
  for (const producer of ['Compiler APIs (e.g. Roslyn, TypeScript)', 'Tree-sitter', 'SCIP protocol and indexers', 'Controlled LSP']) {
    need(report.includes(producer), `producer comparison missing ${producer}`);
  }
  const producerRows = [];
  let inProducerTable = false;
  for (const line of report.split('## Producer comparison')[1]?.split('\n') ?? []) {
    if (line.trim().startsWith('|')) {
      inProducerTable = true;
      producerRows.push(line.trim());
    } else if (inProducerTable) break;
  }
  need(producerRows.length === 6 && producerRows.every(row => row.split('|').length - 2 === 6),
    'producer comparison header and all four rows must have six aligned cells');
  for (const producer of ['Compiler APIs (e.g. Roslyn, TypeScript)', 'Tree-sitter', 'SCIP protocol and indexers', 'Controlled LSP']) {
    need(producerRows.some(row => row.split('|')[1]?.trim() === producer),
      `producer comparison row missing or misaligned: ${producer}`);
  }
  for (const fact of ['MIT', 'Apache-2.0', 'offline', 'source-span', 'not-measured', 'no analyzed builds/package scripts']) {
    need(reportText.toLowerCase().includes(fact.toLowerCase()), `architecture evidence/limitation missing: ${fact}`);
  }
  need(/^-[ ]Status: proposed for owner review; not adopted or implemented$/m.test(adr),
    'MADR-0025 must remain explicitly proposed and unimplemented');
  need(adr.includes('ce.v1') && adr.includes('ce.platform.v1') && adr.includes('MADR-0013')
    && adr.includes('MADR-0023') && adr.includes('#23'),
  'MADR-0025 must preserve the separate contract and host/corpus boundaries');
  need(adr.includes('recorded language/unit demand') && adr.includes('rollback/stop criteria'),
    'MADR-0025 must gate a future pilot on demand and rollback');

  errors.push(...validateSchemaDraft(adapterSchema, {
    name: 'adapter',
    id: 'https://capability-explorer/schema/research/frontend-adapter.contractDraft.v1.schema.json',
    required: ['contract', 'request', 'result'],
    fields: {
      'properties.request.properties.sourceSnapshot': 'object',
      'properties.request.properties.unit': 'object',
      'properties.request.properties.requestedFeatures': 'array',
      'properties.result.properties.producer': 'object',
      'properties.result.properties.facts': 'array',
      'properties.result.properties.coverage': 'object',
      'properties.result.properties.status': 'string',
    },
    requiredPaths: {
      'properties.result.properties.facts.items.properties.evidence.items.required': [
        'startByte', 'endByte', 'byteOffsetEncoding',
      ],
    },
    constants: {
      'properties.result.properties.facts.items.properties.evidence.items.properties.byteOffsetEncoding': 'source-bytes',
    },
  }));
  errors.push(...validateSchemaDraft(discoverySchema, {
    name: 'discovery',
    id: 'https://capability-explorer/schema/research/frontend-discovery.contractDraft.v1.schema.json',
    required: ['contract', 'sourceSnapshot', 'policy', 'units', 'coverage', 'status'],
    fields: {
      'properties.sourceSnapshot': 'object',
      'properties.policy': 'object',
      'properties.policy.properties.maxFilesPerUnit': 'integer',
      'properties.units': 'array',
      'properties.coverage': 'object',
      'properties.status': 'string',
    },
    requiredPaths: {
      'properties.policy.required': ['maxFilesPerUnit', 'maxFiles'],
    },
  }));

  const cases = Array.isArray(compatibility?.cases) ? compatibility.cases : [];
  need(compatibility?.version === 1 && compatibility.status === 'proposed-closed-profiles'
    && compatibility.currentArtifact === 'ce.v1'
    && compatibility.draftAdapterContract === 'ce.frontend.contractDraft.v1',
  'compatibility namespace/version boundary mismatch');
  need(cases.length === COMPATIBILITY_IDS.length && new Set(cases.map(item => item?.id)).size === cases.length
    && COMPATIBILITY_IDS.every(id => cases.some(item => item?.id === id)),
  'compatibility matrix must cover C01-C08 exactly once');
  for (const id of COMPATIBILITY_IDS) {
    const item = cases.find(entry => entry?.id === id);
    need(item && text(item.producer) && text(item.consumer) && text(item.input)
      && text(item.expected) && text(item.rollback),
    `${id} must state producer, consumer, input, expected result and rollback`);
  }
  need(cases.find(item => item.id === 'C01')?.expected.includes('accept')
    && cases.find(item => item.id === 'C03')?.expected.includes('reject'),
  'old artifact acceptance and old-consumer rejection of new vocabulary must be distinct');
  need(Array.isArray(compatibility?.alternatives)
    && ['versioned-closed-profiles', 'registry-reference'].every(id => compatibility.alternatives.some(item => item.id === id)),
  'versioned-profile and registry alternatives must both be explicit');
  errors.push(...containsCompletionClaim(compatibility));
  errors.push(...validateFixtureCatalogue(scenarios, expectedInventories));
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
  if (['architecture', 'all'].includes(argv[0])) {
    const report = readRegular(root, 'docs/research/language-neutral-frontends.md', 262144);
    const adr = readRegular(root, 'docs/adr/0025-language-neutral-frontends-and-discovery.md', 131072);
    const adapterSchema = JSON.parse(readRegular(root, 'schema/research/frontend-adapter.schema.json', 131072));
    const discoverySchema = JSON.parse(readRegular(root, 'schema/research/frontend-discovery.schema.json', 131072));
    const compatibility = JSON.parse(readRegular(root, 'config/research/frontend-compatibility.json'));
    const scenarios = JSON.parse(readRegular(root, 'tests/fixtures/frontend-contracts/layout-scenarios.json', 131072));
    const expectedInventories = JSON.parse(readRegular(root, 'tests/fixtures/frontend-contracts/expected-inventories.json', 131072));
    errors.push(...validateArchitecture(contract, report, adr, adapterSchema, discoverySchema, compatibility, scenarios, expectedInventories));
    errors.push(...validateEvidenceAnchors(contract, root, argv[0] === 'all'));
  }
  if (errors.length) throw new Error(errors.join('\n'));
  const summary = `${argv[0]}: ${ACCEPTANCE_IDS.length} issue obligations traced; constraints checked; no implementation or semantic-accuracy claim`;
  if (argv[0] !== 'all') return summary;
  const tests = spawnSync(process.execPath, ['--test', 'tests/frontend-contracts.test.mjs'],
    { cwd: root, encoding: 'utf8' });
  if (tests.error || tests.status !== 0) throw new Error(`focused refinement tests failed:\n${tests.stdout ?? ''}${tests.stderr ?? tests.error?.message ?? ''}`);
  return `${summary}\n${tests.stdout.trim()}`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { process.stdout.write(run(process.argv.slice(2), fileURLToPath(new URL('../../', import.meta.url))) + '\n'); }
  catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
