import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  run, validateArchitecture, validateFixtureCatalogue, validatePseudocode, validateSpecification,
} from '../scripts/research/check-frontend-contracts.mjs';
import {
  canonical, compareUtf8, evaluateScenario, matchesGlob, validateAdapter, validateDiscovery,
  validateSchemaDefinition, validAdapter, validDiscovery,
} from './fixtures/frontend-contracts/conformance.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const readJson = path => JSON.parse(readFileSync(join(root, path), 'utf8'));
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
const evaluate = scenario => evaluateScenario(scenario, readJson('tests/fixtures/frontend-contracts/layout-scenarios.json'));

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
  ['dropping per-unit budget from discovery schema', value => { delete value.discoverySchema.properties.policy.properties.maxFilesPerUnit; }, /properties.policy.properties.maxFilesPerUnit/],
  ['dropping source-byte offset declaration', value => { delete value.adapterSchema.properties.result.properties.facts.items.properties.evidence.items.properties.byteOffsetEncoding; }, /byteOffsetEncoding/],
  ['misaligning a producer comparison row', value => {
    value.report = value.report.replace(/^\| Tree-sitter \|.*$/m, '| Tree-sitter | syntax only | parse tree | MIT | offline |');
  }, /six aligned cells/],
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

test('draft schemas validate source-byte adapter and discovery instances', () => {
  const { adapterSchema, discoverySchema, scenarios, expectedInventories } = architectureInputs();
  assert.deepEqual(validateSchemaDefinition(adapterSchema), []);
  assert.deepEqual(validateSchemaDefinition(discoverySchema), []);
  assert.deepEqual(validateAdapter(validAdapter(), adapterSchema, { 'web/src/index.ts': Buffer.from('éx') }), []);
  const scenario = scenarios.positive.find(item => item.id === 'mixed-language-monorepo');
  const expected = evaluate(scenario);
  assert.deepEqual(validateDiscovery(validDiscovery(scenario, expected), discoverySchema, scenario, expected,
    readJson('tests/fixtures/frontend-contracts/layout-scenarios.json')), []);
});

test('adapter rejects inconsistent profiles, complete-with-gaps, malformed paths and byte offsets', () => {
  const { adapterSchema } = architectureInputs();
  const mismatch = validAdapter();
  mismatch.result.languageProfileId = 'csharp';
  assert.match(validateAdapter(mismatch, adapterSchema).join('\n'), /profiles differ/);
  const diagnostics = validAdapter();
  diagnostics.result.coverage.diagnostics = ['warning'];
  assert.match(validateAdapter(diagnostics, adapterSchema, { 'web/src/index.ts': Buffer.from('éx') }).join('\n'), /complete result hides/);
  const unresolved = validAdapter();
  unresolved.result.facts.push({
    factId: 'ref:unknown', kind: 'reference', resolution: 'unresolved',
    evidence: unresolved.result.facts[0].evidence,
  });
  unresolved.result.coverage.unresolvedFacts = 1;
  assert.match(validateAdapter(unresolved, adapterSchema, { 'web/src/index.ts': Buffer.from('éx') }).join('\n'), /complete result hides/);
  const badIdentity = validAdapter();
  badIdentity.request.unit.unitKey = 'unit:v1/elsewhere#web';
  assert.match(validateAdapter(badIdentity, adapterSchema).join('\n'), /unit identity disagrees/);
  for (const badPath of ['/absolute.ts', 'C:/drive.ts', 'file:///uri.ts', 'file:relative.txt', 'src\\\\file.ts', 'src//file.ts', 'src/./file.ts', 'src/../file.ts', 'bad\\0path.ts']) {
    const unsafe = validAdapter();
    unsafe.result.facts[0].evidence[0].path = badPath;
    assert.notDeepEqual(validateAdapter(unsafe, adapterSchema), [], JSON.stringify(badPath));
  }
  const wrongUnits = validAdapter();
  wrongUnits.result.facts[0].evidence[0].byteOffsetEncoding = 'utf-16-code-units';
  assert.notDeepEqual(validateAdapter(wrongUnits, adapterSchema), []);
  const byteSpan = validAdapter();
  assert.deepEqual(validateAdapter(byteSpan, adapterSchema, { 'web/src/index.ts': Buffer.from('éx') }), []);
  byteSpan.result.facts[0].evidence[0].endByte = 4;
  assert.match(validateAdapter(byteSpan, adapterSchema, { 'web/src/index.ts': Buffer.from('éx') }).join('\n'), /exceeds frozen bytes/);
});

