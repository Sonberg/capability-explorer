export const RELATIONS = Object.freeze({
  'references': { cyclic: true, symmetric: false, transitive: false, coverage: 'partial' },
  'related-to': { cyclic: true, symmetric: true, transitive: false, coverage: 'partial' },
  'related-from': { cyclic: true, symmetric: false, transitive: false, coverage: 'partial' },
  'depends-on': { cyclic: false, symmetric: false, transitive: false, coverage: 'partial' },
  'extends': { cyclic: false, symmetric: false, transitive: false, coverage: 'partial' },
  'scoped-amends': { cyclic: true, symmetric: false, transitive: false, coverage: 'partial' },
  'scoped-overrides': { cyclic: false, symmetric: false, transitive: false, coverage: 'partial' },
  'full-supersedes': { cyclic: false, symmetric: false, transitive: false, coverage: 'whole' }
});
export const COLORS = Object.freeze({
  proposed: '#fff3cd', accepted: '#d1e7dd', rejected: '#f8d7da',
  deprecated: '#e2e3e5', superseded: '#cfe2ff', unknown: '#eeeeee'
});
export const key = (project, id) => JSON.stringify([project, id]);
export const canonical = value => JSON.stringify(sortKeys(value), null, 2) + '\n';
function sortKeys(value) {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value !== null && typeof value === 'object')
    return Object.fromEntries(Object.keys(value).sort().map(k => [k, sortKeys(value[k])]));
  return value;
}
export const compare = (a, b) => a < b ? -1 : a > b ? 1 : 0;

export function relationError(edge) {
  const rule = RELATIONS[edge.type];
  if (!rule) return 'unknown relation';
  if (!edge.scope?.trim()) return 'missing relation scope';
  if (edge.coverage !== rule.coverage) return 'relation coverage';
  if (edge.from === key(edge.to.project, edge.to.id)) return 'self relation';
  return null;
}

export function validateCycles(edges) {
  for (const [type, rule] of Object.entries(RELATIONS)) {
    if (rule.cyclic) continue;
    const graph = new Map();
    for (const e of edges.filter(e => e.type === type && e.to.state === 'resolved')) {
      if (!graph.has(e.from)) graph.set(e.from, []);
      graph.get(e.from).push(key(e.to.project, e.to.id));
    }
    // Iterative DFS stays within the configured record/edge budgets.
    const seen = new Set(), active = new Set();
    for (const start of graph.keys()) {
      const stack = [[start, false]];
      while (stack.length) {
        const [node, exit] = stack.pop();
        if (exit) { active.delete(node); seen.add(node); continue; }
        if (active.has(node)) throw new Error(`cycle: ${type}`);
        if (seen.has(node)) continue;
        active.add(node);
        stack.push([node, true]);
        for (const next of graph.get(node) ?? []) stack.push([next, false]);
      }
    }
  }
}
