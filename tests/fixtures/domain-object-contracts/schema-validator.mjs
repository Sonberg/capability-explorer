import { createHash } from 'node:crypto';

const GRADES = ['bronze', 'silver', 'gold', 'platinum'];
const utf8Compare = (left, right) => Buffer.compare(Buffer.from(left, 'utf8'), Buffer.from(right, 'utf8'));
const canonicalize = value => Array.isArray(value) ? value.map(canonicalize)
  : value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort(utf8Compare).map(key => [key, canonicalize(value[key])]))
    : value;

export function computeAssessmentIdentity(record) {
  const identity = {
    source: record.source,
    profile: record.profile,
    versions: record.versions,
    subject: record.subject,
    evidence: record.evidence,
    facts: record.facts,
    proposals: record.proposals,
    reviews: record.reviews,
    relations: record.relations,
    exposure: record.exposure,
    criteria: record.criteria,
    resultMode: record.result?.mode,
  };
  return createHash('sha256').update(JSON.stringify(canonicalize(identity))).digest('hex');
}

export function computeReviewBasis(record) {
  const basis = {
    source: record.source,
    profile: record.profile,
    versions: record.versions,
    subject: record.subject,
    evidence: record.evidence,
    facts: record.facts,
    proposals: record.proposals,
    relations: record.relations,
    exposure: record.exposure,
    criteria: record.criteria,
  };
  return createHash('sha256').update(JSON.stringify(canonicalize(basis))).digest('hex');
}

function resolveRef(schema, ref) {
  if (!ref.startsWith('#/')) throw new Error(`external schema reference is unsupported: ${ref}`);
  return ref.slice(2).split('/').reduce((value, part) => value?.[part.replaceAll('~1', '/').replaceAll('~0', '~')], schema);
}

function matchesCondition(schema, value, root, depth) {
  if (depth > 32) return false;
  if (schema.const !== undefined && value !== schema.const) return false;
  return Object.entries(schema.properties ?? {}).every(([key, child]) =>
    value?.[key] === undefined || validateNode(child, value[key], root, depth + 1).length === 0);
}

function validateNode(rule, value, root, depth = 0, path = '$') {
  if (depth > 32) return [`${path}: schema/instance nesting exceeds 32`];
  if (rule.$ref) return validateNode(resolveRef(root, rule.$ref), value, root, depth + 1, path);
  const errors = [];
  const types = Array.isArray(rule.type) ? rule.type : rule.type ? [rule.type] : [];
  const typeMatches = type => type === 'null' ? value === null
    : type === 'array' ? Array.isArray(value)
      : type === 'object' ? value !== null && typeof value === 'object' && !Array.isArray(value)
        : type === 'integer' ? Number.isInteger(value)
          : type === 'string' ? typeof value === 'string'
            : type === 'boolean' ? typeof value === 'boolean'
              : type === 'number' ? typeof value === 'number' : false;
  if (types.length && !types.some(typeMatches)) errors.push(`${path}: expected ${types.join('|')}`);
  if (rule.const !== undefined && value !== rule.const) errors.push(`${path}: expected constant`);
  if (rule.enum && !rule.enum.includes(value)) errors.push(`${path}: value is outside enum`);
  if (typeof value === 'string') {
    if (rule.minLength && value.length < rule.minLength) errors.push(`${path}: string is too short`);
    if (rule.pattern && !new RegExp(rule.pattern).test(value)) errors.push(`${path}: string pattern mismatch`);
    if (rule.format === 'date-time' && !Number.isFinite(Date.parse(value))) errors.push(`${path}: invalid date-time`);
  }
  if (Number.isInteger(value) && rule.minimum !== undefined && value < rule.minimum) errors.push(`${path}: integer below minimum`);
  if (Array.isArray(value)) {
    if (rule.minItems && value.length < rule.minItems) errors.push(`${path}: array has too few items`);
    if (rule.items) value.forEach((item, index) => errors.push(...validateNode(rule.items, item, root, depth + 1, `${path}[${index}]`)));
  }
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    for (const key of rule.required ?? []) if (!(key in value)) errors.push(`${path}.${key}: required`);
    for (const [key, child] of Object.entries(value)) {
      if (rule.properties?.[key]) errors.push(...validateNode(rule.properties[key], child, root, depth + 1, `${path}.${key}`));
      else if (rule.additionalProperties === false) errors.push(`${path}.${key}: unknown property`);
    }
  }
  for (const branch of rule.allOf ?? []) {
    if (matchesCondition(branch.if, value, root, depth + 1)) errors.push(...validateNode(branch.then, value, root, depth + 1, path));
  }
  return errors;
}

