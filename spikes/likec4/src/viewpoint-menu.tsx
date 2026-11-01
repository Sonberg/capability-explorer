import React, {useEffect,useState} from 'react';
type Views=Record<string,{title?:string|null}>;
interface Settings {tracing:boolean;setTracing:(value:boolean)=>void;downloadTrace:()=>void;orthogonal:boolean;setOrthogonal:(value:boolean)=>void;motion:boolean;setAnimations:(value:boolean)=>void;reduced:boolean;dark:boolean;setDark:(value:boolean)=>void;embedded:boolean}
const defaults=['flow','index','data','objects'];
function preferences(source:string,views:Views) {
  try {
    const saved:unknown=JSON.parse(localStorage.getItem('ce.likec4.viewpoints.v1')??'null');
    return {favorites:saved && typeof saved==='object' && 'source' in saved && saved.source===source && 'favorites' in saved && Array.isArray(saved.favorites)?saved.favorites.filter((id:unknown):id is string=>typeof id==='string'&&Object.hasOwn(views,id)):defaults.filter(id=>Object.hasOwn(views,id))};
  } catch {return {favorites:defaults.filter(id=>Object.hasOwn(views,id))};}
}
export const viewpointLabel=(id:string,view:{title?:string|null})=>id==='flow'?'All repositories · integrations':id==='vertical'?'All repositories · vertical':id==='index'?'Landscape':id==='objects'?'SQL object detail':view.title??id;
export function ViewpointMenu({views,current,onSelect,source,settings,coverage,labels}:{labels?:Readonly<Record<string,string>>;views:Views;current:string;onSelect:(id:string)=>void;source:string;settings:Settings;coverage:React.ReactNode}) {
  const [initial]=useState(()=>preferences(source,views));
  const [favorites,setFavorites]=useState<string[]>(initial.favorites), [storageError,setStorageError]=useState(false);
  useEffect(()=>{try{localStorage.setItem('ce.likec4.viewpoints.v1',JSON.stringify({source,favorites}));setStorageError(false);}catch{setStorageError(true);}},[source,favorites]);
  const choose=(id:string)=>onSelect(id);
  const toggle=(id:string)=>setFavorites(old=>old.includes(id)?old.filter(x=>x!==id):[...old,id]);
  const label=(id:string)=>labels?.[id] ?? viewpointLabel(id,views[id]);
  const row=(id:string)=><div className="viewpoint-row" key={id}>
    <button className="viewpoint-link" aria-current={current===id?'page':undefined} data-view-id={id} onClick={()=>choose(id)}>{label(id)}</button>
    <button className="favorite-toggle" aria-label={`${favorites.includes(id)?'Remove':'Add'} ${label(id)} ${favorites.includes(id)?'from':'to'} favorites`} aria-pressed={favorites.includes(id)} onClick={()=>toggle(id)}>{favorites.includes(id)?'★':'☆'}</button>
  </div>;
  const sections:[string,string[]][]=[['Architecture',['index','flow','vertical','applications','data','messaging']],['SQL objects',['objects']],['Repositories',Object.keys(views).filter(id=>id.startsWith('repository_'))],['SQL resources',Object.keys(views).filter(id=>id.startsWith('sql_scope_'))]];
  return <nav id="viewpoint-drawer" className="viewpoint-drawer" data-open="true" aria-label="Viewpoints">
      <div className="drawer-heading"><strong>Viewpoints</strong></div>
      <div className="viewpoint-list">{coverage}<section aria-label="Favorite viewpoints"><h2>Favorites</h2>{favorites.length?favorites.map(row):<p>Star a viewpoint below to add it here.</p>}</section>
      {sections.map(([title,ids])=>ids.some(id=>views[id])&&<section key={title} aria-label={`${title} viewpoints`}><h2>{title}</h2>{ids.filter(id=>views[id]).map(row)}</section>)}
      {storageError&&<p role="status">Preferences cannot be saved in this browser.</p>}
      </div>
      <section className="diagram-settings" aria-label="Settings"><h2>Settings</h2>
        <label><span>Orthogonal</span><input type="checkbox" role="switch" checked={settings.orthogonal} onChange={e=>settings.setOrthogonal(e.target.checked)}/></label>
        <label><span>Animation</span><input type="checkbox" role="switch" checked={settings.motion} disabled={settings.reduced} onChange={e=>settings.setAnimations(e.target.checked)}/></label>
        <label><span>{settings.embedded?'Site theme · dark':'Dark mode'}</span><input type="checkbox" role="switch" checked={settings.dark} disabled={settings.embedded} onChange={e=>settings.setDark(e.target.checked)}/></label>
        <label><span>Routing diagnostics</span><input type="checkbox" role="switch" checked={settings.tracing} onChange={e=>settings.setTracing(e.target.checked)}/></label>
        {settings.tracing&&<button onClick={settings.downloadTrace}>Download routing trace</button>}
        {settings.reduced&&<p>Animation disabled by reduced-motion preference.</p>}
      </section>
    </nav>;
}
