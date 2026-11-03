import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync, rmSync, cpSync, renameSync, readdirSync, existsSync, symlinkSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { readJSON, specification, pseudocode, branchEligibility, traceability } from '../scripts/decisions/contracts.mjs';
import { loadLedger, reconcile } from '../scripts/decisions/ledger.mjs';
import { canonical, key, COLORS, RELATIONS, relationError, validateCycles } from '../scripts/decisions/semantics.mjs';
import { discover, localPath, loadCollection, parseSource, passage, sha256, verifyEvidence } from '../scripts/decisions/source.mjs';
import { strictJSON, parseCollection } from '../scripts/decisions/json-source.mjs';
import { projections, outputViews, report, projectionBoundary } from '../scripts/decisions/project.mjs';
import { validate, auditSchema } from '../scripts/decisions/schema.mjs';
import { authorRecord } from '../scripts/decisions/author.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const fixture = resolve(root, 'tests/fixtures/decision-ledger');
const data = () => ({ config: readJSON(resolve(fixture, 'project.json')), ledger: readJSON(resolve(fixture, 'ledger.json')) });
const baseline = () => loadLedger(fixture, 'project.json');
let caseNumber = 0;
function workspace(t) {
  const path = resolve(fixture, `.case-${process.pid}-${++caseNumber}`);
  mkdirSync(path);
  t.after(() => rmSync(path, { recursive: true }));
  for (const entry of ['markdown project', 'json project', 'frontmatter project', 'project.json', 'ledger.json'])
    cpSync(resolve(fixture, entry), resolve(path, entry), { recursive: true });
  return path;
}
function sourceFor(record, config, base = fixture) {
  const project = config.projects.find(p => p.id === record.project);
  return loadCollection(localPath(base, project.root), record.path, project, config.limits)
    .find(s => (s.pointer ?? '') === (record.pointer ?? ''));
}
function snapshot(path) {
  return Object.fromEntries(readdirSync(path, { recursive: true, withFileTypes: true })
    .filter(e => e.isFile()).map(e => {
      const file = resolve(e.parentPath, e.name);
      return [file.slice(path.length), sha256(readFileSync(file))];
    }).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0));
}
function cli(args) {
  return spawnSync(process.execPath, [resolve(root, 'scripts/decisions/check.mjs'), ...args], { cwd: root, encoding: 'utf8' });
}

test('inventory coverage', () => {
  const config = readJSON(resolve(root, 'config/decisions/project.json'));
  const actual = loadLedger(root, 'config/decisions/project.json');
  const paths = discover(root, config.projects[0], config.limits);
  assert.deepEqual(actual.records.map(r => r.path).sort(), paths.sort());
  assert.ok(actual.edges.every(e => e.standing === 'review-proposed'), 'navigation is explicitly reviewed, not inferred or adopted');
  assert.ok(actual.records.every(r => r.facets.every(f => f.standing === 'review-proposed')), 'classification is a review proposal');
  const { config: c, ledger: l } = data();
  l.records.pop();
  assert.throws(() => reconcile(fixture, c, l), /inventory coverage/);
  const duplicate = data(); duplicate.ledger.records.push(structuredClone(duplicate.ledger.records[0]));
  assert.throws(() => reconcile(fixture, duplicate.config, duplicate.ledger), /duplicate record identity/);
  const wrongTitle = data(); wrongTitle.ledger.records[0].title = 'Incorrect title';
  assert.throws(() => reconcile(fixture, wrongTitle.config, wrongTitle.ledger), /title mismatch/);
});

test('heterogeneous adapters', () => {
  const model = baseline();
  assert.equal(model.records.length, 7);
  const json = model.records.find(r => r.id === 'implemented-choice');
  assert.equal(json.canonical, 'json');
  assert.equal(json.rawStatus, 'Implemented');
  assert.equal(json.status.state, 'unknown');
  assert.equal(json.implementation, 'not-assessed');
  assert.equal(json.sourceMetadata.find(m => m.key === 'verification').value, 'not independently qualified');
  const adoption = model.records.find(r => r.project === 'gamma');
  assert.ok(adoption.sourceMetadata.some(m => m.key === 'shared-adoption-pointer'));
  assert.equal(adoption.status.state, 'proposed');
  assert.ok(!adoption.status.acceptance);
  assert.ok(!model.records.some(r => r.alias === 'archived'));
  assert.ok(model.diagnostics.some(d => d.kind === 'unclassified'));
});

