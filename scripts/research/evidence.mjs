import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, realpathSync } from 'node:fs';
import { isAbsolute, join, resolve } from 'node:path';

/** @typedef {{path: string, sha256: string}} EvidenceRef */
/** @param {string|Buffer} bytes @returns {string} */
export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

/** Canonical JSON preserves array order; it does not normalize tool semantics.
 * @param {unknown} value @returns {string} */
export function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.keys(value).sort()
    .map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  const result = JSON.stringify(value);
  if (result === undefined) throw new Error('non-JSON evidence');
  return result;
}

/** Read a bounded regular file with no symlink path components.
 * Intended for an isolated, quiescent checkout, not a concurrent hostile writer.
 * @param {string} root @param {string} path @returns {Buffer} */
export function readBounded(root, path) {
  if (typeof path !== 'string' || path.length > 1024 || isAbsolute(path)
    || path.includes('\\') || path.includes('\0')) throw new Error('unsafe evidence path');
  const parts = path.split('/');
  if (parts.length > 24 || parts.some(p => !p || p === '.' || p === '..')) throw new Error('unsafe evidence path');
  let current = realpathSync(resolve(root));
  for (const part of parts) {
    current = join(current, part);
    if (lstatSync(current).isSymbolicLink()) throw new Error('symlink evidence forbidden');
  }
  const stat = lstatSync(current);
  if (!stat.isFile() || stat.size > 8 * 1024 * 1024) throw new Error('evidence must be a regular file <=8MiB');
  const bytes = readFileSync(current);
  if (bytes.length > 8 * 1024 * 1024) throw new Error('evidence grew beyond limit');
  return bytes;
}

/** @param {string} root @param {EvidenceRef} ref @returns {Buffer} */
export function readEvidence(root, ref) {
  if (!ref || !/^[a-f0-9]{64}$/.test(ref.sha256 ?? '')) throw new Error('missing evidence digest');
  const bytes = readBounded(root, ref.path);
  if (sha256(bytes) !== ref.sha256) throw new Error(`changed evidence: ${ref.path}`);
  return bytes;
}

/** JSON shape is validated by the consuming gate. @param {Buffer|string} bytes @returns {any} */
export function parseJson(bytes) { return JSON.parse(bytes.toString()); }

/** @param {unknown} value @param {string} label @returns {asserts value} */
export function need(value, label) { if (!value) throw new Error(label); }

/** Bound every top-level collection before iterating it.
 * @param {unknown} value @param {string} label @param {number} [min] @returns {any[]} */
export function list(value, label, min = 1) {
  need(Array.isArray(value) && value.length >= min && value.length <= 256, `invalid ${label}`);
  return value;
}

/** @param {unknown} value @returns {boolean} */
export const text = value => typeof value === 'string' && value.trim().length > 0 && value.length <= 8192;

/** @param {unknown} value @returns {boolean} */
export const commit = value => typeof value === 'string' && /^[a-f0-9]{40}$/.test(value);

/** Rehash the files, not just the manifest which declares their identities.
 * @param {string} root @param {any} manifest @returns {number} */
export function verifySealedCorpus(root, manifest) {
  need(manifest?.schemaVersion === 1 && commit(manifest.baseline), 'invalid sealed corpus identity');
  const refs = list(manifest.files, 'sealed corpus files');
  need(new Set(refs.map(r => r?.path)).size === refs.length, 'duplicate sealed corpus file');
  let total = 0;
  for (const ref of refs) {
    total += readEvidence(root, ref).length;
    need(total <= 64 * 1024 * 1024, 'sealed corpus exceeds 64MiB foundation limit');
  }
  readEvidence(root, manifest.oracle);
  return refs.length;
}
