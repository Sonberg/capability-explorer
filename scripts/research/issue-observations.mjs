import { canonical, commit, list, need, sha256 } from './evidence.mjs';

/**
 * Offline caller declarations only: hashes establish integrity, not GitHub
 * authenticity, exhaustive live capture, approval, satisfaction or completion.
 * No criteria are parsed or certified. All dates use explicit caller time.
 *
 * @typedef {'observed'|'absent'|'denied'|'unavailable'|'unknown'|'unsupported'|'stale'|'unstable'} ObservationState
 * @typedef {{repoID:string, owner:string, name:string, sourceCommit:string, sourceInputSha256:string}} Context
 * Context identifies declared source inputs; it conveys no admission or authority.
 * @typedef {{nodeID:string, number:number, state:'open'|'closed', title:string, body:string, updatedAt:string}} Issue
 * @typedef {{number:number, cursor:string|null, nextCursor:string|null, status:ObservationState, issues:Issue[]}} Page
 * @typedef {{maxPages:number, maxIssues:number, maxUtf8Bytes:number, maxAgeMs:number, maxDurationMs:number}} Bounds
 * @typedef {{startedAt:string, endedAt:string, status:ObservationState, terminal:ObservationState, truncated:boolean}} Capture
 * @typedef {{context:Context, provenance:'declared'|'synthetic', scope:{state:'all'|'open'|'closed', filter:string},
 *   bounds:Bounds, capture:Capture, pages:Page[]}} Declaration
 * @typedef {{nodeID:string, number:number}} IssueRef
 * @typedef {{nodeID:string, number:number, sha256:string}} BodyDigest
 * @typedef {{schemaVersion:1, declaration:Declaration, bodyDigests:BodyDigest[], digest:string, approval:'not-established'}} Snapshot
 * @typedef {{compatible:boolean, beforeDigest:string, afterDigest:string, uncertainty:string[],
 *   new:IssueRef[], missingFromObservedScope:IssueRef[], closed:IssueRef[], reopened:IssueRef[],
 *   bodyChanged:(IssueRef & {beforeSha256:string, afterSha256:string})[], metadataChanged:IssueRef[],
 *   approval:'not-established'}} Comparison
 */

const states = ['observed', 'absent', 'denied', 'unavailable', 'unknown', 'unsupported', 'stale', 'unstable'];
/** @param {unknown} value @returns {boolean} */
const hash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
/** @param {number} value @param {number} max @returns {boolean} */
const integer = (value, max) => Number.isSafeInteger(value) && value > 0 && value <= max;

/** JSON objects have an exact, small field set; unknown authority fields are not carried forward.
 * @param {any} value @param {string[]} keys @param {string} label */
function shape(value, keys, label) {
  need(value && typeof value === 'object' && !Array.isArray(value)
    && [Object.prototype, null].includes(Object.getPrototypeOf(value)), `invalid ${label}`);
  let count = 0;
  for (const key in value) {
    need(keys.includes(key) && Object.hasOwn(value, key), `invalid ${label} field`);
    count++;
  }
  need(count === keys.length, `invalid ${label} fields`);
}

/** Require canonical UTC instants, without normalizing any source text.
 * @param {unknown} value @returns {number} */
function date(value) {
  need(typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(value), 'invalid capture date');
  const ms = Date.parse(value);
  need(Number.isFinite(ms) && new Date(ms).toISOString() === value, 'invalid capture date');
  return ms;
}

/** @template T @param {T} value @returns {T} */
function immutable(value) {
  if (value && typeof value === 'object') {
    for (const child of Object.values(value)) immutable(child);
    Object.freeze(value);
  }
  return value;
}

/** Freeze a bounded, detached declaration. Only issue-record ordering is normalized;
 * pages retain their ordered contiguous cursor chain. maxUtf8Bytes bounds the sum
 * of declared string values (not JSON syntax). Foundation ceilings: 256 pages,
 * 256 total issues, 8MiB strings, seven-day age and one-day duration bounds.
 * An observed terminal is a declaration, not proof of exhaustive live capture.
 * Invalid input throws explicit validation errors; incomplete input stays uncertain.
 * @param {Declaration} input @param {string} now @returns {Snapshot} */
