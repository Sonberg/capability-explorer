import { createHash } from 'node:crypto';
import { readFileSync, realpathSync, readdirSync, lstatSync } from 'node:fs';
import { resolve, relative, isAbsolute, sep } from 'node:path';
import { compare } from './semantics.mjs';
import { parseCollection, resolvePointer } from './json-source.mjs';

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
export function localPath(root, path, mustExist = true) {
  if (typeof path !== 'string' || !path || path.includes('\\') || path.includes('\0') ||
      isAbsolute(path) || path.split('/').some(s => s === '..' || s === ''))
    throw new Error(`unsafe path: ${path}`);
  const base = realpathSync(root), absolute = resolve(base, path);
  const inside = p => { const r = relative(base, p); return r === '' || (!r.startsWith(`..${sep}`) && r !== '..' && !isAbsolute(r)); };
  if (!inside(absolute)) throw new Error(`path escape: ${path}`);
  if (mustExist && !inside(realpathSync(absolute))) throw new Error(`symlink escape: ${path}`);
  return absolute;
}

export function discover(root, project, limits) {
  const found = new Set(), stack = [...project.directories];
  let entries = 0;
  for (const excluded of project.exclude) localPath(root, excluded, false);
  while (stack.length) {
    const dir = stack.pop(), absolute = localPath(root, dir);
    for (const entry of readdirSync(absolute, { withFileTypes: true }).sort((a, b) => compare(a.name, b.name))) {
      if (++entries > limits.maxFiles * 20) throw new Error('discovery entry budget');
      const path = dir === '.' ? entry.name : `${dir}/${entry.name}`;
      if (project.exclude.includes(path)) continue;
      if (entry.isSymbolicLink()) throw new Error(`symlink source: ${path}`);
      if (entry.isDirectory()) stack.push(path);
      else if (entry.isFile() && path.endsWith(project.adapter === 'json-collection-v1' ? '.json' : '.md')) {
        if (found.has(path)) throw new Error(`duplicate discovery locator: ${path}`);
        found.add(path);
        if (found.size > limits.maxFiles) throw new Error('file budget');
      }
    }
  }
  return [...found].sort(compare);
}

