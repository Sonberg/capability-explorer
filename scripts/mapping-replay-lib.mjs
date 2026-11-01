import { createHash } from 'node:crypto';
import { lstatSync, readdirSync, readFileSync, realpathSync } from 'node:fs';
import { isAbsolute, relative, resolve } from 'node:path';

export const hash = (value) => createHash('sha256').update(value).digest('hex');
export const canonical = (value) => JSON.stringify(value, (_key, v) =>
  v && typeof v === 'object' && !Array.isArray(v) ? Object.fromEntries(Object.entries(v).sort(([a], [b]) => a.localeCompare(b))) : v);
export function requireThat(ok, message) { if (!ok) throw new Error(message); }

/** All paths stay inside the checkout; symlinks cannot redirect an input outside it. */
export function inside(root, name) {
  requireThat(typeof name === 'string' && name.length > 0 && !isAbsolute(name), `relative path required: ${name}`);
  const path = resolve(root, name);
  requireThat(path.startsWith(resolve(root) + '/'), `path escapes root: ${name}`);
  let cursor = path;
  while (cursor !== resolve(root)) {
    try { requireThat(!lstatSync(cursor).isSymbolicLink(), `symlink forbidden: ${name}`); }
    catch (e) { if (e.code !== 'ENOENT') throw e; }
    cursor = resolve(cursor, '..');
  }
  return path;
}

export function inventory(directory, excluded = new Set()) {
  const files = {};
  function visit(path) {
    for (const entry of readdirSync(path, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const child = resolve(path, entry.name), name = relative(directory, child).replaceAll('\\', '/');
      requireThat(!entry.isSymbolicLink(), `symlink forbidden: ${name}`);
      if (excluded.has(name)) continue;
      if (entry.isDirectory()) visit(child);
      else { requireThat(entry.isFile(), `not a regular file: ${name}`); files[name] = hash(readFileSync(child)); }
    }
  }
  visit(realpathSync(directory));
  return files;
}

export function verifyLockedFiles(directory, locked) {
  requireThat(locked && Object.keys(locked).length > 0, 'empty locked file inventory');
  requireThat(canonical(inventory(directory)) === canonical(locked), `locked inputs differ: ${directory}`);
}

export function snippet(text, span) {
  const { startLine: a, endLine: b, startColumn: c, endColumn: d } = span;
  const lines = text.replaceAll('\r\n', '\n').split('\n');
  requireThat([a, b, c, d].every((v) => Number.isInteger(v) && v >= 1)
    && b >= a && b <= lines.length && c <= lines[a - 1].length + 1 && d <= lines[b - 1].length + 1
    && (a !== b || d >= c), 'evidence span lies outside source');
  return a === b ? lines[a - 1].slice(c - 1, d - 1)
    : [lines[a - 1].slice(c - 1), ...lines.slice(a, b - 1), lines[b - 1].slice(0, d - 1)].join('\n');
}

export function verifyEvidence(records, source, manifest) {
  const header = records[0];
  requireThat(header.repositoryId === manifest.repositoryId && header.commitSha === manifest.commitSha, 'artifact source identity mismatch');
  let count = 0;
  for (const ev of records.filter((r) => r.kind === 'evidence')) {
    requireThat(ev.repositoryId === header.repositoryId && ev.commitSha === header.commitSha, 'evidence source identity mismatch');
    requireThat(Object.hasOwn(manifest.files, ev.path), `evidence path is not locked: ${ev.path}`);
    const text = readFileSync(inside(source, ev.path), 'utf8');
    requireThat(hash(snippet(text, ev.span)) === ev.snippetSha256, `evidence snippet mismatch: ${ev.path}`);
    count++;
  }
  requireThat(count > 0, 'artifact has no source evidence');
  return count;
}

/** Selectors are exact recursive subsets; no executable predicates or regular expressions. */
export function matches(value, selector) {
  if (!selector || typeof selector !== 'object') return value === selector;
  if (Array.isArray(selector)) return canonical(value) === canonical(selector);
  return value && typeof value === 'object' && Object.entries(selector).every(([key, child]) => matches(value[key], child));
}

export function semanticRecord(record) {
  const { id: _recordId, evidence: _recordEvidenceReferences, ...semantic } = record;
  return semantic;
}

export function checkAssertions(before, after, assertions, expectedChanges = { removed: [], added: [] }) {
  requireThat(Array.isArray(assertions) && assertions.length > 0, 'nonzero named assertions required');
  requireThat(assertions.some((a) => a.baseline + a.candidate > 0), 'assertions must observe nonzero records');
  const names = new Set();
  const results = assertions.map((a) => {
    requireThat(typeof a.name === 'string' && a.name.length > 0 && !names.has(a.name), 'assertion names must be nonempty and unique');
    names.add(a.name);
    requireThat(a.selector && ['node', 'edge', 'diagnostic', 'calculation'].includes(a.selector.kind)
      && Object.keys(a.selector).length >= 2, `bounded record selector required: ${a.name}`);
    requireThat(Number.isInteger(a.baseline) && a.baseline >= 0 && Number.isInteger(a.candidate) && a.candidate >= 0,
      `exact nonnegative assertion counts required: ${a.name}`);
    const baseline = before.filter((r) => matches(r, a.selector)).length;
    const candidate = after.filter((r) => matches(r, a.selector)).length;
    requireThat(baseline === a.baseline && candidate === a.candidate,
      `assertion ${a.name}: expected ${a.baseline}/${a.candidate}, got ${baseline}/${candidate}`);
    return { name: a.name, baseline, candidate };
  });
  // Compare semantic records independently of build-local record/evidence identifiers. Source evidence is
  // validated separately, and repeats compare raw bytes including every identifier and evidence record.
  const included = (r) => ['node', 'edge', 'calculation', 'diagnostic', 'complete'].includes(r.kind);
  const first = before.filter(included).map(semanticRecord), second = after.filter(included).map(semanticRecord);
  const difference = (left, right) => {
    const remaining = new Map();
    for (const record of right) {
      const key = canonical(record); remaining.set(key, (remaining.get(key) ?? 0) + 1);
    }
    return left.filter((record) => {
      const key = canonical(record), count = remaining.get(key) ?? 0;
      if (!count) return true;
      remaining.set(key, count - 1); return false;
    });
  };
  const removed = difference(first, second), added = difference(second, first), changed = [...removed, ...added];
  for (const record of changed) {
    // Counts can change only as a consequence of explicitly accounted records; coverage must stay equal.
    if (record.kind === 'complete') continue;
    requireThat(assertions.some((a) => matches(record, a.selector)),
      `unexplained semantic change: ${canonical(record)}`);
  }
  const semanticChanges = (records) => records.filter((r) => r.kind !== 'complete').map(canonical).sort();
  requireThat(canonical(semanticChanges(removed)) === canonical(semanticChanges(expectedChanges.removed))
    && canonical(semanticChanges(added)) === canonical(semanticChanges(expectedChanges.added)), 'exact semantic change inventory differs from locked expectations');
  const coverage = (records) => records.filter((r) => r.kind === 'complete').map((r) => {
    const { unresolvedEdges: _explainedByEdgeRecords, ...files } = r.coverage;
    return { status: r.status, coverage: files };
  });
  requireThat(canonical(coverage(before)) === canonical(coverage(after)), 'unexplained coverage regression/change');
  return { assertions: results, changedRecords: changed.filter((r) => r.kind !== 'complete').length };
}

export function equalRuns(first, second) {
  requireThat(Object.keys(first).length > 0 && canonical(first) === canonical(second), 'missing artifacts or nondeterministic replay bytes');
}
