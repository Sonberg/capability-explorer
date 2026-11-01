import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { canonical, hash, inventory } from '../scripts/mapping-replay-lib.mjs';
import { semanticDelta, validatePlan, verifyBundle, verifySource } from '../scripts/corpus-replay-lib.mjs';
function setup(t) {
  const root=mkdtempSync(resolve(tmpdir(),'corpus-test-')); t.after(()=>rmSync(root,{recursive:true,force:true}));
  const put=(path,text)=>{mkdirSync(resolve(root,path,'..'),{recursive:true});writeFileSync(resolve(root,path),text);};
  put('checkout/a.ts','export const a = 1;\n'); put('export/a.ts','export const a = 1;\n');
  put('checkout/app.csproj','<Project/>');put('export/app.csproj','<Project/>');
  const git=(...args)=>execFileSync('git',['-C',resolve(root,'checkout'),...args],{encoding:'utf8',stdio:['ignore','pipe','pipe']});
  git('init','-q');git('add','.');git('-c','user.name=Test','-c','user.email=test@example.invalid','commit','-qm','synthetic');
  const commit=git('rev-parse','HEAD').trim(),files=inventory(resolve(root,'export'));
  put('schema/v1/record.schema.json','{}');put('extractor.ts','source');
  for(const lane of ['baseline','candidate']) {
    for(const path of ['csharp/ce-cs.dll','ts/extractors/ts/src/cli.ts','ts/extractors/ts/package-lock.json','ts/schema/v1/record.schema.json']) put(`${lane}/${path}`,path.endsWith('record.schema.json')?'{}':'synthetic');
    const files=inventory(resolve(root,lane)),sourceFiles={'extractor.ts':hash('source')};
    put(`${lane}/build.json`,JSON.stringify({format:'mapping-replay-build.v1',files,buildHash:hash(canonical(files)),sourceFiles,sourceSnapshotHash:hash(canonical(sourceFiles))}));
  }
  const plan={format:'corpus-replay-plan.v1',output:'.sandbox-cache/corpus-replay/run1',images:{node:'sha256:'+'a'.repeat(64),dotnet:'sha256:'+'b'.repeat(64)},builds:Object.fromEntries(['baseline','candidate'].map(path=>[path,{path,buildHash:JSON.parse(readFileSync(resolve(root,path,'build.json'))).buildHash}])),repositories:[{id:'sample',commit,checkout:'checkout',source:'export',files,inputHash:hash(canonical(files)),commands:['ts']}]};
  return {root,plan,put,git};
}
test('accepts exact Git export, sealed builds and fixed command',t=>{const {root,plan}=setup(t);assert.equal(validatePlan(root,plan).planHash,hash(canonical(plan)));});
test('rejects stale full SHA',t=>{const {root,plan}=setup(t);plan.repositories[0].commit='f'.repeat(40);assert.throws(()=>validatePlan(root,plan),/stale/);});
test('rejects mutable ref label',t=>{const {root,plan}=setup(t);plan.repositories[0].commit='HEAD';assert.throws(()=>validatePlan(root,plan),/identity/);});
test('rejects exported bytes different from Git',t=>{const {root,plan,put}=setup(t);put('export/a.ts','altered');assert.throws(()=>validatePlan(root,plan),/export differs/);});
test('rejects extra untracked export input',t=>{const {root,plan,put}=setup(t);put('export/secret.txt','synthetic');assert.throws(()=>validatePlan(root,plan),/export differs/);});
test('rejects falsely sealed source inventory',t=>{const {root,plan}=setup(t);plan.repositories[0].files['a.ts']='0'.repeat(64);assert.throws(()=>validatePlan(root,plan),/export differs/);});
test('rejects source symlink',t=>{const {root,plan}=setup(t);symlinkSync('/etc/passwd',resolve(root,'export/link'));assert.throws(()=>validatePlan(root,plan),/symlink/);});
test('rejects Git tracked symlink',t=>{const {root,plan,git}=setup(t);symlinkSync('a.ts',resolve(root,'checkout/link'));git('add','.');git('-c','user.name=Test','-c','user.email=test@example.invalid','commit','-qm','link');plan.repositories[0].commit=git('rev-parse','HEAD').trim();assert.throws(()=>verifySource(root,plan.repositories[0]),/unsupported/);});
test('rejects unknown executable command',t=>{const {root,plan}=setup(t);plan.repositories[0].commands=['npm install'];assert.throws(()=>validatePlan(root,plan),/command/);});
test('rejects shell escape path',t=>{const {root,plan}=setup(t);plan.repositories[0].source='export;touch hacked';assert.throws(()=>validatePlan(root,plan),/path/);});
test('rejects escaped output after normalization',t=>{const {root,plan}=setup(t);plan.output='.sandbox-cache/corpus-replay/../../outside';assert.throws(()=>validatePlan(root,plan),/output/);});
test('rejects unknown top-level fields',t=>{const {root,plan}=setup(t);plan.command='arbitrary';assert.throws(()=>validatePlan(root,plan),/unknown/);});
test('rejects mutable image tags',t=>{const {root,plan}=setup(t);plan.images.node='node:22';assert.throws(()=>validatePlan(root,plan),/digests/);});
test('rejects missing baseline',t=>{const {root,plan}=setup(t);delete plan.builds.baseline;assert.throws(()=>validatePlan(root,plan),/baseline/);});
test('rejects modified sealed dependency',t=>{const {root,plan,put}=setup(t);put('candidate/ts/extractors/ts/node_modules/injected.js','code');assert.throws(()=>verifyBundle(root,plan.builds.candidate),/unsealed/);});
test('rejects stale candidate source',t=>{const {root,plan,put}=setup(t);put('extractor.ts','changed');assert.throws(()=>validatePlan(root,plan),/stale candidate/);});
test('rejects C# extraction without locked dependency set',t=>{const {root,plan}=setup(t);plan.repositories[0].commands=['csharp'];assert.throws(()=>validatePlan(root,plan),/dependencies/);});
test('accepts exact C# package lock then rejects altered metadata',t=>{const {root,plan,put}=setup(t);put('packages/data.nuspec','metadata');const lockText=JSON.stringify({version:1,projects:{'app.csproj':[]}});put('locks/sample.json',lockText);const repo=plan.repositories[0];repo.commands=['csharp'];repo.dependencies={packages:'packages',lock:'locks/sample.json',lockHash:hash(lockText),packagesHash:hash(canonical(inventory(resolve(root,'packages'))))};validatePlan(root,plan);put('packages/data.nuspec','changed');assert.throws(()=>validatePlan(root,plan),/unsealed package/);});
test('rejects changed lock bytes',t=>{const {root,plan,put}=setup(t);put('packages/data','x');put('lock.json','{}');plan.repositories[0].commands=['csharp'];plan.repositories[0].dependencies={packages:'packages',lock:'lock.json',lockHash:'f'.repeat(64),packagesHash:hash(canonical(inventory(resolve(root,'packages'))))};assert.throws(()=>validatePlan(root,plan),/lock changed/);});
test('semantic delta ignores evidence IDs, preserves duplicate counts and uncertainty',()=>{const before=[{kind:'edge',id:'1',evidence:['a'],resolution:'unresolved'},{kind:'edge',id:'2',evidence:['b'],resolution:'unresolved'}];const after=[{kind:'edge',id:'3',evidence:['c'],resolution:'unresolved'}];assert.deepEqual(semanticDelta(before,after),{removed:[{kind:'edge',resolution:'unresolved'}],added:[]});});