test('unknown profile and capability are unsupported rather than success-shaped', () => {
  const { adapterSchema } = architectureInputs();
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
  const unknownLanguage = validAdapter();
  unknownLanguage.request.profileId = 'new-language';
  unknownLanguage.result.languageProfileId = 'new-language';
  unknownLanguage.result.features = { supported: [], unsupported: ['declarations', 'references', 'source-spans'] };
  unknownLanguage.result.facts = [];
  unknownLanguage.result.status = 'unsupported';
  assert.deepEqual(validateAdapter(unknownLanguage, adapterSchema), []);
  unknownLanguage.result.facts.push(validAdapter().result.facts[0]);
  assert.match(validateAdapter(unknownLanguage, adapterSchema).join('\n'), /unsupported result contains semantic claims/);
});

test('sealed layout oracle matches every independent positive and negative inventory', () => {
  const { scenarios, expectedInventories, discoverySchema } = architectureInputs();
  for (const scenario of scenarios.positive) {
    const sealed = expectedInventories.positive.find(item => item.scenarioId === scenario.id);
    assert.ok(sealed, `missing sealed expectation for ${scenario.id}`);
    assert.equal(canonical(evaluate(scenario)), canonical(sealed), scenario.id);
    assert.deepEqual(validateDiscovery(validDiscovery(scenario, sealed), discoverySchema, scenario, sealed,
      scenarios), [], scenario.id);
  }
  for (const scenario of scenarios.negative) {
    const sealed = expectedInventories.negative.find(item => item.scenarioId === scenario.id);
    assert.ok(sealed, `missing sealed expectation for ${scenario.id}`);
    const normalized = { ...sealed, excluded: [] };
    assert.equal(canonical(evaluate(scenario)), canonical(normalized), scenario.id);
  }
});

test('restricted glob supports zero or many directories and rejects unsupported syntax', () => {
  for (const [pattern, path, expected] of [
    ['src/**/*.ts', 'src/file.ts', true],
    ['src/**/*.ts', 'src/deep/nested/file.ts', true],
    ['**/*', 'file.ts', true],
    ['**/*', 'one/two/file.ts', true],
    ['src/*.ts', 'src/deep/file.ts', false],
  ]) assert.equal(matchesGlob(pattern, path), expected, `${pattern} -> ${path}`);
  for (const pattern of ['src/**.ts', 'src/{a,b}.ts', 'src/[ab].ts', 'src//*.ts', '../*.ts']) {
    assert.throws(() => matchesGlob(pattern, 'src/file.ts'), /unsupported glob pattern/);
  }
});

test('glob oracle uses UTF-8 ordinal ordering and ignores input enumeration order', () => {
  const { scenarios } = architectureInputs();
  const scenario = scenarios.positive.find(item => item.id === 'deep-glob-ordinal-order');
  const expected = evaluate(scenario);
  const reversed = {
    ...scenario,
    entries: [...scenario.entries].reverse(),
    units: scenario.units.map(unit => ({ ...unit, includePatterns: [...unit.includePatterns].reverse() })).reverse(),
  };
  assert.deepEqual(evaluate(reversed), expected);
  assert.deepEqual(expected.units[0].files, [...expected.units[0].files].sort(compareUtf8));
  assert.deepEqual(expected.units.map(unit => unit.unitKey), [...expected.units.map(unit => unit.unitKey)].sort(compareUtf8));
});

