import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  run, validateArchitecture, validateFixtureCatalogue, validatePseudocode, validateSpecification,
} from '../scripts/research/check-frontend-contracts.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const readJson = path => JSON.parse(readFileSync(join(root, path), 'utf8'));
const layoutRegistry = readJson('tests/fixtures/frontend-contracts/layout-scenarios.json');
const contract = () => readJson('config/research/frontend-contracts.json');
const specification = () => readFileSync(join(root, 'docs/research/language-neutral-frontends-specification.md'), 'utf8');
const pseudocode = () => readFileSync(join(root, 'docs/research/language-neutral-frontends-pseudocode.md'), 'utf8');
const policy = () => readJson('config/research/frontend-layout-policy.json');
const architectureInputs = () => ({
  contract: contract(),
  report: readFileSync(join(root, 'docs/research/language-neutral-frontends.md'), 'utf8'),
  adr: readFileSync(join(root, 'docs/adr/0025-language-neutral-frontends-and-discovery.md'), 'utf8'),
  adapterSchema: readJson('schema/research/frontend-adapter.schema.json'),
  discoverySchema: readJson('schema/research/frontend-discovery.schema.json'),
  compatibility: readJson('config/research/frontend-compatibility.json'),
  scenarios: readJson('tests/fixtures/frontend-contracts/layout-scenarios.json'),
  expectedInventories: readJson('tests/fixtures/frontend-contracts/expected-inventories.json'),
});

const schemaKeywords = new Set([
  '$schema', '$id', 'title', 'description', 'type', 'const', 'enum', 'pattern',
  'minLength', 'maxLength', 'minimum', 'required', 'properties',
  'additionalProperties', 'items', 'minItems', 'uniqueItems',
]);

function validateSchemaDefinition(schema, path = '$schema') {
  const errors = Object.keys(schema).filter(key => !schemaKeywords.has(key))
    .map(key => `${path}: unsupported schema keyword ${key}`);
  for (const [key, nested] of Object.entries(schema.properties ?? {})) {
    errors.push(...validateSchemaDefinition(nested, `${path}.properties.${key}`));
  }
  if (schema.items) errors.push(...validateSchemaDefinition(schema.items, `${path}.items`));
  return errors;
}

function validateInstance(value, schema, path = '$') {
  const errors = path === '$' ? validateSchemaDefinition(schema) : [];
  const types = { object: value !== null && typeof value === 'object' && !Array.isArray(value),
    array: Array.isArray(value), string: typeof value === 'string',
    integer: Number.isInteger(value), number: typeof value === 'number' && Number.isFinite(value),
    boolean: typeof value === 'boolean', null: value === null };
  if (schema.type && !types[schema.type]) return [`${path}: expected ${schema.type}`];
  if ('const' in schema && value !== schema.const) errors.push(`${path}: const mismatch`);
  if (schema.enum && !schema.enum.includes(value)) errors.push(`${path}: enum mismatch`);
  if (schema.pattern && (typeof value !== 'string' || !new RegExp(schema.pattern).test(value))) errors.push(`${path}: pattern mismatch`);
  if (typeof value === 'string' && (value.length < (schema.minLength ?? 0) || value.length > (schema.maxLength ?? Infinity))) errors.push(`${path}: string length outside bounds`);
  if (typeof value === 'number' && value < (schema.minimum ?? -Infinity)) errors.push(`${path}: below minimum`);
  if (Array.isArray(value)) {
    if (value.length < (schema.minItems ?? 0)) errors.push(`${path}: too few items`);
    if (schema.uniqueItems && new Set(value.map(canonical)).size !== value.length) errors.push(`${path}: duplicate items`);
    if (schema.items) value.forEach((item, index) => errors.push(...validateInstance(item, schema.items, `${path}[${index}]`)));
  }
  if (types.object) {
    for (const required of schema.required ?? []) if (!(required in value)) errors.push(`${path}: missing ${required}`);
    for (const [key, nested] of Object.entries(value)) {
      if (schema.properties?.[key]) errors.push(...validateInstance(nested, schema.properties[key], `${path}.${key}`));
      else if (schema.additionalProperties === false) errors.push(`${path}: unknown property ${key}`);
    }
  }
  return errors;
}