export function freezeIssueObservation(input, now) {
  const nowMs = date(now);
  shape(input, ['context', 'provenance', 'scope', 'bounds', 'capture', 'pages'], 'issue declaration');
  const b = input.bounds;
  shape(b, ['maxPages', 'maxIssues', 'maxUtf8Bytes', 'maxAgeMs', 'maxDurationMs'], 'observation bounds');
  for (const [key, ceiling] of [['maxPages', 256], ['maxIssues', 256], ['maxUtf8Bytes', 8 * 1024 * 1024],
    ['maxAgeMs', 7 * 86400000], ['maxDurationMs', 86400000]]) {
    need(integer(b[key], ceiling), `invalid ${key} budget`);
  }
  let bytes = 0;
  /** @param {unknown} value @param {string} label @param {boolean} [empty] @returns {string} */
  function string(value, label, empty = false) {
    need(typeof value === 'string', `invalid ${label}`);
    need(value.length <= b.maxUtf8Bytes - bytes, 'exceeded UTF8 byte budget');
    need(empty || value.trim().length > 0, `invalid ${label}`);
    bytes += Buffer.byteLength(value, 'utf8');
    need(bytes <= b.maxUtf8Bytes, 'exceeded UTF8 byte budget');
    for (const point of value) {
      const code = point.charCodeAt(0);
      need(!(point.length === 1 && code >= 0xd800 && code <= 0xdfff), `ill-formed Unicode in ${label}`);
    }
    return value;
  }
  /** @param {unknown} value @param {string} label @returns {ObservationState} */
  function state(value, label) {
    need(typeof value === 'string' && states.includes(value), `invalid ${label}`);
    return /** @type {ObservationState} */ (string(value, label));
  }
  const c = input.context;
  shape(c, ['repoID', 'owner', 'name', 'sourceCommit', 'sourceInputSha256'], 'observation context');
  const context = { repoID: string(c.repoID, 'repoID'), owner: string(c.owner, 'owner'), name: string(c.name, 'name'),
    sourceCommit: string(c.sourceCommit, 'source commit'), sourceInputSha256: string(c.sourceInputSha256, 'source input digest') };
  need(commit(context.sourceCommit) && hash(context.sourceInputSha256), 'invalid source input identity');
  need(['declared', 'synthetic'].includes(input.provenance), 'invalid declared capture provenance');
  const provenance = string(input.provenance, 'provenance');
  shape(input.scope, ['state', 'filter'], 'enumeration scope');
  need(['all', 'open', 'closed'].includes(input.scope.state), 'invalid enumeration state filter');
  const scope = { state: string(input.scope.state, 'scope state'), filter: string(input.scope.filter, 'scope filter', true) };
  const cap = input.capture;
  shape(cap, ['startedAt', 'endedAt', 'status', 'terminal', 'truncated'], 'capture');
  const capture = { startedAt: string(cap.startedAt, 'start date'), endedAt: string(cap.endedAt, 'end date'),
    status: state(cap.status, 'capture state'), terminal: state(cap.terminal, 'terminal state'), truncated: cap.truncated };
  const started = date(capture.startedAt), ended = date(capture.endedAt);
  need(started <= ended && ended <= nowMs, 'invalid capture interval');
  need(typeof capture.truncated === 'boolean', 'invalid truncated declaration');
  const rawPages = list(input.pages, 'observation pages', 0);
  need(rawPages.length <= b.maxPages, 'exceeded page budget');
  const nodes = new Set(), numbers = new Set(), cursors = new Set();
  let issueCount = 0, expectedCursor = null;
  const pages = rawPages.map((p, index) => {
    shape(p, ['number', 'cursor', 'nextCursor', 'status', 'issues'], 'observation page');
    need(p.number === index + 1 && p.cursor === expectedCursor
      && (index === 0 || p.cursor !== null), 'invalid page chain');
    const cursor = p.cursor === null ? null : string(p.cursor, 'page cursor');
    need(!cursors.has(cursor), 'duplicate page cursor'); cursors.add(cursor);
    const nextCursor = p.nextCursor === null ? null : string(p.nextCursor, 'next cursor');
    need(nextCursor === null || !cursors.has(nextCursor), 'duplicate or looping page cursor');
    expectedCursor = nextCursor;
    const status = state(p.status, 'page state');
    const rawIssues = list(p.issues, 'observed issues', 0);
    issueCount += rawIssues.length;
    need(issueCount <= b.maxIssues, 'exceeded issue budget');
    const issues = rawIssues.map(i => {
      shape(i, ['nodeID', 'number', 'state', 'title', 'body', 'updatedAt'], 'issue record');
      const nodeID = string(i.nodeID, 'issue nodeID');
      need(integer(i.number, Number.MAX_SAFE_INTEGER), 'invalid issue number');
      need(!nodes.has(nodeID), 'duplicate issue nodeID'); nodes.add(nodeID);
      need(!numbers.has(i.number), 'duplicate issue number'); numbers.add(i.number);
      need(['open', 'closed'].includes(i.state), 'invalid issue state');
      need(scope.state === 'all' || scope.state === i.state, 'issue outside declared state filter');
      const issue = { nodeID, number: i.number, state: string(i.state, 'issue state'),
        title: string(i.title, 'issue title', true), body: string(i.body, 'issue body', true),
        updatedAt: string(i.updatedAt, 'issue update date') };
      need(date(issue.updatedAt) <= ended, 'invalid issue update interval');
      return issue;
    }).sort((a, z) => a.number - z.number);
    return { number: p.number, cursor, nextCursor, status, issues };
  });
  if (capture.terminal === 'observed') {
    need(pages.length > 0 && pages.at(-1).nextCursor === null
      && pages.at(-1).status === 'observed', 'invalid observed terminal');
  }
  need(capture.status !== 'observed' || pages.length > 0, 'observed capture requires a page');
  const declaration = /** @type {Declaration} */ ({ context, provenance, scope, bounds: { ...b }, capture, pages });
  const bodyDigests = pages.flatMap(p => p.issues).sort((a, z) => a.number - z.number)
    .map(i => ({ nodeID: i.nodeID, number: i.number, sha256: sha256(i.body) }));
  const payload = { schemaVersion: /** @type {const} */ (1), declaration, bodyDigests,
    approval: /** @type {const} */ ('not-established') };
  return immutable({ ...payload, digest: sha256(canonical(payload)) });
}