test('explicit excludes override includes while advisory category overrides remain unsupported', () => {
  const { scenarios, expectedInventories } = architectureInputs();
  const scenario = scenarios.positive.find(item => item.id === 'explicit-exclude-wins');
  const expected = expectedInventories.positive.find(item => item.scenarioId === scenario.id);
  assert.deepEqual(evaluate(scenario), expected);
  const forcedInclude = structuredClone(scenario);
  forcedInclude.units[0].includePatterns.push('src/private.ts');
  assert.equal(canonical(evaluate(forcedInclude)), canonical(expected));
  const categoryInclude = structuredClone(scenario);
  categoryInclude.entries.push({ path: 'tests/extra.ts', kind: 'test', bytes: 12 });
  categoryInclude.units[0].includePatterns.push('tests/*.ts');
  assert.ok(evaluate(categoryInclude).excluded.some(item => item.path === 'tests/extra.ts'));
});

test('negative discovery cases reject unknown and unsafe inputs', () => {
  const { scenarios, expectedInventories } = architectureInputs();
  for (const scenario of scenarios.negative) {
    const expected = expectedInventories.negative.find(item => item.scenarioId === scenario.id);
    assert.ok(expected, `missing negative expectation for ${scenario.id}`);
    assert.equal(canonical(evaluate(scenario)), canonical({ ...expected, excluded: [] }), scenario.id);
  }
  const sourceCase = structuredClone(scenarios.positive.find(item => item.id === 'deep-glob-ordinal-order'));
  for (const [badPath, status] of [
    ['/absolute.ts', 'path-escape'], ['C:/drive.ts', 'path-escape'], ['file:///uri.ts', 'path-escape'],
    ['file:relative.txt', 'path-escape'],
    ['src\\\\file.ts', 'path-escape'], ['src//file.ts', 'path-escape'], ['src/./file.ts', 'path-escape'],
    ['bad\\0path.ts', 'path-escape'],
  ]) {
    const mutated = { ...sourceCase, entries: [{ path: badPath, kind: 'source', bytes: 1 }] };
    assert.equal(evaluate(mutated).diagnostics[0], status, JSON.stringify(badPath));
  }
  const wrongKey = structuredClone(sourceCase);
  wrongKey.units[0].unitKey = 'unit:v1/other#web';
  assert.deepEqual(evaluate(wrongKey).diagnostics, ['unit-identity-mismatch']);
  const badRef = structuredClone(sourceCase);
  badRef.units[0].references = [{ target: '../escape#unit', relation: 'project' }];
  assert.deepEqual(evaluate(badRef).diagnostics, ['path-escape']);
});

test('per-unit and total-file budgets fail independently', () => {
  const { scenarios, expectedInventories } = architectureInputs();
  for (const scenario of scenarios.negative.filter(item => item.id.includes('file-budget'))) {
    const expected = expectedInventories.negative.find(item => item.scenarioId === scenario.id);
    assert.deepEqual(evaluate(scenario), { ...expected, excluded: [] }, scenario.id);
  }
  const perUnit = scenarios.negative.find(item => item.id === 'per-unit-file-budget');
  assert.ok(perUnit.budgets.maxFiles > perUnit.entries.filter(entry => entry.kind === 'source').length);
  assert.ok(perUnit.budgets.maxFilesPerUnit < perUnit.entries.filter(entry => entry.kind === 'source').length);
  const total = scenarios.negative.find(item => item.id === 'total-file-budget');
  assert.ok(total.budgets.maxFiles < total.entries.filter(entry => entry.kind === 'source').length);
  assert.ok(total.budgets.maxFilesPerUnit >= total.entries.filter(entry => entry.kind === 'source').length);
});

test('layout canonical bytes repeat without timestamps', () => {
  const scenario = architectureInputs().scenarios.positive.find(item => item.id === 'mixed-language-monorepo');
  const first = canonical(evaluate(scenario));
  assert.equal(first, canonical(evaluate(scenario)));
  assert.doesNotMatch(first, /generatedAt|timestamp|observedAt/i);
});
