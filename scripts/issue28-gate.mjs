#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function validateContract(c) {
  const problems = [];
  const need = (ok, message) => { if (!ok) problems.push(message); };
  need(c?.version === 1, 'unsupported contract version');
  need(/^[a-f0-9]{40}$/.test(c?.baseline ?? ''), 'baseline must be a full commit');
  need(c?.scope === 'foundation' && c.issueComplete === false, 'foundation cannot claim full issue completion');
  need(c?.snapshot?.identity === 'immutable-uuid' && c.snapshot.publish === 'compare-and-swap'
    && c.snapshot.pages === 'retained-snapshot', 'snapshot consistency contract missing');
  for (const key of ['recordBytes', 'responseBytes', 'searchDefault', 'searchMax', 'edgePreview', 'pageMax', 'queryTimeoutSeconds']) {
    need(Number.isSafeInteger(c?.limits?.[key]) && c.limits[key] > 0, `invalid limit ${key}`);
  }
  need(c?.limits?.searchDefault <= c?.limits?.searchMax && c?.limits?.searchMax <= 100, 'search bound invalid');
  need(c?.limits?.pageMax <= 100 && c?.limits?.edgePreview <= c?.limits?.pageMax, 'page bound invalid');
  need(Number.isSafeInteger(c?.snapshot?.cursorSeconds) && c.snapshot.cursorSeconds > 0, 'cursor expiry missing');
  need(Array.isArray(c?.acceptance) && new Set(c.acceptance).size >= 3, 'acceptance criteria missing');
  need(c?.workload?.largeOptIn === true && Number.isSafeInteger(c?.workload?.seed), 'repeatable opt-in workload required');
  need(c?.workload?.tiers?.includes(c.workload.defaultNodes), 'default tier not declared');
  need(c?.budgets?.status === 'provisional', 'unmeasured budgets cannot be accepted evidence');
  for (const metric of ['app-rss', 'database-memory', 'import-peak', 'disk-index-wal', 'cold-start', 'p50-p95-p99', 'response-bytes', 'ci-seconds']) {
    need(c?.budgets?.measure?.includes(metric), `whole-stack metric missing: ${metric}`);
  }
  const routes = c?.routes ?? [];
  need(Array.isArray(routes) && routes.length > 0, 'endpoint inventory missing');
  const seen = new Set();
  for (const route of routes) {
    need(typeof route.path === 'string' && !seen.has(route.path), 'route missing or duplicated');
    seen.add(route.path);
    need(['foundation', 'remaining'].includes(route.status), `route ${route.path}: status missing`);
    need(['page', 'graph', 'detail'].includes(route.shape), `route ${route.path}: unknown shape`);
    if (route.shape === 'page') {
      need(Number.isSafeInteger(route.max) && route.max > 0 && route.max <= 100, `route ${route.path}: row bound missing`);
      need(Array.isArray(route.sort) && route.sort.length >= 1 && new Set(route.sort).size === route.sort.length,
        `route ${route.path}: stable sort missing`);
    }
    if (route.shape === 'graph') need(route.maxNodes > 0 && route.maxEdges > 0, `route ${route.path}: graph caps missing`);
  }
  for (const path of ['search', 'symbol', 'symbol/edges', 'symbol/evidence', 'architecture', 'integrations', 'changes', 'glossary']) {
    need(seen.has(path), `endpoint inventory omits ${path}`);
  }
  need(c?.ruvector?.role === 'optional-challenger' && c.ruvector.adoption === 'owner-decision'
    && c.ruvector.paidCalls === 0, 'extension adoption/cost boundary missing');
  return problems;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const phase = process.argv[2] ?? 'specification';
    if (phase !== 'specification') throw new Error(`${phase}: executable evidence not implemented; gate remains incomplete`);
    const c = JSON.parse(readFileSync('config/issue28/contract.json', 'utf8'));
    const errors = validateContract(c);
    if (errors.length) throw new Error(errors.join('\n'));
    process.stdout.write('issue28 specification: bounded contract validated; implementation/performance not assessed\n');
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