test('portable identities and rename', t => {
  const base = workspace(t), { config, ledger } = data();
  const record = ledger.records.find(r => r.id === 'choice-a'), oldID = key(record.project, record.id);
  const old = record.path, moved = 'decisions/Décision renommée (2026)#final.md';
  renameSync(resolve(base, config.projects[0].root, old), resolve(base, config.projects[0].root, moved));
  record.path = moved;
  const model = reconcile(base, config, ledger);
  assert.equal(key(model.records.find(r => r.path === moved).project, record.id), oldID);
  const collision = report(model).aliasCollisions.find(g => g.alias === '0005');
  assert.equal(collision.ids.length, 3);
  assert.equal(collision.equivalent, 'not-asserted');
  assert.ok(projections(model, config, base)['index.md'].includes('%282026%29%23final.md'));
  assert.throws(() => authorRecord(base, config, { project: 'alpha', path: moved, alias: '0005', scope: 'team α' }), /requires id/);
});

test('unknown metadata', () => {
  const { config, ledger } = data();
  const unknown = ledger.records.find(r => r.id === 'choice-b');
  assert.equal(unknown.status.state, 'unknown');
  assert.ok(baseline().diagnostics.some(d => d.kind === 'unknown-status'));
  unknown.status.state = 'proposed';
  assert.throws(() => reconcile(fixture, config, ledger), /status mismatch/);
  const parsed = parseSource(Buffer.from('# Sans statut\n\n## Context\n'), 'markdown-header-v1');
  assert.equal(parsed.rawStatus, null);
  const model = baseline();
  assert.ok(model.records.find(r => r.id === 'choice-b').sourceMetadata.some(m => m.key === 'custom metadata'));
});

test('scope qualified acceptance', () => {
  const { config, ledger } = data();
  assert.deepEqual(Object.keys(report(baseline()).activeByScope).sort(), ['team α', 'workspace beta']);
  const accepted = ledger.records.find(r => r.id === 'choice-a');
  delete accepted.status.acceptance;
  const unqualified = reconcile(fixture, config, ledger);
  assert.ok(!Object.hasOwn(report(unqualified).activeByScope, 'team α'));
  assert.ok(unqualified.diagnostics.some(d => d.kind === 'unqualified-acceptance'));
  const bad = data(); bad.ledger.records[0].status.acceptance.scope = 'global';
  assert.throws(() => reconcile(fixture, bad.config, bad.ledger), /scope qualified acceptance/);
  const widened = data();
  widened.ledger.records[0].status.scope = widened.ledger.records[0].status.acceptance.scope = 'global';
  assert.throws(() => reconcile(fixture, widened.config, widened.ledger), /acceptance scope passage/);
  const json = data(), r = json.ledger.records.find(r => r.id === 'accepted-choice');
  r.status.acceptance.evidence.pointer = '/decisions/0/scope';
  assert.throws(() => reconcile(fixture, json.config, json.ledger), /JSON evidence outside record/);
});

test('conflicting acceptance', () => {
  const model = baseline(), duplicate = structuredClone(model.records.find(r => r.id === 'choice-a'));
  duplicate.id = 'different-decision'; model.records.push(duplicate);
  const result = report(model);
  assert.equal(result.scopeReview[0].scope, 'team α');
  assert.match(result.scopeReview[0].outcome, /not necessarily contradiction/);
  assert.match(result.contradictions, /not-assessed/);
  assert.ok(!Object.hasOwn(result.activeByScope, 'global'));
});

test('source change invalidates', t => {
  const base = workspace(t), { config, ledger } = data(), a = ledger.records[0];
  const path = resolve(base, config.projects[0].root, a.path);
  writeFileSync(path, readFileSync(path, 'utf8') + '\nChanged context outside the cited passage.\n');
  assert.throws(() => reconcile(base, config, ledger), /stale source revision/);
  a.revision = `sha256:${sha256(readFileSync(path))}`;
  assert.throws(() => reconcile(base, config, ledger), /stale evidence revision/);
  function rebind(value) {
    if (!value || typeof value !== 'object') return;
    if (value.record === key(a.project, a.id) && value.revision) value.revision = a.revision;
    Object.values(value).forEach(rebind);
  }
  rebind(ledger);
  assert.throws(() => reconcile(base, config, ledger), /stale edge source revision|stale target revision/);
  const unchanged = data();
  unchanged.ledger.records[0].revision = 'sha256:' + '0'.repeat(64);
  unchanged.ledger.records[0].status.evidence.revision = unchanged.ledger.records[0].revision;
  assert.throws(() => reconcile(fixture, unchanged.config, unchanged.ledger), /stale source revision/);
});

