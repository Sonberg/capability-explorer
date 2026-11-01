#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';
import os from 'node:os';
import {performance} from 'node:perf_hooks';
import {hash,root,safeOutput,sourceIdentity} from './issue28-generate.mjs';

export function percentile(values,p) {
  if(!values.length) throw Error('No latency samples');
  const sorted=[...values].sort((a,b)=>a-b);return sorted[Math.max(0,Math.ceil(p*sorted.length)-1)];
}
export function localUrl(value) {
  const u=new URL(value);
  if(u.protocol!=='http:'||!['127.0.0.1','localhost','[::1]','host.docker.internal'].includes(u.hostname)||u.username||u.password||u.search||u.hash) throw Error('Use a local HTTP URL without credentials/query/fragment');
  return u;
}
export function validateResponse(body,bytes,{kind,key,snapshot}) {
  if(bytes>1024*1024) throw Error('Response exceeds one MiB budget');
  if(kind==='symbol') {if(body.key!==key)throw Error('Wrong or absent symbol');return 1;}
  if(!Array.isArray(body.items)||body.items.length>50||body.snapshotId!==snapshot)throw Error('Page limit/snapshot contract failed');
  const ids=body.items.map(r=>r.key??`${r.artifactId}|${r.localId}`);
  if(new Set(ids).size!==ids.length)throw Error('Duplicate rows in a page');
  return body.items.length;
}
export async function benchmark(o) {
  const base=localUrl(o.baseUrl);
  if(fs.statSync(o.manifest).size>1024*1024)throw Error('Manifest exceeds one MiB');
  const manifest=JSON.parse(fs.readFileSync(o.manifest,'utf8'));
  if(manifest.version!==1||!Number.isSafeInteger(manifest.nodes)||manifest.nodes<1||manifest.nodes>10_000_000||!Array.isArray(manifest.query?.terms)||manifest.query.terms.length<1||manifest.query.terms.length>8||manifest.query.terms.some(q=>typeof q!=='string'||q.length>500)||typeof manifest.query?.symbol!=='string'||manifest.query.symbol.length>2048)throw Error('Invalid workload manifest');
  if(fs.statSync(o.metadata).size>16384)throw Error('Metadata exceeds 16KiB');
  const metadata=JSON.parse(fs.readFileSync(o.metadata,'utf8'));
  if(Object.keys(metadata).some(k=>!['appSourceSha','sourceState','databaseImage','applicationBuildSha256'].includes(k)))throw Error('Unexpected metadata fields; do not put credentials in receipts');
  if(metadata.applicationBuildSha256&&!/^[0-9a-f]{64}$/.test(metadata.applicationBuildSha256))throw Error('Invalid application build digest');
  if(!/^[0-9a-f]{40}$/.test(metadata.appSourceSha)||!['clean','dirty'].includes(metadata.sourceState)||!/@sha256:[0-9a-f]{64}$/.test(metadata.databaseImage)) throw Error('Metadata requires appSourceSha, sourceState and pinned databaseImage');
  const samples=Number(o.samples??20),warmup=Number(o.warmup??3);
  if(!Number.isSafeInteger(samples)||samples<5||samples>1000||!Number.isSafeInteger(warmup)||warmup<0||warmup>100)throw Error('Invalid sample counts');
  const request=async(endpoint,params={})=>{
    const u=new URL('/api/'+endpoint,base);for(const[k,v]of Object.entries(params))if(v!==null&&v!==undefined)u.searchParams.set(k,v);
    const start=performance.now(),res=await fetch(u,{signal:AbortSignal.timeout(10000),redirect:'error',headers:{'accept':'application/json','accept-encoding':'identity'}});
    const chunks=[];let bytes=0;
    if(!res.body)throw Error('Missing response body');
    for await(const chunk of res.body){bytes+=chunk.length;if(bytes>1024*1024)throw Error('Response exceeds one MiB');chunks.push(chunk);}
    const elapsed=performance.now()-start;
    if(!res.ok)throw Error(`HTTP ${res.status}: ${endpoint}`);
    return {body:JSON.parse(Buffer.concat(chunks).toString('utf8')),bytes,elapsed};
  };
  const first=await request('meta'),meta=first.body;
  if(meta.source!=='postgres'||!meta.snapshotId||meta.status!=='ready')throw Error('Requires a ready PostgreSQL API');
  const canonical=x=>JSON.stringify(Object.entries(x).sort(([a],[b])=>a.localeCompare(b)));
  if(canonical(meta.selection)!==canonical(manifest.selection))throw Error('Published selection does not match corpus manifest');
  const common={snapshot:meta.snapshotId,limit:50},key=manifest.query.symbol;
  const work=manifest.query.terms.map(q=>({name:'search:'+q,endpoint:'search',params:{...common,q},kind:'page'}));
  work.push({name:'symbol',endpoint:'symbol',params:{...common,key},kind:'symbol',key});
  work.push({name:'hub-outgoing',endpoint:'symbol/edges',params:{...common,key,direction:'outgoing'},kind:'page'});
  const notMeasured=['process startup duration','app peak/idle memory unless supplied separately','DB pool wait','import peak/WAL/disk','query plans unless supplied separately','file baseline','cold OS cache','large-corpus latency','full issue28 endpoint coverage'];
  for(const entry of [work[0],work.at(-1)]) {
    const {body,bytes}=await request(entry.endpoint,entry.params);validateResponse(body,bytes,{...entry,snapshot:meta.snapshotId});
    if(body.nextCursor)work.push({...entry,name:entry.name+':continuation',params:{...entry.params,cursor:body.nextCursor},previousIds:new Set(body.items.map(r=>r.key??`${r.artifactId}|${r.localId}`))});
    else notMeasured.push(entry.name+' continuation: no second page at this tier');
  }
  const results=[];
  for(const entry of work) {
    const times=[],sizes=[],counts=[];let digest;
    for(let i=0;i<samples+warmup;i++) {
      const {body,bytes,elapsed}=await request(entry.endpoint,entry.params);
      const rows=validateResponse(body,bytes,{...entry,snapshot:meta.snapshotId});
      if(entry.previousIds&&body.items.some(r=>entry.previousIds.has(r.key??`${r.artifactId}|${r.localId}`)))throw Error('Continuation overlaps first page: '+entry.name);
      const identity=hash(JSON.stringify(entry.kind==='symbol'?body:body.items));
      if(digest&&digest!==identity)throw Error('Unstable result rows for '+entry.name);digest=identity;
      if(i>=warmup){times.push(elapsed);sizes.push(bytes);counts.push(rows);}
    }
    results.push({name:entry.name,samples,warmup,p50Ms:percentile(times,.5),p95Ms:percentile(times,.95),p99Ms:percentile(times,.99),minMs:Math.min(...times),maxMs:Math.max(...times),maxResponseBytes:Math.max(...sizes),maxRows:Math.max(...counts),responseRowsSha256:digest});
  }
  const observedContainers={};
  for(const k of ['appContainer','databaseContainer']) if(o[k]) {
    if(!/^[a-zA-Z0-9_.-]+$/.test(o[k]))throw Error('Invalid container name');
    observedContainers[k]={inspect:JSON.parse(execFileSync('docker',['inspect','--format','{{json .}}',o[k]],{encoding:'utf8'})),stats:JSON.parse(execFileSync('docker',['stats','--no-stream','--format','{{json .}}',o[k]],{encoding:'utf8'}))};
    // Environment can contain DB passwords; never preserve complete inspect output.
    const i=observedContainers[k].inspect;observedContainers[k].inspect={id:i.Id,imageId:i.Image,imageReference:i.Config.Image,memoryLimit:i.HostConfig.Memory,nanoCpus:i.HostConfig.NanoCpus};
  }
  const searchBudget=200,graphBudget=500;
  return {version:1,status:'MEASURED_FOUNDATION_ONLY',utc:new Date().toISOString(),manifestSha256:hash(fs.readFileSync(o.manifest)),runnerSha256:hash(fs.readFileSync(fileURLToPath(import.meta.url))),runnerSourceSha:sourceIdentity(),metadataSource:'operator supplied; reconcile against served build before acceptance',metadata,snapshotId:meta.snapshotId,generation:meta.generation,nodesGenerated:manifest.nodes,selection:meta.selection,initialMetadataRequestMs:first.elapsed,clientHost:{platform:os.platform(),arch:os.arch(),cpu:os.cpus()[0]?.model,logicalCpus:os.cpus().length,totalMemoryBytes:os.totalmem()},cacheCondition:'warm application/DB; OS caches uncontrolled',client:'sequential fetch, identity encoding, includes network+parse-body transfer but excludes JSON parse',results,observedContainers,provisionalTargets:{searchP95Ms:searchBudget,adjacencyP95Ms:graphBudget,authority:'proposed product targets, not an adoption or full-issue gate'},targetObservations:results.map(r=>({name:r.name,passesProvisionalP95:r.p95Ms<=(r.name.startsWith('hub-')?graphBudget:searchBudget)})),notMeasured};
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {
    const o={};for(let i=2;i<process.argv.length;i+=2){const k=process.argv[i];if(!k.startsWith('--')||!process.argv[i+1])throw Error('Expected --name value');o[k.slice(2).replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]=process.argv[i+1];}
    for(const k of ['baseUrl','manifest','metadata','out'])if(!o[k])throw Error('Missing --'+k);
    const output=path.resolve(root,o.out);safeOutput(path.dirname(output));fs.mkdirSync(path.dirname(output),{recursive:true});
    const report=await benchmark(o);fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});
    console.log(JSON.stringify({report:o.out,status:report.status,queries:report.results.length}));
  }catch(e){console.error(e.message);process.exitCode=1;}
}
