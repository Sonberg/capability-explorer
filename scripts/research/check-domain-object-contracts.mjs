#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  computeAssessmentIdentity,
  computeReviewBasis,
  validateDraftRecord,
} from '../../tests/fixtures/domain-object-contracts/schema-validator.mjs';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const CASES = 'tests/fixtures/domain-object-contracts/cases.json';
const EXPECTED = 'tests/fixtures/domain-object-contracts/expected.json';
const SCHEMA = 'schema/research/domain-object-assessment.schema.json';
const CONFIG = 'config/research/domain-object-contracts.json';
const RUBRIC = 'config/research/domain-object-rubric.json';
const IDS = ['DO01', 'DO02', 'DO03', 'DO04', 'DO05', 'DO06', 'DO07'];
const GRADES = ['bronze', 'silver', 'gold', 'platinum'];
const ALLOWED_SCHEMA_KEYS = new Set([
  '$schema', '$id', '$defs', 'title', 'description', 'type', 'additionalProperties',
  'required', 'properties', 'const', 'enum', '$ref', 'pattern', 'minLength',
  'minItems', 'minimum', 'items', 'format', 'allOf', 'if', 'then',
]);
const encoder = new TextEncoder();
const utf8Compare = (a, b) => Buffer.compare(Buffer.from(a, 'utf8'), Buffer.from(b, 'utf8'));
const sortById = items => [...items].sort((a, b) => utf8Compare(a.id, b.id));
const digest = value => createHash('sha256').update(value).digest('hex');
const canonicalize = value => {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort(utf8Compare).map(key => [key, canonicalize(value[key])]));
  }
  return value;
};
export const canonicalBytes = value => encoder.encode(JSON.stringify(canonicalize(value)));
const readJson = path => JSON.parse(readFileSync(join(ROOT, path), 'utf8'));
export const readFixtureData = () => ({
  cases: readJson(CASES),
  expected: readJson(EXPECTED),
  rubric: readJson(RUBRIC),
  schema: readJson(SCHEMA),
});

