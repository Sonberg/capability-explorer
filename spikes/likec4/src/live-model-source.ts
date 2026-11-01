import type {Element,ElementStyle,ModelExpression,ParsedLikeC4ModelData} from '@likec4/core/types';

// One typed semantic projection supplies both the downloadable DSL and browser data.
const quote=(value:string)=>JSON.stringify(value);
function style(style:ElementStyle):string {
  return Object.entries(style).map(([key,value])=>`${key} ${key==='opacity'?`${value}%`:value}`).join(' ');
}
function expression(expr:ModelExpression):string {
  if('wildcard' in expr&&expr.wildcard)return '*';
  if('ref' in expr&&expr.ref)return `${expr.ref.model}${expr.selector==='children'?'.*':expr.selector==='descendants'?'.**':''}`;
  throw new Error('Unsupported live projection expression');
}
export function serializeLiveProjection(model:ParsedLikeC4ModelData):string {
  const lines=['// Architecture API snapshot; certainty is not upgraded by visualization.','specification {'];
  for(const [name,color] of Object.entries(model.specification.customColors??{}))lines.push(`  color ${name} ${color.elements.fill}`);
  for(const [kind,spec] of Object.entries(model.specification.elements))lines.push(`  element ${kind} { style { ${style(spec.style??{})} } }`);
  for(const [kind,spec] of Object.entries(model.specification.relationships))lines.push(`  relationship ${kind} { ${['color','line','head','tail','multiple'].flatMap(key=>key in spec?[`${key} ${spec[key as keyof typeof spec]}`]:[]).join(' ')} }`);
  lines.push('}','model {');
  const elements=Object.values(model.elements).sort((a,b)=>a.id<b.id?-1:a.id>b.id?1:0);
  function element(el:Element,indent:string):void {
    lines.push(`${indent}${el.id.split('.').at(-1)} = ${el.kind} ${quote(el.title)} {`);
    if(el.description)lines.push(`${indent}  description ${quote('txt' in el.description?el.description.txt??'':el.description.md??'')}`);
    if(el.metadata) {
      lines.push(`${indent}  metadata {`);
      for(const [key,value] of Object.entries(el.metadata))if(typeof value==='string')lines.push(`${indent}    ${key} ${quote(value)}`);
      lines.push(`${indent}  }`);
    }
    for(const child of elements.filter(e=>e.id.slice(0,e.id.lastIndexOf('.'))===el.id))element(child,`${indent}  `);
    lines.push(`${indent}}`);
  }
  for(const el of elements.filter(e=>!e.id.includes('.')))element(el,'  ');
  for(const rel of Object.values(model.relations)) {
    lines.push(`  ${rel.source.model} -[${rel.kind}]-> ${rel.target.model} ${quote(rel.title??'')} {`);
    lines.push(`    style { ${(['color','line','head','tail'] as const).flatMap(key=>rel[key]?[`${key} ${rel[key]}`]:[]).join(' ')} }`);
    if(rel.description)lines.push(`    description ${quote('txt' in rel.description?rel.description.txt??'':rel.description.md??'')}`);
    lines.push('    metadata {',`      source ${quote(String(rel.metadata?.source??''))}`,'    }','  }');
  }
  lines.push('}','views {');
  for(const view of Object.values(model.views)) {
    if(view._type!=='element')throw new Error('Unsupported live view');
    lines.push(`  view ${view.id}${view.viewOf?` of ${view.viewOf}`:''} {`,`    title ${quote(view.title??'')}`);
    for(const rule of view.rules) {
      if('include' in rule&&rule.include?.length)lines.push(`    include ${rule.include.map(expression).join(', ')}`);
      else if('direction' in rule)lines.push(`    autoLayout ${rule.direction==='TB'?'TopBottom':'LeftRight'}`);
    }
    lines.push('  }');
  }
  lines.push('}','');return lines.join('\n');
}
