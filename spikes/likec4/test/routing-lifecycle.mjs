import {chromium} from 'playwright';
import assert from 'node:assert/strict';
import {mkdir,writeFile,readFile,readdir} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import os from 'node:os';
const count=Number(process.env.TRACE_MOVES??12),runs=Number(process.env.TRACE_RUNS??1);
assert.ok(Number.isInteger(count)&&count>0&&count<=1000&&Number.isInteger(runs)&&runs>0&&runs<=10);
const url=process.env.TRACE_URL??'http://host.docker.internal:5092/?routingTrace=1';
const output='/work/.sandbox-cache/likec4-browser';await mkdir(output,{recursive:true});
async function files(dir){const result=[];for(const entry of await readdir(dir,{withFileTypes:true})){const path=`${dir}/${entry.name}`;result.push(...(entry.isDirectory()?await files(path):[path]));}return result;}
const hash=createHash('sha256');for(const path of [...await files('src'),'package-lock.json','tsconfig.json','tsconfig.worker.json'].sort()){hash.update(path);hash.update(await readFile(path));}
const sourceHash=hash.digest('hex'),browser=await chromium.launch({headless:true,args:['--no-sandbox']});
const results=[];
try {
 for(const [width,height] of [[1920,1080],[5120,2160]])for(let run=0;run<runs;run++){
  const page=await browser.newPage({viewport:{width,height}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);
  let frame=page.mainFrame();
  if(url.includes('#/architecture')){
   const navigation=page.waitForEvent('framenavigated',{predicate:frame=>frame.url().includes('/likec4/')});
   await page.getByRole('tab',{name:'LikeC4 · experiment',exact:true}).click();
   frame=await navigation;
  }
  assert.ok(frame,'diagram frame available');
  await frame.locator('[data-routing-state=ready]').waitFor({timeout:30000});
  await frame.getByRole('switch',{name:'Routing diagnostics',exact:true}).check();
  await frame.getByRole('button',{name:'Minimize ledger',exact:true}).click();
  await frame.evaluate(()=>window.ceRoutingTrace.reset());
  const cdp=process.env.TRACE_CPU==='1'?await page.context().newCDPSession(page):null;
  if(cdp){await cdp.send('Profiler.enable');await cdp.send('Profiler.start');}
  const samples=[];
  for(let i=0;i<count;i++){
   const node=frame.locator('.react-flow__node[data-id="repository_0.n1"]');
   const box=await node.boundingBox(),before=await node.getAttribute('style');assert.ok(box);
   const previous=await frame.evaluate(()=>window.ceRoutingTrace.snapshot().records.at(-1)?.id??0);
   await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
   await page.mouse.move(box.x+box.width/2+(i%2?-30:30),box.y+box.height/2+(i%2?-8:8),{steps:3});await page.mouse.up();
   await frame.waitForFunction(id=>window.ceRoutingTrace.snapshot().records.some(r=>r.id>id&&r.kind==='node'&&r.status==='complete'),previous,{timeout:15000});
   const record=await frame.evaluate(()=>window.ceRoutingTrace.snapshot().records.findLast(r=>r.kind==='node'&&r.status==='complete'));
   await writeFile(`${output}/routing-last-sample.json`,JSON.stringify(record,null,2));
   assert.notEqual(await node.getAttribute('style'),before,'must move a node');
   for(const name of ['release-to-snapshot','worker-startup-and-request-delivery','worker-total','result-delivery','result-to-native-view','release-to-frame-opportunity','order0.search','order2.quality'])assert.ok(record.spans.some(s=>s.name===name)||(name==='result-delivery'&&record.invalidTimings?.some(s=>s.name===name&&s.reason==='reversed'&&s.start-s.end<1)),`missing or invalid ${name}`);
   assert.equal(record.events.filter(e=>e.name==='pointer.release').length,1);
   samples.push(record);console.log(`${width} run${run+1} move${i+1}: ${record.spans.find(s=>s.name==='release-to-frame-opportunity').end-record.spans.find(s=>s.name==='release-to-frame-opportunity').start}ms`);
  }
  if(cdp){const {profile}=await cdp.send('Profiler.stop');await writeFile(`${output}/routing-${width}-${run}.cpuprofile`,JSON.stringify(profile));await cdp.detach();}
  const summary={};
  for(const name of [...new Set(samples.flatMap(r=>r.spans.map(s=>s.name)))]){
   const values=samples.flatMap(r=>r.spans.filter(s=>s.name===name).map(s=>s.end-s.start)).sort((a,b)=>a-b);
   summary[name]={samples:values.length,p50:values[Math.ceil(values.length*.5)-1],p95:values[Math.ceil(values.length*.95)-1],p99:values[Math.ceil(values.length*.99)-1]};
  }
  assert.deepEqual(errors,[]);
  results.push({width,height,run,samples,summary,browser:browser.version(),environment:await frame.evaluate(()=>({userAgent:navigator.userAgent,logicalCPUs:navigator.hardwareConcurrency,devicePixelRatio,visibility:document.visibilityState})),diagnostics:await frame.evaluate(()=>window.ceRoutingTrace.snapshot())});
  await page.close();
 }
 await writeFile(`${output}/routing-lifecycle.json`,JSON.stringify({sourceHash,os:{platform:os.platform(),release:os.release(),arch:os.arch(),cpu:os.cpus()[0]?.model},count,runs,results,limitation:'Final endpoint is two frame opportunities after native view application, not exact paint. Small default runs are diagnostic only; CPU profiling changes timings. Browser and container hardware are not a physical display benchmark.'},null,2));
 console.log(JSON.stringify(results.map(({width,run,summary})=>({width,run,summary})),null,2));
}finally{await browser.close();}
