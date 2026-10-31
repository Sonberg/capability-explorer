import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

// Canonical serialization also stabilizes nested evidence objects across API serializers.
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])]));
  return value;
}
export function toLikeC4(graph) {
  if (!graph || !Array.isArray(graph.nodes) || !Array.isArray(graph.edges)) throw new Error('Expected architecture graph nodes and edges');
  const quote = value => JSON.stringify(String(value ?? '').replace(/[\r\n\t]/g, ' '));
  const order = (a, b) => a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  const validateIds = items => {
    const seen = new Set();
    for (const item of items) {
      if (!item || typeof item.id !== 'string' || !item.id || seen.has(item.id)) throw new Error('Missing or duplicate record id');
      seen.add(item.id);
    }
  };
  validateIds(graph.nodes); validateIds(graph.edges);
  const nodes = [...graph.nodes].sort(order), edges = [...graph.edges].sort(order);
  const ids = new Map(nodes.map((n, i) => [n.id, `n${i}`]));
  const lines = ['// Architecture API snapshot; certainty is not upgraded by visualization.',
    'specification {', '  color eventColor #8b5cf6', '  element artifact', '  element service { style { color blue } }', '  element application { style { color sky } }', '  element datastore { style { color amber } }', '  element channel { style { color eventColor } }', '  relationship observed { line solid }', '  relationship association { line dashed head none }', '  relationship uncertain { line dashed }', '}', 'model {'];
  for (const node of nodes) lines.push(`  ${ids.get(node.id)} = ${['service','application','datastore','channel'].includes(node.category) ? node.category : 'artifact'} ${quote(node.label)} {`,
    `    description ${quote(node.sublabel ?? node.category ?? 'Architecture artifact')}`,
    '    metadata {', `      source ${quote(JSON.stringify(canonical(node)))}`, '    }', '  }');
  for (const edge of edges) {
    if (!ids.has(edge.from) || !ids.has(edge.to)) throw new Error('Dangling edge endpoint');
    const certainty = edge.certainty ?? 'unresolved';
    if (!['proven','static','bound','broken','unbound','candidate','unresolved'].includes(certainty)) throw new Error('Unknown certainty');
    lines.push(`  ${ids.get(edge.from)} -[${edge.kind === 'sameName' ? 'association' : ['candidate','unresolved','unbound','broken'].includes(certainty) ? 'uncertain' : 'observed'}]-> ${ids.get(edge.to)} ${quote(`${edge.label ?? edge.kind ?? 'relation'} [${certainty}]`)} {`,
      `    description ${quote(`${certainty}; ${edge.count ?? edge.facts?.length ?? 0} source facts. Metadata retains the original evidence.`)}`,
      '    metadata {', `      source ${quote(JSON.stringify(canonical({...edge, certainty})))}`, '    }',
      '  }');
  }
  lines.push('}', 'views {', '  view index {', `    title ${quote(graph.title ?? 'Architecture snapshot')}`,
    '    include *', '    autoLayout LeftRight', '  }', '  view vertical {', '    title \"Architecture · vertical\"', '    include *', '    autoLayout TopBottom', '  }', '}', '');
  return lines.join('\n');
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (process.argv.length !== 4) throw new Error('Usage: node scripts/export-graph.mjs graph.json model.c4');
  writeFileSync(process.argv[3], toLikeC4(JSON.parse(readFileSync(process.argv[2], 'utf8'))));
}
