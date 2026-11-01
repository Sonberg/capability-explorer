import {test} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {randomUUID} from 'node:crypto';
import path from 'node:path';
import {hash,options,records,safeOutput,generate,root,DISK_RESERVE_GIB} from './issue28-generate.mjs';
import http from 'node:http';
import {localUrl,percentile,validateResponse,benchmark} from './issue28-http.mjs';

test('seeded streamed records reproduce identities, counts and high-degree/repeated-name workload',()=>{
 const args={count:1000,repo:'bench-0',revision:1,seed:28};
 const a=[...records(args)],b=[...records(args)];assert.equal(hash(JSON.stringify(a)),hash(JSON.stringify(b)));
 const nodes=a.filter(r=>r.kind==='node'),edges=a.filter(r=>r.kind==='edge');assert.equal(nodes.length,1000);assert.equal(edges.length,999);
 assert.equal(a[0].kind,'header');assert.equal(a.at(-1).kind,'complete');assert.equal(new Set(nodes.map(n=>n.symbolKey)).size,1000);
 assert.ok(new Set(nodes.map(n=>n.name)).size<10);assert.equal(edges.filter(e=>e.from.endsWith('|symbol0#value')).length,900);
 assert.notEqual(a[0].commitSha,[...records({...args,revision:2})][0].commitSha);
});
test('large generation requires explicit opt-in; unsupported dimensions rejected',()=>{
 assert.equal(DISK_RESERVE_GIB,10);
 assert.throws(()=>options(['--nodes','100001']),/--large/);assert.equal(options(['--nodes','100001','--large']).large,true);
 for(const args of [['--nodes','10000001','--large'],['--seed','NaN'],['--repos','5','--nodes','1']])assert.throws(()=>options(args));
});
test('outputs cannot escape ignored benchmark storage or traverse symlink ancestors',()=>{
 assert.throws(()=>safeOutput('fixtures/expected/evil'));assert.throws(()=>safeOutput('.sandbox-cache/issue28-bench/../../../docs'));
 const base=path.join(root,'.sandbox-cache/issue28-bench');fs.mkdirSync(base,{recursive:true});
 const link=path.join(base,'symlink-test-'+process.pid);fs.symlinkSync(root,link);
 try{assert.throws(()=>safeOutput(path.join(link,'output')),/real directories/);}finally{fs.unlinkSync(link);}
});
test('streamed disk artifacts reproduce byte hashes over revisions and refuse overwrite',async()=>{
 const out='.sandbox-cache/issue28-bench/test-'+randomUUID();
 const m=await generate(options(['--nodes','1000','--repos','2','--revisions','2','--out',out]));
 assert.equal(m.files.length,4);assert.equal(m.files.reduce((n,f)=>n+f.nodes,0),1000);
 for(const f of m.files){const data=fs.readFileSync(path.join(root,out,f.file));assert.equal(hash(data),f.sha256);assert.equal(data.length,f.bytes);}
 await assert.rejects(generate(options(['--out',out])),/EEXIST/);
});
test('benchmark rejects remote endpoints, credentials, over-limit and unstable-snapshot responses',()=>{
 for(const url of ['https://example.com','http://example.com','http://x:y@localhost','http://localhost/?token=x'])assert.throws(()=>localUrl(url));
 assert.equal(localUrl('http://127.0.0.1:5118').port,'5118');
 assert.throws(()=>validateResponse({items:[],snapshotId:'wrong'},10,{snapshot:'expected'}));
 assert.throws(()=>validateResponse({items:Array(51).fill({key:'x'}),snapshotId:'s'},10,{snapshot:'s'}));
 assert.throws(()=>validateResponse({items:[{key:'x'},{key:'x'}],snapshotId:'s'},10,{snapshot:'s'}));
 assert.throws(()=>validateResponse({items:[],snapshotId:'s'},1048577,{snapshot:'s'}));
 assert.equal(percentile([4,1,3,2],.95),4);
});

test('HTTP measurement runner consumes real HTTP test-double pages with bounded stable continuations',async()=>{
 const snapshot='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',symbol='ts|bench-0|@bench/bench-0|src/generated.ts|symbol0#value';
 const server=http.createServer((req,res)=>{
  const u=new URL(req.url,'http://127.0.0.1'),second=u.searchParams.has('cursor');
  let body;
  if(u.pathname==='/api/meta')body={source:'postgres',status:'ready',snapshotId:snapshot,generation:1,selection:{'bench-0':'a'.repeat(40)}};
  else if(u.pathname==='/api/symbol')body={key:symbol,name:'x'};
  else body={items:[u.pathname.endsWith('/edges')?{artifactId:1,localId:second?'e:2':'e:1'}:{key:second?'next':'first'}],snapshotId:snapshot,nextCursor:second?null:'test-cursor'};
  res.setHeader('content-type','application/json');res.end(JSON.stringify(body));
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const dir=path.join(root,'.sandbox-cache/issue28-bench/http-test-'+randomUUID());fs.mkdirSync(dir,{recursive:true});
 const manifest=path.join(dir,'manifest.json'),metadata=path.join(dir,'metadata.json');
 fs.writeFileSync(manifest,JSON.stringify({version:1,nodes:1000,selection:{'bench-0':'a'.repeat(40)},query:{terms:['','x'],symbol}}));
 fs.writeFileSync(metadata,JSON.stringify({appSourceSha:'a'.repeat(40),sourceState:'dirty',databaseImage:'test/fixture@sha256:'+'b'.repeat(64)}));
 try {
  const result=await benchmark({baseUrl:`http://127.0.0.1:${server.address().port}`,manifest,metadata,samples:5,warmup:0});
  assert.equal(result.status,'MEASURED_FOUNDATION_ONLY');assert.equal(result.results.length,6);
  assert.equal(result.results[0].name,'search:');
  assert.equal(result.results.filter(r=>r.name==='search:').length,1);
  assert.ok(result.results.every(r=>r.samples===5&&r.maxRows===1&&r.p99Ms>=r.p50Ms));
  assert.ok(result.notMeasured.includes('full issue28 endpoint coverage'));
 }finally{await new Promise(resolve=>server.close(resolve));}
});

test('EXPLAIN replay preserves captured SQL and ordered parameters, rejecting absent or write statements',async()=>{
 const {capturedQueries}=await import('./issue28-explain.mjs');
 const log="2026-11-01 UTC [1] LOG: duration: 1 ms execute <unnamed>: with selected as (\n\t select 1\n\t) select * from selected a join node n on n.artifact_id=$1\n2026-11-01 UTC [1] DETAIL:  Parameters: $1 = '28'";
 const parsed=capturedQueries(log);assert.equal(parsed.length,1);assert.equal(parsed[0].kind,'symbol');assert.deepEqual(parsed[0].values,["'28'"]);
 assert.ok(parsed[0].sql.includes('\n select 1\n'));
 assert.throws(()=>capturedQueries(log.replace('select 1','delete from artifact')),/read statement/);
 assert.throws(()=>capturedQueries(log.replace(/DETAIL:.*$/,'')),/parameters/);
 assert.throws(()=>capturedQueries('unrelated log'),/No exact/);
});