function scenarioErrors(scenario, fixtureRoot) {
  const errors = [];
  const evidence = new Set(scenario.evidenceIds ?? []);
  const reviews = new Set(scenario.reviewIds ?? []);
  const hasEvidence = prefix => [...evidence].some(id => id.startsWith(prefix));
  const hasReview = prefix => [...reviews].some(id => id.startsWith(prefix));
  if (evidence.size !== (scenario.evidenceIds ?? []).length) errors.push('duplicate evidence id');
  if (reviews.size !== (scenario.reviewIds ?? []).length) errors.push('duplicate review id');
  if (!['business-object', 'domain-event', 'integration-event'].includes(scenario.profile)) errors.push('unknown profile');
  if (!['approved', 'needs-review', 'policy-violation', 'unknown'].includes(scenario.exposure)) errors.push('unknown exposure verdict');
  if (!['supported', 'partial', 'unsupported'].includes(scenario.sourceShape)) errors.push('unknown source shape');
  if (!['unknown', 'ambiguous'].includes(scenario.context) && !fixtureRoot.contextIds.includes(scenario.context)) {
    errors.push(`context is absent from fixture context map: ${scenario.context}`);
  }
  if (!['unknown', 'ambiguous'].includes(scenario.context) &&
      fixtureRoot.contextAssignments?.[scenario.id] !== scenario.context) {
    errors.push(`context assignment lacks matching current map evidence: ${scenario.context}`);
  }
  if (scenario.glossary?.status === 'current-reviewed' &&
      (scenario.glossary.context !== scenario.context || scenario.glossary.version !== scenario.versions.glossary)) {
    errors.push('current glossary decision is stale or belongs to another context');
  }
  if (scenario.exposure === 'approved') {
    if (!scenario.audience || scenario.audience === 'unknown' || !hasEvidence('exposure-evidence-') ||
        !hasReview('exposure-review-')) errors.push('approved exposure lacks known audience or reviewed evidence');
    for (const field of scenario.fields) {
      if (field.shape !== 'supported' || !field.purpose || field.purpose === 'unknown' ||
          !field.audience || field.audience !== scenario.audience) errors.push(`approved exposure lacks supported purpose/audience for ${field.path}`);
    }
  }
  if (scenario.context !== 'unknown' && scenario.context !== 'ambiguous' &&
      (!hasEvidence('context-evidence-') || !hasReview('context-review-'))) {
    errors.push('assigned context lacks explicit context-map review evidence');
  }
  if (scenario.classification !== 'unknown' &&
      (!hasEvidence('classification-evidence-') || !hasReview('classification-review-'))) {
    errors.push('classification lacks explicit review evidence');
  }
  if (scenario.glossary?.status === 'current-reviewed' &&
      (!hasEvidence('glossary-evidence-') || !hasReview('glossary-review-'))) {
    errors.push('current glossary meaning lacks context-scoped review evidence');
  }
  const validCriterionStatuses = new Set(['pass', 'fail', 'unknown', 'not-applicable']);
  for (const [criterion, status] of Object.entries(scenario.criteria ?? {})) {
    if (!validCriterionStatuses.has(status)) errors.push(`invalid criterion status for ${criterion}: ${status}`);
  }
  for (const relation of scenario.relations ?? []) {
    if (!evidence.has(relation.evidence)) errors.push(`invented relation evidence: ${relation.evidence}`);
    if (relation.kind === 'explicit-projection') {
      if (!reviews.has(relation.mappingReview)) errors.push(`invented projection review: ${relation.mappingReview}`);
      if (!Array.isArray(relation.mapping) || !relation.mapping.length) errors.push('projection needs explicit field mapping');
      for (const mapping of relation.mapping ?? []) {
        if (!(scenario.sourceFields?.[relation.from] ?? []).includes(mapping.from)) errors.push(`invented source field: ${mapping.from}`);
        if (!scenario.fields.some(field => field.path === mapping.to)) errors.push(`invented target field: ${mapping.to}`);
        if (!mapping.transform) errors.push('projection mapping requires transformation semantics');
      }
    }
    if (relation.kind === 'same-concept' && scenario.subjects?.some((left, index) =>
      scenario.subjects.slice(index + 1).some(right => left.name === right.name && left.context !== right.context))) {
      errors.push('same spelling across distinct contexts cannot establish same-concept');
    }
  }
  return errors;
}