const canonical = value => JSON.stringify(Array.isArray(value) ? value.map(item => JSON.parse(canonical(item))) :
  value && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, JSON.parse(canonical(value[key]))]))
    : value);
const featureRegistry = new Set(['declarations', 'references', 'source-spans', 'workspace-links']);

function validateAdapter(value, schema) {
  const errors = validateInstance(value, schema);
  if (errors.length) return errors;
  const { request, result } = value;
  const supported = new Set(result.features.supported);
  const unsupported = new Set(result.features.unsupported);
  const knownLanguages = new Set(layoutRegistry.supportedLanguages);
  if ([...supported].some(feature => unsupported.has(feature))) errors.push('feature is both supported and unsupported');
  for (const feature of supported) {
    if (!featureRegistry.has(feature)) errors.push(`unknown supported capability ${feature}`);
  }
  for (const feature of request.requestedFeatures) {
    if (!supported.has(feature) && !unsupported.has(feature)) errors.push(`unreported requested capability ${feature}`);
    if (!featureRegistry.has(feature) && (!unsupported.has(feature) || !['unsupported', 'partial'].includes(result.status))) {
      errors.push(`unknown capability ${feature} was not rejected explicitly`);
    }
  }
  for (const feature of unsupported) if (!featureRegistry.has(feature) && !request.requestedFeatures.includes(feature)) {
    errors.push(`unrequested unknown capability ${feature}`);
  }
  if ((!knownLanguages.has(request.profileId) || !knownLanguages.has(result.languageProfileId)) &&
      result.status !== 'unsupported') errors.push('unknown profile is not reported unsupported');
  if (result.status === 'unsupported' && (supported.size || result.facts.length)) errors.push('unsupported profile contains semantic claims');
  if (result.coverage.filesAnalyzed > result.coverage.filesTotal) errors.push('analyzed files exceed total');
  if (result.status === 'complete' && (result.coverage.filesAnalyzed !== result.coverage.filesTotal ||
      result.coverage.omittedPaths.length || unsupported.size)) errors.push('complete result hides incomplete coverage');
  const factIds = new Set(result.facts.map(fact => fact.factId));
  if (factIds.size !== result.facts.length) errors.push('duplicate fact identity');
  for (const fact of result.facts) {
    if (['resolved', 'static-target', 'candidate'].includes(fact.resolution) &&
        (!fact.targetFactId || !factIds.has(fact.targetFactId))) errors.push(`unlinked ${fact.resolution} fact`);
    for (const span of fact.evidence) {
      if (span.endByte <= span.startByte) errors.push(`invalid source span for ${fact.factId}`);
    }
  }
  return errors;
}

function validateDiscovery(value, schema, scenario, expected) {
  const errors = validateInstance(value, schema);
  if (errors.length) return errors;
  const keys = new Set(value.units.map(unit => unit.unitKey));
  if (keys.size !== value.units.length) errors.push('duplicate unit identity');
  if (value.coverage.unitsAccepted !== value.units.length) errors.push('unit coverage count mismatch');
  const files = value.units.flatMap(unit => unit.files);
  if (value.coverage.filesIncluded !== files.length) errors.push('file coverage count mismatch');
  if (new Set(files).size !== files.length) errors.push('file has conflicting owners');
  if (value.units.some(unit => unit.capabilities.some(capability => !layoutRegistry.supportedCapabilities.includes(capability)))) errors.push('unknown capability');
  if (value.units.some(unit => !layoutRegistry.supportedLanguages.includes(unit.languageProfileId))) errors.push('unknown language profile');
  const overBudget = scenario.units.length > scenario.budgets.maxCandidates ||
      scenario.entries.some(entry => entry.path.split('/').length - 1 > scenario.budgets.maxDepth) ||
      scenario.entries.length > scenario.budgets.maxFiles ||
      scenario.entries.reduce((sum, entry) => sum + entry.bytes, 0) > scenario.budgets.maxBytes;
  if (overBudget && (value.status !== 'failed' || !value.coverage.diagnostics.includes('limit-exceeded'))) errors.push('budget failure is hidden');
  if (value.status !== expected.status) errors.push('inventory status differs from sealed expectation');
  const actualInventory = value.units.map(unit => ({
    unitKey: unit.unitKey, languageProfileId: unit.languageProfileId, files: unit.files, references: unit.references,
  }));
  if (canonical(actualInventory) !== canonical(expected.units)) errors.push('inventory differs from sealed expectation');
  if (canonical(value.coverage.pathsExcluded) !== canonical(expected.excluded.map(item => item.path))) errors.push('exclusions differ from sealed expectation');
  if (canonical(value.coverage.diagnostics) !== canonical(expected.diagnostics)) errors.push('diagnostics differ from sealed expectation');
  return errors;
}

