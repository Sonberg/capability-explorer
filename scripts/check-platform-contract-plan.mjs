#!/usr/bin/env node
import { lstatSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const BOUNDARIES = ['enrollment', 'evidence-bundle', 'intake-receipt', 'alignment', 'product-api', 'viewpoint-projection', 'team-feedback'];
const PROFILES = ['enrollment', 'evidence-bundle', 'intake-receipt'];
const RUNTIME = ['centralIntake', 'authentication', 'artifactByteVerification', 'publicationStateMachine', 'productApis', 'viewpointAuthorization'];
const TERMS = ['Repository enrollment', 'Repository cluster', 'Business capability', 'Evidence bundle', 'Repository representation', 'Aligned finding', 'Data product revision', 'Viewpoint projection'];
const PHASES = ['specification', 'pseudocode', 'architecture', 'foundation'];
const text = value => typeof value === 'string' && value.trim().length > 0;

/** Planning integrity only; never a runtime, authentication or publication proof.
 * @param {any} plan @returns {string[]} */
export function validateCatalogue(plan) {
  const errors = [];
  const need = (ok, message) => { if (!ok) errors.push(message); };
  need(plan?.version === 1 && plan.issue === 33 && plan.scope === 'draft-envelope-shapes', 'draft scope missing');
  need(/^[0-9a-f]{40}$/.test(plan?.baseline ?? ''), 'baseline must be a full commit');
  need(plan?.wireFamily === 'ce.platform.v1' && plan.payloadFamily === 'ce.v1', 'wire/payload namespaces must remain distinct');
  need(plan?.schema === 'schema/platform/v1/envelope.schema.json', 'unexpected schema path');
  need(plan?.profileStatus === 'owner-review-draft' && plan.formatAssertionsRequired === true, 'draft/format assertion policy missing');
  need(RUNTIME.every(key => plan?.implemented?.[key] === false)
    && Object.keys(plan?.implemented ?? {}).length === RUNTIME.length, 'draft cannot claim implemented runtime capabilities');
  const c = plan?.canonicalization;
  need(c?.status === 'proposed-not-implemented' && c.scheme === 'RFC8785-JCS'
    && c.encoding === 'UTF-8' && c.hash === 'SHA-256' && c.subject === 'evidence-bundle.content'
    && JSON.stringify(c.excluded) === JSON.stringify(['publicationAttempt', 'contentSha256'])
    && c.reference === 'https://www.rfc-editor.org/rfc/rfc8785', 'digest boundary or proposed canonicalization changed without review');
  const boundaries = Array.isArray(plan?.boundaries) ? plan.boundaries : [];
  need(boundaries.length === 7 && new Set(boundaries.map(x => x.id)).size === 7, 'seven unique boundaries required');
  for (const id of BOUNDARIES) {
    const b = boundaries.find(x => x.id === id);
    need(b && text(b.from) && text(b.to) && text(b.purpose), `boundary ${id} missing/incomplete`);
    need(b?.status === (PROFILES.includes(id) ? 'draft-shape' : 'design-only'), `boundary ${id} cannot claim runtime delivery`);
    need(PROFILES.includes(id) ? b?.profileKind === id : b?.profileKind === undefined, `boundary ${id} profile assignment invalid`);
  }
  const terms = plan?.terminology;
  need(terms?.existingCapabilityRole === 'repository-selection'
    && JSON.stringify(terms.notEquivalentTo) === JSON.stringify(['repository-cluster', 'business-capability']), 'Capability meaning must remain repository selection');
  need(Array.isArray(terms?.draftTerms) && terms.draftTerms.length === TERMS.length
    && new Set(terms.draftTerms).size === TERMS.length && TERMS.every(term => terms.draftTerms.includes(term)), 'draft terminology incomplete');
  const acceptance = Array.isArray(plan?.acceptance) ? plan.acceptance : [];
  need(acceptance.length === 6 && new Set(acceptance.map(x => x.id)).size === 6, 'six unique acceptance criteria required');
  for (let i = 1; i <= 6; i++) {
    const a = acceptance.find(x => x.id === `C${i}`);
    need(a && ['dotnet-shape-tests', 'node-plan-tests', 'ordinary-local-ci'].includes(a.instrument)
      && text(a.positive) && text(a.negative), `C${i} positive/negative trace missing`);
  }
  const future = plan?.futureSemanticChecks;
  need(Array.isArray(future) && ['enrollment-authorization', 'artifact-byte-and-digest-verification',
    'atomic-last-good-publication', 'current-and-derived-source-access'].every(x => future.includes(x)), 'future semantic obligations missing');
  return errors;
}

/** Read only known tracked planning inputs, never a path supplied by analyzed source.
 * @param {string} root @param {string} path @returns {any} */
function json(root, path) {
  const file = join(root, path), stat = lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 65536) throw new Error('planning input must be a regular file <=64KiB');
  return JSON.parse(readFileSync(file, 'utf8'));
}

/** @param {string[]} argv @param {string} root @returns {string} */
export function run(argv, root) {
  if (argv.length !== 1 || !PHASES.includes(argv[0])) throw new Error('usage: check-platform-contract-plan.mjs specification|pseudocode|architecture|foundation; runtime completion is not implemented');
  const plan = json(root, 'config/platform/exchange-contracts.json');
  const errors = validateCatalogue(plan);
  const schema = json(root, 'schema/platform/v1/envelope.schema.json');
  if (schema.$schema !== 'https://json-schema.org/draft/2020-12/schema'
    || schema.$id !== 'https://capability-explorer/schema/platform/v1/envelope.schema.json') errors.push('schema identity/dialect mismatch');
  for (const [name, kind] of Object.entries({ enrollment: 'enrollment', evidenceBundle: 'evidence-bundle', intakeReceipt: 'intake-receipt' })) {
    const profile = schema.$defs?.[name];
    if (profile?.additionalProperties !== false || profile.properties?.schema?.const !== 'ce.platform.v1'
      || profile.properties?.kind?.const !== kind) errors.push(`schema profile ${name} mismatch`);
  }
  if (errors.length) throw new Error(errors.join('\n'));
  return `${argv[0]}: seven boundaries/three draft profiles checked; no byte, authorization, runtime or full-study claim`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { process.stdout.write(run(process.argv.slice(2), fileURLToPath(new URL('../', import.meta.url))) + '\n'); }
  catch (error) { process.stderr.write(error.message + '\n'); process.exitCode = 1; }
}
