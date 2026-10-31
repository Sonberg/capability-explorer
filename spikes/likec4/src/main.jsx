import React, { useEffect, useState, useMemo, useRef } from 'react';
import { createRoot } from 'react-dom/client';
import { useLikeC4Model } from 'likec4:single-project';
import { ReactLikeC4, LikeC4ModelProvider, LikeC4EditorProvider, createLikeC4Editor } from 'likec4/react';
import { LikeC4Model } from 'likec4/model';
import { animateEdges } from './edge-motion.mjs';
import { groupView } from './grouping.mjs';
import source from './model/architecture.c4?raw';
import './style.css';

function App() {
  const sourceModel = useLikeC4Model();
  const [view, setView] = useState('index');
  const [resetKey, setResetKey] = useState(0);
  const [customView, setCustomView] = useState(null);
  const [members, setMembers] = useState([]);
  const [groupName, setGroupName] = useState('My group');
  const [showGroups, setShowGroups] = useState(false);
  const api = useRef(null);
  const canvas = useRef(null);
  const currentView = customView ?? sourceModel.$data.views[view];
  const model = useMemo(() => LikeC4Model.create({...sourceModel.$data,views:{...sourceModel.$data.views,[view]:currentView},manualLayouts:{}}), [sourceModel,currentView]);
  const editor = useMemo(() => createLikeC4Editor({fetchView: id => model.$data.views[id],handleChange: (id, change) => {if (id === view && change.op === 'save-view-snapshot') setCustomView(change.layout); return {waitForViewSync:false};}}), [model,view]);
  function createGroup() {
    setCustomView(groupView(currentView,members,groupName)); setMembers([]);
  }
  const [dark, setDark] = useState(() => matchMedia('(prefers-color-scheme: dark)').matches);
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
        <label>Arrangement <select value={view} onChange={e=>{setView(e.target.value);setCustomView(null);setMembers([]);}}><option value="index">Horizontal</option><option value="vertical">Vertical</option></select></label>
        <button aria-pressed={dark} onClick={() => setDark(!dark)}>{dark ? 'Light mode' : 'Dark mode'}</button>
        <label><input type="checkbox" checked={motion} disabled={reduced} onChange={e => setAnimations(e.target.checked)} /> Animation {motion ? 'on' : 'off'}</label>
        <button onClick={() => setShowGroups(!showGroups)} aria-expanded={showGroups}>Groups & layout</button>
        <button onClick={() => {setCustomView(null);setMembers([]);setResetKey(k=>k+1);}}>Reset layout</button>
        <button onClick={() => setShowSource(!showSource)} aria-expanded={showSource}>Diagram source</button>
        <button onClick={download}>Download .c4</button>
      </div>
    </header>
    <div className="notice">Experimental renderer · build-time architecture snapshot · no live refresh or runtime flow claims.{reduced && ' Motion disabled by system preference.'}</div>
    {showGroups && <section className="group-tools" aria-label="Grouping controls">
      <div><strong>Presentation group</strong><p>Choose ungrouped nodes. Drag the resulting group header to move its members together. Grouping and layout are temporary; source downloads keep the original snapshot.</p></div>
      <div className="member-list">{currentView.nodes.filter(n => !n.parent && n.kind !== '@group' && !n.children?.length).map(n => <label key={n.id}><input type="checkbox" checked={members.includes(n.id)} onChange={e => setMembers(e.target.checked ? [...members,n.id] : members.filter(id=>id!==n.id))}/>{n.title}</label>)}</div>
      <label>Group name <input value={groupName} maxLength={80} onChange={e=>setGroupName(e.target.value)}/></label>
      <button disabled={members.length<2} onClick={createGroup}>Create group</button>
      <button disabled={members.length<2} onClick={()=>{api.current?.select({nodes:members});api.current?.align('Top');}}>Align selected</button>
    </section>}
    <main className={showSource ? 'with-source' : ''}>
      <section ref={canvas} className="canvas" aria-label="LikeC4 architecture diagram">
        <LikeC4ModelProvider likec4model={model}><LikeC4EditorProvider editor={editor}>
        <ReactLikeC4 key={`${view}-${resetKey}-${currentView.nodes.length}`} onInitialized={({diagram}) => {api.current=diagram;}} reactFlowProps={{nodesDraggable:true,elementsSelectable:true}} injectFontCss={false} viewId={view} onNavigateTo={setView} pannable zoomable keepAspectRatio={false}
          reduceGraphics={!motion} enableDynamicViewWalkthrough={false}
          enableElementDetails enableRelationshipDetails showNavigationButtons showDiagramTitle background="dots" colorScheme={dark ? 'dark' : 'light'}
          style={{ width: '100%', height: '100%' }}>
          <style>{`
            @keyframes ce-edge-flow { 0%, 100% { filter: drop-shadow(0 0 0px currentColor); opacity: .65; } 50% { filter: drop-shadow(0 0 5px currentColor); opacity: 1; } }
            ${motion ? '.react-flow__edge-path { animation: ce-edge-flow 2.4s ease-in-out infinite; } .react-flow__edge:nth-child(3n) .react-flow__edge-path { animation-delay: -.8s; } .react-flow__edge:nth-child(3n + 1) .react-flow__edge-path { animation-delay: -1.6s; }' : '*, *::before, *::after { animation: none !important; transition: none !important; }'}
            @media (prefers-reduced-motion: reduce) { *, *::before, *::after { animation: none !important; transition: none !important; } }
          `}</style>
        </ReactLikeC4></LikeC4EditorProvider></LikeC4ModelProvider>
      </section>
      {showSource && <aside aria-label="Diagram source"><h2>Reviewable source</h2><p>Labels retain certainty. Diagram layout does not add evidence.</p><pre>{source}</pre></aside>}
    </main>
    <footer>Pan to explore · scroll to zoom · animation is optional decoration, not observed traffic. Source and evidence remain authoritative.</footer>
  </div>;
}
createRoot(document.getElementById('root')).render(<App />);