function glob(pattern) {
  const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*\*\//g, '(?:.*/)?').replace(/\*\*/g, '.*').replace(/\*/g, '[^/]*');
  return new RegExp(`^${escaped}$`);
}

function evaluateScenario(scenario) {
  const fail = (status, diagnostic) => ({ scenarioId: scenario.id, status, units: [], excluded: [], diagnostics: [diagnostic] });
  if (scenario.entries.some(entry => entry.kind === 'symlink')) return fail('failed', 'symlink-escape');
  if (scenario.entries.some(entry => entry.path.split('/').includes('..'))) return fail('failed', 'path-escape');
  const unknownLanguage = scenario.units.some(unit => !layoutRegistry.supportedLanguages.includes(unit.language));
  if (unknownLanguage) return fail('unsupported', 'unknown-language-profile');
  if (scenario.units.some(unit => unit.capabilities.some(item => !layoutRegistry.supportedCapabilities.includes(item)))) {
    return fail('unsupported', 'unknown-capability');
  }
  const { budgets } = scenario;
  if (scenario.units.length > budgets.maxCandidates ||
      scenario.entries.some(entry => entry.path.split('/').length - 1 > budgets.maxDepth) ||
      scenario.entries.length > budgets.maxFiles ||
      scenario.entries.reduce((sum, entry) => sum + entry.bytes, 0) > budgets.maxBytes) return fail('failed', 'limit-exceeded');
  const paths = scenario.entries.filter(entry => entry.kind === 'source').map(entry => entry.path);
  const units = scenario.units.map(unit => ({ ...unit, key: `unit:v1/${unit.root}#${unit.id}` }));
  const identities = new Map();
  for (const unit of units) {
    const identity = `${unit.root}#${unit.id}`;
    if (identities.has(identity) && identities.get(identity) !== unit.language) return fail('failed', 'conflicting-unit-identity');
    identities.set(identity, unit.language);
  }
  const owners = new Map();
  let deduplicated = false;
  const nestedRoots = scenario.entries.filter(entry => entry.kind === 'nested-repository').map(entry => entry.path.replace(/\/\.git$/, ''));
  for (const path of paths) {
    if (nestedRoots.some(nestedRoot => path.startsWith(`${nestedRoot}/`))) continue;
    const matches = units.filter(unit => {
      const prefix = unit.root === '.' ? '' : `${unit.root}/`;
      if (!path.startsWith(prefix)) return false;
      const relative = path.slice(prefix.length);
      const count = unit.includePatterns.filter(pattern => glob(pattern).test(relative)).length;
      if (count > 1) deduplicated = true;
      return count > 0;
    }).sort((a, b) => b.root.split('/').length - a.root.split('/').length);
    if (matches.length) {
      const owner = matches[0];
      if (matches.some(match => match.root === owner.root && match.id !== owner.id)) return fail('failed', 'ownership-conflict');
      owners.set(path, owner);
    }
  }
  const grouped = new Map(units.map(unit => [unit.key, new Set()]));
  for (const [path, owner] of owners) grouped.get(owner.key).add(path);
  const inventory = units.map(unit => ({
    unitKey: unit.key, languageProfileId: unit.language,
    files: [...grouped.get(unit.key)].sort(), references: unit.references.map(reference => {
      const target = `unit:v1/${reference.target}`;
      return { targetUnitKey: target, relation: reference.relation,
        status: units.some(candidate => candidate.key === target) ? 'resolved' : 'missing-dependency' };
    }),
  })).sort((a, b) => a.unitKey.localeCompare(b.unitKey));
  const nested = scenario.entries.filter(entry => entry.kind === 'nested-repository');
  const excluded = scenario.entries.filter(entry => ['test', 'generated'].includes(entry.kind) ||
      nestedRoots.some(root => entry.path === root || entry.path.startsWith(`${root}/`)) || entry.kind === 'nested-repository')
    .map(entry => ({ path: entry.path, reason: entry.kind === 'test' ? 'test-policy-exclude' :
      entry.kind === 'generated' ? 'generated' : 'nested-repository-boundary' }))
    .sort((a, b) => a.path.localeCompare(b.path));
  const diagnostics = [];
  const markerPaths = scenario.entries.filter(entry => entry.kind === 'marker').map(entry => entry.path);
  const packageDirs = markerPaths.filter(path => path.endsWith('/package.json')).map(path => path.slice(0, -'/package.json'.length));
  if (markerPaths.some(path => path === 'package.json') && markerPaths.includes('tsconfig.json') ||
      packageDirs.some(directory => markerPaths.includes(`${directory}/tsconfig.json`))) {
    diagnostics.push('package-context-under-tsconfig');
  }
  if (nested.length) diagnostics.push('nested-repository-boundary');
  if (deduplicated) diagnostics.push('same-owner-path-deduplicated');
  if (inventory.some(unit => unit.references.some(reference => reference.status === 'missing-dependency'))) diagnostics.push('missing-dependency');
  if (!units.length && paths.length) diagnostics.push('marker-free-source-ambiguous');
  const status = diagnostics.includes('missing-dependency') ? 'partial' :
    diagnostics.includes('marker-free-source-ambiguous') ? 'ambiguous' : 'complete';
  return { scenarioId: scenario.id, status, units: inventory, excluded, diagnostics };
}

