import {configureTracing,downloadTrace,tracePointerDown,tracePointerUp,traceCancel,traceSnapshot} from './routing-trace.ts';
import {RoutingTraceBridge} from './routing-trace-bridge.tsx';
import type {LayoutedView,DiagramNode} from 'likec4/model';
import type {DiagramApi,LikeC4EditorCallbacks} from 'likec4/react';
import type {Group} from './types.ts';
import React, { useEffect, useState, useMemo, useRef } from 'react';
import { createRoot } from 'react-dom/client';
import { useLikeC4Model } from 'likec4:single-project';
import { ReactLikeC4, LikeC4ModelProvider, LikeC4EditorProvider, createLikeC4Editor } from 'likec4/react';
import { LikeC4Model } from 'likec4/model';
import {useRouting} from './use-routing.tsx';
import { animateEdges } from './edge-motion.ts';
import { automaticGroups, applyGroups, saveGroup, validateGroups } from './grouping.ts';
import { GroupControls, SelectionBridge } from './group-controls.tsx';
import {ViewpointMenu,viewpointLabel} from './viewpoint-menu.tsx';
import {DiagramLegend} from './diagram-legend.tsx';
import graph from '../test/all-repositories.graph.json';
import selection from '../test/all-repositories.selection.json';
import source from './model/architecture.c4?raw';
import './style.css';

