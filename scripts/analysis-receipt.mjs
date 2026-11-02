// Redacted local receipt: no repository URLs, credentials, original source or raw scanner reports.
import {readFileSync,writeFileSync,readdirSync,lstatSync,mkdirSync,existsSync,renameSync,unlinkSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {join,relative,resolve,parse} from 'node:path';
import {loadCapabilityDefinitions,localDefinitionsPath} from './capability-definitions.mjs';
const hash=x=>createHash('sha256').update(x).digest('hex');
function safePath(path,allowMissing=false){
 const full=resolve(path);let current=parse(full).root;
 for(const segment of full.slice(current.length).split('/').filter(Boolean)){
  current=join(current,segment);
  let stat;
  try {stat=lstatSync(current);} catch(error){if(allowMissing&&error.code==='ENOENT')continue;throw Error('Missing analysis input');}
  if(stat.isSymbolicLink())throw Error('Analysis symlink rejected');
 }
 return full;
}
function read(path){const file=safePath(path);if(lstatSync(file).size>1048576)throw Error('Analysis metadata limit exceeded');return readFileSync(file);}
try {
safePath('.sandbox-cache',true);mkdirSync('.sandbox-cache',{recursive:true});
const output=safePath('.sandbox-cache/analysis-receipt.json',true);
if(process.argv[2]==='--clear'){if(existsSync(output))unlinkSync(output);process.exit(0);}
const config=read(process.argv[2]);
const localPath=safePath(localDefinitionsPath(process.argv[2]),true);
const localConfig=existsSync(localPath)?read(localPath):null;
const applicationCommit=process.argv[3],sourceState=process.argv[4],bindings=process.argv[5];
if(!/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(applicationCommit)||!['clean','dirty'].includes(sourceState))throw Error('Invalid application source identity');
const selected=new Set();
const definitions=loadCapabilityDefinitions(process.argv[2],path=>path===process.argv[2]?config:localConfig);
if(!Array.isArray(definitions.capabilities))throw Error('Invalid capability list');
for(const cap of definitions.capabilities){
 if(!/^[a-z0-9][a-z0-9._-]{0,99}$/.test(cap.id))throw Error('Invalid capability identity');
 const manifest=read(`.sandbox-cache/analyze/${cap.id}/manifest.tsv`).toString('utf8');
 for(const line of manifest.split('\n').filter(Boolean)){
  const [repo,revision]=line.split('\t');
  if(!/^[a-z0-9][a-z0-9._-]{0,99}$/.test(repo)||!/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(revision))throw Error('Invalid locked repository identity');
  selected.add(`${repo}@${revision}`);
 }
}
const files=[];let visited=0,total=0;
function walk(dir){
 for(const name of readdirSync(dir).sort()){
  if(++visited>100000)throw Error('Analysis receipt inventory limit exceeded');
  const path=join(dir,name),stat=lstatSync(path);
  if(stat.isSymbolicLink())throw Error('Analysis artifact symlink rejected');
  if(stat.isDirectory())walk(path);
  else if(name.endsWith('.jsonl')){
   if(stat.size>64*1024*1024)throw Error('Analysis artifact size limit exceeded');
   total+=stat.size;if(total>512*1024*1024)throw Error('Analysis receipt byte budget exceeded');
   const bytes=readFileSync(path),header=JSON.parse(bytes.toString('utf8').split('\n')[0]);
   if(header.schema!=='ce.v1'||header.kind!=='header'||!/^[a-z0-9][a-z0-9._-]{0,99}$/.test(header.repositoryId)||!/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(header.commitSha))throw Error('Invalid analysis artifact identity');
   if(!selected.has(`${header.repositoryId}@${header.commitSha}`))continue;
   files.push({path:relative('.sandbox-cache/artifacts',path),repositoryId:header.repositoryId,commitSha:header.commitSha,sha256:hash(bytes)});
  }
 }
}
safePath('.sandbox-cache/artifacts');walk('.sandbox-cache/artifacts');
for(const identity of selected)if(!files.some(file=>`${file.repositoryId}@${file.commitSha}`===identity))throw Error('Locked repository has no analysis artifacts');
const receipt={format:'ce.analysis-replay.v1',applicationCommit,sourceState,capabilitiesHash:hash(config),capabilitiesLocalHash:localConfig?hash(localConfig):null,runtimeImagesHash:hash(read('config/runtime/images.env')),bindingsHash:bindings?hash(read(bindings)):null,artifacts:files};
mkdirSync('.sandbox-cache',{recursive:true});
const temporary=output+'.'+process.pid+'.tmp';
writeFileSync(temporary,JSON.stringify(receipt,null,2)+'\n',{mode:0o600,flag:'wx'});
renameSync(temporary,output);
console.log(`Recorded ${files.length} artifact hashes in private .sandbox-cache/analysis-receipt.json; compare these with the owner, alongside the application commit`);

} catch { console.error('Analysis receipt rejected invalid, unsafe or incomplete inputs');process.exitCode=1; }
