import test from 'node:test';
import assert from 'node:assert/strict';
import {
  canonicalBytes,
  evaluateScenario,
  materializeDraftRecord,
  mutationOutcomes,
  readFixtureData,
} from '../scripts/research/check-domain-object-contracts.mjs';
import {
  computeAssessmentIdentity,
  validateDraftRecord,
  validateSchemaInstance,
} from './fixtures/domain-object-contracts/schema-validator.mjs';

const { cases, expected, rubric, schema } = readFixtureData();
const byId = id => cases.cases.find(item => item.id === id);
const expectedById = id => expected.expectations.find(item => item.id === id);

for (const fixture of cases.cases) {
  test(`sealed expected outcome: ${fixture.id}`, () => {
    const outcome = evaluateScenario(fixture, cases, rubric);
    const oracle = expectedById(fixture.id);
    const relation = fixture.relations[0]?.kind ?? 'none';
    const { id: _id, ...expectedOutcome } = oracle;
    assert.deepEqual({
      classification: outcome.classification,
      exposure: outcome.exposure,
      grade: outcome.grade,
      state: outcome.state,
      relation,
    }, expectedOutcome);
  });
}

test('all fixture records conform to the strict draft subset and semantic guards', () => {
  for (const fixture of cases.cases) {
    const record = materializeDraftRecord(fixture);
    assert.deepEqual(validateSchemaInstance(schema, record), [], fixture.id);
    assert.deepEqual(validateDraftRecord(record, schema, rubric), [], fixture.id);
    assert.equal(record.reviews.every(review => review.basis === 'synthetic-oracle'), true);
    assert.equal(record.source.kind, 'synthetic-fixture');
  }
});

test('closed object schema rejects invented properties and invalid source identities', () => {
  const record = materializeDraftRecord(byId('public-projection'));
  record.subject.unsupportedInference = true;
  assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes('unknown property')));
  const broken = materializeDraftRecord(byId('public-projection'));
  broken.source.sha256 = 'not-a-digest';
  assert.ok(validateDraftRecord(broken, schema, rubric).some(error => error.includes('pattern mismatch')));
});

test('raw source values cannot be added to strict fact records', () => {
  const record = materializeDraftRecord(byId('public-projection'));
  record.facts[0].value = 'synthetic secret-like value';
  assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes('unknown property')));
});

test('nested fields are represented and unknown nested leaves cannot receive approved exposure', () => {
  const record = materializeDraftRecord(byId('sensitive-nested-field'));
  const leaf = record.subject.fields[0];
  leaf.path = 'customer.address.postalCode';
  record.subject.fields = [{
    id: 'customer',
    path: 'customer',
    type: 'object',
    shape: 'supported',
    meaningStatus: 'unknown',
    children: [{
      id: 'customer-address',
      path: 'customer.address',
      type: 'object',
      shape: 'supported',
      meaningStatus: 'unknown',
      children: [leaf],
    }],
  }];
  const identity = computeAssessmentIdentity(record);
  record.assessmentId = identity;
  record.result.identityDigest = identity;
  assert.deepEqual(validateSchemaInstance(schema, record), []);
  const approved = materializeDraftRecord(byId('public-projection'));
  approved.subject.fields[0].shape = 'recursive';
  approved.subject.fields[0].children = [{
    ...approved.subject.fields[0],
    id: 'recursive-child',
    path: `${approved.subject.fields[0].path}.dynamic`,
    shape: 'unknown',
    purpose: 'unknown',
  }];
  const approvedIdentity = computeAssessmentIdentity(approved);
  approved.assessmentId = approvedIdentity;
  approved.result.identityDigest = approvedIdentity;
  assert.ok(validateDraftRecord(approved, schema, rubric).some(error => error.includes('every nested leaf')));
});

test('unknown context cannot be waived with not-applicable status to earn bronze', () => {
  const record = materializeDraftRecord(byId('public-projection'));
  record.subject.contextStatus = 'unknown';
  record.subject.contextId = null;
  record.subject.contextEvidence = [];
  record.result.grade = 'bronze';
  record.result.state = 'graded';
  record.criteria.find(item => item.id === 'producer-boundary-evidence').status = 'not-applicable';
  record.criteria.find(item => item.id === 'producer-boundary-evidence').applicability = 'excluded';
  record.criteria.find(item => item.id === 'producer-boundary-evidence').rationale = 'internal events do not need a broker';
  const identity = computeAssessmentIdentity(record);
  record.assessmentId = identity;
  record.result.identityDigest = identity;
  assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes('grade requires current context ownership review')));
});