const hash = 'a'.repeat(64);
function validAdapter() {
  const evidence = [{ path: 'web/src/index.ts', sourceSha256: hash, startByte: 0, endByte: 4, sourceEncoding: 'utf8-bytes' }];
  return {
    contract: 'ce.frontend.contractDraft.v1',
    request: {
      sourceSnapshot: { repositoryId: 'sample-repo', revisionSha: 'b'.repeat(40), snapshotSha256: hash },
      unit: { unitKey: 'unit:v1/web#web', path: 'web', name: 'web' },
      profileId: 'typescript', requestedFeatures: ['declarations', 'references', 'source-spans'],
    },
    result: {
      producer: { name: 'fixture-producer', version: '1.0', executableSha256: hash, configurationSha256: hash },
      languageProfileId: 'typescript',
      features: { supported: ['declarations', 'references', 'source-spans'], unsupported: [] },
      facts: [
        { factId: 'symbol:target', kind: 'declaration', resolution: 'not-applicable', evidence },
        { factId: 'reference:call', kind: 'reference', resolution: 'resolved', targetFactId: 'symbol:target', evidence },
      ],
      coverage: { filesTotal: 1, filesAnalyzed: 1, omittedPaths: [], unresolvedFacts: 0, diagnostics: [] },
      status: 'complete',
    },
  };
}

function validDiscovery(scenario, expected) {
  const sourceSnapshot = { repositoryId: 'sample-repo', revisionSha: 'b'.repeat(40), snapshotSha256: hash };
  return {
    contract: 'ce.discovery.contractDraft.v1',
    sourceSnapshot,
    policy: { registryId: 'fixture-policy', registryVersion: '1.0.0', ...scenario.budgets, includeTests: 'exclude' },
    units: expected.units.map(unit => {
      const source = scenario.units.find(candidate => `unit:v1/${candidate.root}#${candidate.id}` === unit.unitKey);
      return {
        ...unit, ownerDirectory: source.root, markerPath: source.markerPath, declaredUnitId: source.id,
        capabilities: source.capabilities,
      };
    }),
    coverage: {
      candidateMarkers: scenario.units.length,
      unitsAccepted: expected.units.length,
      filesIncluded: expected.units.reduce((count, unit) => count + unit.files.length, 0),
      pathsExcluded: expected.excluded.map(item => item.path),
      diagnostics: expected.diagnostics,
    },
    status: expected.status,
  };
}