/** Revalidate all declaration bounds and derived hashes; supplied hashes are not trusted.
 * @param {Snapshot} snapshot @param {string} now @returns {Snapshot} */
function revalidate(snapshot, now) {
  shape(snapshot, ['schemaVersion', 'declaration', 'bodyDigests', 'approval', 'digest'], 'observation snapshot');
  need(snapshot.schemaVersion === 1 && snapshot.approval === 'not-established' && hash(snapshot.digest),
    'invalid snapshot integrity fields');
  const fresh = freezeIssueObservation(snapshot.declaration, now);
  const digests = list(snapshot.bodyDigests, 'body digests', 0);
  need(digests.length === fresh.bodyDigests.length && snapshot.digest === fresh.digest, 'snapshot integrity mismatch');
  for (let index = 0; index < digests.length; index++) {
    const supplied = digests[index], expected = fresh.bodyDigests[index];
    shape(supplied, ['nodeID', 'number', 'sha256'], 'body digest');
    need(supplied.nodeID === expected.nodeID && supplied.number === expected.number
      && supplied.sha256 === expected.sha256, 'snapshot integrity body digest mismatch');
  }
  return fresh;
}

/** @param {Snapshot} snapshot @param {string} now @returns {string[]} */
function uncertainty(snapshot, now) {
  const { capture: c, bounds: b, pages } = snapshot.declaration, reasons = [];
  if (c.status !== 'observed') reasons.push(`capture:${c.status}`);
  if (c.terminal !== 'observed') reasons.push(`terminal:${c.terminal}`);
  if (c.truncated) reasons.push('truncated');
  if (pages.length === 0 || pages.at(-1).nextCursor !== null) reasons.push('nonterminal');
  for (const p of pages) if (p.status !== 'observed') reasons.push(`page:${p.number}:${p.status}`);
  if (date(now) - date(c.endedAt) > b.maxAgeMs) reasons.push('stale');
  if (date(c.endedAt) - date(c.startedAt) > b.maxDurationMs) reasons.push('capture-duration');
  return reasons;
}