test('an envelope-only record cannot retain a maturity grade', () => {
  const record = materializeDraftRecord(byId('benign-envelope-field'));
  record.subject.fields = record.subject.fields.filter(field => field.envelope);
  const identity = computeAssessmentIdentity(record);
  record.assessmentId = identity;
  record.result.identityDigest = identity;
  assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes('envelope-only fields')));
});

test('a policy violation must retain its independent verdict and hard ceiling', () => {
  const record = materializeDraftRecord(byId('orm-exposure'));
  assert.deepEqual(validateDraftRecord(record, schema, rubric), []);
  record.result.hardCeilings = [];
  assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes('mandatory hard ceiling')));
});

for (const [version, changed, expectedError] of [
  ['glossary', record => { record.versions.glossary = 'g-8'; }, 'glossary review is stale'],
  ['context map', record => { record.versions.contextMap = 'ctx-5'; }, 'context-map version'],
  ['exposure policy', record => { record.versions.exposurePolicy = 'exp-4'; }, 'exposure version'],
  ['rubric', record => { record.versions.rubric = 'r-2'; }, 'classification version'],
]) {
  test(`changed ${version} version invalidates the unchanged source review`, () => {
    const record = materializeDraftRecord(byId('public-projection'));
    changed(record);
    const identity = computeAssessmentIdentity(record);
    record.assessmentId = identity;
    record.result.identityDigest = identity;
    assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes(expectedError)));
  });
}

test('claim evidence cannot cross repository, commit, span, or content hash identity', () => {
  const record = materializeDraftRecord(byId('public-projection'));
  for (const mutate of [
    source => { source.repository = 'other/repository'; },
    source => { source.commit = 'b'.repeat(40); },
    source => { source.span = { startLine: 9, startColumn: 1, endLine: 9, endColumn: 2 }; },
    source => { source.sha256 = 'c'.repeat(64); },
  ]) {
    const broken = structuredClone(record);
    mutate(broken.evidence[0].source);
    broken.assessmentId = 'd'.repeat(64);
    broken.result.identityDigest = 'd'.repeat(64);
    assert.ok(validateDraftRecord(broken, schema, rubric).some(error => error.includes('different source identity')));
  }
});

test('explicit field projections require reviewed evidence and real source and target paths', () => {
  const valid = byId('domain-to-integration-projection');
  assert.deepEqual(evaluateScenario(valid, cases, rubric).errors ?? [], []);
  const inventedTarget = structuredClone(valid);
  inventedTarget.relations[0].mapping[0].to = 'invoice.secret';
  assert.ok(evaluateScenario(inventedTarget, cases, rubric).errors.some(error => error.includes('invented target field')));
  const noReview = structuredClone(valid);
  noReview.reviewIds = noReview.reviewIds.filter(id => id !== noReview.relations[0].mappingReview);
  assert.ok(evaluateScenario(noReview, cases, rubric).errors.some(error => error.includes('invented projection review')));
  const record = materializeDraftRecord(valid);
  record.relations[0].fieldMap[0].toPath = 'invoice.secret';
  const identity = computeAssessmentIdentity(record);
  record.assessmentId = identity;
  record.result.identityDigest = identity;
  assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes('invented target field')));
});

test('matching field names or channel-call facts do not assert context identity', () => {
  const record = materializeDraftRecord(byId('ambiguous-shared-word-two-contexts'));
  record.relations.push({
    id: 'name-call-topic-chain',
    kind: 'same-concept',
    from: 'Account',
    to: 'Account',
    evidence: [record.evidence[0].id],
    basis: 'source-fact',
  });
  const identity = computeAssessmentIdentity(record);
  record.assessmentId = identity;
  record.result.identityDigest = identity;
  assert.ok(validateDraftRecord(record, schema, rubric).some(error => error.includes('current context-scoped review')));
});

test('ambiguous identical spelling in separate contexts remains two concepts', () => {
  const fixture = byId('ambiguous-shared-word-two-contexts');
  assert.equal(fixture.subjects[0].name, fixture.subjects[1].name);
  assert.notEqual(fixture.subjects[0].context, fixture.subjects[1].context);
  const inventedIdentity = structuredClone(fixture);
  inventedIdentity.relations = [{ kind: 'same-concept', from: 'Account', to: 'Account', evidence: 'missing' }];
  assert.ok(evaluateScenario(inventedIdentity, cases, rubric).errors.some(error => error.includes('same spelling')));
});

