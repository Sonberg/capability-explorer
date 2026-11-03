import { resolve } from 'node:path';
import { validate } from './schema.mjs';
import { readJSON } from './contracts.mjs';
import { localPath, discover, loadCollection, verifyEvidence, passage } from './source.mjs';
import { key, compare, canonical, relationError, validateCycles } from './semantics.mjs';
import { resolvePointer } from './json-source.mjs';

const unique = (values, message) => {
  if (new Set(values).size !== values.length) throw new Error(message);
};
const locator = (project, path, pointer = '') => JSON.stringify([project, path, pointer]);

export function loadLedger(root, configPath) {
  const config = readJSON(localPath(root, configPath));
  validate(config, 'config');
  const ledger = readJSON(localPath(root, config.sidecar));
  return reconcile(resolve(root), config, ledger);
}

export function reconcile(root, config, ledger) {
  validate(config, 'config'); validate(ledger, 'ledger');
  unique(config.projects.map(p => p.id), 'duplicate project identity');
  unique(config.vocabulary.map(v => v.facet), 'duplicate vocabulary facet');
  unique(ledger.records.map(r => key(r.project, r.id)), 'duplicate record identity');
  unique(ledger.records.map(r => locator(r.project, r.path, r.pointer)), 'duplicate source locator');
  unique(ledger.edges.map(e => e.id), 'duplicate assertion identity');
  if (ledger.edges.length > config.limits.maxEdges) throw new Error('edge budget');
  const sources = new Map(), recordMap = new Map(), sourceLocators = new Map(), projectMap = new Map();
  const diagnostics = [], declarations = [];
  let totalBytes = 0, totalRecords = 0;
  for (const project of config.projects) {
    if ((project.adapter === 'json-collection-v1') !== (project.canonical === 'json'))
      throw new Error('canonical source / adapter mismatch');
    if ((project.adapter === 'json-collection-v1') !== Boolean(project.jsonProfile))
      throw new Error('JSON profile boundary');
    unique(project.statusMap.map(m => m.raw), 'duplicate status mapping');
    if (project.jsonProfile) {
      const p = project.jsonProfile;
      unique([p.id, p.title, p.status, ...(p.acceptance ? [p.acceptance] : []),
        ...p.relations.map(r => r.field)], 'ambiguous JSON field mapping');
    }
    projectMap.set(project.id, project);
    const projectRoot = localPath(root, project.root);
    for (const path of discover(projectRoot, project, config.limits)) {
      const collection = loadCollection(projectRoot, path, project, config.limits);
      if (!collection.length) throw new Error(`empty canonical collection: ${path}`);
      totalBytes += collection[0].bytes;
      if (totalBytes > config.limits.maxBytes) throw new Error('total byte budget');
      for (const source of collection) {
        if (++totalRecords > config.limits.maxFiles) throw new Error('record budget');
        sourceLocators.set(locator(project.id, path, source.pointer), source);
      }
    }
  }
  for (const record of ledger.records) {
    const k = key(record.project, record.id), project = projectMap.get(record.project);
    if (!project) throw new Error(`unknown record project: ${record.project}`);
    const loc = locator(record.project, record.path, record.pointer), source = sourceLocators.get(loc);
    if (!source) throw new Error(`uncovered sidecar source: ${record.path}`);
    sourceLocators.delete(loc);
    if (source.revision !== record.revision) throw new Error(`stale source revision: ${record.path}`);
    if (source.title !== record.title) throw new Error(`title mismatch: ${record.path}`);
    if (source.alias !== undefined && source.alias !== record.alias) throw new Error('JSON local alias mismatch');
    sources.set(k, source); recordMap.set(k, record);
  }
  if (sourceLocators.size) throw new Error(`inventory coverage: ${[...sourceLocators.keys()].join(', ')}`);
  for (const project of config.projects.filter(p => p.canonical === 'json'))
    unique(ledger.records.filter(r => r.project === project.id).map(r => r.alias), 'ambiguous JSON local id; configure separate collection namespaces');
  const vocabulary = new Map(config.vocabulary.map(v => [v.facet, new Set(v.values)]));
  for (const [k, record] of recordMap) {
    const source = sources.get(k), project = projectMap.get(record.project);
    const mapped = project.statusMap.find(m => m.raw === source.rawStatus)?.state ?? 'unknown';
    if (mapped !== record.status.state) throw new Error(`status mismatch: ${record.path}`);
    verifyEvidence(record.status.evidence, sources, k);
    const statusLine = source.statusLine ?? source.titleLine;
    if (record.status.evidence.start !== statusLine ||
        record.status.evidence.end !== (source.statusEnd ?? statusLine) ||
        record.status.evidence.pointer !== source.statusPointer) throw new Error('status passage binding');
    if (mapped === 'unknown') diagnostics.push({ record: k, kind: 'unknown-status', raw: source.rawStatus });
    for (const message of source.diagnostics) diagnostics.push({ record: k, kind: 'uninterpreted-metadata', message });
    const acceptance = record.status.acceptance;
    if (acceptance) {
      if (mapped !== 'accepted' || acceptance.scope !== record.status.scope) throw new Error('scope qualified acceptance');
      verifyEvidence(acceptance.evidence, sources, k);
      if (source.document !== undefined) {
        if (!source.acceptancePointer || acceptance.evidence.pointer !== source.acceptancePointer)
          throw new Error('JSON acceptance profile binding');
        const value = resolvePointer(source.document, acceptance.evidence.pointer);
        if (!value || typeof value !== 'object' || value.scope !== acceptance.scope ||
            value.agent !== acceptance.authority.agent) throw new Error('JSON acceptance receipt binding');
      } else if (!acceptance.evidence.quote.includes(acceptance.scope) ||
                 !acceptance.evidence.quote.toLowerCase().includes('accepted')) throw new Error('acceptance scope passage');
    } else if (mapped === 'accepted') diagnostics.push({ record: k, kind: 'unqualified-acceptance' });
    unique(record.facets.map(f => key(f.facet, f.value)), 'duplicate record facet');
    for (const facet of record.facets) {
      if (!vocabulary.get(facet.facet)?.has(facet.value)) throw new Error('unknown facet vocabulary');
      verifyEvidence(facet.evidence, sources, k);
    }
    if (!record.facets.length) diagnostics.push({ record: k, kind: 'unclassified' });
    for (const d of source.declaredRelations ?? []) declarations.push({ ...d, record: k, project: record.project });
  }
  const declaredUsed = new Set(), normalizedEdges = new Set();
  for (const edge of ledger.edges) {
    const why = relationError(edge);
    if (why) throw new Error(why);
    const from = recordMap.get(edge.from);
    if (!from) throw new Error('dangling edge source');
    if (edge.fromRevision !== from.revision) throw new Error('stale edge source revision');
    const toKey = key(edge.to.project, edge.to.id), target = recordMap.get(toKey);
    if (edge.to.state === 'resolved') {
      if (!target) throw new Error('dangling resolved target');
      if (edge.to.revision !== target.revision) throw new Error('stale target revision');
      if (edge.type === 'full-supersedes' && edge.scope !== target.status.scope) throw new Error('supersession target scope');
    } else {
      if (!edge.to.reason || edge.to.revision !== undefined) throw new Error('unavailable target needs reason, not pinned revision');
      diagnostics.push({ assertion: edge.id, kind: `${edge.to.state}-target`, target: toKey, reason: edge.to.reason });
    }
    const signature = JSON.stringify([edge.type, edge.from, toKey, edge.scope]);
    if (normalizedEdges.has(signature)) throw new Error('duplicate normalized relation; use corroboration for inverse declarations');
    normalizedEdges.add(signature);
    const receipts = [edge.evidence, ...(edge.corroboration ?? [])];
    unique(receipts.map(r => JSON.stringify([r.record, r.pointer, r.start, r.end])), 'duplicate edge evidence');
    for (const receipt of receipts) {
      if (![edge.from, toKey].includes(receipt.record)) throw new Error('edge evidence endpoint');
      verifyEvidence(receipt, sources, receipt.record);
      const source = sources.get(receipt.record);
      if (source.document !== undefined) {
        const d = declarations.find(d => d.record === receipt.record && d.pointer === receipt.pointer);
        if (!d || d.type !== edge.type) throw new Error('JSON relation field binding');
        const other = d.direction === 'forward' ? target : from;
        const own = d.direction === 'forward' ? edge.from : toKey;
        if (receipt.record !== own || !other || d.project !== other.project || d.target !== other.alias)
          throw new Error('JSON relation direction/target binding');
        declaredUsed.add(JSON.stringify([d.record, d.pointer]));
      } else {
        if (receipt.record !== edge.from) throw new Error('Markdown edge evidence must be source assertion');
        if (edge.standing === 'source-declared') {
          const tokens = [from.id, edge.type, edge.to.id, edge.scope,
            ...(from.project === edge.to.project ? [] : [edge.to.project])];
          if (!tokens.every(token => receipt.quote.includes(token))) throw new Error('Markdown typed declaration binding');
        }
      }
    }
  }
  for (const d of declarations)
    if (!declaredUsed.has(JSON.stringify([d.record, d.pointer])))
      diagnostics.push({ record: d.record, kind: 'unimported-typed-relation', pointer: d.pointer, target: d.target, type: d.type });
  validateCycles(ledger.edges);
  const records = ledger.records.map(record => {
    const k = key(record.project, record.id), source = sources.get(k);
    return {
      ...record, facets: [...record.facets].sort((a, b) => compare(key(a.facet, a.value), key(b.facet, b.value))),
      canonical: projectMap.get(record.project).canonical,
      rawStatus: source.rawStatus, sourceMetadata: source.metadata,
      titleEvidence: passage(source, k, source.titleLine, source.titleEnd ?? source.titleLine, source.titlePointer)
    };
  }).sort((a, b) => compare(key(a.project, a.id), key(b.project, b.id)));
  const edges = ledger.edges.map(e => ({ ...e, ...(e.corroboration ? {
    corroboration: [...e.corroboration].sort((a, b) => compare(canonical(a), canonical(b)))
  } : {}) })).sort((a, b) => compare(a.id, b.id));
  return { version: ledger.version, records, edges,
    diagnostics: diagnostics.sort((a, b) => compare(JSON.stringify(a), JSON.stringify(b))) };
}
