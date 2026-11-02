import {loadCapabilityDefinitions} from './capability-definitions.mjs';
try {
const data=loadCapabilityDefinitions(process.argv[2]);
if(!Array.isArray(data.capabilities)||data.capabilities.length>1000)throw Error('Invalid capability list');
const ids=new Set();
for(const cap of data.capabilities){
 if(!cap||typeof cap.id!=='string'||!/^[a-z0-9][a-z0-9._-]{0,99}$/.test(cap.id)||ids.has(cap.id))throw Error('Invalid or duplicate capability identity');
 ids.add(cap.id);
}
for(const id of [...ids].sort())console.log(id);

} catch { console.error('Analysis configuration rejected');process.exitCode=1; }