test('approved exposure requires field purpose, audience and supported nested shape', () => {
  const fixture = byId('benign-envelope-field');
  const noAudience = structuredClone(fixture);
  noAudience.fields[1].audience = 'unknown';
  assert.ok(evaluateScenario(noAudience, cases, rubric).errors.some(error => error.includes('approved exposure lacks supported purpose/audience')));
  const unknownField = structuredClone(fixture);
  unknownField.fields[0].shape = 'recursive';
  assert.ok(evaluateScenario(unknownField, cases, rubric).grade === null);
});

test('event-carried state and benign envelope metadata are not automatic leakage', () => {
  assert.equal(evaluateScenario(byId('intentional-event-carried-state'), cases, rubric).exposure, 'approved');
  const envelope = byId('benign-envelope-field');
  assert.equal(envelope.fields.find(field => field.envelope).path, 'ce.correlationid');
  assert.equal(evaluateScenario(envelope, cases, rubric).grade, 'silver');
});

test('unknown, stale language, and model-only evidence cannot silently earn bronze or gold', () => {
  const unknown = evaluateScenario(byId('unsupported-dynamic-recursion'), cases, rubric);
  assert.equal(unknown.grade, null);
  const stale = structuredClone(byId('public-projection'));
  stale.glossary.status = 'stale';
  assert.equal(evaluateScenario(stale, cases, rubric).grade, 'silver');
  const suggestionOnly = structuredClone(byId('public-projection'));
  suggestionOnly.classification = 'unknown';
  suggestionOnly.context = 'unknown';
  suggestionOnly.exposure = 'unknown';
  suggestionOnly.criteria = { 'versioned-contract-evidence': 'pass' };
  assert.equal(evaluateScenario(suggestionOnly, cases, rubric).grade, null);
});

test('policy violations cannot be averaged away and applicability cannot waive exposure', () => {
  const caseData = structuredClone(byId('orm-exposure'));
  caseData.criteria = Object.fromEntries(Object.keys(caseData.criteria).map(id => [id, 'pass']));
  caseData.applicability = Object.fromEntries(Object.keys(caseData.criteria).map(id => [id, 'not relevant']));
  const outcome = evaluateScenario(caseData, cases, rubric);
  assert.equal(outcome.state, 'blocked');
  assert.equal(outcome.grade, null);
  assert.equal(outcome.exposure, 'policy-violation');
  assert.ok(outcome.hardCeilings.includes('policy-violation'));
  const invalidNa = structuredClone(byId('public-projection'));
  invalidNa.criteria['field-exposure-review'] = 'not-applicable';
  const invalidNaOutcome = evaluateScenario(invalidNa, cases, rubric);
  assert.ok(invalidNaOutcome.hardCeilings.some(error => error.includes('not-applicable')));
});

test('internal domain-event profile has no broker, outbox, or envelope prerequisites', () => {
  const requirements = rubric.profiles['domain-event'].tiers;
  const allCriteria = Object.values(requirements).flat().join(' ').toLowerCase();
  assert.deepEqual(rubric.profiles['domain-event'].externalInfrastructurePrerequisites, []);
  for (const externalGate of ['broker', 'outbox', 'cloudevents', 'asyncapi', 'external-consumer']) {
    assert.equal(allCriteria.includes(externalGate), false);
  }
  const missingContext = structuredClone(byId('intentional-event-carried-state'));
  missingContext.profile = 'domain-event';
  missingContext.classification = 'domain-event';
  missingContext.context = 'unknown';
  missingContext.glossary.status = 'unknown';
  missingContext.criteria = Object.fromEntries(
    Object.keys(requirements.bronze.concat(requirements.silver)).map(id => [id, 'pass']),
  );
  missingContext.criteria['context-ownership-evidence'] = 'not-applicable';
  missingContext.applicability = {
    'context-ownership-evidence': 'No broker is required for internal events.',
  };
  const result = evaluateScenario(missingContext, cases, rubric);
  assert.equal(result.grade, null);
  assert.equal(result.state, 'unassessed');
});

test('all required mutations are rejected by observable outcomes', () => {
  for (const [description, passed] of mutationOutcomes()) assert.equal(passed, true, description);
});

test('canonical accepted findings are byte-identical and identity changes with bound versions', () => {
  const source = byId('public-projection');
  const first = materializeDraftRecord(source);
  const second = materializeDraftRecord(source);
  assert.deepEqual(canonicalBytes(first), canonicalBytes(second));
  const stale = structuredClone(source);
  stale.versions.glossary = 'g-8';
  assert.notEqual(materializeDraftRecord(stale).assessmentId, first.assessmentId);
  const excluded = structuredClone(source);
  excluded.applicability = { 'delivery-guarantee-evidence': 'not required for this bounded flow' };
  assert.notEqual(materializeDraftRecord(excluded).assessmentId, first.assessmentId);
});