test('evidence passage hashes cannot self-attest', () => {
  const { config, ledger } = data(), a = ledger.records[0], s = sourceFor(a, config);
  const receipts = new Map([[key(a.project, a.id), s]]);
  const forged = structuredClone(a.status.evidence);
  forged.quote = 'accepted globally'; forged.sha256 = sha256(forged.quote);
  assert.throws(() => verifyEvidence(forged, receipts, forged.record), /passage mismatch/);
  forged.end = s.lines.length + 1;
  assert.throws(() => verifyEvidence(forged, receipts, forged.record), /evidence span/);
  a.facets[0].value = 'made-up-category';
  assert.throws(() => reconcile(fixture, config, ledger), /unknown facet vocabulary/);
});

test('external target states', () => {
  const { config, ledger } = data(), from = ledger.records[0], s = sourceFor(from, config);
  for (const [state, project, id] of [['unresolved', 'remote', 'external-choice'], ['private', 'private-project', 'private-choice']]) {
    const line = s.lines.findIndex(l => l.includes(`${project}/${id}`)) + 1;
    ledger.edges.push({
      id: `${state}-reference`, from: key(from.project, from.id), fromRevision: from.revision,
      to: { project, id, state, reason: 'not available to the bounded local resolver' },
      type: 'references', scope: 'team α', coverage: 'partial', standing: 'source-declared',
      authority: from.status.authority, evidence: passage(s, key(from.project, from.id), line)
    });
  }
  const model = reconcile(fixture, config, ledger);
  assert.equal(model.diagnostics.filter(d => /^(private|unresolved)-target$/.test(d.kind)).length, 2);
  const graph = projections(model, config, fixture)['graph.mmd'];
  assert.ok(graph.includes('(unresolved)') && graph.includes('(private)'));
  ledger.edges.at(-1).to.state = 'resolved';
  assert.throws(() => reconcile(fixture, config, ledger), /dangling resolved target/);
  ledger.edges.at(-1).to.state = 'private'; ledger.edges.at(-1).to.revision = from.revision;
  assert.throws(() => reconcile(fixture, config, ledger), /unavailable target needs reason/);
});

test('relation cycles', () => {
  for (const [type, rule] of Object.entries(RELATIONS)) {
    const cycle = [
      { type, from: key('p', 'one'), to: { project: 'p', id: 'two', state: 'resolved' } },
      { type, from: key('p', 'two'), to: { project: 'p', id: 'one', state: 'resolved' } }
    ];
    if (rule.cyclic) assert.doesNotThrow(() => validateCycles(cycle), type);
    else assert.throws(() => validateCycles(cycle), new RegExp(`cycle: ${type}`));
  }
  validateCycles([
    { type: 'depends-on', from: key('a', 'one'), to: { project: 'a', id: 'two', state: 'resolved' } },
    { type: 'references', from: key('a', 'two'), to: { project: 'a', id: 'one', state: 'resolved' } }
  ]);
});

test('mixed prerequisite cycles and draft status', () => {
  assert.throws(() => validateCycles([
    { type: 'depends-on', from: key('p', 'a'), to: { project: 'p', id: 'b', state: 'resolved' } },
    { type: 'extends', from: key('p', 'b'), to: { project: 'p', id: 'a', state: 'resolved' } }
  ]), /cycle: prerequisite/);
  const { config, ledger } = data();
  const record = ledger.records.find(r => r.id === 'choice-b');
  config.projects.find(p => p.id === record.project).statusMap.push({ raw: sourceFor(record, config).rawStatus, state: 'draft' });
  record.status.state = 'draft';
  assert.equal(reconcile(fixture, config, ledger).records.find(r => r.id === record.id).status.state, 'draft');
  assert.equal(COLORS.draft, '#f8d7da');
});

test('single-object JSON requires an explicit profile', () => {
  const bytes = Buffer.from(JSON.stringify({ id: 'local-0005', title: 'Standalone decision', status: 'Proposed' }));
  const profile = { collection: '', id: 'id', title: 'title', status: 'status', relations: [] };
  assert.throws(() => parseCollection(bytes, profile), /must be an array/);
  const [source] = parseCollection(bytes, { ...profile, singleObject: true });
  assert.equal(source.pointer, '');
  assert.equal(source.statusPointer, '/status');
  assert.equal(source.alias, 'local-0005');
  assert.throws(() => parseCollection(Buffer.from('[]'), { ...profile, singleObject: true }), /must be an object/);
});