test('rejects incomplete C# lock project coverage',t=>{const {root,plan,put}=setup(t);const text=JSON.stringify({version:1,projects:{'other.csproj':[]}});put('packages/data','x');put('lock.json',text);plan.repositories[0].commands=['csharp'];plan.repositories[0].dependencies={packages:'packages',lock:'lock.json',lockHash:hash(text),packagesHash:hash(canonical(inventory(resolve(root,'packages'))))};assert.throws(()=>validatePlan(root,plan),/project coverage/);});
test('rejects missing locked package folder',t=>{const {root,plan,put}=setup(t);const text=JSON.stringify({version:1,projects:{'app.csproj':[{id:'Example.Package',version:'1.0.0'}]}});put('packages/data','x');put('lock.json',text);plan.repositories[0].commands=['csharp'];plan.repositories[0].dependencies={packages:'packages',lock:'lock.json',lockHash:hash(text),packagesHash:hash(canonical(inventory(resolve(root,'packages'))))};assert.throws(()=>validatePlan(root,plan),/missing locked/);});

test('ignores Git replacement refs when proving commit blobs',t=>{const {root,plan,put,git}=setup(t);put('checkout/evil.txt','replacement bytes');const original=git('rev-parse','HEAD:a.ts').trim(),replacement=git('hash-object','-w','evil.txt').trim();git('replace',original,replacement);assert.equal(git('cat-file','blob',original),'replacement bytes');assert.doesNotThrow(()=>validatePlan(root,plan));});
