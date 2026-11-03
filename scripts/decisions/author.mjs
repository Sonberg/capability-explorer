import { localPath, loadCollection, passage } from './source.mjs';
import { key } from './semantics.mjs';

// Explicit enrollment aid, never called by check/generate. IDs and scope are authored inputs.
export function authorRecord(root, config, declaration) {
  for (const field of ['project', 'id', 'alias', 'path', 'scope'])
    if (typeof declaration[field] !== 'string' || !declaration[field]) throw new Error(`enrollment requires ${field}`);
  const project = config.projects.find(p => p.id === declaration.project);
  if (!project) throw new Error('unknown enrollment project');
  const source = loadCollection(localPath(root, project.root), declaration.path, project, config.limits)
    .find(s => (s.pointer ?? '') === (declaration.pointer ?? ''));
  if (!source) throw new Error('unknown enrollment locator');
  const k = key(declaration.project, declaration.id);
  const line = source.statusLine ?? source.titleLine;
  const evidence = passage(source, k, line, source.statusEnd ?? line, source.statusPointer);
  const authority = { kind: 'source-recorded', agent: 'source author; identity not authenticated' };
  const status = {
    state: project.statusMap.find(m => m.raw === source.rawStatus)?.state ?? 'unknown',
    scope: declaration.scope, authority, evidence
  };
  if (declaration.acceptance) status.acceptance = declaration.acceptance;
  return {
    project: declaration.project, id: declaration.id, alias: declaration.alias,
    path: declaration.path, ...(declaration.pointer === undefined ? {} : { pointer: declaration.pointer }),
    revision: source.revision, title: source.title, status,
    facets: declaration.facets ?? [], implementation: 'not-assessed'
  };
}
