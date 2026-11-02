#!/usr/bin/env node
// Replay exact captured application statements/parameters inside a read-only EXPLAIN transaction.
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';
import {hash,root,safeOutput} from './issue28-generate.mjs';
import {requireDockerRuntime} from '../lib/docker-only.mjs';

export function capturedQueries(text) {
  const lines=text.split('\n'),queries=[];
  for(let i=0;i<lines.length;i++) {
    const match=lines[i].match(/execute <unnamed>: (with selected as \(.*)$/);
    if(!match)continue;
    let sql=match[1];
    while(i+1<lines.length&&/^\t/.test(lines[i+1]))sql+='\n'+lines[++i].slice(1);
    const params=lines[i+1]?.match(/DETAIL:  Parameters: (.*)$/)?.[1];
    if(!params)throw Error('Captured statement is missing its parameters');
    const values=[...params.matchAll(/\$(\d+) = ('(?:[^']|'')*'|NULL)(?=, \$|$)/g)];
    if(!values.length||values.some((m,n)=>Number(m[1])!==n+1))throw Error('Parameter capture is incomplete');
    if(sql.includes(';')||/\b(insert|update|delete|create|alter|drop|copy|call)\b/i.test(sql))throw Error('Expected a single read statement');
    const kind=sql.includes('ranked as')?'search':sql.includes('join edge e')?'adjacency':sql.includes('join node n')?'symbol':null;
    if(kind)queries.push({kind,sql,values:values.map(m=>m[2])});
  }
  if(!queries.length)throw Error('No exact application projection statements found');
  return queries;
}
export function explain({log,container,database,out}) {
  requireDockerRuntime('database EXPLAIN benchmark');
  if(!/^ce-issue28-[a-zA-Z0-9_.-]+$/.test(container)||!/^ce(?:_[a-zA-Z0-9_]+)?$/.test(database))throw Error('Use an isolated ce-issue28 container/database');
  const input=fs.readFileSync(log,'utf8');if(input.length>4*1024*1024)throw Error('Capture log exceeds 4MiB');
  const label=execFileSync('docker',['inspect','--format','{{index .Config.Labels "ce.workstream"}}',container],{encoding:'utf8'}).trim();
  if(!label.startsWith('issue28'))throw Error('Container is not labelled as issue28 work');
  const reports=[];
  for(const [i,q]of capturedQueries(input).entries()) {
    const name='issue28_explain_'+i;
    const command=`BEGIN READ ONLY; SET LOCAL statement_timeout='5s'; SET LOCAL lock_timeout='1s'; PREPARE ${name} AS ${q.sql};\nSELECT parameter_types::text FROM pg_prepared_statements WHERE name='${name}';\nEXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) EXECUTE ${name}(${q.values.join(',')}); ROLLBACK;`;
    const result=execFileSync('docker',['exec','-i',container,'psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','ce','-d',database],{input:command,encoding:'utf8',timeout:15000,maxBuffer:4*1024*1024});
    const split=result.indexOf('\n'),parameterTypes=result.slice(0,split),plan=JSON.parse(result.slice(split+1));
    reports.push({...q,sqlSha256:hash(q.sql),parameterTypes,plan});
  }
  const output=path.resolve(root,out);safeOutput(path.dirname(output));fs.mkdirSync(path.dirname(output),{recursive:true});
  const report={version:1,status:'EXACT_CAPTURED_SQL_REPLAY',utc:new Date().toISOString(),container,database,logSha256:hash(input),runnerSha256:hash(fs.readFileSync(fileURLToPath(import.meta.url))),scope:'Captured application SQL verbatim and logged values, inferred PREPARE parameter types. Separate read-only custom plans, not the original Npgsql execution plan or startup/pool metrics.',queries:reports};
  fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});
  return report;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try{const o={};for(let i=2;i<process.argv.length;i+=2)o[process.argv[i].replace(/^--/,'')]=process.argv[i+1];const r=explain(o);console.log(JSON.stringify(r.queries.map(q=>({kind:q.kind,executionMs:q.plan[0]['Execution Time']}))));}
  catch(e){console.error(e.message);process.exitCode=1;}
}