export function evaluateScenario(scenario, fixtureRoot, rubric) {
  const errors = scenarioErrors(scenario, fixtureRoot);
  if (errors.length) return { state: 'blocked', grade: null, classification: 'unknown', exposure: scenario.exposure, errors };
  const classification = scenario.classification;
  const profile = scenario.profile;
  const exposure = scenario.exposure;
  if (exposure === 'policy-violation') return { state: 'blocked', grade: null, classification, exposure, hardCeilings: ['policy-violation'] };
  if (classification === 'command' || (classification !== 'unknown' && classification !== profile)) {
    return { state: 'blocked', grade: null, classification, exposure, hardCeilings: ['classification-does-not-match-profile'] };
  }
  if (exposure !== 'approved') {
    const state = exposure === 'needs-review' ? 'insufficient-evidence' : 'unassessed';
    return { state, grade: null, classification, exposure, hardCeilings: ['exposure-not-approved'] };
  }
  if (classification === 'unknown' || scenario.context === 'unknown' || scenario.context === 'ambiguous') {
    return { state: 'unassessed', grade: null, classification, exposure, hardCeilings: ['semantic-identity-unknown'] };
  }
  if (scenario.sourceShape !== 'supported' ||
      scenario.fields.some(field => ['dynamic', 'recursive', 'external', 'unknown'].includes(field.shape))) {
    return { state: classification === 'command' ? 'blocked' : 'unassessed', grade: null, classification, exposure, hardCeilings: ['classification-or-shape-incomplete'] };
  }
  const tiers = rubric.profiles[profile].tiers;
  let grade = null;
  let state = 'insufficient-evidence';
  for (const tier of GRADES) {
    let tierStatus = 'pass';
    for (const criterion of tiers[tier]) {
      let actual = scenario.criteria[criterion] ?? 'unknown';
      if (criterion === 'current-context-language-review' &&
          (scenario.glossary.status !== 'current-reviewed' ||
           scenario.glossary.context !== scenario.context ||
           scenario.glossary.version !== scenario.versions.glossary)) actual = 'unknown';
      if (actual === 'fail') { tierStatus = 'fail'; break; }
      if (actual === 'unknown') { tierStatus = 'unknown'; break; }
      if (actual === 'not-applicable') {
        const rationale = scenario.applicability?.[criterion];
        if (!rationale || criterion.includes('exposure') || criterion.includes('classification')) {
          errors.push(`invalid not-applicable decision for ${criterion}`);
          tierStatus = 'fail';
          break;
        }
      }
    }
    if (tierStatus === 'pass') {
      grade = tier;
      state = 'graded';
      continue;
    }
    if (!grade) state = tierStatus === 'fail' ? 'blocked' : 'insufficient-evidence';
    break;
  }
  return { state, grade, classification, exposure, hardCeilings: errors };
}

