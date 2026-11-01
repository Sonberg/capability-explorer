import React, {useEffect, useMemo, useRef, useState} from 'react';
import {LikeC4Model} from 'likec4/model';
import type {DiagramNode, LayoutedView} from 'likec4/model';
import {createLikeC4Editor, LikeC4EditorProvider, LikeC4ModelProvider, ReactLikeC4} from 'likec4/react';
import type {DiagramApi} from 'likec4/react';
import type {LiveEdge, LiveGraph, LiveModelResult, LiveNode} from './live-contracts.ts';
import {useLiveModel} from './live-use-model.ts';
import {applyLiveGroups} from './live-canvas-groups.ts';
import {LiveSourceBridge} from './live-canvas-bridge.tsx';
import {attachImmediateLayout, geometryKey} from './immediate-layout.ts';
import {useRouting} from './use-routing.tsx';
import {animateEdges} from './edge-motion.ts';
import {saveGroup, validateGroups} from './grouping.ts';
import {GroupControls, SelectionBridge} from './group-controls.tsx';
import {ViewpointMenu, viewpointLabel} from './viewpoint-menu.tsx';
import {DiagramLegend} from './diagram-legend.tsx';
import {RoutingTraceBridge} from './routing-trace-bridge.tsx';
import {cancelTraceInteraction, configureTracing, downloadTrace, traceCancel, tracePointerDown, tracePointerUp, traceSnapshot} from './routing-trace.ts';
import type {Group} from './types.ts';
import './live-canvas.css';

export interface LiveArchitectureCanvasProps<N extends LiveNode=LiveNode, E extends LiveEdge=LiveEdge> {
  graph:Omit<LiveGraph,'nodes'|'edges'> & {readonly nodes:readonly N[]; readonly edges:readonly E[]};
  snapshotKey:string;
  associationIds?:ReadonlySet<string>;
  hiddenEdgeIds?:ReadonlySet<string>;
  selectedId?:string;
  onNodeSelect:(node:N)=>void;
  onEdgeSelect:(edge:E)=>void;
  onNodeDrill?:(node:N)=>void;
  onClearSelection?:()=>void;
}

export function LiveArchitectureCanvas<N extends LiveNode, E extends LiveEdge>(props:LiveArchitectureCanvasProps<N,E>) {
  const compiled = useLiveModel(props.graph, props.snapshotKey, props.associationIds);
  const label = 'level' in props.graph && props.graph.level === 'glossary-flow' ? 'Glossary connection canvas' : 'Architecture canvas';
  return <div className="live-architecture-canvas" aria-label={label} data-input-identity={props.snapshotKey}
    data-generation={compiled.generation} data-status={compiled.error ? 'error' : compiled.result ? 'ready' : 'loading'}>
    {compiled.error ? <div className="live-model-status" role="alert"><p>{compiled.error}</p><button onClick={compiled.retry}>Retry diagram</button></div>
      : compiled.result ? <ReadyCanvas key={compiled.identity} {...props} result={compiled.result}/>
      : <div className="live-model-status" role="status">Compiling selected architecture snapshot…</div>}
  </div>;
}

function savedGroups(result:LiveModelResult) {
  try {
    const saved:unknown = JSON.parse(localStorage.getItem('ce.live-likec4.groups.v1') ?? 'null');
    if (saved && typeof saved === 'object' && 'source' in saved && 'groups' in saved) {
      if (saved.source === result.source) return {groups:validateGroups(saved.groups, result.modelData.views.flow), notice:''};
      return {groups:result.groups, notice:'Presentation groups reset for the newly selected snapshot.'};
    }
  } catch {return {groups:result.groups, notice:'Saved groups could not be restored; identified source groups are shown.'};}
  return {groups:result.groups, notice:''};
}

