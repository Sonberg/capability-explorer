import React, { useEffect, useState, useMemo, useRef } from 'react';
import { createRoot } from 'react-dom/client';
import { useLikeC4Model } from 'likec4:single-project';
import { ReactLikeC4, LikeC4ModelProvider, LikeC4EditorProvider, createLikeC4Editor } from 'likec4/react';
import { LikeC4Model } from 'likec4/model';
import { animateEdges } from './edge-motion.mjs';
import { automaticGroups, applyGroups, saveGroup, validateGroups } from './grouping.mjs';
import { GroupControls, SelectionBridge } from './group-controls.jsx';
import {ViewpointMenu,viewpointLabel} from './viewpoint-menu.jsx';
import {DiagramLegend} from './diagram-legend.jsx';
import graph from '../test/weekly-sales.graph.json';
import source from './model/architecture.c4?raw';
import './style.css';

function OptionalEditor({enabled,editor,children}) {
  return enabled?<LikeC4EditorProvider editor={editor}>{children}</LikeC4EditorProvider>:children;
}
function App() {
  const sourceModel = useLikeC4Model();
  const [orthogonal,setOrthogonal]=useState(true);
  const [view, setView] = useState('flow');
  const [resetKey, setResetKey] = useState(0);
  const [customView, setCustomView] = useState(null);
  const [selected, setSelected] = useState([]);
  const [editing, setEditing] = useState(null);
  const [draftMembers, setDraftMembers] = useState([]);
  const [groupName, setGroupName] = useState('');
  const [error, setError] = useState('');
  const [groups, setGroups] = useState(() => {
    try {
      const saved=JSON.parse(localStorage.getItem('ce.likec4.groups.v1'));
      if (saved?.source===source) return validateGroups(saved.groups,sourceModel.$data.views.flow);
    } catch { /* Invalid or obsolete saved preferences cannot change the source model. */ }
    return automaticGroups(graph);
  });
  const api = useRef(null);
  const canvas = useRef(null);
  const editable=view==='flow'||view==='vertical';
  const generatedView=useMemo(()=>editable?applyGroups(sourceModel.$data.views[view],groups):sourceModel.$data.views[view],[sourceModel,view,groups,editable]);
  const layoutView = editable ? (customView ?? generatedView) : generatedView;
  const currentView = useMemo(()=>({...layoutView,routing:orthogonal?'ortho':'spline'}),[layoutView,orthogonal]);
  const model = useMemo(() => LikeC4Model.create({...sourceModel.$data,views:{...sourceModel.$data.views,[view]:currentView},manualLayouts:{}}), [sourceModel,currentView]);
  const editor = useMemo(() => createLikeC4Editor({fetchView: id => model.$data.views[id],handleChange: (id, change) => {if (id === view && change.op === 'save-view-snapshot') setCustomView(change.layout); return {waitForViewSync:false};}}), [model,view]);
  const selectedLeaves=selected.filter(id=>currentView.nodes.some(n=>n.id===id&&n.kind!=='@group'));
  const activeGroup=groups.find(g=>selected.includes(g.id));
  useEffect(()=>{
    try {localStorage.setItem('ce.likec4.groups.v1',JSON.stringify({source,groups}));}
    catch {setError('Browser storage is unavailable; groups will last only for this page.');}
  },[groups]);
  function changeGroups(next) {
    setCustomView(applyGroups(currentView,next));setGroups(next);setSelected([]);setEditing(null);setError('');setResetKey(k=>k+1);
  }
  function edit(group) {
    const ids=group?.members ?? selectedLeaves;
    setEditing(group?.id ?? 'new');setGroupName(group?.title ?? 'My group');setDraftMembers(ids);setError('');
    api.current?.select({nodes:ids});
  }
  function save() {
    try {changeGroups(saveGroup(groups,currentView,{id:editing==='new'?undefined:editing,title:groupName,members:draftMembers}));}
    catch(e){setError(e.message);}
  }
  function switchView(id) {setView(id);setCustomView(null);setSelected([]);setEditing(null);setError('');}
  function nodeClick(node) {
    if(!editing || node.kind==='@group')return;
    const ids=draftMembers.includes(node.id)?draftMembers.filter(id=>id!==node.id):[...draftMembers,node.id];
    setDraftMembers(ids);api.current?.select({nodes:ids});
  }
  const [dark, setDark] = useState(true);
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
  return <div className="spike" data-theme={dark ? 'dark' : 'light'} data-motion={motion ? 'on' : 'off'}>
    <header><div><small>CAPABILITY EXPLORER / EXPERIMENT</small><h1>Architecture · LikeC4</h1></div>
      <div className="actions">
        <span className="current-viewpoint">{viewpointLabel(view,sourceModel.$data.views[view])}</span>
        <button onClick={() => {setCustomView(null);setSelected([]);setEditing(null);setResetKey(k=>k+1);}}>Reset layout</button>
        <button onClick={() => setShowSource(!showSource)} aria-expanded={showSource}>Diagram source</button>
        <button onClick={download}>Download .c4</button>
      </div>
    </header>
    <div className="notice">Experimental renderer · build-time architecture snapshot · no live refresh or runtime flow claims.{reduced && ' Motion disabled by system preference.'}</div>
    <main className={showSource ? 'with-source' : ''}>
      <div className="diagram-workspace">
        <ViewpointMenu views={sourceModel.$data.views} current={view} onSelect={switchView} source={source} settings={{orthogonal,setOrthogonal,motion,setAnimations,reduced,dark,setDark}}/>
      <section ref={canvas} className="canvas" aria-label="LikeC4 architecture diagram">
        <DiagramLegend/>
        <div className="canvas-hint">{editable?'Click a group to manage it · Shift-click nodes to group · edits stay with this snapshot':'Source hierarchy · choose a repository or SQL-resource detail viewpoint to explore its contents'}</div>
        {editable && <GroupControls group={activeGroup} selectedCount={selectedLeaves.length} editing={editing} name={groupName} setName={setGroupName}
          memberCount={draftMembers.length} onCreate={()=>edit(null)} onRename={()=>edit(activeGroup)} onEdit={()=>edit(activeGroup)}
          onOpen={activeGroup && sourceModel.$data.views[`${activeGroup.id}_detail`] ? ()=>switchView(`${activeGroup.id}_detail`) : null} onSave={save} onCancel={()=>{setEditing(null);setError('');}} onDelete={()=>changeGroups(groups.filter(g=>g.id!==activeGroup.id))} error={error}/>}
        {error && !editing && <div className="storage-notice" role="status">{error}</div>}
        <div className="diagram-surface"><LikeC4ModelProvider likec4model={model}><OptionalEditor enabled={editable} editor={editor}>
        <ReactLikeC4 key={`${view}-${resetKey}-${currentView.nodes.length}`} onNodeClick={nodeClick} onInitialized={({diagram}) => {api.current=diagram;}} reactFlowProps={{nodesDraggable:editable,elementsSelectable:true,multiSelectionKeyCode:['Shift','Control','Meta']}} injectFontCss={false} viewId={view} onNavigateTo={switchView} pannable zoomable keepAspectRatio={false}
          reduceGraphics={!motion} enableDynamicViewWalkthrough={false}
          enableElementDetails enableRelationshipDetails showNavigationButtons showDiagramTitle background="dots" colorScheme={dark ? 'dark' : 'light'}
          style={{ width: '100%', height: '100%' }}>
          <SelectionBridge onSelection={setSelected}/>
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
createRoot(document.getElementById('root')).render(<App />);