export function materializeDraftRecord(scenario, fixturePath = CASES) {
  const sourceHash = digest(Buffer.from(canonicalBytes(scenario)));
  const source = {
    kind: 'synthetic-fixture',
    path: fixturePath,
    pointer: `/cases/${scenario.id}`,
    sha256: sourceHash,
  };
  const configHash = digest(Buffer.from(canonicalBytes({ profile: scenario.profile, versions: scenario.versions })));
  const versions = {
    extractor: { name: 'sealed-fixture-reference', version: '1', bindingVersion: 'fixture-binding/1', configHash, ruleVersion: 'fixture-rules/1' },
    rubric: scenario.versions.rubric,
    glossary: scenario.versions.glossary,
    contextMap: scenario.versions.contextMap,
    exposurePolicy: scenario.versions.policy,
  };
  const sourceEvidenceId = `source-${scenario.id}`;
  const evidence = [...new Set([sourceEvidenceId, ...scenario.evidenceIds])].sort(utf8Compare).map(id => ({ id, source: { ...source } }));
  const compact = value => Object.fromEntries(Object.entries(value).filter(([, child]) => child !== undefined));
  const fieldRecords = scenario.fields.map((field, index) => compact({
    id: `${scenario.id}-field-${index + 1}`,
    path: field.path,
    type: 'fixture-shape-unspecified',
    shape: field.shape === 'unsupported' ? 'unknown' : field.shape,
    meaningStatus: scenario.glossary.status === 'current-reviewed' ? 'reviewed' : scenario.glossary.status === 'ambiguous' ? 'ambiguous' : 'unknown',
    purpose: field.purpose,
    audience: field.audience,
    sensitivity: field.sensitivity,
    envelope: field.envelope,
    children: [],
  }));
  const subject = {
    id: scenario.id,
    kind: scenario.classification,
    name: scenario.subject,
    contextId: ['unknown', 'ambiguous'].includes(scenario.context) ? null : scenario.context,
    contextStatus: scenario.context === 'ambiguous' ? 'ambiguous' : scenario.context === 'unknown' ? 'unknown' : 'reviewed',
    contextEvidence: ['unknown', 'ambiguous'].includes(scenario.context) ? [] : [scenario.evidenceIds.find(id => id.startsWith('context-evidence-')) ?? sourceEvidenceId],
    alternatives: scenario.subjects?.map(item => 'business-object') ?? [],
    fields: fieldRecords,
  };
  const reviews = scenario.reviewIds.map(id => {
    const decision = id.startsWith('classification-') ? 'classification'
      : id.startsWith('context-') ? 'context-ownership'
        : id.startsWith('glossary-') ? 'glossary'
          : id.startsWith('exposure-') ? 'exposure' : 'projection';
    const version = decision === 'context-ownership' ? versions.contextMap
      : decision === 'glossary' ? versions.glossary
        : decision === 'exposure' ? versions.exposurePolicy : scenario.versions.rubric;
    const evidenceId = scenario.evidenceIds.find(evidenceRef => evidenceRef.startsWith(`${decision === 'context-ownership' ? 'context' : decision}-evidence-`)) ??
      scenario.evidenceIds.find(evidenceRef => evidenceRef.startsWith('reviewed-map-'));
    return compact({
      id,
      subjectId: scenario.id,
      decision,
      status: 'current',
      basis: 'synthetic-oracle',
      reviewer: 'fixture-only oracle; not a human reviewer',
      reviewedAt: '2026-11-03T00:00:00Z',
      version,
      reviewBasisDigest: '',
      contextId: ['unknown', 'ambiguous'].includes(scenario.context) ? undefined : scenario.context,
      evidence: [evidenceId ?? sourceEvidenceId],
    });
  });
  const relations = scenario.relations.map((relation, index) => compact({
    id: `${scenario.id}-relation-${index + 1}`,
    kind: relation.kind,
    from: relation.from,
    to: relation.to,
    evidence: [relation.evidence],
    basis: relation.mappingReview ? 'current-reviewed-assertion' : 'source-fact',
    reviewId: relation.mappingReview,
    fieldMap: relation.mapping?.map(mapping => ({ fromPath: mapping.from, toPath: mapping.to, transform: mapping.transform })),
  }));
  const exposureReviewId = scenario.reviewIds.find(id => id.startsWith('exposure-review-')) ?? `exposure-review-${scenario.id}`;
  const exposure = {
    verdict: scenario.exposure,
    ruleId: 'fixture-exposure-rule/1',
    policyOwner: 'fixture-policy-owner; synthetic expectation only',
    audience: scenario.audience,
    evidence: [scenario.evidenceIds.find(id => id.startsWith('exposure-evidence-')) ?? sourceEvidenceId],
    reviewId: exposureReviewId,
  };
  const criteria = Object.entries(scenario.criteria).map(([id, status]) => compact({
    id,
    status,
    applicability: status === 'not-applicable' ? 'excluded' : 'applicable',
    rationale: scenario.applicability?.[id],
    evidence: status === 'unknown' ? [] : [scenario.evidenceIds[0] ?? sourceEvidenceId],
  })).sort((a, b) => utf8Compare(a.id, b.id));
  const facts = fieldRecords.map((field, index) => ({
    id: `${scenario.id}-fact-${index + 1}`,
    kind: 'shape',
    subjectId: scenario.id,
    fieldPath: field.path,
    evidence: scenario.evidenceIds.includes(`classification-evidence-${scenario.id}`) ?
      [`classification-evidence-${scenario.id}`] : [scenario.evidenceIds[0] ?? sourceEvidenceId],
  }));
  const proposals = [{
    id: `${scenario.id}-model-suggestion`,
    subjectId: scenario.id,
    claim: `possible classification: ${scenario.classification}`,
    method: 'optional-model-suggestion',
    evidence: [],
  }];
  const outcome = evaluateScenario(scenario, readJson(CASES), readJson(RUBRIC));
  const record = {
    schema: 'domain-object-assessment.v1',
    assessmentId: '',
    profile: scenario.profile,
    source,
    versions,
    subject,
    evidence,
    facts,
    proposals,
    reviews,
    relations,
    exposure,
    criteria,
    result: { state: outcome.state, grade: outcome.grade, hardCeilings: outcome.hardCeilings ?? [], identityDigest: '', mode: 'fixture-reference-only' },
  };
  const reviewBasisDigest = computeReviewBasis(record);
  for (const review of record.reviews) review.reviewBasisDigest = reviewBasisDigest;
  const identity = computeAssessmentIdentity(record);
  record.assessmentId = identity;
  record.result.identityDigest = identity;
  return record;
}

