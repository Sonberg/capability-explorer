import { mkdirSync, readFileSync, writeFileSync, existsSync, realpathSync } from 'node:fs';
import { resolve, dirname, relative, isAbsolute, sep } from 'node:path';
import { canonical, key, COLORS, compare, RELATIONS } from './semantics.mjs';
import { sha256 } from './source.mjs';

const html = text => String(text).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
  .replaceAll('"', '&quot;').replaceAll("'", '&#39;');
const md = text => html(text).replaceAll('|', '&#124;').replaceAll('\n', ' ');
const mermaid = text => [...String(text)].map(c => /[\p{L}\p{N} _.,:;()/-]/u.test(c) ? c : `#${c.codePointAt(0)};`).join('');
const node = k => `n${sha256(k)}`;
const encode = part => encodeURIComponent(part).replace(/[!'()*]/gu, c => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
const href = (root, output, record, projects) => {
  const project = projects.find(p => p.id === record.project);
  return relative(resolve(root, output), resolve(root, project.root, record.path)).split(sep).map(encode).join('/') +
    (record.pointer ? `#${encodeURIComponent(record.pointer)}` : '');
};

export function report(ledger) {
  const activeByScope = Object.create(null), aliases = new Map(), invalidation = new Map();
  for (const r of ledger.records) {
    const k = key(r.project, r.id);
    if (r.status.acceptance) (activeByScope[r.status.scope] ??= []).push(k);
    if (!aliases.has(r.alias)) aliases.set(r.alias, []);
    aliases.get(r.alias).push(k);
    invalidation.set(k, [`record:${k}`, ...r.facets.map(f => `facet:${f.facet}/${f.value}`)]);
  }
  for (const e of ledger.edges) {
    for (const k of new Set([e.from, ...(e.to.state === 'resolved' ? [key(e.to.project, e.to.id)] : []),
      e.evidence.record, ...(e.corroboration ?? []).map(r => r.record)])) invalidation.get(k)?.push(`edge:${e.id}`);
  }
  return {
    activeByScope,
    acceptanceInterpretation: 'source-recorded exact-scope candidates, not an authenticated active-decision verdict',
    scopeReview: Object.entries(activeByScope).filter(([, ids]) => ids.length > 1)
      .map(([scope, ids]) => ({ scope, candidates: ids, outcome: 'overlap-needs-human-review; not necessarily contradiction' })),
    contradictions: 'not-assessed; no semantic adjudication',
    aliasCollisions: [...aliases].filter(([, ids]) => ids.length > 1).map(([alias, ids]) => ({ alias, ids, equivalent: 'not-asserted' })),
    unreviewed: {
      proposedOrUnknownRecords: ledger.records.filter(r => ['proposed', 'unknown'].includes(r.status.state)).map(r => key(r.project, r.id)),
      facets: ledger.records.flatMap(r => r.facets.filter(f => f.standing !== 'review-confirmed').map(f => ({
        record: key(r.project, r.id), facet: f.facet, value: f.value
      }))),
      edges: ledger.edges.filter(e => e.standing !== 'review-confirmed').map(e => e.id)
    },
    invalidation: [...invalidation].sort(([a], [b]) => compare(a, b)).map(([source, claims]) => ({ source, claims: [...new Set(claims)].sort(compare) }))
  };
}

export function projections(ledger, config, root = '.', output = config.output) {
  outputDirectory(root, output);
  const summary = report(ledger);
  const lines = [
    '# Decision ledger preview', '',
    'Generated navigation only. Configured canonical sources retain authority. Same aliases do not mean equivalent decisions.', '',
    '| Project / stable record | Alias | Title | Raw status | State / exact scope |',
    '| --- | --- | --- | --- | --- |'
  ];
  const graph = ['flowchart LR'];
  for (const r of ledger.records) {
    const k = key(r.project, r.id), n = node(k);
    lines.push(`| ${md(r.project)} / ${md(r.id)} | ${md(r.alias)} | [${md(r.title)}](${href(root, output, r, config.projects)}) | ${md(r.rawStatus ?? '(missing)')} | ${md(r.status.state)} / ${md(r.status.scope)} |`);
    graph.push(`  ${n}["${mermaid(`${r.project}/${r.alias}: ${r.title} (${r.status.state})`)}"]`);
    graph.push(`  style ${n} fill:${COLORS[r.status.state]}`);
  }
  const external = new Set();
  for (const e of ledger.edges) {
    const from = node(e.from), toKey = key(e.to.project, e.to.id);
    const to = node(e.to.state === 'resolved' ? toKey : JSON.stringify([toKey, e.to.state]));
    if (e.to.state !== 'resolved' && !external.has(to)) {
      graph.push(`  ${to}["${mermaid(`${e.to.project}/${e.to.id} (${e.to.state})`)}"]`);
      external.add(to);
    }
    graph.push(`  ${from} ${RELATIONS[e.type].symmetric ? '<-->' : '-->'}|"${mermaid(e.type)}"| ${to}`);
  }
  lines.push('', `Records: ${ledger.records.length}; asserted relationships: ${ledger.edges.length}.`,
    '', '## Diagnostics', '', '```json', canonical(ledger.diagnostics).trimEnd(), '```',
    '', '## Scope, alias and source-change review', '', '```json', canonical(summary).trimEnd(), '```', '');
  return {
    'ledger.json': canonical({ ...ledger, report: summary }),
    'index.md': lines.join('\n'),
    'graph.mmd': graph.join('\n') + '\n'
  };
}

export function outputDirectory(root, output) {
  if (!output || isAbsolute(output) || output.includes('\\') || output.includes('\0') ||
      output.split('/').some(s => !s || s === '..' || s === '.'))
    throw new Error('unsafe output path');
  const base = realpathSync(root), destination = resolve(base, output);
  let existing = destination;
  while (!existsSync(existing)) existing = dirname(existing);
  const rel = relative(base, realpathSync(existing));
  if (rel === '..' || rel.startsWith(`..${sep}`) || isAbsolute(rel)) throw new Error('output symlink escape');
  if (realpathSync(existing) !== existing) throw new Error('output symlink ancestor');
  return destination;
}

export function projectionBoundary(root, output, config, configPath, ledger) {
  root = realpathSync(root);
  const destination = outputDirectory(root, output);
  const files = ['ledger.json', 'index.md', 'graph.mmd'].map(f => resolve(destination, f));
  const protectedInputs = [resolve(root, configPath), resolve(root, config.sidecar),
    ...ledger.records.map(r => resolve(root, config.projects.find(p => p.id === r.project).root, r.path))];
  if (files.some(f => protectedInputs.includes(f))) throw new Error('projection would overwrite canonical input');
  if (files.some(f => existsSync(f) && realpathSync(f) !== f)) throw new Error('projection symlink');
  const beneath = (path, directory) => path === directory || path.startsWith(`${directory}${sep}`);
  for (const project of config.projects) {
    const projectRoot = realpathSync(resolve(root, project.root));
    if (project.directories.some(d => beneath(destination, realpathSync(resolve(projectRoot, d)))) &&
        !project.exclude.some(d => beneath(destination, resolve(projectRoot, d))))
      throw new Error('projection intersects canonical discovery; explicitly exclude generated directory');
  }
}

export function outputViews(root, output, views, mode) {
  if (!['check', 'generate'].includes(mode)) throw new Error('explicit check/generate mode required');
  const destination = outputDirectory(root, output);
  for (const [file, bytes] of Object.entries(views)) {
    if (!['ledger.json', 'index.md', 'graph.mmd'].includes(file)) throw new Error('unknown projection path');
    const target = resolve(destination, file);
    if (existsSync(target) && realpathSync(target) !== target) throw new Error('projection symlink');
    if (mode === 'check') {
      if (!existsSync(target) || readFileSync(target, 'utf8') !== bytes) throw new Error(`stale projection: ${file}`);
    } else {
      mkdirSync(destination, { recursive: true });
      writeFileSync(target, bytes);
    }
  }
}