function scalar(raw) {
  if (raw.startsWith('"')) {
    const value = JSON.parse(raw);
    if (typeof value !== 'string') throw new Error('unsupported frontmatter scalar');
    return value;
  }
  if (raw.startsWith("'") && raw.endsWith("'")) return raw.slice(1, -1).replaceAll("''", "'");
  if (!raw || /[{}[\]&*!|>]/u.test(raw) || /:\s| #/u.test(raw)) throw new Error('unsupported frontmatter scalar');
  return raw;
}

export function parseSource(bytes, adapter) {
  const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
  const lines = text.split('\n'), metadata = [];
  let start = 0;
  if (!['markdown-header-v1', 'frontmatter-scalars-v1'].includes(adapter)) throw new Error(`unsupported adapter: ${adapter}`);
  if (adapter === 'frontmatter-scalars-v1') {
    if (lines[0]?.replace(/\r$/, '') !== '---') throw new Error('missing frontmatter');
    const end = lines.findIndex((line, i) => i > 0 && line.replace(/\r$/, '') === '---');
    if (end < 0) throw new Error('unclosed frontmatter');
    for (let i = 1; i < end; i++) {
      const line = lines[i].replace(/\r$/, '');
      if (!line || line.startsWith('#')) continue;
      const m = /^([a-zA-Z][a-zA-Z0-9-]*):\s*(.+)$/.exec(line);
      if (!m) throw new Error(`unsupported frontmatter at line ${i + 1}`);
      metadata.push({ key: m[1].toLowerCase(), value: scalar(m[2]), line: i + 1 });
    }
    start = end + 1;
  } else if (lines[0] === '---') throw new Error('adapter mismatch: frontmatter');
  let fence = null;
  const visible = lines.map(line => {
    const marker = /^ {0,3}(`{3,}|~{3,})/u.exec(line);
    if (marker) {
      if (!fence) fence = marker[1];
      else if (marker[1][0] === fence[0] && marker[1].length >= fence.length &&
          /^ {0,3}[`~]+[ \t]*\r?$/u.test(line)) fence = null;
      return '';
    }
    return fence ? '' : line;
  });
  if (fence) throw new Error('unclosed Markdown fence');
  const titles = visible.map((line, i) => ({ line: i + 1, match: /^# (.+?)\r?$/.exec(line) })).filter(t => t.match);
  if (titles.length !== 1) throw new Error('expected exactly one H1 title');
  const title = titles[0];
  if (adapter === 'markdown-header-v1') {
    for (let i = start; i < lines.length; i++) {
      if (/^## /u.test(visible[i])) break;
      const m = /^(?:[*-] )?([A-Za-z][A-Za-z -]*):\s*(.*?)\r?$/.exec(visible[i]);
      if (m) metadata.push({ key: m[1].trim().toLowerCase(), value: m[2], line: i + 1 });
    }
  } else {
    // Two status mechanisms would introduce undocumented precedence.
    for (let i = start; i < lines.length && !/^## /u.test(visible[i]); i++)
      if (/^(?:[*-] )?Status:/iu.test(visible[i])) throw new Error('ambiguous status mechanisms');
  }
  const keys = metadata.map(m => m.key);
  if (new Set(keys).size !== keys.length) throw new Error('ambiguous duplicate metadata');
  const status = metadata.find(m => m.key === 'status');
  if (status && !status.value.trim()) throw new Error('empty status');
  return {
    text, lines, title: title.match[1], titleLine: title.line,
    rawStatus: status?.value ?? null, statusLine: status?.line ?? null, metadata,
    diagnostics: metadata.filter(m => !['status', 'date', 'scope'].includes(m.key))
      .map(m => `uninterpreted metadata: ${m.key}`)
  };
}

export function loadSource(root, path, adapter, limits) {
  const absolute = localPath(root, path);
  const stat = lstatSync(absolute);
  if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`not a source file: ${path}`);
  if (stat.size > limits.maxBytes) throw new Error('source byte budget');
  const bytes = readFileSync(absolute);
  return { ...parseSource(bytes, adapter), bytes: bytes.length, revision: `sha256:${sha256(bytes)}` };
}

export function loadCollection(root, path, project, limits) {
  if (project.adapter !== 'json-collection-v1') return [loadSource(root, path, project.adapter, limits)];
  const absolute = localPath(root, path), stat = lstatSync(absolute);
  if (!stat.isFile() || stat.isSymbolicLink()) throw new Error(`not a source file: ${path}`);
  if (stat.size > limits.maxBytes) throw new Error('source byte budget');
  const bytes = readFileSync(absolute);
  return parseCollection(bytes, project.jsonProfile).map(s => ({
    ...s, bytes: bytes.length, revision: `sha256:${sha256(bytes)}`
  }));
}

export function passage(source, record, start, end = start, pointer) {
  const quote = source.lines.slice(start - 1, end).join('\n');
  return { record, revision: source.revision, start, end, quote, sha256: sha256(quote),
    ...(pointer === undefined ? {} : { pointer }) };
}

export function verifyEvidence(evidence, sources, expectedRecord) {
  if (evidence.record !== expectedRecord) throw new Error('evidence record mismatch');
  const source = sources.get(evidence.record);
  if (!source || evidence.revision !== source.revision) throw new Error('stale evidence revision');
  if (evidence.end < evidence.start || evidence.end > source.lines.length) throw new Error('evidence span');
  const actual = passage(source, evidence.record, evidence.start, evidence.end);
  if (actual.quote !== evidence.quote || actual.sha256 !== evidence.sha256) throw new Error('evidence passage mismatch');
  if (source.document !== undefined) {
    if (typeof evidence.pointer !== 'string') throw new Error('JSON evidence needs field pointer');
    if (!evidence.pointer.startsWith(`${source.pointer}/`)) throw new Error('JSON evidence outside record');
    resolvePointer(source.document, evidence.pointer);
  } else if (evidence.pointer !== undefined) throw new Error('Markdown evidence cannot use JSON pointer');
}
