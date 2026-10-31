// Presentation-only groups: preserve model references and all evidence edges.
export function groupView(view, memberIds, title) {
  const members = view.nodes.filter(n => memberIds.includes(n.id));
  if (members.length < 2 || members.some(n => n.parent || n.kind === '@group' || n.children?.length)) throw new Error('Select at least two ungrouped nodes');
  const ids = new Set(view.nodes.map(n => n.id));
  let id = 'presentation_group_1';
  for (let i = 2; ids.has(id); i++) id = `presentation_group_${i}`;
  const x = Math.min(...members.map(n => n.x)) - 32;
  const y = Math.min(...members.map(n => n.y)) - 64;
  const width = Math.max(...members.map(n => n.x + n.width)) - x + 32;
  const height = Math.max(...members.map(n => n.y + n.height)) - y + 32;
  const group = {id,kind:'@group',title:title.trim() || 'Presentation group',parent:null,children:members.map(n=>n.id),
    x,y,width,height,labelBBox:{x:x+16,y:y+12,width:width-32,height:32},level:0,
    shape:'rectangle',color:'blue',style:{},tags:[],inEdges:[],outEdges:[]};
  const minX=Math.min(view.bounds.x,x), minY=Math.min(view.bounds.y,y);
  const bounds={x:minX,y:minY,width:Math.max(view.bounds.x+view.bounds.width,x+width)-minX,height:Math.max(view.bounds.y+view.bounds.height,y+height)-minY};
  return {...view,bounds,nodes:[group,...view.nodes.map(n=>memberIds.includes(n.id)?{...n,parent:id,level:1}:n)]};
}
