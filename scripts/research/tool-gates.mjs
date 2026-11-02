import { canonical, commit, list, need, parseJson, readEvidence, sha256, text, verifySealedCorpus } from './evidence.mjs';

const roles = ['sme', 'adversary', 'advisor', 'judge'];
const stages = ['desk', 'trial', 'decision', 'pr'];

/** Bind the complete decision packet, excluding only its own digest field.
 * @param {any} d @returns {string} */
export function councilPacketHash(d) {
  return sha256(canonical({ proposal: d.proposal, references: d.references, authors: d.authors,
    blockers: d.blockers, deskDecision: d.deskDecision,
    reviews: d.reviews.map(({ role, agent, subjectHash, references, brief }) =>
      ({ role, agent, subjectHash, references, brief })) }));
}

/** Data integrity gate, not a signature verifier or opinion/truth oracle.
 * @param {any} dossier @param {string} stage @param {string} root
 * @returns {{id:string, stage:string, verdict:string}} */
export function validateTool(dossier, stage, root) {
  need(stages.includes(stage), 'unknown tool stage');
  const d = dossier, p = d?.proposal;
  need(d?.schemaVersion === 1 && p, 'unsupported tool dossier');
  need(/^[a-z][a-z0-9-]{0,63}$/.test(p.id ?? ''), 'invalid tool id');
  for (const key of ['mode', 'role', 'gap', 'executionMethod', 'epistemicStatus']) need(text(p[key]), `missing ${key}`);
  need(['observed', 'derived', 'candidate', 'unresolved', 'reviewed'].includes(p.epistemicStatus), 'mixed evidence status');
  need(commit(p.baseline) && commit(p.sourceCommit), 'unbound baseline/source');
  need(list(p.criteria, 'criteria', 3).every(text), 'invalid role-specific criteria');
  const required = list(p.requiredCases, 'required cases', 3);
  need(required.every(text) && new Set(required).size === required.length, 'duplicate/invalid cases');
  need(['paidCalls', 'minutes', 'downloadGiB', 'outputGiB', 'reserveGiB'].every(key => Number.isFinite(p.budget?.[key]))
    && p.budget?.paidCalls === 0 && p.budget?.minutes <= 20 && p.budget?.minutes > 0
    && p.budget?.downloadGiB <= 2 && p.budget?.downloadGiB > 0 && p.budget?.outputGiB <= 1
    && p.budget?.outputGiB > 0 && p.budget?.reserveGiB >= 10, 'invalid tool budget');
  readEvidence(root, p.corpus); readEvidence(root, p.oracle);
  const subject = sha256(canonical(p));
  const refs = list(d.references, 'claim references');
  const ids = new Set();
  for (const ref of refs) {
    need(text(ref.id) && !ids.has(ref.id), 'duplicate/invalid reference'); ids.add(ref.id);
    need(text(ref.claim) && ['primary-source', 'primary-doc', 'measured', 'secondary', 'unknown'].includes(ref.kind), 'invalid claim class');
    need(['inspected', 'claim', 'unavailable'].includes(ref.status), 'invalid reference status');
    let url; try { url = new URL(ref.url); } catch { throw new Error('invalid reference URL'); }
    need(url.protocol === 'https:' && !url.username && !url.password, 'unsafe reference URL');
    need(!(ref.status === 'unavailable' && ref.kind === 'measured'), 'inaccessible source asserted tested');
  }
  const reviews = list(d.reviews, 'council reviews', 4);
  need(reviews.length === 4 && new Set(reviews.map(r => r.role)).size === 4, 'council roles missing/duplicated');
  need(new Set(reviews.map(r => r.agent)).size === 4, 'council agent conflict');
  for (const review of reviews) {
    need(roles.includes(review.role) && text(review.agent), 'invalid council role/identity');
    need(review.subjectHash === subject, 'stale council subject');
    need(list(review.references, 'review references').every(id => ids.has(id)), 'unknown review reference');
    need(readEvidence(root, review.brief).length > 0, 'empty council brief');
  }
  const judge = reviews.find(r => r.role === 'judge');
  need(!list(d.authors, 'authors', 0).includes(judge.agent), 'author cannot judge');
  const verdict = d.deskDecision;
  need(['retain', 'reject', 'defer', 'approve-trial'].includes(verdict), 'invalid desk verdict');
  const blockers = list(d.blockers, 'blockers', 0);
  const blockerIds = new Set();
  for (const b of blockers) {
    need(text(b.id) && !blockerIds.has(b.id) && text(b.reason), 'invalid/duplicate blocker'); blockerIds.add(b.id);
    need(['open', 'resolved'].includes(b.status), 'invalid blocker status');
    if (b.status === 'resolved') readEvidence(root, b.resolution);
  }
  const packetHash = councilPacketHash(d);
  need(judge.packetHash === packetHash, 'stale judge council packet');
  if (verdict === 'approve-trial') need(blockers.every(b => b.status === 'resolved'), 'unresolved council blocker');
  if (stage === 'desk') return { id: p.id, stage, verdict };
  need(verdict === 'approve-trial', 'defer/reject/retain is not a trial pass');
  const corpus = parseJson(readEvidence(root, p.corpus));
  verifySealedCorpus(root, corpus);
  need(corpus.baseline === p.baseline && canonical(corpus.oracle) === canonical(p.oracle), 'wrong sealed corpus/oracle identity');
  const t = d.trial;
  need(t && t.subjectHash === subject, 'missing/stale trial');
  const receipt = parseJson(readEvidence(root, t.receipt));
  need(receipt.schemaVersion === 1 && receipt.subjectHash === subject && receipt.exitCode === 0, 'invalid trial receipt');
  need(receipt.baseline === p.baseline && receipt.sourceCommit === p.sourceCommit
    && receipt.mode === p.mode && receipt.corpusHash === p.corpus.sha256
    && receipt.oracleHash === p.oracle.sha256, 'wrong trial identity');
  need(text(receipt.platform) && text(receipt.sourceToArtifact) && text(receipt.command), 'artifact provenance incomplete');
  // Commands are labels only; the gate never runs receipt-supplied commands.
  for (const key of ['artifact', 'configuration', 'dependencyClosure', 'environment']) readEvidence(root, receipt[key]);
  need(canonical(receipt.budget) === canonical(p.budget), 'changed authorization envelope');
  const cases = list(receipt.cases, 'executed cases', 3);
  need(cases.length === required.length && new Set(cases.map(c => c.id)).size === cases.length, 'missing/duplicate test cases');
  for (const c of cases) {
    need(required.includes(c.id) && c.status === 'pass' && c.skipped === false, 'failed/skipped/unknown test case');
    readEvidence(root, c.evidence);
  }
  need(receipt.safety?.sourceUnchanged === true && receipt.safety?.egressDenied === true
    && receipt.safety?.targetExecutionDenied === true, 'missing safety observations');
  const runs = list(receipt.runs, 'repeatability runs', 2);
  need(new Set(runs.map(r => r.processId)).size === runs.length, 'repeatability needs distinct processes');
  let factsHash;
  for (const run of runs) {
    need(text(run.processId) && text(run.conditions), 'missing run conditions');
    const facts = parseJson(readEvidence(root, run.facts));
    readEvidence(root, run.raw);
    const hash = sha256(canonical(facts));
    need(!factsHash || factsHash === hash, 'nonrepeatable canonical facts'); factsHash = hash;
  }
  if (stage === 'trial') return { id: p.id, stage, verdict: 'trial-validated' };
  const decision = d.introductionDecision;
  need(decision?.verdict === 'approve-introduction-proposal' && decision.agent === judge.agent
    && decision.subjectHash === subject && decision.packetHash === packetHash
    && decision.receiptHash === t.receipt.sha256, 'missing/stale independent introduction verdict');
  readEvidence(root, decision.brief);
  need(decision.hardGates?.license === true && decision.hardGates?.provenance === true
    && decision.hardGates?.safety === true && decision.hardGates?.value === true, 'introduction hard gate failed');
  if (stage === 'decision') return { id: p.id, stage, verdict: decision.verdict };
  const pr = d.pr;
  need(pr?.base === 'main' && commit(pr.head) && pr.foundationMerged === true, 'wrong PR base/head or unmerged foundation');
  need(Array.isArray(pr.tools) && pr.tools.length === 1 && pr.tools[0] === p.id
    && Array.isArray(pr.modes) && pr.modes.length === 1 && pr.modes[0] === p.mode, 'bundled introduction');
  need(pr.optIn === true && text(pr.rollback), 'missing opt-in/rollback');
  const ci = readEvidence(root, pr.localCi).toString('utf8');
  const lines = ci.trim().split('\n');
  for (const [key, value] of Object.entries({ head: pr.head, target: 'main', base: p.baseline })) {
    const matches = lines.filter(line => line.startsWith(`${key}=`));
    need(matches.length === 1 && matches[0] === `${key}=${value}`, 'stale/wrong local CI identity');
  }
  const check = readEvidence(root, pr.prCheck).toString('utf8').trim();
  need(check === `pr-check: verified HEAD ${pr.head} against main ${p.baseline}`, 'stale/wrong PR-check identity');
  // Repository scripts remain the authority for actual CI/head/base checks.
  return { id: p.id, stage, verdict: 'proposal-record-valid; repository pr-check still required' };
}