test('scope and relation semantics', () => {
  const { config, ledger } = data(), supersedes = ledger.edges.find(e => e.type === 'full-supersedes');
  supersedes.scope = 'a narrower scope';
  assert.throws(() => reconcile(fixture, config, ledger), /supersession target scope/);
  const edge = { from: key('one', 'same-local-id'), to: { project: 'two', id: 'same-local-id' },
    scope: 'local', type: 'full-supersedes', coverage: 'whole' };
  assert.equal(relationError(edge), null, 'cross-project supersession never equates identity');
  edge.type = 'scoped-overrides';
  assert.equal(relationError(edge), 'relation coverage');
  assert.equal(RELATIONS.extends.transitive, false);
  assert.equal(RELATIONS.references.cyclic, true);
});

test('directed JSON relations and inverse evidence', () => {
  const model = baseline(), supersedes = model.edges.filter(e => e.type === 'full-supersedes');
  assert.equal(supersedes.length, 1);
  assert.equal(supersedes[0].corroboration.length, 1);
  assert.equal(model.edges.filter(e => e.type === 'related-from').length, 1);
  const { config, ledger } = data();
  const duplicate = structuredClone(ledger.edges.find(e => e.type === 'full-supersedes'));
  duplicate.id = 'duplicate-inverse'; ledger.edges.push(duplicate);
  assert.throws(() => reconcile(fixture, config, ledger), /duplicate normalized relation/);
  ledger.edges.pop();
  ledger.edges.find(e => e.type === 'related-from').type = 'related-to';
  assert.throws(() => reconcile(fixture, config, ledger), /JSON relation field binding/);
});

test('unimported typed declarations remain visible', () => {
  const { config, ledger } = data();
  ledger.edges = ledger.edges.filter(e => e.type !== 'full-supersedes');
  const model = reconcile(fixture, config, ledger);
  assert.equal(model.diagnostics.filter(d => d.kind === 'unimported-typed-relation').length, 2);
  assert.equal(model.edges.filter(e => e.type === 'full-supersedes').length, 0);
});

test('canonical projections', () => {
  const { config, ledger } = data(), expected = projections(baseline(), config, fixture);
  config.projects.reverse(); config.vocabulary.reverse();
  for (const p of config.projects) p.statusMap.reverse();
  ledger.records.reverse(); ledger.edges.reverse();
  for (const r of ledger.records) r.facets.reverse();
  const actual = projections(reconcile(fixture, config, ledger), config, fixture);
  assert.deepEqual(actual, expected);
  assert.equal(canonical({ z: 1, a: { β: 1, a: 2 } }), canonical({ a: { a: 2, β: 1 }, z: 1 }));
});

