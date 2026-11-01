import type {DiagramNode, LayoutedView, NodeId} from 'likec4/model';
import {validateGroups} from './grouping.ts';
import type {Group} from './types.ts';

/** Presentation groups may change parents; source SQL hierarchy remains in the model. */
export function applyLiveGroups(view:LayoutedView, groups:Group[]):LayoutedView {
  const validated = validateGroups(groups, view);
  const presentation = new Set(view.nodes.filter(node => node.kind === 'repository' || node.kind === '@group').map(node => node.id));
  const nodes:DiagramNode[] = view.nodes.filter(node => !presentation.has(node.id)).map(node => ({
    ...node, parent:node.parent && !presentation.has(node.parent) ? node.parent : null,
    children:[...node.children],
  }));
  for (const group of validated) {
    const members = nodes.filter(node => group.members.includes(node.id));
    const x = Math.min(...members.map(node => node.x)) - 32;
    const y = Math.min(...members.map(node => node.y)) - 64;
    const width = Math.max(...members.map(node => node.x + node.width)) - x + 32;
    const height = Math.max(...members.map(node => node.y + node.height)) - y + 32;
    // validateGroups restricts synthetic IDs and checks original membership.
    const id = group.id as NodeId;
    nodes.push({id, kind:'@group', title:group.title, parent:null, children:members.map(node => node.id),
      x, y, width, height, labelBBox:{x:x+16, y:y+12, width:width-32, height:32}, level:0,
      shape:'rectangle', color:'slate', style:{opacity:12, border:'dashed'}, tags:[], inEdges:[], outEdges:[]});
    for (const member of members) member.parent = id;
  }
  const byId = new Map(nodes.map(node => [node.id, node]));
  const ancestors = (id:NodeId) => {
    const path:NodeId[] = []; let parent = byId.get(id)?.parent;
    while (parent && byId.has(parent)) {path.push(parent); parent = byId.get(parent)?.parent;}
    return path;
  };
  for (const node of nodes) {
    node.children = nodes.filter(child => child.parent === node.id).map(child => child.id);
    node.level = ancestors(node.id).length;
  }
  const edges = view.edges.map(edge => {
    const targets = new Set(ancestors(edge.target));
    return {...edge, parent:ancestors(edge.source).find(id => targets.has(id)) ?? null};
  });
  if (!nodes.length) return {...view, nodes, edges};
  const x = Math.min(view.bounds.x, ...nodes.map(node => node.x));
  const y = Math.min(view.bounds.y, ...nodes.map(node => node.y));
  const bounds = {x, y, width:Math.max(view.bounds.x+view.bounds.width, ...nodes.map(node => node.x+node.width))-x,
    height:Math.max(view.bounds.y+view.bounds.height, ...nodes.map(node => node.y+node.height))-y};
  return {...view, nodes, edges, bounds};
}
