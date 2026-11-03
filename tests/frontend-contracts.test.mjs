import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { run, validateSpecification } from '../scripts/research/check-frontend-contracts.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const readJson = path => JSON.parse(readFileSync(join(root, path), 'utf8'));
const contract = () => readJson('config/research/frontend-contracts.json');
const specification = () => readFileSync(join(root, 'docs/research/language-neutral-frontends-specification.md'), 'utf8');

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