function loadText(path) {
  return readFileSync(join(ROOT, path), 'utf8');
}

function checkCitation(anchor) {
  const [path, lines] = [anchor.path, [...anchor.anchor.matchAll(/L(\d+)(?:-L(\d+))?/g)]];
  if (!path || path.startsWith('/') || path.split('/').includes('..')) return `invalid citation path: ${path}`;
  if (!lines.length) return `source anchor needs a line citation: ${path}`;
  for (const [, start, end] of lines) {
    if (Number(start) < 1 || Number(end ?? start) < Number(start)) return `invalid citation range: ${path}`;
  }
  return undefined;
}

export function specificationErrors() {
  const contract = readJson(CONFIG);
  const spec = loadText('docs/research/domain-object-specification.md');
  const adr = loadText('docs/adr/0027-domain-object-event-assessment.md');
  const errors = [];
  if (contract.issue !== 53 || contract.scope !== 'bounded-domain-object-event-assessment-research' ||
      contract.baseline !== 'ceba8797eab7b465f214ac02847f4ee90b43d34d') errors.push('contract baseline/scope mismatch');
  const acceptance = contract.acceptance ?? [];
  if (acceptance.length !== IDS.length || new Set(acceptance.map(item => item.id)).size !== IDS.length ||
      IDS.some(id => !acceptance.some(item => item.id === id))) errors.push('acceptance IDs do not map exactly once');
  for (const item of acceptance) {
    if (!item.title || !item.evidencePath || !item.evidenceAnchor || !item.positiveCase || !item.negativeCase) errors.push(`${item.id} lacks positive/negative trace evidence`);
    if (!spec.includes(item.id) && !adr.includes(item.id)) errors.push(`${item.id} missing from research trace`);
  }
  for (const anchor of contract.sourceAnchors ?? []) {
    const error = checkCitation(anchor);
    if (error) errors.push(error);
  }
  for (const constraint of contract.constraints ?? []) {
    if (!spec.includes(constraint) && !adr.includes(constraint)) errors.push(`constraint missing from prose: ${constraint}`);
  }
  for (const phrase of ['unknown', 'not-applicable', 'policy-violation', 'context-scoped', 'not DDD standards']) {
    if (!spec.toLowerCase().includes(phrase.toLowerCase())) errors.push(`specification misses ${phrase}`);
  }
  return errors;
}

export function pseudocodeErrors() {
  const text = loadText('docs/research/domain-object-pseudocode.md');
  const cases = readJson(CASES);
  const expected = readJson(EXPECTED);
  const rubric = readJson(RUBRIC);
  const errors = [];
  const steps = [...text.matchAll(/^(\d+)\.\s/gm)].map(match => Number(match[1]));
  if (steps.length !== 14 || steps.some((step, index) => step !== index + 1)) errors.push('ordered procedure must define steps 1 through 14 once, in order');
  if (!text.includes('O(F + L + P + V + C + E log E)')) errors.push('bounded complexity omits fields/relations/proposals/reviews/criteria/evidence');
  const ids = new Set(expected.expectations.map(item => item.id));
  if (cases.cases.length !== ids.size || cases.cases.some(item => !ids.has(item.id))) errors.push('pseudocode fixtures are not one-to-one with separately declared expected outcomes');
  for (const scenario of cases.cases) {
    const outcome = evaluateScenario(scenario, cases, readJson(RUBRIC));
    if (!outcome.state || !Object.hasOwn(outcome, 'grade')) errors.push(`no deterministic outcome for ${scenario.id}`);
  }
  for (const [description, passed] of mutationOutcomes()) if (!passed) errors.push(`precedence mutation failed: ${description}`);
  if (Object.keys(rubric.profiles).sort(utf8Compare).join(',') !== 'business-object,domain-event,integration-event') {
    errors.push('pseudocode lacks the three distinct assessment profiles');
  }
  if (rubric.profiles['domain-event'].externalInfrastructurePrerequisites.length) {
    errors.push('internal domain-event profile incorrectly requires external infrastructure');
  }
  return errors;
}

