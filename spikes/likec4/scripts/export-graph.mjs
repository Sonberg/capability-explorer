import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { ledgerPalette, ledgerToken } from '../src/ledger-palette.mjs';
import { sqlResources, sqlObjectDescription } from '../src/sql-resources.mjs';
import { automaticGroups, modelIdentities } from '../src/grouping.mjs';

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
  const ids = modelIdentities(graph);
  const groups = automaticGroups(graph);
  const scopes = sqlResources(graph);
  const lines = ['// Architecture API snapshot; certainty is not upgraded by visualization.',
    'specification {', ...ledgerPalette.map(item=>`  color ${ledgerToken(item.key)} ${item.color}`), '  element repository { style { color slate opacity 12% } }', '  element artifact', '  element service { style { color ledgerApplication } }', '  element application { style { color ledgerApplication } }', '  element datastore { notation \"Data reference\" style { color ledgerItComponent } }', '  element sqlObject { notation \"SQL object\" style { color ledgerDataObject shape rectangle } }', '  element database { notation \"Concrete database reference\" style { color ledgerItComponent shape storage } }', '  element databaseReference { notation \"Unresolved database reference\" style { color ledgerItComponent shape component border dashed opacity 12% } }', '  element sqlModel { notation \"SQL source model\" style { color ledgerItComponent shape component opacity 12% } }', '  element channel { style { color ledgerInterface } }', '  relationship observed { color ledgerInterface line solid }', '  relationship association { color ledgerInterface line dashed head none }', '  relationship uncertain { color ledgerInterface line dashed }', '}', 'model {'];
  function writeNode(node, indent) {
    lines.push(`${indent}${ids.get(node.id).split('.').at(-1)} = ${node.category==='datastore' && node.symbolKey?.startsWith('sql|') ? 'sqlObject' : ['service','application','datastore','channel'].includes(node.category) ? node.category : 'artifact'} ${quote(node.label)} {`,
      `${indent}  description ${quote((node.category==='datastore' && node.symbolKey?.startsWith('sql|') ? sqlObjectDescription(node,graph)+' · ' : '') + (node.sublabel ?? node.category ?? 'Architecture artifact'))}`,
      `${indent}  metadata {`, `${indent}    source ${quote(JSON.stringify(canonical(node)))}`, `${indent}  }`, `${indent}}`);
  }
  for(const group of groups) {
    lines.push(`  ${group.id} = repository ${quote(group.title)} {`, `    description ${quote('Explicit repository membership: '+group.owner)}`);
    for(const node of nodes.filter(n=>ids.get(n.id).startsWith(group.id+'.')))writeNode(node,'    ');
    lines.push('  }');
  }
  for(const scope of scopes) {
    lines.push(`  ${scope.id} = ${scope.kind} ${quote(scope.title)} {`, `    description ${quote(scope.description)}`, '    metadata {', `      source ${quote(JSON.stringify(canonical(scope)))}`, '    }');
    for(const node of nodes.filter(n=>scope.nodeIds.includes(n.id)))writeNode(node,'    ');
    lines.push('  }');
  }
  for(const node of nodes.filter(n=>!ids.get(n.id).includes('.')))writeNode(node,'  ');
  for (const edge of edges) {
    if (!ids.has(edge.from) || !ids.has(edge.to)) throw new Error('Dangling edge endpoint');
    const certainty = edge.certainty ?? 'unresolved';
    if (!['proven','static','bound','broken','unbound','candidate','unresolved'].includes(certainty)) throw new Error('Unknown certainty');
    lines.push(`  ${ids.get(edge.from)} -[${edge.kind === 'sameName' ? 'association' : ['candidate','unresolved','unbound','broken'].includes(certainty) ? 'uncertain' : 'observed'}]-> ${ids.get(edge.to)} ${quote(`${edge.label ?? edge.kind ?? 'relation'} [${certainty}]`)} {`,
      `    description ${quote(`${certainty}; ${edge.count ?? edge.facts?.length ?? 0} source facts. Metadata retains the original evidence.`)}`,
      '    metadata {', `      source ${quote(JSON.stringify(canonical({...edge, certainty})))}`, '    }',
      '  }');
  }
  lines.push('}', 'views {', '  view index {', `    title ${quote((graph.title ?? 'Architecture')+' · landscape')}`, '    include *', '    autoLayout LeftRight', '  }');
  for (const [id,direction] of [['flow','LeftRight'],['vertical','TopBottom'],['objects','LeftRight']]) {
    lines.push(`  view ${id} {`, `    title ${quote((graph.title ?? 'Architecture')+(id==='objects'?' · SQL object detail':' · resource overview'))}`, '    include *');
    for (const group of groups) lines.push(`    include ${group.id}.*`);
    if(id==='objects')for(const scope of scopes)lines.push(`    include ${scope.id}.*`);
    lines.push(`    autoLayout ${direction}`, '  }');
  }
  for(const [name,title,categories,neighbors] of [
    ['applications','Application landscape',['service','application'],false],
    ['data','Data dependencies',['datastore'],true],
    ['messaging','Messaging dependencies',['channel'],true],
  ]) {
    const selected=new Set(nodes.filter(n=>categories.includes(n.category)).map(n=>n.id));
    if(!selected.size)continue;
    if(neighbors)for(const edge of edges)if(selected.has(edge.from)||selected.has(edge.to)) {
      // Only direct neighbors of the selected category, never transitive inference.
      if(nodes.some(n=>n.id===edge.from&&categories.includes(n.category)))selected.add(edge.to);
      if(nodes.some(n=>n.id===edge.to&&categories.includes(n.category)))selected.add(edge.from);
    }
    lines.push(`  view ${name} {`, `    title ${quote(title)}`, `    include ${[...new Set(nodes.filter(n=>selected.has(n.id)).map(n=>{const scope=scopes.find(s=>s.nodeIds.includes(n.id));return scope?scope.id:ids.get(n.id);} ))].join(', ')}`, '    autoLayout LeftRight', '  }');
  }
  for(const group of groups)lines.push(`  view ${group.id}_detail of ${group.id} {`, `    title ${quote(group.title+' · repository')}`, '    include *', '  }');
  for(const scope of scopes)lines.push(`  view ${scope.id}_detail of ${scope.id} {`, `    title ${quote(scope.title+' · SQL objects')}`, '    include *', '  }');
  lines.push('}', '');
  return lines.join('\n');
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (process.argv.length !== 4) throw new Error('Usage: node scripts/export-graph.mjs graph.json model.c4');
  writeFileSync(process.argv[3], toLikeC4(JSON.parse(readFileSync(process.argv[2], 'utf8'))));
}