test('specification traces every issue obligation to distinct review evidence', () => {
  assert.deepEqual(validateSpecification(contract(), specification()), []);
  assert.match(run(['specification'], root), /19 issue obligations traced/);
});

for (const [name, mutate, expected] of [
  ['missing checkbox', value => { value.acceptance.pop(); }, /map all 19 issue obligations/],
  ['duplicate checkbox', value => { value.acceptance[1].id = value.acceptance[0].id; }, /map all 19 issue obligations/],
  ['missing negative obligation', value => { value.acceptance[0].negativeCase = ''; }, /F01 needs typed evidence anchors/],
  ['evidence without anchor', value => { value.acceptance[0].evidenceAnchor = ''; }, /F01 needs typed evidence anchors/],
  ['self-attested completion', value => { value.acceptance[0].complete = true; }, /self-attested status field/],
  ['dropped security boundary', value => { value.constraints.pop(); }, /machine-readable constraint missing/],
]) test(`specification rejects ${name}`, () => {
  const value = contract(); mutate(value);
  assert.notDeepEqual(validateSpecification(value, specification()), []);
  assert.match(validateSpecification(value, specification()).join('\n'), expected);
});

test('phase names are closed and runtime completion is not inferred', () => {
  for (const args of [[], ['foundation'], ['completion'], ['production'], ['all', '--skip']]) {
    assert.throws(() => run(args, root), /usage: check-frontend-contracts/);
  }
});

test('pseudocode binds ordered operations, failure modes, acceptance and complexity', () => {
  assert.deepEqual(validatePseudocode(policy(), pseudocode()), []);
  assert.match(run(['pseudocode'], root), /19 issue obligations traced/);
});

for (const [name, mutate, expected] of [
  ['missing algorithm step', value => { value.steps.pop(); }, /P01-P11 must be ordered/],
  ['reordered policy operation', value => { [value.steps[2], value.steps[3]] = [value.steps[3], value.steps[2]]; }, /P01-P11 must be ordered/],
  ['missing adapter operation', value => { value.adapterSteps.pop(); }, /A01-A11 must be ordered/],
  ['unmapped obligation', value => { value.requirementTrace.pop(); }, /policy trace must cover/],
  ['invented step reference', value => { value.requirementTrace[0].steps[0] = 'P99'; }, /F01 must map/],
  ['unbounded complexity', value => { value.complexity.boundedBy.pop(); }, /every hard input budget/],
  ['self-attested policy status', value => { value.complete = true; }, /self-attested status field/],
]) test(`pseudocode rejects ${name}`, () => {
  const value = policy(); mutate(value);
  assert.match(validatePseudocode(value, pseudocode()).join('\n'), expected);
});

test('architecture binds source observations, producer evidence, strict drafts and rollback matrix', () => {
  const value = architectureInputs();
  assert.deepEqual(validateArchitecture(value.contract, value.report, value.adr,
    value.adapterSchema, value.discoverySchema, value.compatibility,
    value.scenarios, value.expectedInventories), []);
  assert.match(run(['architecture'], root), /19 issue obligations traced/);
});

for (const [name, mutate, expected] of [
  ['source citation removal', value => { value.report = value.report.replace('schema/v1/record.schema.json#L61-L79', ''); }, /exact-source citation missing/],
  ['claiming the MADR was implemented', value => { value.adr = value.adr.replace('not adopted or implemented', 'implemented'); }, /remain explicitly proposed/],
  ['permitting an unknown adapter field', value => { value.adapterSchema.properties.request.additionalProperties = true; }, /adapter .* must reject unknown properties/],
  ['dropping adapter toolchain provenance', value => { delete value.adapterSchema.properties.result.properties.producer; }, /adapter field properties.result.properties.producer/],
  ['weakening old-consumer rejection', value => { value.compatibility.cases.find(item => item.id === 'C03').expected = 'accept new language'; }, /old artifact acceptance and old-consumer rejection/],
  ['dropping rollback evidence', value => { value.compatibility.cases.find(item => item.id === 'C05').rollback = ''; }, /C05 must state producer/],
  ['self-attesting compatibility', value => { value.compatibility.cases[0].passed = true; }, /self-attested status field/],
]) test(`architecture rejects ${name}`, () => {
  const value = architectureInputs(); mutate(value);
  assert.match(validateArchitecture(value.contract, value.report, value.adr,
    value.adapterSchema, value.discoverySchema, value.compatibility,
    value.scenarios, value.expectedInventories).join('\n'), expected);
});