function OptionalEditor({enabled,editor,children}:{enabled:boolean;editor:LikeC4EditorCallbacks;children:React.ReactNode}) {
  return enabled?<LikeC4EditorProvider editor={editor}>{children}</LikeC4EditorProvider>:children;
}
const embedded=new URLSearchParams(location.search).get('embed')==='1';
function App() {
  const sourceModel = useLikeC4Model();
  const [tracing,setTracing]=useState(()=>new URLSearchParams(location.search).get('routingTrace')==='1');
  useEffect(()=>{configureTracing(tracing);return ()=>configureTracing(false);},[tracing]);
  useEffect(()=>{document.addEventListener('pointerup',tracePointerUp,true);document.addEventListener('pointercancel',traceCancel,true);return ()=>{document.removeEventListener('pointerup',tracePointerUp,true);document.removeEventListener('pointercancel',traceCancel,true);};},[]);
  const [orthogonal,setOrthogonal]=useState(true);
  const [view, setView] = useState('flow');
  const [resetKey, setResetKey] = useState(0);
  const [customView, setCustomView] = useState<LayoutedView|null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [editing, setEditing] = useState<string|null>(null);
  const [draftMembers, setDraftMembers] = useState<string[]>([]);
  const [groupName, setGroupName] = useState('');
  const [error, setError] = useState('');
  const [groups, setGroups] = useState(() => {
    try {
      const saved:unknown=JSON.parse(localStorage.getItem('ce.likec4.groups.v1')??'null');
      if (saved && typeof saved==='object' && 'source' in saved && saved.source===source && 'groups' in saved) return validateGroups(saved.groups,sourceModel.$data.views.flow);
    } catch { /* Invalid or obsolete saved preferences cannot change the source model. */ }
    return automaticGroups(graph);
  });
  const api = useRef<DiagramApi|null>(null);
  const canvas = useRef<HTMLElement|null>(null);
  const editable=view==='flow'||view==='vertical';
  const generatedView=useMemo(()=>editable?applyGroups(sourceModel.$data.views[view],groups):sourceModel.$data.views[view],[sourceModel,view,groups,editable]);
  const layoutView = editable ? (customView ?? generatedView) : generatedView;
  const routing=useRouting(layoutView,orthogonal,resetKey,tracing);
  const currentView=routing.view;
  const model = useMemo(() => LikeC4Model.create({...sourceModel.$data,views:{...sourceModel.$data.views,[view]:currentView},manualLayouts:{}}), [sourceModel,currentView]);
  const editor = useMemo(() => createLikeC4Editor({fetchView: id => model.$data.views[id],handleChange: (id, change) => {if (id === view && change.op === 'save-view-snapshot') {traceSnapshot(change.layout);setCustomView(change.layout);} return {waitForViewSync:false};}}), [model,view]);
  const selectedLeaves=selected.filter(id=>currentView.nodes.some(n=>n.id===id&&n.kind!=='@group'));
  const activeGroup=groups.find(g=>selected.includes(g.id));
  useEffect(()=>{
    try {localStorage.setItem('ce.likec4.groups.v1',JSON.stringify({source,groups}));}
    catch {setError('Browser storage is unavailable; groups will last only for this page.');}
  },[groups]);
  function changeGroups(next:Group[]) {
    setCustomView(applyGroups(currentView,next));setGroups(next);setSelected([]);setEditing(null);setError('');setResetKey(k=>k+1);
  }
  function edit(group?:Group) {
    const ids=group?.members ?? selectedLeaves;
    setEditing(group?.id ?? 'new');setGroupName(group?.title ?? 'My group');setDraftMembers(ids);setError('');
    api.current?.select({nodes:currentView.nodes.filter(node=>ids.includes(node.id)).map(node=>node.id)});
  }
  function save() {
    try {changeGroups(saveGroup(groups,currentView,{id:editing==='new'?undefined:editing??undefined,title:groupName,members:draftMembers}));}
    catch(e){setError(e instanceof Error?e.message:String(e));}
  }
  function switchView(id:string) {setView(id);setCustomView(null);setSelected([]);setEditing(null);setError('');}
  function nodeClick(node:DiagramNode) {
    if(!editing || node.kind==='@group')return;
    const ids=draftMembers.includes(node.id)?draftMembers.filter(id=>id!==node.id):[...draftMembers,node.id];
    setDraftMembers(ids);api.current?.select({nodes:currentView.nodes.filter(node=>ids.includes(node.id)).map(node=>node.id)});
  }
  const [dark, setDark] = useState(()=>embedded?matchMedia('(prefers-color-scheme: dark)').matches:true);
  useEffect(()=>{if(!embedded)return;const query=matchMedia('(prefers-color-scheme: dark)');const update=()=>setDark(query.matches);query.addEventListener('change',update);return ()=>query.removeEventListener('change',update);},[]);
  const [animations, setAnimations] = useState(false);
  const [reduced, setReduced] = useState(() => matchMedia('(prefers-reduced-motion: reduce)').matches);
  const [showSource, setShowSource] = useState(false);
  useEffect(() => {
    const query = matchMedia('(prefers-reduced-motion: reduce)');
    const change = () => { setReduced(query.matches); if (query.matches) setAnimations(false); };
    query.addEventListener('change', change);
    return () => query.removeEventListener('change', change);
  }, []);
  const motion = animations && !reduced;
  useEffect(() => { if (motion && canvas.current) return animateEdges(canvas.current); }, [motion,view,resetKey,currentView.nodes.length]);
  function download() {
    const url = URL.createObjectURL(new Blob([source], { type: 'text/plain;charset=utf-8' }));
    const a = document.createElement('a'); a.href = url; a.download = 'architecture.c4'; a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  return <div className={`spike${embedded?' embedded':''}`} data-theme={dark ? 'dark' : 'light'} data-motion={motion ? 'on' : 'off'}>
    <header><div className="spike-heading"><small>CAPABILITY EXPLORER / EXPERIMENT</small><h1>Architecture · LikeC4</h1></div>
      <div className="actions">
        <span className="current-viewpoint">{viewpointLabel(view,sourceModel.$data.views[view])}</span>
        <button onClick={() => {setCustomView(null);setSelected([]);setEditing(null);setResetKey(k=>k+1);}}>Reset layout</button>
        <button onClick={() => setShowSource(!showSource)} aria-expanded={showSource}>Diagram source</button>
        <button onClick={download}>Download .c4</button>
      </div>
    </header>
    <div className="notice">All {Object.keys(selection).length} loaded repositories · build-time fixture snapshot · no live refresh or runtime flow claims.{reduced && ' Motion disabled by system preference.'}</div>
    <main className={showSource ? 'with-source' : ''}>
      <div className="diagram-workspace">
        <ViewpointMenu views={sourceModel.$data.views} current={view} onSelect={switchView} source={source} coverage={<details><summary>Snapshot coverage · {Object.keys(selection).length} repositories</summary><p>Only analyzed repositories at the revisions below. Missing integrations may be unextracted or unresolved.</p>{Object.entries(selection).map(([id,sha])=><p key={id}><strong>{id}</strong> · {graph.nodes.find(n=>n.id===`repo:${id}`)?.badges?.join(" · ")}<br/><code title={sha}>{sha.slice(0,12)}</code></p>)}{graph.notes.map(note=><p key={note}>{note}</p>)}</details>} settings={{tracing,setTracing,downloadTrace,orthogonal,setOrthogonal,motion,setAnimations,reduced,dark,setDark,embedded}}/>
      <section onPointerDownCapture={event=>{const target=event.nativeEvent.composedPath().find((part):part is Element=>part instanceof Element&&part.matches('.react-flow__node'));if(target){const node=currentView.nodes.find(n=>n.id===target.getAttribute('data-id'));tracePointerDown(node?.children?.length?'group':'node',event.pointerId);}}} ref={canvas} data-routing-generation={routing.generation} data-routing-state={routing.state} data-preview-ms={routing.previewMs} data-refinement-ms={routing.refinementMs} className="canvas" aria-label="LikeC4 architecture diagram">
        <DiagramLegend/>
        {routing.state==='pending'&&<div className="routing-status" role="status">Refining routes… provisional connectors may cross</div>}
        {routing.state==='error'&&<div className="routing-status" role="status">Routing refinement failed; reset layout to retry.</div>}
        <div className="canvas-hint">{editable?'Click a group to manage it · Shift-click nodes to group · edits stay with this snapshot':'Source hierarchy · choose a repository or SQL-resource detail viewpoint to explore its contents'}</div>
        {editable && <GroupControls group={activeGroup} selectedCount={selectedLeaves.length} editing={editing} name={groupName} setName={setGroupName}
          memberCount={draftMembers.length} onCreate={()=>edit()} onRename={()=>edit(activeGroup)} onEdit={()=>edit(activeGroup)}
          onOpen={activeGroup && sourceModel.$data.views[`${activeGroup.id}_detail`] ? ()=>switchView(`${activeGroup.id}_detail`) : null} onSave={save} onCancel={()=>{setEditing(null);setError('');}} onDelete={()=>changeGroups(groups.filter(g=>g.id!==activeGroup?.id))} error={error}/>}
        {error && !editing && <div className="storage-notice" role="status">{error}</div>}
        <div className="diagram-surface"><LikeC4ModelProvider likec4model={model}><OptionalEditor enabled={editable} editor={editor}>
        <ReactLikeC4 key={`${view}-${resetKey}-${currentView.nodes.length}`} onNodeClick={nodeClick} onInitialized={({diagram}) => {api.current=diagram;}} reactFlowProps={{nodesDraggable:editable,elementsSelectable:true,multiSelectionKeyCode:['Shift','Control','Meta']}} injectFontCss={false} viewId={view} onNavigateTo={switchView} pannable zoomable keepAspectRatio={false}
          reduceGraphics={!motion} enableDynamicViewWalkthrough={false}
          controls={!embedded} mantineTheme={embedded?{fontFamily:'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',colors:{dark:['#fafafa','#f4f4f5','#d4d4d8','#a1a1aa','#71717a','#3f3f46','#27272a','#18181b','#09090b','#09090b']}}:undefined} enableElementDetails enableRelationshipDetails showNavigationButtons={!embedded} background="dots" colorScheme={dark ? 'dark' : 'light'}
          style={{ width: '100%', height: '100%' }}>
          <SelectionBridge onSelection={setSelected}/>
          <RoutingTraceBridge view={currentView} context={routing.trace} ready={routing.state==='ready'}/>
          <style>{`
            @keyframes ce-edge-flow { 0%, 100% { filter: drop-shadow(0 0 0px currentColor); opacity: .65; } 50% { filter: drop-shadow(0 0 5px currentColor); opacity: 1; } }
            ${motion ? '.react-flow__edge-path { animation: ce-edge-flow 2.4s ease-in-out infinite; } .react-flow__edge:nth-child(3n) .react-flow__edge-path { animation-delay: -.8s; } .react-flow__edge:nth-child(3n + 1) .react-flow__edge-path { animation-delay: -1.6s; }' : '*, *::before, *::after { animation: none !important; transition: none !important; }'}
            @media (prefers-reduced-motion: reduce) { *, *::before, *::after { animation: none !important; transition: none !important; } }
          `}</style>
        </ReactLikeC4></OptionalEditor></LikeC4ModelProvider></div>
      </section></div>
      {showSource && <aside aria-label="Diagram source"><h2>Reviewable source</h2><p>Labels retain certainty. Browser group edits are saved for this snapshot; this source stays unchanged.</p><pre>{source}</pre></aside>}
    </main>
    <footer>Pan to explore · scroll to zoom · animation is optional decoration, not observed traffic. Source and evidence remain authoritative.</footer>
  </div>;
}
createRoot(document.getElementById('root')!).render(<App />);
