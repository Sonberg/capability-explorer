import {sqlResources} from './sql-resources.mjs';
// Groups affect presentation only. Evidence records and relationships stay intact.
export function modelIdentities(graph) {
  const nodes=[...graph.nodes].sort((a,b)=>a.id<b.id?-1:a.id>b.id?1:0);
  const scopes=sqlResources(graph);
  const owners=[...new Set(nodes.map(n=>n.repositoryId).filter(id=>typeof id==='string'&&id.trim()))].sort();
  return new Map(nodes.map((n,i)=>{const scope=scopes.find(s=>s.nodeIds.includes(n.id));return [n.id,scope?`${scope.id}.n${i}`:owners.includes(n.repositoryId)?`repository_${owners.indexOf(n.repositoryId)}.n${i}`:`n${i}`];}));
}
export function automaticGroups(graph) {
  const identities=modelIdentities(graph), owners=new Map();
  for(const node of [...graph.nodes].sort((a,b)=>a.id<b.id?-1:a.id>b.id?1:0)) {
    if(typeof node.repositoryId!=='string'||!node.repositoryId.trim()||identities.get(node.id).startsWith('sql_scope_'))continue;
    const members=owners.get(node.repositoryId)??[];members.push(identities.get(node.id));owners.set(node.repositoryId,members);
  }
  const repositoryNames=[...new Set(graph.nodes.map(n=>n.repositoryId).filter(id=>typeof id==='string'&&id.trim()))].sort();
  return [...owners].sort(([a],[b])=>a<b?-1:a>b?1:0)
    .map(([owner,members],i)=>({id:`repository_${repositoryNames.indexOf(owner)}`,title:owner.slice(0,80),members,origin:'repository',owner}));
}
const isContainer = n => n.kind==='@group'||n.kind==='repository';
export function validateGroups(groups, view) {
  if (!Array.isArray(groups)) throw new Error('Invalid saved groups');
  const leaves=new Set(view.nodes.filter(n=>!isContainer(n)&&!n.children?.length).map(n=>n.id));
  const occupied=new Set(), ids=new Set(view.nodes.filter(n=>!isContainer(n)).map(n=>n.id));
  for (const group of groups) {
    if (!group || typeof group.id!=='string' || !/^(presentation_group|repository)_[0-9]+$/.test(group.id) || ids.has(group.id)
      || typeof group.title!=='string' || !group.title.trim() || group.title.length>80
      || !Array.isArray(group.members) || !group.members.length) throw new Error('Invalid group');
    ids.add(group.id);
    for (const id of group.members) {
      if (!leaves.has(id) || occupied.has(id)) throw new Error('Invalid or overlapping membership');
      occupied.add(id);
    }
  }
  return groups;
}
export function applyGroups(view, groups) {
  validateGroups(groups,view);
  if(view.nodes.some(n=>!isContainer(n)&&n.children?.length))throw new Error('This spike supports flat source elements only');
  const nodes=view.nodes.filter(n=>!isContainer(n)).map(n=>({...n,parent:null,level:0}));
  const compounds=[];
  for (const group of groups) {
    const members=nodes.filter(n=>group.members.includes(n.id));
    const x=Math.min(...members.map(n=>n.x))-32, y=Math.min(...members.map(n=>n.y))-64;
    const width=Math.max(...members.map(n=>n.x+n.width))-x+32;
    const height=Math.max(...members.map(n=>n.y+n.height))-y+32;
    compounds.push({id:group.id,kind:'@group',title:group.title,parent:null,children:group.members,
      x,y,width,height,labelBBox:{x:x+16,y:y+12,width:width-32,height:32},level:0,
      shape:'rectangle',color:'slate',style:{opacity:12,border:'dashed'},tags:[],inEdges:[],outEdges:[]});
    for(const node of members){node.parent=group.id;node.level=1;}
  }
  const all=[...compounds,...nodes];
  if(!all.length)return {...view,nodes:[]};
  const x=Math.min(view.bounds.x,...all.map(n=>n.x)), y=Math.min(view.bounds.y,...all.map(n=>n.y));
  const bounds={x,y,width:Math.max(view.bounds.x+view.bounds.width,...all.map(n=>n.x+n.width))-x,height:Math.max(view.bounds.y+view.bounds.height,...all.map(n=>n.y+n.height))-y};
  const parents=new Map(all.map(n=>[n.id,n.parent]));
  const ancestors=id=>{const path=[];let parent=parents.get(id);while(parent){path.push(parent);parent=parents.get(parent);}return path;};
  const updated=view.edges.map(edge=>{
    const targetParents=new Set(ancestors(edge.target));
    const parent=ancestors(edge.source).find(id=>targetParents.has(id))??null;
    return (edge.parent??null)===parent?edge:{...edge,parent};
  });
  const edges=updated.every((edge,i)=>edge===view.edges[i])?view.edges:updated;
  return {...view,bounds,nodes:all,edges};
}
export function saveGroup(groups, view, {id,title,members}) {
  if (typeof title!=='string'||!title.trim()) throw new Error('Enter a group name');
  if (!Array.isArray(members)||new Set(members).size<(id?1:2)) throw new Error('Select at least two nodes');
  const known=new Set(groups.map(g=>g.id));
  if (id && !known.has(id)) throw new Error('Unknown group');
  if (!id) {let index=1;while(known.has(`presentation_group_${index}`))index++;id=`presentation_group_${index}`;}
  // Moving a node into this group removes it from its former presentation group.
  const next=groups.filter(g=>g.id!==id).map(g=>({...g,members:g.members.filter(n=>!members.includes(n))})).filter(g=>g.members.length);
  next.push({id,title:title.trim(),members:[...new Set(members)],origin:'user'});
  return validateGroups(next,view);
}
// Compatibility helper for the deterministic group geometry test.
export function groupView(view, memberIds, title) {
  if(memberIds.some(id=>view.nodes.find(n=>n.id===id)?.parent))throw new Error('Select ungrouped nodes');
  return applyGroups(view,saveGroup([],view,{title,members:memberIds}));
}