function ReadyCanvas<N extends LiveNode, E extends LiveEdge>({graph, snapshotKey, selectedId, hiddenEdgeIds, onNodeSelect, onEdgeSelect, onNodeDrill, onClearSelection, result}:LiveArchitectureCanvasProps<N,E> & {result:LiveModelResult}) {
  const sourceModel = useMemo(() => LikeC4Model.create(result.modelData), [result]);
  const [initial] = useState(() => savedGroups(result));
  const [groups, setGroups] = useState<Group[]>(initial.groups);
  const [error, setError] = useState(initial.notice);
  const [view, setView] = useState('flow');
  const [resetKey, setResetKey] = useState(0);
  const [customView, setCustomView] = useState<LayoutedView|null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [editing, setEditing] = useState<string|null>(null);
  const [draftMembers, setDraftMembers] = useState<string[]>([]);
  const [groupName, setGroupName] = useState('');
  const [edgeChoices, setEdgeChoices] = useState<readonly E[]>([]);
  const [nodeChoices, setNodeChoices] = useState<readonly N[]>([]);
  const [orthogonal, setOrthogonal] = useState(true);
  const [dark, setDark] = useState(true);
  const [animations, setAnimations] = useState(false);
  const [reduced, setReduced] = useState(() => matchMedia('(prefers-reduced-motion: reduce)').matches);
  const [showSource, setShowSource] = useState(false);
  const [tracing, setTracing] = useState(false);
  const api = useRef<DiagramApi|null>(null);
  const canvas = useRef<HTMLElement|null>(null);
  const detachImmediate = useRef<(()=>void)|null>(null);
  const acceptedGeometry = useRef<string|null>(null);
  const acceptLayout = useRef<(layout:LayoutedView)=>void>(()=>{});
  const editable = view === 'flow' || view === 'vertical';
  const generatedView = useMemo(() => editable ? applyLiveGroups(sourceModel.$data.views[view], groups) : sourceModel.$data.views[view], [sourceModel, view, groups, editable]);
  const layoutView = useMemo(() => {
    const customEdges = new Map(customView?.edges.map(edge => [edge.id, edge]));
    // Captured native edits contain only visible connectors. Restore every source
    // connector before filtering so toggling a kind back on cannot lose its IDs.
    const layout = editable && customView ? {...customView, edges:generatedView.edges.map(edge => customEdges.get(edge.id) ?? edge)} : generatedView;
    if (!hiddenEdgeIds?.size) return layout;
    return {...layout, edges:layout.edges.filter(edge => (result.edgeSourceIds[edge.id] ?? []).some(id => !hiddenEdgeIds.has(id)))};
  }, [customView, editable, generatedView, hiddenEdgeIds, result]);
  const visibleGraph = useMemo(() => ({...graph, edges:graph.edges.filter(edge => !hiddenEdgeIds?.has(edge.id))}), [graph, hiddenEdgeIds]);
  const routing = useRouting(layoutView, orthogonal, resetKey, tracing);
  const currentView = routing.view;
  const model = useMemo(() => LikeC4Model.create({...sourceModel.$data, views:{...sourceModel.$data.views, [view]:currentView}, manualLayouts:{}}), [sourceModel, currentView, view]);
  acceptLayout.current = layout => {
    if (layout.id !== view) return;
    const key = geometryKey(layout);
    if (acceptedGeometry.current === key) return;
    acceptedGeometry.current = key; traceSnapshot(layout); setCustomView(layout);
  };
  const editor = useMemo(() => createLikeC4Editor({fetchView:id => model.$data.views[id], handleChange:(id, change) => {
    if (id === view && change.op === 'save-view-snapshot' && !detachImmediate.current) acceptLayout.current(change.layout);
    return {waitForViewSync:false};
  }}), [model, view]);
  const selectedLeaves = selected.filter(id => currentView.nodes.some(node => node.id === id && node.kind !== '@group' && !node.children.length));
  const activeGroup = groups.find(group => selected.includes(group.id));
  const motion = animations && !reduced;

  useEffect(() => {configureTracing(tracing); return () => configureTracing(false);}, [tracing]);
  useEffect(() => {setEdgeChoices(choices => choices.some(edge => !hiddenEdgeIds?.has(edge.id)) ? choices : []);}, [hiddenEdgeIds]);
  useEffect(() => {
    document.addEventListener('pointerup', tracePointerUp, true);
    document.addEventListener('pointercancel', traceCancel, true);
    return () => {document.removeEventListener('pointerup', tracePointerUp, true); document.removeEventListener('pointercancel', traceCancel, true); detachImmediate.current?.(); cancelTraceInteraction();};
  }, []);
  useEffect(() => {
    const query = matchMedia('(prefers-reduced-motion: reduce)');
    const update = () => {setReduced(query.matches); if (query.matches) setAnimations(false);};
    query.addEventListener('change', update); return () => query.removeEventListener('change', update);
  }, []);
  useEffect(() => {if (motion && canvas.current) return animateEdges(canvas.current);}, [motion, view, resetKey, currentView.nodes.length]);
  useEffect(() => {
    try {localStorage.setItem('ce.live-likec4.groups.v1', JSON.stringify({source:result.source, groups}));}
    catch {setError('Browser storage is unavailable; groups will last only for this page.');}
  }, [groups, result.source]);

  function selectMembers(ids:string[]) {
    api.current?.select({nodes:currentView.nodes.filter(node => ids.includes(node.id)).map(node => node.id)});
  }
  function changeGroups(next:Group[]) {
    cancelTraceInteraction(); setCustomView(applyLiveGroups(currentView, next)); setGroups(next);
    setSelected([]); setEditing(null); setError(''); setResetKey(key => key + 1);
  }
  function edit(group?:Group) {
    const members = group?.members ?? selectedLeaves;
    setEditing(group?.id ?? 'new'); setGroupName(group?.title ?? 'My group'); setDraftMembers(members); setError(''); selectMembers(members);
  }
  function save() {
    try {changeGroups(saveGroup(groups, currentView, {id:editing === 'new' ? undefined : editing ?? undefined, title:groupName, members:draftMembers}));}
    catch (error) {setError(error instanceof Error ? error.message : String(error));}
  }
  function clear() {setEdgeChoices([]); setNodeChoices([]); onClearSelection?.();}
  function switchView(id:string) {
    if (!Object.hasOwn(sourceModel.$data.views, id)) return;
    cancelTraceInteraction(); setView(id); setCustomView(null); setSelected([]); setEditing(null); setError(''); clear();
  }
  function nodeClick(node:DiagramNode, event:React.MouseEvent) {
    if (editing && node.kind !== '@group' && !node.children.length) {
      const members = draftMembers.includes(node.id) ? draftMembers.filter(id => id !== node.id) : [...draftMembers, node.id];
      setDraftMembers(members); selectMembers(members); return;
    }
    if (node.kind === '@group' || event.shiftKey || event.ctrlKey || event.metaKey) return;
    const originals = graph.nodes.filter(original => result.nodeSourceIds[node.id]?.includes(original.id));
    setEdgeChoices([]);
    if (originals.length === 1) {setNodeChoices([]); if (event.detail === 2 && onNodeDrill) onNodeDrill(originals[0]); else onNodeSelect(originals[0]);}
    else setNodeChoices(originals);
  }
  function download() {
    const url = URL.createObjectURL(new Blob([result.source], {type:'text/plain;charset=utf-8'}));
    const link = document.createElement('a'); link.href = url; link.download = 'architecture.c4'; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  return <div className="live-canvas-shell" data-theme={dark ? 'dark' : 'light'} data-motion={motion ? 'on' : 'off'}>
    <header className="live-canvas-header"><strong>{graph.title ?? 'Architecture'}</strong><div className="actions">
      <span className="current-viewpoint">{view === 'flow' ? graph.title ?? 'Selected architecture' : viewpointLabel(view, sourceModel.$data.views[view])}</span>
      <button onClick={() => {cancelTraceInteraction(); setCustomView(null); setSelected([]); setEditing(null); clear(); setResetKey(key => key+1);}}>Reset layout</button>
      {editable && <><button onClick={() => api.current?.editorActor().send({type:'undo'})}>Undo layout</button><button onClick={() => api.current?.editorActor().send({type:'redo'})}>Redo layout</button></>}
      <button onClick={() => setShowSource(!showSource)} aria-expanded={showSource}>Diagram source</button><button onClick={download}>Download .c4</button>
    </div></header>
    <main className={showSource ? 'with-source' : ''}><div className="diagram-workspace">
      <ViewpointMenu views={sourceModel.$data.views} current={view} onSelect={switchView} source={result.source}
        {...{labels:{flow:graph.title ?? 'Selected architecture', vertical:`${graph.title ?? 'Selected architecture'} · vertical`, index:'Selected scope · overview'}}}
        coverage={<details><summary>Snapshot coverage · {graph.nodes.length} elements</summary><p>Selected analyzed artifacts. Missing relationships may be unextracted or unresolved.</p><code>{snapshotKey}</code></details>}
        settings={{tracing, setTracing, downloadTrace, orthogonal, setOrthogonal, motion, setAnimations, reduced, dark, setDark, embedded:false}}/>
      <section ref={canvas} className="canvas" aria-label="Live LikeC4 diagram" data-routing-generation={routing.generation} data-routing-state={routing.state} data-preview-ms={routing.previewMs} data-refinement-ms={routing.refinementMs}
        onPointerDownCapture={event => {const target = event.nativeEvent.composedPath().find((part):part is Element => part instanceof Element && part.matches('.react-flow__node')); if (target) {const node=currentView.nodes.find(node => node.id === target.getAttribute('data-id')); tracePointerDown(node?.children.length ? 'group' : 'node', event.pointerId);}}}>
        <DiagramLegend/>
        {routing.state === 'pending' && <div className="routing-status" role="status">Refining routes… provisional connectors may cross</div>}
        {routing.state === 'error' && <div className="routing-status" role="alert">Routing refinement failed. Reset layout to retry.</div>}
        <div className="canvas-hint">{editable ? 'Click a group to manage it · Shift-click nodes to group · drag groups together' : 'Source hierarchy · choose a detail viewpoint to explore its contents'}</div>
        {editable && <GroupControls group={activeGroup} selectedCount={selectedLeaves.length} editing={editing} name={groupName} setName={setGroupName} memberCount={draftMembers.length}
          onCreate={() => edit()} onRename={() => edit(activeGroup)} onEdit={() => edit(activeGroup)} onSave={save} onCancel={() => {setEditing(null); setError('');}}
          onOpen={activeGroup && sourceModel.$data.views[`${activeGroup.id}_detail`] ? () => switchView(`${activeGroup.id}_detail`) : null}
          onDelete={() => changeGroups(groups.filter(group => group.id !== activeGroup?.id))} error={error}/>}
        {error && !editing && <div className="storage-notice" role="status">{error}</div>}
        {(edgeChoices.length > 0 || nodeChoices.length > 0) && <aside className="live-originals" aria-label="Original relationships and elements"><button onClick={clear}>Close originals</button>
          {edgeChoices.length > 0 && <><h2>Original relationships · {edgeChoices.length}</h2><p>This connector represents all relationships below. Select one to inspect its original evidence and direction.</p>{edgeChoices.map(edge => <button key={edge.id} aria-label={`Inspect relationship ${edge.id}`} onClick={() => onEdgeSelect(edge)}><strong>{edge.label ?? edge.kind ?? edge.id}</strong><span>{edge.from} → {edge.to} · {edge.certainty} · {edge.count ?? 1} facts{hiddenEdgeIds?.has(edge.id) ? ' · hidden by current kind filters' : ''}</span><code>{edge.id}</code></button>)}</>}
          {nodeChoices.length > 0 && <><h2>Represented source elements</h2>{nodeChoices.map(node => <button key={node.id} aria-label={`Inspect element ${node.id}`} onClick={() => onNodeSelect(node)}>{node.label ?? node.id}<code>{node.id}</code></button>)}</>}
        </aside>}
        <div className="diagram-surface"><LikeC4ModelProvider likec4model={model}><LikeC4EditorProvider editor={editor}>
          <ReactLikeC4 key={`${view}-${resetKey}`} className="ce-live-diagram" viewId={view} injectFontCss={false} onNodeClick={nodeClick}
            onEdgeClick={edge => {const originals=graph.edges.filter(original => result.edgeSourceIds[edge.id]?.includes(original.id)); setNodeChoices([]); if (originals.length===1) {setEdgeChoices([]); onEdgeSelect(originals[0]);} else setEdgeChoices(originals);}}
            onCanvasClick={() => {if (!editing) {setSelected([]); clear();}}}
            onInitialized={initialized => {detachImmediate.current?.(); api.current=initialized.diagram; acceptedGeometry.current=null; detachImmediate.current=editable ? attachImmediateLayout(initialized, layout => acceptLayout.current(layout)) : null;}}
            reactFlowProps={{nodesDraggable:editable, elementsSelectable:true, multiSelectionKeyCode:['Shift','Control','Meta']}}
            onNavigateTo={switchView} pannable zoomable controls keepAspectRatio={false} reduceGraphics={!motion} enableDynamicViewWalkthrough={false}
            enableElementDetails={false} enableRelationshipDetails={false} showNavigationButtons background="dots" colorScheme={dark ? 'dark' : 'light'}
            mantineTheme={{fontFamily:'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif'}} style={{width:'100%', height:'100%'}}>
            <SelectionBridge onSelection={setSelected}/><LiveSourceBridge graph={visibleGraph} result={result} selectedId={selectedId}/>
            <RoutingTraceBridge view={currentView} context={routing.trace} ready={routing.state === 'ready'}/>
            <style>{`.ce-live-diagram .react-flow__node[data-neighborhood="outside"], .ce-live-diagram .react-flow__edge[data-neighborhood="outside"] { opacity:.18; }
              .ce-live-diagram .react-flow__node[data-source-selected="true"] { outline:3px solid #60a5fa; outline-offset:3px; }
              .ce-live-diagram .react-flow__edge[data-source-selected="true"] .react-flow__edge-path { stroke-width:4; }
              ${motion ? '.ce-live-diagram .react-flow__edge-path { animation:ce-live-edge-flow 2.4s ease-in-out infinite; }' : '.ce-live-diagram *, .ce-live-diagram *::before, .ce-live-diagram *::after { animation:none!important; transition:none!important; }'}
              @keyframes ce-live-edge-flow { 0%,100% { filter:drop-shadow(0 0 0 currentColor); } 50% { filter:drop-shadow(0 0 5px currentColor); } }
              @media (prefers-reduced-motion: reduce) { .ce-live-diagram *, .ce-live-diagram *::before, .ce-live-diagram *::after { animation:none!important; transition:none!important; } }`}</style>
          </ReactLikeC4>
        </LikeC4EditorProvider></LikeC4ModelProvider></div>
      </section></div>
      {showSource && <aside className="live-source" aria-label="Diagram source"><h2>Reviewable source</h2><p>Source IDs, certainty and associations come from the selected artifacts. Presentation edits do not change evidence.</p><pre data-live-source data-input-identity={snapshotKey}>{result.source}</pre></aside>}
    </main>
    <footer>Pan to explore · scroll to zoom · animation is optional decoration. Source and evidence remain authoritative.</footer>
  </div>;
}
