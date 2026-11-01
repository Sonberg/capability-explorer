import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { extract } from '../src/extract/extractor.ts';
import { validateArtifact } from '../src/contracts/validateArtifact.ts';
const marker = 'CE_SYNTHETIC_CREDENTIAL';
function run(code: string) {
  const directory = mkdtempSync(join(tmpdir(),'ce-resource-security-'));
  try {
    writeFileSync(join(directory,'package.json'),'{"name":"resource-test"}');
    writeFileSync(join(directory,'index.js'),code);
    const result = extract({source:directory,repository:'sample',commit:'0'.repeat(40)})[0]!;
    assert.equal(validateArtifact(result.jsonl).ok,true);
    assert.equal(result.jsonl.includes(marker),false,'derived artifact must not contain synthetic credential');
    return result.jsonl.trimEnd().split('\n').map(line=>JSON.parse(line));
  } finally { rmSync(directory,{recursive:true,force:true}); }
}
const bus=(value:string)=>`import {ServiceBusClient} from '@azure/service-bus';const client=new ServiceBusClient(${JSON.stringify(value)});export function send(){return client.createSender('orders');}`;
const pg=(value:string)=>`import pg from 'pg';const pool=new pg.Pool({connectionString:${JSON.stringify(value)}});export function read(){return pool.query('SELECT id FROM orders');}`;
for(const value of [
  `Endpoint=sb://user:${marker}@jobs.servicebus.windows.net/;SharedAccessKey=x`,
  `sb://jobs.servicebus.windows.net?sig=${marker}`,
  `SharedAccessKey=${marker};UnsupportedEndpoint=jobs.servicebus.windows.net`,
]) test('Service Bus malformed or credential-bearing namespace stays unresolved without raw fallback: '+value.split(':')[0],()=>{
  const records=run(bus(value));const edge=records.find(r=>r.relation==='PUBLISHES');
  assert.equal(edge.resolution,'unresolved');assert.equal(edge.to,undefined);
  assert.ok(records.some(r=>r.code==='SERVICEBUS_NAMESPACE_UNSUPPORTED'));
});
test('Service Bus standard connection string exports namespace only',()=>{
  const records=run(bus(`Endpoint=sb://jobs.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=${marker}`));
  assert.equal(records.find(r=>r.relation==='PUBLISHES').to,'chan|servicebus|jobs.servicebus.windows.net|entity/orders');
});
test('PostgreSQL credentials query and fragment never enter derived resource',()=>{
  const records=run(pg(`postgresql://user:p@${marker}@db.example/orders?password=${marker}#${marker}`));
  assert.equal(records.find(r=>r.relation==='READS').to,'sql|@unbound:sample/resource-test:db=db.example/orders|public.orders');
});
for(const database of ['order%2Farchive','order%252Farchive']) test('PostgreSQL preserves encoded database segment '+database,()=>{
  const records=run(pg(`postgres://user:pass@db.example/${database}`));
  assert.equal(records.find(r=>r.relation==='READS').to,`sql|@unbound:sample/resource-test:db=db.example/${database}|public.orders`);
});
for(const path of ['/nested/database','/before/../database','/%2e%2e/database']) test('PostgreSQL ambiguous database path is unresolved '+path,()=>{
  const records=run(pg(`postgres://db.example${path}`));
  assert.equal(records.find(r=>r.relation==='READS').to,'sql|@unbound:sample/resource-test:pool=pool|public.orders');
  assert.ok(records.some(r=>r.code==='PG_CONNECTION_UNSUPPORTED'));
});
test('Service Bus dynamic credential constructor does not copy embedded literal into props',()=>{
  const records=run(`import {ServiceBusClient} from '@azure/service-bus';const client=new ServiceBusClient('jobs.servicebus.windows.net',new (getCredentialType('${marker}'))());export function send(){return client.createSender('orders');}`);
  assert.equal(records.find(r=>r.relation==='PUBLISHES').props?.credential,undefined);
});
test('PostgreSQL dynamic expression annotation never copies embedded literal',()=>{
  const records=run('import pg from "pg";const pool=new pg.Pool({connectionString:process.env.DB});export function read(){return pool.query(`SELECT id FROM orders WHERE token = ${getToken("'+marker+'")} `);}');
  const edge=records.find(r=>r.relation==='READS');assert.equal(edge.props.sqlAnalysis,'partial');
  assert.ok(edge.props.dynamicParts[0].includes('<dynamic-expression>'));
});