test('projection inventory titles colors and links', () => {
  const model = baseline(), { config } = data(), views = projections(model, config, fixture);
  const nodes = [...views['graph.mmd'].matchAll(/^  (n[a-f0-9]{64})\["/gm)];
  assert.equal(nodes.length, model.records.length);
  assert.equal(new Set(nodes.map(n => n[1])).size, model.records.length);
  for (const r of model.records)
    assert.ok(views['graph.mmd'].includes(`style n${sha256(key(r.project, r.id))} fill:${COLORS[r.status.state]}`));
  assert.match(views['index.md'], /&lt;untrusted&gt;/);
  assert.ok(!views['index.md'].includes('<untrusted>'));
  assert.ok(views['index.md'].includes('Premi%C3%A8re%20d%C3%A9cision.md'));
  for (const [, href] of views['index.md'].matchAll(/\]\(([^)]+)\)/g)) {
    const path = decodeURIComponent(href.split('#')[0]);
    assert.ok(existsSync(resolve(fixture, config.output, path)), href);
  }
  assert.equal(Object.keys(JSON.parse(views['ledger.json']).report.activeByScope).length, 2);
});

test('readonly stale check', t => {
  const base = workspace(t), { config, ledger } = data(), output = config.output;
  const views = projections(reconcile(base, config, ledger), config, base);
  const initial = snapshot(base);
  assert.throws(() => outputViews(base, output, views, 'check'), /stale projection/);
  assert.deepEqual(snapshot(base), initial);
  assert.ok(!existsSync(resolve(base, output)), 'check did not mkdir');
  outputViews(base, output, views, 'generate');
  outputViews(base, output, views, 'check');
  writeFileSync(resolve(base, output, 'index.md'), 'stale\n');
  const before = snapshot(base);
  assert.throws(() => outputViews(base, output, views, 'check'), /stale projection: index.md/);
  assert.deepEqual(snapshot(base), before);
  const result = cli(['check', '--root', base, '--config', 'project.json']);
  assert.equal(result.status, 1); assert.match(result.stderr, /stale projection/);
  assert.deepEqual(snapshot(base), before);
});

test('CLI explicit modes are readonly unless generate', t => {
  const base = workspace(t), before = snapshot(base);
  for (const mode of ['architecture', 'generate']) {
    const result = cli([mode, '--root', base, '--config', 'project.json', ...(mode === 'generate' ? ['--dry-run'] : [])]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, new RegExp(`${mode} PASS`));
  }
  assert.deepEqual(snapshot(base), before);
  const invalid = cli(['check', '--dry-run']);
  assert.equal(invalid.status, 1); assert.match(invalid.stderr, /only applies to generate/);
  assert.equal(cli(['unknown']).status, 1);
  assert.equal(cli(['architecture', '--root']).status, 1);
  const escaping = cli(['generate', '--root', base, '--config', 'project.json', '--dry-run', '--output', '../escape']);
  assert.equal(escaping.status, 1); assert.match(escaping.stderr, /unsafe output path/);
  assert.deepEqual(snapshot(base), before);
});

test('parser support boundaries fail closed', () => {
  const markdown = text => parseSource(Buffer.from(text), 'markdown-header-v1');
  assert.throws(() => markdown('# A\nStatus: proposed\nStatus: accepted\n'), /ambiguous duplicate metadata/);
  assert.throws(() => markdown('# A\n# B\n'), /exactly one H1/);
  assert.throws(() => markdown('# A\nStatus: \n'), /empty status/);
  assert.throws(() => markdown('---\nstatus: accepted\n---\n# A\n'), /adapter mismatch/);
  assert.throws(() => parseSource(Buffer.from('# A'), 'guessed-language'), /unsupported adapter/);
  assert.throws(() => parseSource(Buffer.from([0xff]), 'markdown-header-v1'), /encoded data/);
  const frontmatter = text => parseSource(Buffer.from(text), 'frontmatter-scalars-v1');
  assert.throws(() => frontmatter('---\nstatus:\n  - accepted\n---\n# A\n'), /unsupported frontmatter/);
  assert.throws(() => frontmatter('---\nstatus: Proposed\n---\n# A\nStatus: accepted\n'), /ambiguous status/);
  assert.equal(markdown('# A\nStatus: proposed\n## Context\n```\n# Fake\nStatus: accepted\n```\n').title, 'A');
  assert.equal(markdown('# A\nStatus: proposed\n## Context\n```sh\n```not-a-close\n# Fake\n```\n').title, 'A');
});

test('strict JSON source boundaries', () => {
  assert.throws(() => strictJSON('{"x":1,"x":2}'), /duplicate JSON key/);
  assert.throws(() => strictJSON('{"x":1,}'), /JSON object key/);
  assert.throws(() => strictJSON('[1] trailing'), /trailing JSON/);
  assert.throws(() => strictJSON('1e9999'), /out of range/);
  assert.throws(() => strictJSON('[\u00a01]'), /JSON value/);
  const { config } = data(), profile = config.projects.find(p => p.id === 'beta').jsonProfile;
  assert.throws(() => parseCollection(Buffer.from('{"decisions":[{"id":"x","title":"A","status":"Accepted","related":"y"}]}'), profile), /unsupported JSON relation/);
  assert.throws(() => parseCollection(Buffer.from('{"decisions":[{"id":"x","title":"A","status":"Accepted","acceptance":{"scope":"local","agent":"a","rejected":true}}]}'), profile), /unsupported JSON acceptance fields/);
});

test('JSON acceptance needs an explicit configured field', () => {
  const { config, ledger } = data();
  delete config.projects.find(p => p.id === 'beta').jsonProfile.acceptance;
  assert.throws(() => reconcile(fixture, config, ledger), /JSON acceptance profile binding/);
  delete ledger.records.find(r => r.id === 'accepted-choice').status.acceptance;
  const result = reconcile(fixture, config, ledger);
  assert.ok(!Object.hasOwn(report(result).activeByScope, 'workspace beta'));
  assert.ok(result.diagnostics.some(d => d.message === 'uninterpreted JSON metadata: acceptance'));
});

test('JSON collection local aliases cannot be ambiguous', t => {
  const base = workspace(t), { config, ledger } = data();
  const path = resolve(base, 'json project/registry/current decisions.json'), json = readJSON(path);
  json.decisions[1].id = json.decisions[0].id;
  writeFileSync(path, JSON.stringify(json));
  for (const r of ledger.records.filter(r => r.project === 'beta')) {
    const s = sourceFor(r, config, base);
    r.alias = s.alias; r.revision = s.revision;
  }
  assert.throws(() => reconcile(base, config, ledger), /ambiguous JSON local id/);
});

test('strict contract rejects unknown fields and adapters', () => {
  const { config, ledger } = data(); ledger.records[0].automaticAcceptance = true;
  assert.throws(() => validate(ledger, 'ledger'), /unknown field automaticAcceptance/);
  config.projects[0].adapter = 'auto-detect';
  assert.throws(() => validate(config, 'config'), /enum/);
  assert.throws(() => auditSchema({ allOf: [] }), /unsupported schema keyword/);
  assert.throws(() => auditSchema({ $ref: 'external-schema' }), /unsupported schema ref/);
  const mismatch = data(); mismatch.config.projects[0].canonical = 'json';
  assert.throws(() => reconcile(fixture, mismatch.config, mismatch.ledger), /canonical source \/ adapter mismatch/);
});

test('source and output path boundaries', t => {
  const base = workspace(t);
  assert.throws(() => localPath(base, '../project.json'), /unsafe path/);
  assert.throws(() => localPath(base, '/etc/passwd'), /unsafe path/);
  symlinkSync(fixture, resolve(base, 'outside'));
  assert.throws(() => localPath(base, 'outside/project.json'), /symlink escape/);
  assert.throws(() => outputViews(base, 'outside/view', { 'index.md': 'x' }, 'generate'), /output symlink escape/);
  assert.throws(() => outputViews(base, '../view', {}, 'generate'), /unsafe output path/);
  assert.throws(() => outputViews(base, 'safe', { '../escape': 'x' }, 'generate'), /unknown projection path/);
});

test('configured budgets fail closed', () => {
  const { config, ledger } = data(); config.limits.maxFiles = 1;
  assert.throws(() => reconcile(fixture, config, ledger), /file budget|record budget/);
  const bytes = data(); bytes.config.limits.maxBytes = 2;
  assert.throws(() => reconcile(fixture, bytes.config, bytes.ledger), /byte budget/);
  const edges = data(); edges.config.limits.maxEdges = 1;
  assert.throws(() => reconcile(fixture, edges.config, edges.ledger), /edge budget/);
});

test('projections cannot overwrite canonical inputs or enter discovery', () => {
  const config = readJSON(resolve(root, 'config/decisions/project.json'));
  const model = loadLedger(root, 'config/decisions/project.json');
  assert.throws(() => projectionBoundary(root, 'config/decisions', config, 'config/decisions/project.json', model), /overwrite canonical input/);
  assert.throws(() => projectionBoundary(root, 'docs/adr/preview', config, 'config/decisions/project.json', model), /intersects canonical discovery/);
  config.projects[0].exclude.push('docs/adr/preview');
  assert.doesNotThrow(() => projectionBoundary(root, 'docs/adr/preview', config, 'config/decisions/project.json', model));
});

test('specification and pseudocode checker negatives', () => {
  const contract = readJSON(resolve(root, 'config/decisions/requirements.json'));
  traceability(contract, readFileSync(fileURLToPath(import.meta.url), 'utf8'), path => readJSON(resolve(root, path)));
  assert.throws(() => traceability(contract, '', path => readJSON(resolve(root, path))), /missing test/);
  specification(contract); contract.requirements.pop();
  assert.throws(() => specification(contract), /seven unique obligations/);
  const examples = readJSON(resolve(root, 'config/decisions/examples.json'));
  pseudocode(examples); examples.cases[0].expected = 'unknown relation';
  assert.throws(() => pseudocode(examples), /ordinary reference/);
});

test('branch eligibility policy', () => {
  const policy = readJSON(resolve(root, 'config/decisions/requirements.json')).branchPolicy;
  const complete = Object.fromEntries(policy.required.map(k => [k, true]));
  assert.equal(branchEligibility(complete, policy), 'eligible-for-lead-recheck');
  for (const field of policy.required) {
    for (const unknown of [undefined, false, 'unknown', 'true'])
      assert.equal(branchEligibility({ ...complete, [field]: unknown }, policy), 'retain', field);
  }
  assert.match(policy.permissionReceipt, /readback false/);
  assert.equal(policy.executor, 'lead-only');
});
