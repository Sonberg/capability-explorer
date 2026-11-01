#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {Readable, Transform} from 'node:stream';
import {pipeline} from 'node:stream/promises';
import {execFileSync} from 'node:child_process';

export const DISK_RESERVE_GIB=10;
export const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
export function sourceIdentity(){try{return execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8',stdio:['ignore','pipe','ignore']}).trim();}catch{return null;}}
export const hash=(s,algorithm='sha256')=>createHash(algorithm).update(s).digest('hex');
export function options(argv) {
  const o={nodes:1000,repos:1,revisions:1,seed:28,out:'.sandbox-cache/issue28-bench/default',large:false};
  for(let i=0;i<argv.length;i++) {
    const k=argv[i].replace(/^--/,'');
    if(k==='large') o.large=true;
    else if(['nodes','repos','revisions','seed','out'].includes(k)&&argv[i+1]!==undefined) o[k]=k==='out'?argv[++i]:Number(argv[++i]);
    else throw Error('Unknown or incomplete argument');
  }
  for(const k of ['nodes','repos','revisions','seed']) if(!Number.isSafeInteger(o[k])||o[k]<1) throw Error('Positive integer required: '+k);
  if(o.nodes>10_000_000||o.repos>50||o.revisions>10||o.nodes<o.repos*o.revisions) throw Error('Unsupported corpus dimensions');
  if(o.nodes>100_000&&!o.large) throw Error('Above 100K nodes requires --large');
  return o;
}
export function safeOutput(value) {
  const base=path.join(root,'.sandbox-cache/issue28-bench');
  const target=path.resolve(root,value);
  if(target!==base&&!target.startsWith(base+path.sep)) throw Error('Output must stay inside .sandbox-cache/issue28-bench');
  let current=root;
  for(const part of path.relative(root,target).split(path.sep)) {
    current=path.join(current,part);
    if(fs.existsSync(current)&&(!fs.lstatSync(current).isDirectory()||fs.lstatSync(current).isSymbolicLink())) throw Error('Output ancestors must be real directories');
  }
  return target;
}
export function* records({count,repo,revision,seed}) {
  const sha=hash(`${seed}|${repo}|${revision}`,'sha1'), tool={name:'issue28-seeded',version:'1'};
  const wrap=(kind,data)=>({schema:'ce.v1',kind,...data});
  const key=i=>`ts|${repo}|@bench/${repo}|src/generated.ts|symbol${i}#value`;
  yield wrap('header',{repositoryId:repo,commitSha:sha,unit:{path:'tsconfig.json',name:'@bench/'+repo},language:'typescript',extractor:tool,configHash:hash('issue28-v1|'+seed),inputHash:hash(`${seed}|${repo}|${revision}|${count}`)});
  yield wrap('evidence',{id:'ev:generated',repositoryId:repo,commitSha:sha,path:'src/generated.ts',span:{startLine:1,startColumn:1,endLine:1,endColumn:2},snippetSha256:hash('synthetic'),extractor:tool});
  const names=['Repeated','x','get.foo','Order-28','A_B','a','Price?','Repeated'];
  for(let i=0;i<count;i++) yield wrap('node',{id:'n:'+i,symbolKey:key(i),nodeKind:'Function',name:names[(i+seed)%names.length],signature:`function symbol${i}()`,evidence:['ev:generated']});
  // Ninety percent of outgoing edges originate at symbol0: deterministic high-degree hub.
  for(let i=1;i<count;i++) yield wrap('edge',{id:'e:'+i,from:key(i%10===0?i-1:0),relation:'CALLS',to:key(i),derivation:'compiler',resolution:'resolved',evidence:['ev:generated']});
  yield wrap('complete',{status:'full',counts:{node:count,edge:count-1,evidence:1,calculation:0,diagnostic:0},coverage:{filesTotal:1,filesAnalyzed:1,unresolvedEdges:0}});
}
export async function generate(o) {
  const target=safeOutput(o.out), disk=fs.statfsSync(root);
  // Conservative encoded-data estimate; DB import/index/WAL require a separate budget.
  if(disk.bavail*disk.bsize<DISK_RESERVE_GIB*1024**3+o.nodes*1200) throw Error('Need 10GiB reserve plus estimated JSONL bytes');
  fs.mkdirSync(target,{recursive:true});
  const files=[],selection={}; let remaining=o.nodes;
  for(let r=0;r<o.repos;r++) for(let v=0;v<o.revisions;v++) {
    const slots=o.repos*o.revisions-r*o.revisions-v,count=Math.floor(remaining/slots); remaining-=count;
    const repo=`bench-${r}`,revision=v+1,filename=`${repo}-r${revision}.jsonl`;
    const h=createHash('sha256');let bytes=0;
    const measure=new Transform({transform(chunk,encoding,done){h.update(chunk);bytes+=chunk.length;done(null,chunk);}});
    await pipeline(Readable.from((function*(){for(const row of records({count,repo,revision,seed:o.seed}))yield JSON.stringify(row)+'\n';})()),measure,fs.createWriteStream(path.join(target,filename),{flags:'wx'}));
    const sha=hash(`${o.seed}|${repo}|${revision}`,'sha1');selection[repo]=sha;
    files.push({file:filename,repositoryId:repo,commitSha:sha,nodes:count,edges:count-1,bytes,sha256:h.digest('hex')});
  }
  const manifest={version:1,generator:'issue28-seeded-v1',sourceSha:sourceIdentity(),generatorSha256:hash(fs.readFileSync(fileURLToPath(import.meta.url))),seed:o.seed,nodes:o.nodes,repositories:o.repos,revisions:o.revisions,selection,files,query:{terms:['Repeated','x','get.foo','Order-28'],symbol:'ts|bench-0|@bench/bench-0|src/generated.ts|symbol0#value'},distribution:{repeatedNames:8,hubOutgoingFraction:.9,crossRepositoryEdges:0,revisionSymbolIdentity:'same keys across revisions'}};
  fs.writeFileSync(path.join(target,'manifest.json'),JSON.stringify(manifest,null,2)+'\n',{flag:'wx'});
  return manifest;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {const o=options(process.argv.slice(2));const m=await generate(o);console.log(JSON.stringify({manifest:path.join(o.out,'manifest.json'),nodes:m.nodes,artifacts:m.files.length,bytes:m.files.reduce((n,f)=>n+f.bytes,0)}));}
  catch(e){console.error(e.message);process.exitCode=1;}
}
