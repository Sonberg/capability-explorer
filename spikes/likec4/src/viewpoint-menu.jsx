import React, {useEffect,useState} from 'react';
const defaults=['flow','index','data','objects'];
function preferences(source,views) {
  try {
    const saved=JSON.parse(localStorage.getItem('ce.likec4.viewpoints.v1'));
    return {favorites:saved?.source===source&&Array.isArray(saved.favorites)?saved.favorites.filter(id=>Object.hasOwn(views,id)):defaults.filter(id=>Object.hasOwn(views,id))};
  } catch {return {favorites:defaults.filter(id=>Object.hasOwn(views,id))};}
}
export const viewpointLabel=(id,view)=>id==='flow'?'Resource overview':id==='vertical'?'Resource overview · vertical':id==='index'?'Landscape':id==='objects'?'SQL object detail':view.title??id;
export function ViewpointMenu({views,current,onSelect,source,settings}) {
  const [initial]=useState(()=>preferences(source,views));
  const [favorites,setFavorites]=useState(initial.favorites), [storageError,setStorageError]=useState(false);
  useEffect(()=>{try{localStorage.setItem('ce.likec4.viewpoints.v1',JSON.stringify({source,favorites}));setStorageError(false);}catch{setStorageError(true);}},[source,favorites]);
  const choose=id=>onSelect(id);
  const toggle=id=>setFavorites(old=>old.includes(id)?old.filter(x=>x!==id):[...old,id]);
  const row=id=><div className="viewpoint-row" key={id}>
    <button className="viewpoint-link" aria-current={current===id?'page':undefined} data-view-id={id} onClick={()=>choose(id)}>{viewpointLabel(id,views[id])}</button>
    <button className="favorite-toggle" aria-label={`${favorites.includes(id)?'Remove':'Add'} ${viewpointLabel(id,views[id])} ${favorites.includes(id)?'from':'to'} favorites`} aria-pressed={favorites.includes(id)} onClick={()=>toggle(id)}>{favorites.includes(id)?'★':'☆'}</button>
  </div>;
  const sections=[['Architecture',['index','flow','vertical','applications','data','messaging']],['SQL objects',['objects']],['Repositories',Object.keys(views).filter(id=>id.startsWith('repository_'))],['SQL resources',Object.keys(views).filter(id=>id.startsWith('sql_scope_'))]];
  return <nav id="viewpoint-drawer" className="viewpoint-drawer" data-open="true" aria-label="Viewpoints">
      <div className="drawer-heading"><strong>Viewpoints</strong></div>
      <div className="viewpoint-list"><section aria-label="Favorite viewpoints"><h2>Favorites</h2>{favorites.length?favorites.map(row):<p>Star a viewpoint below to add it here.</p>}</section>
      {sections.map(([title,ids])=>ids.some(id=>views[id])&&<section key={title} aria-label={`${title} viewpoints`}><h2>{title}</h2>{ids.filter(id=>views[id]).map(row)}</section>)}
      {storageError&&<p role="status">Preferences cannot be saved in this browser.</p>}
      </div>
      <section className="diagram-settings" aria-label="Settings"><h2>Settings</h2>
        <label><span>Orthogonal</span><input type="checkbox" role="switch" checked={settings.orthogonal} onChange={e=>settings.setOrthogonal(e.target.checked)}/></label>
        <label><span>Animation</span><input type="checkbox" role="switch" checked={settings.motion} disabled={settings.reduced} onChange={e=>settings.setAnimations(e.target.checked)}/></label>
        <label><span>Dark mode</span><input type="checkbox" role="switch" checked={settings.dark} onChange={e=>settings.setDark(e.target.checked)}/></label>
        {settings.reduced&&<p>Animation disabled by reduced-motion preference.</p>}
      </section>
    </nav>;
}