export function architectureErrors() {
  const { cases, expected, rubric, schema } = readFixtureData();
  const architecture = loadText('docs/research/domain-object-architecture.md');
  const contract = readJson(CONFIG);
  const markdownLinks = [...architecture.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)].map(([, target]) => target);
  const errors = [];
  if (cases.cases.length !== 9 || expected.expectations.length !== 9) errors.push('expected exactly nine sealed synthetic cases');
  if (!architecture.includes('base-revision') && !architecture.includes('Baseline')) errors.push('architecture does not bind source gap analysis');
  if (!rubric.rules || rubric.rules.averageAcrossGates !== false || rubric.rules.policyViolationMaximumGrade !== null) errors.push('rubric lacks cumulative hard-gate policy');
  for (const anchor of contract.sourceAnchors ?? []) {
    const sourceLinks = markdownLinks.filter(target => target.includes(anchor.path) && target.includes(contract.baseline));
    if (!sourceLinks.length) errors.push(`architecture omits exact baseline link: ${anchor.path}`);
    for (const [, start, end] of anchor.anchor.matchAll(/L(\d+)(?:-L(\d+))?/g)) {
      const citation = `L${start}${end ? `-L${end}` : ''}`;
      if (!sourceLinks.some(target => target.includes(`#${citation}`) || target.includes(`,${citation}`))) {
        errors.push(`architecture omits cited baseline lines ${citation}: ${anchor.path}`);
      }
    }
  }
  if (schema.$defs.fact.properties.value !== undefined || !schema.$defs.fact.properties.valueDigest) {
    errors.push('draft schema must exclude raw field values and permit only a digest');
  }
  for (const scenario of cases.cases) {
    const record = materializeDraftRecord(scenario);
    const recordErrors = validateDraftRecord(record, schema, rubric);
    if (recordErrors.length) errors.push(`${scenario.id}: ${recordErrors.join('; ')}`);
  }
  return errors;
}

export function mutationOutcomes() {
  const { cases, rubric } = readFixtureData();
  const byId = id => structuredClone(cases.cases.find(item => item.id === id));
  const checks = [];

  const inventedContext = byId('public-projection');
  inventedContext.context = 'orders';
  checks.push(['invented context assignment rejected', scenarioErrors(inventedContext, cases).some(error => error.includes('matching current map evidence'))]);

  const falsePropagation = byId('domain-to-integration-projection');
  falsePropagation.relations[0].mapping[0].from = 'Invoice.secret';
  checks.push(['invented field propagation rejected', scenarioErrors(falsePropagation, cases).some(error => error.includes('invented source field'))]);

  const falseNoLeak = byId('benign-envelope-field');
  falseNoLeak.fields[0].purpose = 'unknown';
  checks.push(['false no-leakage approval rejected', scenarioErrors(falseNoLeak, cases).some(error => error.includes('approved exposure lacks supported purpose'))]);

  const avgAway = byId('orm-exposure');
  avgAway.criteria = Object.fromEntries(Object.keys(avgAway.criteria).map(key => [key, 'pass']));
  checks.push(['policy violation remains a hard stop', evaluateScenario(avgAway, cases, rubric).grade === null && evaluateScenario(avgAway, cases, rubric).state === 'blocked']);

  const unknownBronze = byId('unsupported-dynamic-recursion');
  checks.push(['unknown is not bronze', evaluateScenario(unknownBronze, cases, rubric).grade === null]);

  const staleGlossary = byId('public-projection');
  staleGlossary.glossary.status = 'stale';
  checks.push(['stale glossary cannot sustain gold', evaluateScenario(staleGlossary, cases, rubric).grade === 'silver']);

  const envelopeOnly = byId('public-projection');
  envelopeOnly.classification = 'unknown';
  envelopeOnly.context = 'unknown';
  envelopeOnly.exposure = 'unknown';
  envelopeOnly.criteria = { 'versioned-contract-evidence': 'pass' };
  checks.push(['schema/envelope-only evidence cannot promote', evaluateScenario(envelopeOnly, cases, rubric).grade === null]);

  const noMappingReview = byId('public-projection');
  noMappingReview.relations[0].mappingReview = 'invented-review';
  checks.push(['unreviewed mapping rejected', scenarioErrors(noMappingReview, cases).some(error => error.includes('invented projection review'))]);
  return checks;
}