/** Compare compatible declared scopes, not completion. "Missing" means only absent
 * from the declared observed scope, never deletion or satisfaction. Membership
 * changes require both enumerations to have no uncertainty. Common observed
 * records still report independent changes when enumeration is uncertain.
 * Compatibility requires the same declared source inputs, bounds and provenance;
 * it does not establish completeness, authenticity or permission.
 * Metadata here is literal title and updatedAt; state and body have separate axes.
 * @param {Snapshot} before @param {Snapshot} after @param {string} now @returns {Comparison} */
export function compareIssueObservations(before, after, now) {
  const a = revalidate(before, now), b = revalidate(after, now);
  const left = a.declaration, right = b.declaration;
  need(date(left.capture.endedAt) <= date(right.capture.endedAt), 'invalid comparison capture interval');
  const sameRepository = ['repoID', 'owner', 'name'].every(k => left.context[k] === right.context[k]);
  const sameScope = canonical(left.scope) === canonical(right.scope);
  const sameSource = ['sourceCommit', 'sourceInputSha256'].every(k => left.context[k] === right.context[k]);
  const sameBounds = canonical(left.bounds) === canonical(right.bounds);
  const sameProvenance = left.provenance === right.provenance;
  const reasons = [...uncertainty(a, now).map(r => `before:${r}`), ...uncertainty(b, now).map(r => `after:${r}`)];
  if (!sameRepository) reasons.push('incompatible-repository');
  if (!sameScope) reasons.push('incompatible-scope');
  if (!sameSource) reasons.push('incompatible-source-inputs');
  if (!sameBounds) reasons.push('incompatible-bounds');
  if (!sameProvenance) reasons.push('incompatible-provenance');
  const oldIssues = left.pages.filter(p => p.status === 'observed').flatMap(p => p.issues).sort((x, z) => x.number - z.number);
  const newIssues = right.pages.filter(p => p.status === 'observed').flatMap(p => p.issues).sort((x, z) => x.number - z.number);
  const oldNumbers = new Map(oldIssues.map(i => [i.number, i]));
  const oldNodes = new Map(oldIssues.map(i => [i.nodeID, i]));
  if (sameRepository) for (const i of newIssues) {
    need((!oldNumbers.has(i.number) || oldNumbers.get(i.number).nodeID === i.nodeID)
      && (!oldNodes.has(i.nodeID) || oldNodes.get(i.nodeID).number === i.number), 'changed issue identity mapping');
  }
  /** @type {Comparison} */
  const result = { compatible: sameRepository && sameScope && sameSource && sameBounds && sameProvenance,
    beforeDigest: a.digest, afterDigest: b.digest,
    uncertainty: reasons, new: [], missingFromObservedScope: [], closed: [], reopened: [],
    bodyChanged: [], metadataChanged: [], approval: 'not-established' };
  if (!result.compatible) return immutable(result);
  const newNodes = new Set(newIssues.map(i => i.nodeID));
  /** @param {Issue} i @returns {IssueRef} */
  const ref = i => ({ nodeID: i.nodeID, number: i.number });
  for (const issue of newIssues) {
    const previous = oldNodes.get(issue.nodeID);
    if (!previous) { if (!reasons.length) result.new.push(ref(issue)); continue; }
    if (previous.state !== issue.state) result[issue.state === 'closed' ? 'closed' : 'reopened'].push(ref(issue));
    if (previous.body !== issue.body) result.bodyChanged.push({ ...ref(issue),
      beforeSha256: sha256(previous.body), afterSha256: sha256(issue.body) });
    if (previous.title !== issue.title || previous.updatedAt !== issue.updatedAt) result.metadataChanged.push(ref(issue));
  }
  if (!reasons.length) result.missingFromObservedScope = oldIssues.filter(i => !newNodes.has(i.nodeID)).map(ref);
  return immutable(result);
}
