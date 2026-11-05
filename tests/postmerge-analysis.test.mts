import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import type {BinaryLike} from 'node:crypto';
import {spawnSync, type SpawnSyncReturns} from 'node:child_process';
import {chmodSync,copyFileSync,existsSync,lstatSync,mkdirSync,mkdtempSync,readFileSync,rmSync,symlinkSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {dirname,join,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {loadCapabilityDefinitions} from '../scripts/capability-definitions.mjs';

const repository=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const application='a'.repeat(40),revision='b'.repeat(40),oldRevision='c'.repeat(40);
type CommandResult=SpawnSyncReturns<string>;
const hash=(bytes:BinaryLike):string=>createHash('sha256').update(bytes).digest('hex');
const receiptPath='.sandbox-cache/analysis-receipt.json';
function write(root:string,path:string,text:string):void{mkdirSync(dirname(join(root,path)),{recursive:true});writeFileSync(join(root,path),text);}
function fixture(run:(root:string)=>void):void{
 const root=mkdtempSync(join(tmpdir(),'ce-postmerge-analysis-'));
 try{
  mkdirSync(join(root,'scripts/lib'),{recursive:true});
  copyFileSync(join(repository,'scripts/lib/container-engine.sh'),join(root,'scripts/lib/container-engine.sh'));
  write(root,'capabilities.json',JSON.stringify({capabilities:[{id:'one',repositories:[{id:'repo',location:'https://private.invalid/PRIVATE_LOCATION',ref:'main'}]}]}));
  write(root,'config/runtime/images.env','NODE_IMAGE=node@sha256:synthetic\n');
  write(root,'bindings.json','{"PRIVATE_BINDING":"synthetic"}\n');
  write(root,'.sandbox-cache/analyze/one/manifest.tsv',`repo\t${revision}\t/PRIVATE_EXPORTED_PATH\n`);
  write(root,'.sandbox-cache/artifacts/one/current.jsonl',artifact('repo',revision));
  return run(root);
 }finally{rmSync(root,{recursive:true,force:true});}
}
const artifact=(repo:string,sha:string):string=>JSON.stringify({schema:'ce.v1',kind:'header',repositoryId:repo,commitSha:sha})+'\n';
function helper(root:string,name:string,args:string[]=[]):CommandResult{
 return spawnSync(process.execPath,[join(repository,'scripts',name),...args],{cwd:root,encoding:'utf8',timeout:20000,env:{PATH:process.env.PATH}});
}
function receipt(root:string,...args:string[]):CommandResult{return helper(root,'analysis-receipt.mjs',args.length?args:['capabilities.json',application,'clean','bindings.json']);}
function failed(result:CommandResult):void{assert.equal(result.error,undefined);assert.notEqual(result.status,0);assert.equal(result.stdout,'');}

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
 const one=loadCapabilityDefinitions(join(root,'capabilities.json')).capabilities.find(capability=>capability.id==='one');
 assert.ok(one);assert.deepEqual(one.repositories,[]);
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
 const env:NodeJS.ProcessEnv={...process.env,CE_CONTAINER_ENGINE:'docker'};
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
  mkdirSync(join(root,'config/build'),{recursive:true});
  mkdirSync(join(root,'platform'));
  copyFileSync(join(repository,'config/build/container-engine.mk'),join(root,'config/build/container-engine.mk'));
  write(root,'scripts/analyze-all','#!/bin/sh\necho analyzed >> .sandbox-cache/startup-analysis\n');
  write(root,'scripts/sandbox','#!/bin/sh\nshift\nexec "$@"\n');
  write(root,'bin/dotnet','#!/bin/sh\nprintf "%s\\n" "$2" >> .sandbox-cache/startup-import\n');
  for(const path of ['scripts/analyze-all','scripts/sandbox','bin/dotnet'])chmodSync(join(root,path),0o700);
  const args=['-s','-f',join(repository,'config/build',`${profile}.mk`),'dev-data','-o','build','-o','postgres','DEV_OLLAMA=0','ARTIFACTS=.sandbox-cache/artifacts'];
  const env:NodeJS.ProcessEnv={...process.env,PATH:join(root,'bin')+':'+process.env.PATH,CE_CONTAINER_ENGINE:'docker'};
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

test('both make profiles export default and overridden analysis job limits to recipes',()=>{
 const env:NodeJS.ProcessEnv={...process.env,CE_CONTAINER_ENGINE:'docker'};delete env.ANALYZE_JOBS;
 const probeDir=mkdtempSync(join(tmpdir(),'ce-make-probe-'));
 const probe=join(probeDir,'probe.mk');
 writeFileSync(probe,'print-analysis-jobs:\n\t@printf \'%s\\n\' "$$ANALYZE_JOBS"\n');
 try{
 for(const profile of ['native','sandbox'])for(const jobs of [undefined,1,4]){
  const args=['-s','-f',join(repository,'config/build',profile+'.mk'),'-f',probe,'print-analysis-jobs'];
  if(jobs!==undefined)args.push('ANALYZE_JOBS='+jobs);
  const result=spawnSync('make',args,{cwd:repository,env,encoding:'utf8',timeout:20000});
    assert.equal(result.status,0,result.stderr);assert.equal(result.stdout,String(jobs??1)+'\n');
 }
 }finally{rmSync(probeDir,{recursive:true,force:true});}
});


test('analysis wrappers pass qualified engines and literal arguments to the host coordinator without touching receipts',()=>{
for(const [script,command,args,expected] of [
  ['analyze-all','analysis-all',['config with spaces.json'],['--capabilities','config with spaces.json']],
  ['analyze-worker','analysis-worker',[],['--capabilities','capabilities.json']],
  ['analyze','analysis-run',['one','config with spaces.json'],['--capability','one','--capabilities','config with spaces.json']]
 ] satisfies [string,string,string[],string[]][])for(const engine of ['docker','podman'])fixture(root=>{
  mkdirSync(join(root,'scripts/lib'),{recursive:true});
  copyFileSync(join(repository,'scripts',script),join(root,'scripts',script));
  copyFileSync(join(repository,'scripts/lib/container-engine.sh'),join(root,'scripts/lib/container-engine.sh'));
  write(root,'platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll','fixture');
  write(root,'bin/dotnet','#!/usr/bin/env node\nrequire("fs").writeFileSync("args.json",JSON.stringify(process.argv.slice(2)));require("fs").writeFileSync("engine.txt",process.env.CE_CONTAINER_ENGINE);process.exit(23);\n');
  chmodSync(join(root,'bin/dotnet'),0o700);
  write(root,receiptPath,'preserve until ownership');
  const result=spawnSync('bash',['scripts/'+script,...args],{cwd:root,encoding:'utf8',timeout:20000,env:{PATH:join(root,'bin')+':'+process.env.PATH,CE_CONTAINER_ENGINE:engine}});
  assert.equal(result.status,23,result.stderr);
  assert.deepEqual(JSON.parse(readFileSync(join(root,'args.json'),'utf8')).slice(1),[command,...expected]);
  assert.equal(readFileSync(join(root,'engine.txt'),'utf8'),engine);
  assert.equal(readFileSync(join(root,receiptPath),'utf8'),'preserve until ownership');
 });
});

test('analysis wrappers reject invalid engine selections before invoking the host or changing receipts',()=>{
for(const [script,args] of [['analyze',['one']],['analyze-all',[]],['analyze-worker',[]]] satisfies [string,string[]][])
for(const engine of ['','invalid'])fixture(root=>{
 mkdirSync(join(root,'scripts/lib'),{recursive:true});
 copyFileSync(join(repository,'scripts',script),join(root,'scripts',script));
 copyFileSync(join(repository,'scripts/lib/container-engine.sh'),join(root,'scripts/lib/container-engine.sh'));
 write(root,receiptPath,'preserved');
 write(root,'platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll','fixture');
 write(root,'bin/dotnet','#!/bin/sh\necho invoked > host-invoked\nexit 23\n');
 chmodSync(join(root,'bin/dotnet'),0o700);
 const result=spawnSync('bash',['scripts/'+script,...args],{cwd:root,encoding:'utf8',timeout:20000,env:{PATH:join(root,'bin')+':'+process.env.PATH,CE_CONTAINER_ENGINE:engine}});
 assert.equal(result.status,2);
 assert.match(result.stderr,/CE_CONTAINER_ENGINE must be docker or podman/);
 assert.equal(existsSync(join(root,'host-invoked')),false);
 assert.equal(readFileSync(join(root,receiptPath),'utf8'),'preserved');
});
});
