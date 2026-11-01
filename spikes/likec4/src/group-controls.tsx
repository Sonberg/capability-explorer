import type {Group} from './types.ts';
import React, {useEffect} from 'react';
import {useDiagramSelector} from 'likec4/react';
export function SelectionBridge({onSelection}:{onSelection:(ids:string[])=>void}) {
  const selected=useDiagramSelector(s=>s.context.xynodes.filter(n=>n.selected).map(n=>n.id),
    (a,b)=>a.length===b.length&&a.every((id,i)=>id===b[i]));
  useEffect(()=>onSelection(selected),[selected,onSelection]);
  return null;
}
export function GroupControls({group,selectedCount,editing,name,setName,memberCount,onCreate,onRename,onEdit,onSave,onOpen,onCancel,onDelete,error}:{group?:Group;selectedCount:number;editing:string|null;name:string;setName:(name:string)=>void;memberCount:number;onCreate:()=>void;onRename:()=>void;onEdit:()=>void;onSave:()=>void;onOpen:(()=>void)|null;onCancel:()=>void;onDelete:()=>void;error:string}) {
  if (!group && !selectedCount && !editing) return null;
  return <div className="group-context" role="region" aria-label="Selected group controls">
    {editing ? <>
      <label>Group name <input autoFocus value={name} maxLength={80} onChange={e=>setName(e.target.value)} /></label>
      <span>{memberCount} members · click nodes to add or remove</span>
      <button onClick={onSave} disabled={memberCount<(editing==='new'?2:1)}>Save group</button>
      <button onClick={onCancel}>Cancel</button>
    </> : group ? <>
      <strong>{group.title}</strong><span>{group.members.length} members · {group.origin==='repository'?'identified repository ownership':'custom grouping'}</span>
      {onOpen && <button onClick={onOpen}>Source repository</button>}<button onClick={onRename}>Rename</button><button onClick={onEdit}>Edit members</button>
      <button onClick={onDelete}>Ungroup</button>
    </> : <><span>{selectedCount} selected · Shift-click to select more</span><button disabled={selectedCount<2} onClick={onCreate}>Group selection</button></>}
    {error && <span role="alert">{error}</span>}
  </div>;
}