for (const [name, mutate, expected] of [
  ['missing scenario expectation', value => { value.positive.pop(); }, /expected positive inventories must independently cover/],
  ['standalone accidentally succeeds', value => { value.positive.find(item => item.scenarioId === 'standalone-source').status = 'complete'; }, /standalone source must remain explicitly ambiguous/],
  ['negative case silently succeeds', value => { value.negative.find(item => item.scenarioId === 'symlink-escape').status = 'complete'; }, /negative case cannot be successful/],
  ['overlap loses dedupe', value => { value.positive.find(item => item.scenarioId === 'overlap-dedupe').diagnostics = []; }, /overlap fixture must preserve/],
  ['missing link hidden', value => { value.positive.find(item => item.scenarioId === 'missing-dependency').status = 'complete'; }, /missing dependency must remain an explicit partial/],
  ['self-attested oracle status', value => { value.verified = true; }, /self-attested status field/],
]) test(`sealed expected inventories reject ${name}`, () => {
  const value = architectureInputs().expectedInventories; mutate(value);
  assert.match(validateFixtureCatalogue(architectureInputs().scenarios, value).join('\n'), expected);
});

test('draft schemas validate representative adapter and discovery instances', () => {
  const { adapterSchema, discoverySchema, scenarios, expectedInventories } = architectureInputs();
  assert.deepEqual(validateSchemaDefinition(adapterSchema), []);
  assert.deepEqual(validateSchemaDefinition(discoverySchema), []);
  assert.deepEqual(validateAdapter(validAdapter(), adapterSchema), []);
  const scenario = scenarios.positive.find(item => item.id === 'mixed-language-monorepo');
  const expected = evaluateScenario(scenario);
  const inventory = validDiscovery(scenario, expected);
  assert.deepEqual(validateDiscovery(inventory, discoverySchema, scenario, expected), []);
});

test('schema instances reject malformed evidence, features, coverage and identities', () => {
  const { adapterSchema, discoverySchema, scenarios } = architectureInputs();
  const badFeature = validAdapter();
  badFeature.result.features.supported.push('semantic.magic');
  assert.match(validateAdapter(badFeature, adapterSchema).join('\n'), /unknown supported capability/);
  const hiddenCoverage = validAdapter();
  hiddenCoverage.result.coverage.filesAnalyzed = 0;
  assert.match(validateAdapter(hiddenCoverage, adapterSchema).join('\n'), /complete result hides incomplete coverage/);
  const badSpan = validAdapter();
  badSpan.result.facts[0].evidence[0].endByte = 0;
  assert.match(validateAdapter(badSpan, adapterSchema).join('\n'), /invalid source span/);
  const unknownField = validAdapter();
  unknownField.result.injected = true;
  assert.match(validateAdapter(unknownField, adapterSchema).join('\n'), /unknown property injected/);
  const unknownCapability = validAdapter();
  unknownCapability.request.requestedFeatures = ['semantic.magic'];
  unknownCapability.result.features = { supported: [], unsupported: ['semantic.magic'] };
  unknownCapability.result.facts = [];
  unknownCapability.result.coverage = {
    filesTotal: 1, filesAnalyzed: 0, omittedPaths: ['web/src/index.ts'],
    unresolvedFacts: 0, diagnostics: ['unknown-capability'],
  };
  unknownCapability.result.status = 'unsupported';
  assert.deepEqual(validateAdapter(unknownCapability, adapterSchema), []);
  const unknownLanguageProfileCase = validAdapter();
  unknownLanguageProfileCase.request.profileId = 'new-language';
  unknownLanguageProfileCase.result.languageProfileId = 'new-language';
  unknownLanguageProfileCase.result.features = { supported: [], unsupported: ['declarations', 'references', 'source-spans'] };
  unknownLanguageProfileCase.result.facts = [];
  unknownLanguageProfileCase.result.status = 'unsupported';
  assert.deepEqual(validateAdapter(unknownLanguageProfileCase, adapterSchema), []);

  const scenario = scenarios.positive.find(item => item.id === 'mixed-language-monorepo');
  const expected = evaluateScenario(scenario);
  const unknownProfile = validDiscovery(scenario, expected);
  unknownProfile.units[0].languageProfileId = 'new-language';
  assert.match(validateDiscovery(unknownProfile, discoverySchema, scenario, expected).join('\n'), /unknown language profile/);
  const unsafePath = validDiscovery(scenario, expected);
  unsafePath.units[0].files[0] = '../outside.cs';
  assert.notDeepEqual(validateDiscovery(unsafePath, discoverySchema, scenario, expected), []);
});