export function validateSchemaInstance(schema, instance) {
  return validateNode(schema, instance, schema);
}

const currentReview = (record, decision) => record.reviews.find(review =>
  review.decision === decision && review.status === 'current');
const leafFields = fields => fields.flatMap(field =>
  field.children?.length ? leafFields(field.children) : [field]);
const allFields = fields => fields.flatMap(field => [field, ...allFields(field.children ?? [])]);

export function validateDraftRecord(record, schema, rubric) {
  const errors = validateSchemaInstance(schema, record);
  const reviewBasis = computeReviewBasis(record);
  const ids = new Set((record.evidence ?? []).map(item => item.id));
  const evidenceRefs = [
    ...(record.subject?.contextEvidence ?? []),
    ...(record.facts ?? []).flatMap(item => item.evidence ?? []),
    ...(record.proposals ?? []).flatMap(item => item.evidence ?? []),
    ...(record.reviews ?? []).flatMap(item => item.evidence ?? []),
    ...(record.relations ?? []).flatMap(item => item.evidence ?? []),
    ...(record.exposure?.evidence ?? []),
    ...(record.criteria ?? []).flatMap(item => item.evidence ?? []),
  ];
  for (const ref of evidenceRefs) if (!ids.has(ref)) errors.push(`unresolved evidence reference: ${ref}`);
  if (record.source?.path?.startsWith('/') || record.source?.path?.split('/').includes('..')) {
    errors.push('source path must be relative and traversal-free');
  }
  for (const item of record.evidence ?? []) {
    if (JSON.stringify(canonicalize(item.source)) !== JSON.stringify(canonicalize(record.source))) {
      errors.push(`evidence ${item.id} is bound to a different source identity`);
    }
  }
  if (record.source?.kind === 'repository-symbol' &&
      (!record.source.repository || !record.source.commit || !record.source.unit || !record.source.symbol)) {
    errors.push('repository-symbol identity requires repository, full commit, unit, and symbol');
  }
  if (record.source?.kind === 'repository-document' &&
      (!record.source.repository || !record.source.commit || !record.source.span)) {
    errors.push('repository-document identity requires repository, full commit, and span');
  }
  const context = currentReview(record, 'context-ownership');
  if (record.subject?.contextStatus === 'reviewed' &&
      (!record.subject.contextId || !record.subject.contextEvidence.length || !context ||
       context.contextId !== record.subject.contextId || context.version !== record.versions.contextMap)) {
    errors.push('reviewed context must bind current context-map version and evidence');
  }
  const glossary = currentReview(record, 'glossary');
  if (glossary && (glossary.version !== record.versions.glossary ||
      glossary.contextId !== record.subject.contextId)) errors.push('glossary review is stale or out of context');
  const exposureReview = currentReview(record, 'exposure');
  if (record.exposure?.verdict === 'approved') {
    if (!exposureReview || exposureReview.id !== record.exposure.reviewId ||
        exposureReview.version !== record.versions.exposurePolicy || exposureReview.evidence.some(id => !ids.has(id))) {
      errors.push('approved exposure lacks current policy-owner evidence');
    }
    if (allFields(record.subject.fields).some(field => field.shape !== 'supported') ||
        leafFields(record.subject.fields).some(field =>
          !field.purpose || field.purpose === 'unknown' || field.audience !== record.exposure.audience)) {
      errors.push('approved exposure lacks supported purpose/audience for every nested leaf');
    }
  }
  for (const review of record.reviews ?? []) {
    const expectedVersion = {
      'context-ownership': record.versions.contextMap,
      glossary: record.versions.glossary,
      exposure: record.versions.exposurePolicy,
      classification: record.versions.rubric,
      projection: record.versions.rubric,
    }[review.decision];
    if (review.status === 'current' && expectedVersion && review.version !== expectedVersion) {
      errors.push(`review ${review.id} is stale against ${review.decision} version`);
    }
    if (review.status === 'current' && review.reviewBasisDigest !== reviewBasis) {
      errors.push(`review ${review.id} is stale against assessed source/tool/config/version inputs`);
    }
  }
  for (const collection of [record.facts, record.proposals, record.reviews]) {
    for (const item of collection ?? []) if (item.subjectId !== record.subject.id) errors.push(`${item.id} is linked to an invented subject`);
  }
  for (const relation of record.relations ?? []) {
    if (relation.kind === 'explicit-projection' &&
        (relation.basis !== 'current-reviewed-assertion' || !relation.reviewId ||
         !record.reviews.some(review => review.id === relation.reviewId && review.decision === 'projection' && review.status === 'current') ||
         !relation.fieldMap?.length)) errors.push(`projection ${relation.id} lacks current explicit field mapping`);
    if (relation.kind === 'explicit-projection' &&
        (relation.to !== record.subject.name ||
         relation.fieldMap.some(mapping => !leafFields(record.subject.fields).some(field => field.path === mapping.toPath)))) {
      errors.push(`projection ${relation.id} contains an invented target field`);
    }
    if (relation.kind === 'same-concept' &&
        (record.subject.contextStatus !== 'reviewed' || relation.basis !== 'current-reviewed-assertion' ||
         !record.reviews.some(review => review.id === relation.reviewId && review.decision === 'classification' && review.status === 'current'))) {
      errors.push(`same-concept relation ${relation.id} lacks current context-scoped review`);
    }
  }
  if (record.criteria?.some(item => item.status === 'not-applicable' &&
      (item.applicability !== 'excluded' || !item.rationale?.trim() || item.id.includes('exposure') || item.id.includes('classification')))) {
    errors.push('not-applicable criterion requires excluded status and rationale');
  }
  if ((record.result?.state === 'graded') !== Boolean(record.result?.grade)) errors.push('graded state and grade must agree');
  if (record.result?.grade) {
    const tiers = rubric.profiles[record.profile]?.tiers;
    const gradeIndex = GRADES.indexOf(record.result.grade);
    if (record.subject?.kind !== record.profile) errors.push('grade requires a classification matching its profile');
    const classification = currentReview(record, 'classification');
    if (!classification || classification.version !== record.versions.rubric) errors.push('grade requires current classification review');
    if (record.subject?.contextStatus !== 'reviewed' || !context ||
        context.contextId !== record.subject.contextId || context.version !== record.versions.contextMap) {
      errors.push('grade requires current context ownership review');
    }
    if (leafFields(record.subject.fields).every(field => field.envelope)) {
      errors.push('envelope-only fields cannot support a maturity grade');
    }
    for (const tier of GRADES.slice(0, gradeIndex + 1)) {
      for (const id of tiers?.[tier] ?? []) {
        const criterion = record.criteria.find(item => item.id === id);
        const applicablePass = criterion?.status === 'pass' && criterion.applicability === 'applicable' && criterion.evidence.length > 0;
        const justifiedExclusion = criterion?.status === 'not-applicable' && criterion.applicability === 'excluded' &&
          Boolean(criterion.rationale?.trim()) && !id.includes('exposure') && !id.includes('classification');
        if (!applicablePass && !justifiedExclusion) errors.push(`grade ${record.result.grade} lacks cumulative current gate ${id}`);
      }
    }
    if (['gold', 'platinum'].includes(record.result.grade) &&
        (!glossary || glossary.version !== record.versions.glossary ||
         glossary.contextId !== record.subject.contextId ||
         record.subject.contextStatus !== 'reviewed')) {
      errors.push('gold/platinum requires current context-scoped glossary review');
    }
  }
  if (record.result?.identityDigest !== computeAssessmentIdentity(record) ||
      record.assessmentId !== record.result?.identityDigest) {
    errors.push('assessment identity digest does not bind source and versioned inputs');
  }
  if (record.exposure?.verdict === 'policy-violation' &&
      (record.result?.grade !== null || record.result?.state !== 'blocked' ||
       !record.result.hardCeilings.includes('policy-violation'))) {
    errors.push('policy violation must preserve its independent verdict and mandatory hard ceiling');
  }
  if (record.result?.grade && record.exposure?.verdict !== 'approved') errors.push('non-approved exposure cannot receive a grade');
  if (record.source?.kind === 'synthetic-fixture' && record.result?.mode !== 'fixture-reference-only') {
    errors.push('synthetic fixture cannot represent an owner-reviewed assessment');
  }
  if (record.result?.mode === 'owner-reviewed-assessment' &&
      (record.source?.kind === 'synthetic-fixture' ||
       record.reviews.some(review => review.basis !== 'human-review' || review.status !== 'current'))) {
    errors.push('owner-reviewed assessment requires current human reviews and non-fixture provenance');
  }
  return errors;
}
