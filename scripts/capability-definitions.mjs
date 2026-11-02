import {existsSync,readFileSync} from 'node:fs';
import {extname} from 'node:path';

export function localDefinitionsPath(path){
 const extension=extname(path);
 return path.slice(0,path.length-extension.length)+'.local'+extension;
}

export function loadCapabilityDefinitions(path,read=path=>readFileSync(path)){
 const localPath=localDefinitionsPath(path);
 const shared=JSON.parse(read(path));
 const local=existsSync(localPath)?JSON.parse(read(localPath)):{capabilities:[]};
 for(const definitions of [shared,local]){
  if(!Array.isArray(definitions.capabilities)||definitions.capabilities.length>1000)throw Error('Invalid capability list');
  const ids=new Set();
  for(const capability of definitions.capabilities){
   if(!capability||typeof capability.id!=='string'||!/^[a-z0-9][a-z0-9._-]{0,99}$/.test(capability.id)||ids.has(capability.id))throw Error('Invalid or duplicate capability identity');
   ids.add(capability.id);
  }
 }
 const merged=new Map(shared.capabilities.map(capability=>[capability.id,capability]));
 for(const capability of local.capabilities)merged.set(capability.id,capability);
 if(merged.size>1000)throw Error('Invalid capability list');
 return {...shared,capabilities:[...merged.values()]};
}