test('sealed layout oracle matches every independent positive and negative inventory', () => {
  const { scenarios, expectedInventories } = architectureInputs();
  for (const scenario of scenarios.positive) {
    const sealed = expectedInventories.positive.find(item => item.scenarioId === scenario.id);
    assert.ok(sealed, `missing sealed expectation for ${scenario.id}`);
    assert.equal(canonical(evaluateScenario(scenario)), canonical(sealed), scenario.id);
    const discovery = validDiscovery(scenario, sealed);
    assert.deepEqual(validateDiscovery(discovery, architectureInputs().discoverySchema, scenario, sealed), [], scenario.id);
  }
  for (const scenario of scenarios.negative) {
    const sealed = expectedInventories.negative.find(item => item.scenarioId === scenario.id);
    assert.ok(sealed, `missing sealed expectation for ${scenario.id}`);
    const normalized = { ...sealed, excluded: [] };
    assert.equal(canonical(evaluateScenario(scenario)), canonical(normalized), scenario.id);
    const discovery = validDiscovery(scenario, normalized);
    assert.deepEqual(validateDiscovery(discovery, architectureInputs().discoverySchema, scenario, normalized), [], scenario.id);
  }
});

test('layout canonical bytes repeat without timestamps', () => {
  const scenario = architectureInputs().scenarios.positive.find(item => item.id === 'mixed-language-monorepo');
  const first = canonical(evaluateScenario(scenario));
  const second = canonical(evaluateScenario(scenario));
  assert.equal(first, second);
  assert.doesNotMatch(first, /generatedAt|timestamp|observedAt/i);
});

test('negative discovery cases reject unknown and unsafe inputs', () => {
  const scenarios = architectureInputs().scenarios.negative;
  for (const [id, status, diagnostic] of [
    ['unknown-language', 'unsupported', 'unknown-language-profile'],
    ['unknown-capability', 'unsupported', 'unknown-capability'],
    ['marker-free-ambiguity', 'ambiguous', 'marker-free-source-ambiguous'],
    ['conflicting-owner', 'failed', 'ownership-conflict'],
    ['conflicting-override', 'failed', 'conflicting-unit-identity'],
    ['budget-exceeded', 'failed', 'limit-exceeded'],
    ['path-traversal', 'failed', 'path-escape'],
    ['symlink-escape', 'failed', 'symlink-escape'],
  ]) {
    const result = evaluateScenario(scenarios.find(scenario => scenario.id === id));
    assert.equal(result.status, status, id);
    assert.deepEqual(result.diagnostics, [diagnostic], id);
  }
  const budgetCase = scenarios.find(scenario => scenario.id === 'budget-exceeded');
  for (const budgets of [{ maxDepth: 8, maxFiles: 1, maxBytes: 1024 }, { maxDepth: 8, maxFiles: 8, maxBytes: 10 }]) {
    const constrained = { ...budgetCase, budgets: { ...budgetCase.budgets, ...budgets } };
    assert.deepEqual(evaluateScenario(constrained).diagnostics, ['limit-exceeded']);
  }
});