function checkSchemaDefinition(schema, path = '$') {
  const errors = [];
  if (!schema || typeof schema !== 'object') return [`${path}: invalid schema node`];
  for (const [key, value] of Object.entries(schema)) {
    if (!ALLOWED_SCHEMA_KEYS.has(key)) errors.push(`${path}: unsupported schema keyword ${key}`);
    if (key === 'properties' || key === '$defs') {
      for (const [name, child] of Object.entries(value)) errors.push(...checkSchemaDefinition(child, `${path}.${key}.${name}`));
    } else if (key === 'items' || key === 'if' || key === 'then') {
      errors.push(...checkSchemaDefinition(value, `${path}.${key}`));
    } else if (key === 'allOf') {
      value.forEach((child, index) => errors.push(...checkSchemaDefinition(child, `${path}.allOf[${index}]`)));
    }
  }
  return errors;
}

function runNodeTests() {
  const result = spawnSync(process.execPath, ['--test', '--test-reporter=tap', 'tests/domain-object-contracts.test.mjs'], {
    cwd: ROOT,
    encoding: 'utf8',
    maxBuffer: 1024 * 1024,
  });
  const output = `${result.stdout ?? ''}${result.stderr ?? ''}`;
  const count = Number(output.match(/^# tests (\d+)$/m)?.[1] ?? 0);
  if (result.status !== 0 || count === 0) throw new Error(`focused node:test failed or reported no tests (${count})\n${output}`);
  return { count, output };
}

function run(phase) {
  const checkers = { specification: specificationErrors, pseudocode: pseudocodeErrors, architecture: architectureErrors };
  const phases = phase === 'all' ? Object.keys(checkers) : [phase];
  const errors = [];
  for (const name of phases) {
    const phaseErrors = checkers[name]();
    if (phaseErrors.length) errors.push(...phaseErrors.map(error => `${name}: ${error}`));
  }
  if (phase === 'architecture' || phase === 'all') {
    const schema = readJson(SCHEMA);
    errors.push(...checkSchemaDefinition(schema));
    for (const [name, passed] of mutationOutcomes()) if (!passed) errors.push(`mutation contract failed: ${name}`);
  }
  if (errors.length) throw new Error(errors.join('\n'));
  if (phase === 'all') {
    const testResult = runNodeTests();
    process.stdout.write(`${testResult.output}`);
    process.stdout.write(`issue53 checker: all 3 research phases passed; ${testResult.count} node:test cases passed\n`);
  } else {
    process.stdout.write(`issue53 checker: ${phase} passed\n`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const phase = process.argv[2];
  if (!['specification', 'pseudocode', 'architecture', 'all'].includes(phase)) {
    process.stderr.write('usage: node scripts/research/check-domain-object-contracts.mjs specification|pseudocode|architecture|all\n');
    process.exitCode = 2;
  } else {
    try { run(phase); } catch (error) {
      process.stderr.write(`${error.message}\n`);
      process.exitCode = 1;
    }
  }
}
