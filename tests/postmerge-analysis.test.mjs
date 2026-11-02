import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {spawnSync} from 'node:child_process';
import {chmodSync,copyFileSync,existsSync,lstatSync,mkdirSync,mkdtempSync,readFileSync,rmSync,symlinkSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {dirname,join,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

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

test('analysis orchestration invalidates stale receipts before invalid input or a failed capability run',()=>{
 for(const invalidInput of [true,false])fixture(root=>{
  mkdirSync(join(root,'scripts'),{recursive:true});
  for(const name of ['analyze-all','analysis-capability-ids.mjs','analysis-receipt.mjs'])copyFileSync(join(repository,'scripts',name),join(root,'scripts',name));
  mkdirSync(join(root,'scripts/lib'),{recursive:true});
  copyFileSync(join(repository,'scripts/lib/container-engine.sh'),join(root,'scripts/lib/container-engine.sh'));
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
