import React,{useState} from 'react';
function Symbol({kind}) {
  if(kind.startsWith('line'))return <svg viewBox="0 0 44 20" aria-hidden="true"><path d="M2 10H38" fill="none" stroke="currentColor" strokeWidth="2" strokeDasharray={kind==='line-solid'?undefined:'5 3'}/>{kind!=='line-association'&&<path d="M33 5L39 10L33 15" fill="none" stroke="currentColor" strokeWidth="2"/>}</svg>;
  return <svg viewBox="0 0 44 26" aria-hidden="true"><rect x="3" y="3" width="36" height="20" rx="2" className={`legend-symbol ${kind}`}/>{(kind==='reference'||kind==='sql-model')&&<><rect x="0" y="7" width="8" height="5" className={`legend-symbol ${kind}`}/><rect x="0" y="15" width="8" height="5" className={`legend-symbol ${kind}`}/></>}</svg>;
}
export function DiagramLegend() {
  const [open,setOpen]=useState(false);
  return <div className="diagram-legend">
    <button aria-expanded={open} aria-controls="diagram-legend-content" onClick={()=>setOpen(!open)}>Legend {open?'−':'+'}</button>
    {open&&<section id="diagram-legend-content" aria-label="Diagram legend">
      <h2>Elements & boundaries</h2>
      {[['repository','Repository / presentation group'],['service','Service / repository aggregate'],['application','Application'],['channel','Messaging channel'],['reference','Database reference · target unresolved'],['sql-model','SQL source model · deployment unresolved'],['sql-object','SQL object · see label for declaration evidence']].map(([kind,label])=><div className="legend-row" key={kind}><Symbol kind={kind}/><span>{label}</span></div>)}
      <h2>Relationships & evidence</h2>
      {[['line-solid','Source-supported relationship · label states certainty'],['line-dashed','Candidate, unresolved, unbound or broken relationship'],['line-association','Same-name association · identity not established']].map(([kind,label])=><div className="legend-row" key={kind}><Symbol kind={kind}/><span>{label}</span></div>)}
      <p>A database cylinder is reserved for a concrete database reference; none is established in this snapshot. Read/write direction and evidence remain in relationship details. Motion is decorative.</p>
    </section>}
  </div>;
}
