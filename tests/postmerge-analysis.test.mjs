import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {spawnSync} from 'node:child_process';
import {chmodSync,copyFileSync,existsSync,lstatSync,mkdirSync,mkdtempSync,readFileSync,rmSync,symlinkSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {dirname,join,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {loadCapabilityDefinitions} from '../scripts/capability-definitions.mjs';

const repository=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const application='a'.repeat(40),revision='b'.repeat(40),oldRevision='c'.repeat(40);
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const receiptPath='.sandbox-cache/analysis-receipt.json';
function write(root,path,text){mkdirSync(dirname(join(root,path)),{recursive:true});writeFileSync(join(root,path),text);}
function fixture(run){
 const root=mkdtempSync(join(tmpdir(),'ce-postmerge-analysis-'));
 try{
  write(root,'capabilities.json',JSON.stringify({capabilities:[{id:'one',repositories:[{id:'repo',location:'https://private.invalid/PRIVATE_LOCATION',ref:'main'}]}]}));
  write(root,'config/runtime/images.env','NODE_IMAGE=node@sha256:synthetic\n');
  write(root,'bindings.json','{"PRIVATE_BINDING":"synthetic"}\n');
  write(root,'.sandbox-cache/analyze/one/manifest.tsv',`repo\t${revision}\t/PRIVATE_EXPORTED_PATH\n`);
  write(root,'.sandbox-cache/artifacts/one/current.jsonl',artifact('repo',revision));
  return run(root);
 }finally{rmSync(root,{recursive:true,force:true});}
}
const artifact=(repo,sha)=>JSON.stringify({schema:'ce.v1',kind:'header',repositoryId:repo,commitSha:sha})+'\n';
function helper(root,name,args=[]){
 return spawnSync(process.execPath,[join(repository,'scripts',name),...args],{cwd:root,encoding:'utf8',timeout:20000,env:{PATH:process.env.PATH}});
}
function receipt(root,...args){return helper(root,'analysis-receipt.mjs',args.length?args:['capabilities.json',application,'clean','bindings.json']);}
function failed(result){assert.equal(result.error,undefined);assert.notEqual(result.status,0);assert.equal(result.stdout,'');}

test('capability IDs are completely validated before emitting a sorted, private-free list',()=>fixture(root=>{
 write(root,'capabilities.json',JSON.stringify({capabilities:[{id:'z-last',location:'PRIVATE_LOCATION'},{id:'a-first'}]}));
 const result=helper(root,'analysis-capability-ids.mjs',['capabilities.json']);
 assert.equal(result.status,0,result.stderr);assert.equal(result.stdout,'a-first\nz-last\n');
}));

test('unsafe, duplicate and missing capability IDs fail before any valid ID is emitted',()=>fixture(root=>{
 for(const invalid of ['../escape','a/b','line\nbreak','contains\0nul','one',null]){
  write(root,'capabilities.json',JSON.stringify({capabilities:[{id:'one'},{id:invalid}]}));
  failed(helper(root,'analysis-capability-ids.mjs',['capabilities.json']));
 }
}));

test('local capability definitions combine with shared definitions and override matching IDs',()=>fixture(root=>{
 write(root,'capabilities.local.json',JSON.stringify({capabilities:[{id:'one',repositories:[]},{id:'local-only',repositories:[]}]}));
 const result=helper(root,'analysis-capability-ids.mjs',['capabilities.json']);
 assert.equal(result.status,0,result.stderr);assert.equal(result.stdout,'local-only\none\n');
 assert.deepEqual(loadCapabilityDefinitions(join(root,'capabilities.json')).capabilities.find(capability=>capability.id==='one').repositories,[]);
 write(root,'capabilities.local.json','{"capabilities":[{"id":"bad/id"}]}');
 failed(helper(root,'analysis-capability-ids.mjs',['capabilities.json']));
}));

test('local capability receipt hashes include local-only repositories without exposing definitions',()=>fixture(root=>{
 write(root,'capabilities.local.json',JSON.stringify({capabilities:[{id:'local-only',repositories:[{id:'local-repo',location:'PRIVATE_LOCAL_LOCATION'}]}]}));
 write(root,'.sandbox-cache/analyze/local-only/manifest.tsv',`local-repo\t${revision}\t/private\n`);
 write(root,'.sandbox-cache/artifacts/local-only/current.jsonl',artifact('local-repo',revision));
 const result=receipt(root);assert.equal(result.status,0,result.stderr);
 const text=readFileSync(join(root,receiptPath),'utf8'),saved=JSON.parse(text);
 assert.equal(saved.capabilitiesLocalHash,hash(readFileSync(join(root,'capabilities.local.json'))));
 assert.equal(saved.artifacts.length,2);assert.doesNotMatch(text,/PRIVATE_LOCAL_LOCATION/);
 write(root,'capabilities.local.json','invalid');failed(receipt(root));
}));

test('source mounts use parsed merged definitions and exclude overridden shared locations',()=>fixture(root=>{
 const shared=join(root,'shared'),local=join(root,'local space');
 mkdirSync(shared);mkdirSync(local);
 write(root,'capabilities.json',JSON.stringify({capabilities:[{id:'one',repositories:[{location:shared}]}]}));
 write(root,'capabilities.local.json',JSON.stringify({capabilities:[{id:'one',repositories:[{location:local},{location:local},{location:'https://example.invalid/repo'}]}]}));
 const result=spawnSync('/bin/bash',[join(repository,'scripts/capability-sources'),'capabilities.json'],{cwd:root,env:{PATH:process.env.PATH,HOME:root},encoding:'utf8',timeout:20000});
 assert.equal(result.status,0,result.stderr);assert.equal(result.stdout,local);
}));

test('receipt binds raw current inputs and only the exact locked repository revisions',()=>fixture(root=>{
 write(root,'.sandbox-cache/artifacts/one/stale.jsonl',artifact('repo',oldRevision));
 write(root,'.sandbox-cache/artifacts/other/unrelated.jsonl',artifact('unrelated',revision));
 const result=receipt(root);assert.equal(result.status,0,result.stderr);
 const text=readFileSync(join(root,receiptPath),'utf8'),saved=JSON.parse(text);
 assert.equal(saved.format,'ce.analysis-replay.v1');assert.equal(saved.applicationCommit,application);assert.equal(saved.sourceState,'clean');
 for(const [field,path] of [['capabilitiesHash','capabilities.json'],['runtimeImagesHash','config/runtime/images.env'],['bindingsHash','bindings.json']])
  assert.equal(saved[field],hash(readFileSync(join(root,path))));
 assert.deepEqual(saved.artifacts,[{path:'one/current.jsonl',repositoryId:'repo',commitSha:revision,sha256:hash(readFileSync(join(root,'.sandbox-cache/artifacts/one/current.jsonl')))}]);
 assert.equal(lstatSync(join(root,receiptPath)).mode&0o777,0o600);
 assert.doesNotMatch(text,/PRIVATE_LOCATION|PRIVATE_BINDING|PRIVATE_EXPORTED_PATH/);
}));

test('receipt distinguishes dirty application inputs and changes in binding bytes',()=>fixture(root=>{
 assert.equal(receipt(root).status,0);
 const first=JSON.parse(readFileSync(join(root,receiptPath),'utf8'));
 write(root,'bindings.json','{"changed":true}\n');
 const result=receipt(root,'capabilities.json',application,'dirty','bindings.json');assert.equal(result.status,0,result.stderr);
 const second=JSON.parse(readFileSync(join(root,receiptPath),'utf8'));
 assert.equal(second.sourceState,'dirty');assert.equal(second.applicationCommit,application);assert.notEqual(second.bindingsHash,first.bindingsHash);
 assert.deepEqual(second.artifacts,first.artifacts);
}));

test('invalid application and locked manifest identities cannot produce success receipts',()=>fixture(root=>{
 for(const sha of ['a'.repeat(41),'main','../escape']){
  failed(receipt(root,'capabilities.json',sha,'clean',''));assert.equal(existsSync(join(root,receiptPath)),false);
 }
 for(const line of [`repo\t${'b'.repeat(41)}\t/private\n`,`../escape\t${revision}\t/private\n`]){
  write(root,'.sandbox-cache/analyze/one/manifest.tsv',line);failed(receipt(root));assert.equal(existsSync(join(root,receiptPath)),false);
 }
}));

test('old artifacts cannot stand in for a missing currently locked repository revision',()=>fixture(root=>{
 write(root,'.sandbox-cache/artifacts/one/current.jsonl',artifact('repo',oldRevision));
 failed(receipt(root));assert.equal(existsSync(join(root,receiptPath)),false);
}));

test('malformed artifact identities fail rather than being silently omitted',()=>fixture(root=>{
 for(const text of [artifact('../escape',revision),artifact('repo','b'.repeat(41)),JSON.stringify({schema:'ce.v1',kind:'node'})+'\n']){
  write(root,'.sandbox-cache/artifacts/one/current.jsonl',text);failed(receipt(root));assert.equal(existsSync(join(root,receiptPath)),false);
 }
}));

test('symlink artifact files, inventory roots and receipt destinations are refused without external writes',()=>{
 for(const scenario of ['file','root','output']) fixture(root=>{
  write(root,'outside/sentinel','EXTERNAL_SENTINEL');
  if(scenario==='file')symlinkSync(join(root,'outside/sentinel'),join(root,'.sandbox-cache/artifacts/linked.jsonl'));
  if(scenario==='root'){
   rmSync(join(root,'.sandbox-cache/artifacts'),{recursive:true});
   write(root,'outside/current.jsonl',artifact('repo',revision));
   symlinkSync(join(root,'outside'),join(root,'.sandbox-cache/artifacts'));
  }
  if(scenario==='output')symlinkSync(join(root,'outside/sentinel'),join(root,receiptPath));
  failed(receipt(root));assert.equal(readFileSync(join(root,'outside/sentinel'),'utf8'),'EXTERNAL_SENTINEL');
 });
});

test('make defaults to git authentication while preserving explicit overrides',()=>{
 const env={...process.env};
 delete env.GITHUB_AUTH;
 for(const profile of ['native','sandbox']){
  for(const mode of [undefined,'auto','none']){
   const args=['-n','analyze-all','-o','build',`EXECUTION=${profile}`];
   if(mode!==undefined)args.push(`GITHUB_AUTH=${mode}`);
   const result=spawnSync('make',args,{cwd:repository,encoding:'utf8',timeout:20000,env});
   assert.equal(result.status,0,result.stderr);
   assert.match(result.stdout,new RegExp(`GITHUB_AUTH=${mode??'git'} .*scripts/analyze-all`));
  }
 }
});

test('development startup imports saved artifacts unless re-analysis is explicitly enabled',()=>{
 for(const profile of ['native','sandbox'])fixture(root=>{
  write(root,'scripts/analyze-all','#!/bin/sh\necho analyzed >> .sandbox-cache/startup-analysis\n');
  write(root,'scripts/sandbox','#!/bin/sh\nshift\nexec "$@"\n');
  write(root,'bin/dotnet','#!/bin/sh\nprintf "%s\\n" "$2" >> .sandbox-cache/startup-import\n');
  for(const path of ['scripts/analyze-all','scripts/sandbox','bin/dotnet'])chmodSync(join(root,path),0o700);
  const args=['-s','-f',join(repository,'config/build',`${profile}.mk`),'dev-data','-o','build','-o','postgres','DEV_OLLAMA=0','ARTIFACTS=.sandbox-cache/artifacts'];
  const env={...process.env,PATH:join(root,'bin')+':'+process.env.PATH};
  delete env.DEV_REANALYZE;
  const startup=spawnSync('make',args,{cwd:root,encoding:'utf8',timeout:20000,env});
  assert.equal(startup.status,0,startup.stderr);
  assert.equal(existsSync(join(root,'.sandbox-cache/startup-analysis')),false);
  assert.equal(readFileSync(join(root,'.sandbox-cache/startup-import'),'utf8'),'migrate\nimport\nembed\n');
  const refresh=spawnSync('make',[...args,'DEV_REANALYZE=1'],{cwd:root,encoding:'utf8',timeout:20000,env});
  assert.equal(refresh.status,0,refresh.stderr);
  assert.equal(readFileSync(join(root,'.sandbox-cache/startup-analysis'),'utf8'),'analyzed\n');
 });
});

test('analysis cleanup removes only manifest-owned sources and retains stale export trees',()=>fixture(root=>{
 mkdirSync(join(root,'scripts'),{recursive:true});
 copyFileSync(join(repository,'scripts/analyze'),join(root,'scripts/analyze'));
 write(root,'scripts/github-token','#!/bin/sh\nexit 0\n');
 write(root,'scripts/capability-sources','#!/bin/sh\nexit 0\n');
 write(root,'scripts/sandbox','#!/bin/sh\n[ "$1" != --offline ] || shift\nshift\nexec "$@"\n');
 write(root,'bin/dotnet',`#!/bin/sh
case "$2" in
 export)
  mkdir -p .sandbox-cache/analyze/one/src/current/repo
  printf 'repo\\t${revision}\\tsrc/current/repo\\n' > .sandbox-cache/analyze/one/manifest.tsv
  printf '0\\n' > .sandbox-cache/analyze/one/include-tests;;
 extract)
  while [ "$1" != --out ]; do shift; done
  shift
  mkdir -p "$1"
  printf '%s\\n' '${artifact('repo',revision).trim()}' > "$1/result.jsonl";;
 *) exit 1;;
esac
`);
 write(root,'.sandbox-cache/analyze/one/src/stale/obj/sentinel','STALE_EXPORT');
 for(const path of ['scripts/analyze','scripts/github-token','scripts/capability-sources','scripts/sandbox','bin/dotnet'])chmodSync(join(root,path),0o700);
 const env={...process.env,PATH:join(root,'bin')+':'+process.env.PATH,PACKAGES:'0',CE_DATABASE:'',SANDBOX_PASS_ENV:''};
 const result=spawnSync('bash',['scripts/analyze','one','capabilities.json'],{cwd:root,encoding:'utf8',timeout:20000,env});
 assert.equal(result.status,0,result.stderr);
 assert.equal(readFileSync(join(root,'.sandbox-cache/analyze/one/src/stale/obj/sentinel'),'utf8'),'STALE_EXPORT');
 assert.equal(existsSync(join(root,'.sandbox-cache/analyze/one/src/current')),false);
 assert.equal(existsSync(join(root,`.sandbox-cache/artifacts/repo/${revision}/result.jsonl`)),true);
}));

function extractionFixture(root){
 write(root,'scripts/analyze',readFileSync(join(repository,'scripts/analyze'),'utf8'));
 write(root,'scripts/github-token','#!/bin/sh\nexit 0\n');
 write(root,'scripts/capability-sources','#!/bin/sh\nexit 0\n');
 write(root,'scripts/sandbox','#!/bin/sh\n[ "$1" != --offline ] || shift\nshift\nexec "$@"\n');
 write(root,'extractors/ts/src/cli.ts','"extract" "extract-sql"\n');
 write(root,'bin/dotnet',`#!/bin/sh\nexec '${process.execPath}' tool.mjs "$@"\n`);
 write(root,'bin/node',`#!/bin/sh\nexec '${process.execPath}' tool.mjs "$@"\n`);
 write(root,'bin/docker',`#!/bin/sh\nexec '${process.execPath}' engine.mjs "$@"\n`);
 write(root,'engine.mjs',`import {readFileSync,appendFileSync} from 'node:fs';
const args=process.argv.slice(2);
appendFileSync('engine.log',JSON.stringify(args)+'\\n');
if(args[0]==='ps'){
 const events=readFileSync('events','utf8').trim().split('\\n').map(JSON.parse);
 for(const event of events.filter(event=>event.kind==='start'))console.log(String(event.pid));
}else if(args[0]==='kill')for(const pid of args.slice(1)){try{process.kill(Number(pid),'SIGTERM');}catch{}}
`);
 write(root,'tool.mjs',`import {appendFileSync,mkdirSync,writeFileSync,readFileSync} from 'node:fs';
const args=process.argv.slice(2),work='.sandbox-cache/analyze/one';
if(args[1]==='export'){
 mkdirSync(work+'/src/current/repo space',{recursive:true});
 writeFileSync(work+'/manifest.tsv','repo\\t${revision}\\tsrc/current/repo space\\nother\\t${revision}\\tsrc/current/repo space\\n');
 writeFileSync(work+'/include-tests','1');
 process.exit(0);
}
if(args[1]==='fetch-packages'){
 const lock=args[args.indexOf('--lock')+1];writeFileSync(lock,'{}');process.exit(0);
}
if(!args.includes('--out')){appendFileSync('store-calls',args[1]+'\\n');process.exit(0);}
const language=args[0].endsWith('ce-cs.dll')?'cs':args[1]==='extract-sql'?'sql':'ts';
const out=args[args.indexOf('--out')+1],repo=args[args.indexOf('--repository')+1];
function event(kind){appendFileSync('events',JSON.stringify({kind,language,repo,pid:process.pid,args,label:process.env.SANDBOX_LABEL,extraLabel:process.env.SANDBOX_EXTRA_LABEL})+'\\n');}
event('start');
process.on('SIGTERM',()=>{event('stopped');process.exit(143);});
function finish(){
 if(process.env.FAIL_EXTRACTOR===language){event('failed');process.exit(7);}
 mkdirSync(out,{recursive:true});
 writeFileSync(out+'/'+language+'.jsonl',language);
 writeFileSync(out+'/collision.jsonl',language);
 event('finish');
}
if(process.env.OVERLAP_BARRIER){
 const timer=setInterval(()=>{
  const started=readFileSync('events','utf8').trim().split('\\n').map(JSON.parse).filter(event=>event.kind==='start').length;
  if(started>=Number(process.env.OVERLAP_BARRIER)){clearInterval(timer);finish();}
 },10);
}else if(process.env.FAIL_EXTRACTOR&&language==='cs')setTimeout(finish,30000);
else setTimeout(finish,process.env.CANCEL_EXTRACTION?30000:language==='cs'?180:80);
`);
 for(const path of ['scripts/analyze','scripts/github-token','scripts/capability-sources','scripts/sandbox','bin/dotnet','bin/node','bin/docker'])chmodSync(join(root,path),0o700);
 write(root,'events','');
 return {...process.env,PATH:join(root,'bin')+':'+process.env.PATH,PACKAGES:'0',CE_DATABASE:'',SANDBOX_PASS_ENV:'',SANDBOX_ENGINE:'docker',SANDBOX_LABEL:'ce-analyze=one'};
}
function extractionEvents(root){return readFileSync(join(root,'events'),'utf8').trim().split('\n').filter(Boolean).map(JSON.parse);}

test('parallel analysis bounds total jobs and merges isolated extractor outputs deterministically',()=>{
 for(const jobs of [1,2,20])fixture(root=>{
    const env={...extractionFixture(root),ANALYZE_JOBS:String(jobs),OVERLAP_BARRIER:String(Math.min(jobs,6))};
  const result=spawnSync('/bin/bash',['scripts/analyze','one'],{cwd:root,env,encoding:'utf8',timeout:20000});
  assert.equal(result.status,0,result.stderr);
  const events=extractionEvents(root);let active=0,maximum=0;
  for(const event of events){active+=event.kind==='start'?1:-1;maximum=Math.max(maximum,active);assert.ok(active>=0);}
  assert.equal(active,0);assert.ok(maximum<=jobs);assert.equal(maximum,Math.min(jobs,6));
  for(const repo of ['repo','other']){
   const output=join(root,'.sandbox-cache/artifacts',repo,revision);
   for(const language of ['cs','ts','sql'])assert.equal(readFileSync(join(output,language+'.jsonl'),'utf8'),language);
   assert.equal(readFileSync(join(output,'collision.jsonl'),'utf8'),'sql');
  }
  for(const event of events.filter(event=>event.kind==='start')){
   assert.equal(event.label,'ce-analyze=one');assert.match(event.extraLabel,/^ce-extraction=one-/);
   assert.ok(event.args.includes('.sandbox-cache/analyze/one/src/current/repo space'));
   assert.equal(event.args.includes('--include-tests'),event.language!=='sql');
  }
  assert.match(result.stdout,/timing: export/);assert.match(result.stdout,/timing: extraction/);
 });
});

test('parallel analysis rejects invalid job limits before exporting or cleaning outputs',()=>{
 for(const jobs of ['0','-1','1.5','abc','10000'])fixture(root=>{
  const env={...extractionFixture(root),ANALYZE_JOBS:jobs};
  const result=spawnSync('/bin/bash',['scripts/analyze','one'],{cwd:root,env,encoding:'utf8',timeout:20000});
  assert.equal(result.status,2);assert.match(result.stderr,/ANALYZE_JOBS/);
  assert.equal(extractionEvents(root).length,0);
  assert.equal(readFileSync(join(root,'.sandbox-cache/analyze/one/manifest.tsv'),'utf8'),`repo\t${revision}\t/PRIVATE_EXPORTED_PATH\n`);
 });
});

test('parallel analysis failure stops sibling jobs and preserves previous artifacts and sources',()=>fixture(root=>{
 const env={...extractionFixture(root),ANALYZE_JOBS:'6',FAIL_EXTRACTOR:'ts',CE_DATABASE:'synthetic'};
 write(root,`.sandbox-cache/artifacts/repo/${revision}/sentinel.jsonl`,'PREVIOUS');
 const result=spawnSync('/bin/bash',['scripts/analyze','one'],{cwd:root,env,encoding:'utf8',timeout:20000});
 assert.equal(result.status,7,result.stderr);
 assert.equal(readFileSync(join(root,`.sandbox-cache/artifacts/repo/${revision}/sentinel.jsonl`),'utf8'),'PREVIOUS');
 assert.ok(existsSync(join(root,'.sandbox-cache/analyze/one/src/current/repo space')));
 const events=extractionEvents(root),started=events.filter(event=>event.kind==='start');
 assert.equal(result.error,undefined);
 for(const event of started)assert.ok(events.some(done=>done.pid===event.pid&&done.kind!=='start'));
 assert.ok(events.some(event=>event.language==='cs'&&event.kind==='stopped'));
 assert.equal(existsSync(join(root,'store-calls')),false);
 assert.match(readFileSync(join(root,'engine.log'),'utf8'),/label=ce-extraction=one-/);
}));

test('parallel analysis cancellation stops children without publishing',()=>fixture(root=>{
 const env={...extractionFixture(root),ANALYZE_JOBS:'2',CANCEL_EXTRACTION:'1'};
 write(root,'cancel.mjs',`import {spawn} from 'node:child_process';
import {readFileSync} from 'node:fs';
const child=spawn('/bin/bash',['scripts/analyze','one'],{stdio:'inherit'});
const timer=setInterval(()=>{
 if(readFileSync('events','utf8').trim().split('\\n').filter(Boolean).length>=2){clearInterval(timer);child.kill('SIGTERM');}
},10);
child.on('exit',code=>{clearInterval(timer);process.exit(code);});
`);
 const result=spawnSync(process.execPath,['cancel.mjs'],{cwd:root,env,encoding:'utf8',timeout:20000});
 assert.equal(result.error,undefined);
 assert.equal(result.status,143,result.stderr);
 const events=extractionEvents(root);
 assert.equal(events.filter(event=>event.kind==='start').length,2);
 assert.equal(events.filter(event=>event.kind==='stopped').length,2);
 assert.ok(existsSync(join(root,'.sandbox-cache/analyze/one/src/current/repo space')));
}));

test('parallel analysis preserves package lock arguments when optional extractors are absent',()=>fixture(root=>{
 const env={...extractionFixture(root),ANALYZE_JOBS:'2',PACKAGES:'1'};
 rmSync(join(root,'extractors/ts/src/cli.ts'));
 const result=spawnSync('/bin/bash',['scripts/analyze','one'],{cwd:root,env,encoding:'utf8',timeout:20000});
 assert.equal(result.status,0,result.stderr);
 const started=extractionEvents(root).filter(event=>event.kind==='start');assert.equal(started.length,2);
 for(const event of started){
  assert.equal(event.language,'cs');
  assert.equal(event.args[event.args.indexOf('--packages')+1],'.sandbox-cache/analyze-packages');
  assert.equal(event.args[event.args.indexOf('--lock')+1],'.sandbox-cache/analyze/one/locks/'+event.repo+'.json');
 }
}));

test('both make profiles export default and overridden analysis job limits to recipes',()=>{
 const env={...process.env};delete env.ANALYZE_JOBS;
 for(const profile of ['native','sandbox'])for(const jobs of [undefined,1,4]){
  const args=['-s','-f',join(repository,'config/build',profile+'.mk'),'--eval',"print-analysis-jobs:\n\t@printf '%s\\n' \"$$ANALYZE_JOBS\"",'print-analysis-jobs'];
  if(jobs!==undefined)args.push('ANALYZE_JOBS='+jobs);
  const result=spawnSync('make',args,{cwd:repository,env,encoding:'utf8',timeout:20000});
  assert.equal(result.status,0,result.stderr);assert.equal(result.stdout,String(jobs??2)+'\n');
 }
});

test('analysis orchestration invalidates stale receipts before invalid input or a failed capability run',()=>{
 for(const invalidInput of [true,false])fixture(root=>{
  mkdirSync(join(root,'scripts'),{recursive:true});
  for(const name of ['analyze-all','analysis-capability-ids.mjs','analysis-receipt.mjs','capability-definitions.mjs'])copyFileSync(join(repository,'scripts',name),join(root,'scripts',name));
  write(root,'scripts/sandbox','#!/bin/sh\nshift\nshift\nexec "$@"\n');
  write(root,'scripts/analyze','#!/bin/sh\nprintf "%s\\n" "$1" >> .sandbox-cache/attempted\nexit 1\n');
  write(root,'bin/git',`#!/bin/sh\ncase "$1" in rev-parse) printf '%s\\n' '${application}';; status) exit 0;; *) exit 1;; esac\n`);
  for(const path of ['scripts/analyze-all','scripts/sandbox','scripts/analyze','bin/git'])chmodSync(join(root,path),0o700);
  write(root,receiptPath,'{"stale-success":true}\n');
  if(invalidInput)write(root,'capabilities.json','{"capabilities":[{"id":"../escape"}]}');
  const result=spawnSync('bash',['scripts/analyze-all','capabilities.json'],{cwd:root,encoding:'utf8',timeout:20000,env:{PATH:join(root,'bin')+':'+process.env.PATH}});
  assert.equal(result.error,undefined);assert.notEqual(result.status,0);assert.equal(existsSync(join(root,receiptPath)),false);
  if(invalidInput)assert.equal(existsSync(join(root,'.sandbox-cache/attempted')),false);
  else assert.equal(readFileSync(join(root,'.sandbox-cache/attempted'),'utf8'),'one\n');
 });
});
