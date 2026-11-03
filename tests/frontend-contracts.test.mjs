import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { run, validatePseudocode, validateSpecification } from '../scripts/research/check-frontend-contracts.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const readJson = path => JSON.parse(readFileSync(join(root, path), 'utf8'));
const contract = () => readJson('config/research/frontend-contracts.json');
const specification = () => readFileSync(join(root, 'docs/research/language-neutral-frontends-specification.md'), 'utf8');
const pseudocode = () => readFileSync(join(root, 'docs/research/language-neutral-frontends-pseudocode.md'), 'utf8');
const policy = () => readJson('config/research/frontend-layout-policy.json');

test('specification traces every issue obligation to distinct review evidence', () => {
  assert.deepEqual(validateSpecification(contract(), specification()), []);
  assert.match(run(['specification'], root), /19 issue obligations traced/);
});

for (const [name, mutate, expected] of [
  ['missing checkbox', value => { value.acceptance.pop(); }, /map all 19 issue obligations/],
  ['duplicate checkbox', value => { value.acceptance[1].id = value.acceptance[0].id; }, /map all 19 issue obligations/],
  ['missing negative obligation', value => { value.acceptance[0].negativeCase = ''; }, /F01 needs named evidence/],
  ['evidence without heading', value => { value.acceptance[0].evidenceHeading = ''; }, /F01 needs named evidence/],
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
