import React, {useEffect,useRef,useState} from 'react';
const defaults=['flow','index','data','objects'];
function preferences(source,views) {
  try {
    const saved=JSON.parse(localStorage.getItem('ce.likec4.viewpoints.v1'));
    return {pinned:saved?.pinned===true,favorites:saved?.source===source&&Array.isArray(saved.favorites)?saved.favorites.filter(id=>Object.hasOwn(views,id)):defaults.filter(id=>Object.hasOwn(views,id))};
  } catch {return {pinned:false,favorites:defaults.filter(id=>Object.hasOwn(views,id))};}
}
export const viewpointLabel=(id,view)=>id==='flow'?'Resource overview':id==='vertical'?'Resource overview · vertical':id==='index'?'Landscape':id==='objects'?'SQL object detail':view.title??id;
export function ViewpointMenu({views,current,onSelect,source}) {
  const [initial]=useState(()=>preferences(source,views));
  const [pinned,setPinned]=useState(initial.pinned), [open,setOpen]=useState(initial.pinned);
  const [favorites,setFavorites]=useState(initial.favorites), [storageError,setStorageError]=useState(false);
  const drawer=useRef(null), trigger=useRef(null);
  useEffect(()=>{try{localStorage.setItem('ce.likec4.viewpoints.v1',JSON.stringify({source,pinned,favorites}));setStorageError(false);}catch{setStorageError(true);}},[source,pinned,favorites]);
  useEffect(()=>{
    if(!open)return;
    const outside=e=>{if(!pinned&&!drawer.current?.contains(e.target)&&!trigger.current?.contains(e.target))setOpen(false);};
    const escape=e=>{if(e.key==='Escape'){setOpen(false);setPinned(false);trigger.current?.focus();}};
    document.addEventListener('pointerdown',outside);document.addEventListener('keydown',escape);
    drawer.current?.querySelector('[aria-current="page"]')?.focus({preventScroll:true});
    return ()=>{document.removeEventListener('pointerdown',outside);document.removeEventListener('keydown',escape);};
  },[open,pinned]);
  const choose=id=>{onSelect(id);if(!pinned){setOpen(false);trigger.current?.focus();}};
  const toggle=id=>setFavorites(old=>old.includes(id)?old.filter(x=>x!==id):[...old,id]);
  const row=id=><div className="viewpoint-row" key={id}>
    <button className="viewpoint-link" aria-current={current===id?'page':undefined} data-view-id={id} onClick={()=>choose(id)}>{viewpointLabel(id,views[id])}</button>
    <button className="favorite-toggle" aria-label={`${favorites.includes(id)?'Remove':'Add'} ${viewpointLabel(id,views[id])} ${favorites.includes(id)?'from':'to'} favorites`} aria-pressed={favorites.includes(id)} onClick={()=>toggle(id)}>{favorites.includes(id)?'★':'☆'}</button>
  </div>;
  const sections=[['Architecture',['index','flow','vertical','applications','data','messaging']],['SQL objects',['objects']],['Repositories',Object.keys(views).filter(id=>id.startsWith('repository_'))],['SQL resources',Object.keys(views).filter(id=>id.startsWith('sql_scope_'))]];
  return <>
    <button ref={trigger} className="viewpoint-launch" aria-expanded={open} aria-controls="viewpoint-drawer" onClick={()=>setOpen(!open)}>☰ Viewpoints</button>
    <nav id="viewpoint-drawer" ref={drawer} className="viewpoint-drawer" data-open={open} aria-label="Viewpoints" aria-hidden={!open} inert={!open}>
      <div className="drawer-heading"><strong>Viewpoints</strong><button aria-pressed={pinned} onClick={()=>setPinned(!pinned)}>{pinned?'Unpin menu':'Pin menu'}</button><button aria-label="Close viewpoints" title="Unpin and close (Escape)" onClick={()=>{setOpen(false);setPinned(false);trigger.current?.focus();}}>×</button></div>
      <section aria-label="Favorite viewpoints"><h2>Favorites</h2>{favorites.length?favorites.map(row):<p>Star a viewpoint below to add it here.</p>}</section>
      {sections.map(([title,ids])=>ids.some(id=>views[id])&&<section key={title} aria-label={`${title} viewpoints`}><h2>{title}</h2>{ids.filter(id=>views[id]).map(row)}</section>)}
      {storageError&&<p role="status">Preferences cannot be saved in this browser.</p>}
    </nav>
  </>;
}
