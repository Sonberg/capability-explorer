import {chromium} from 'playwright';
import assert from 'node:assert/strict';
const browser=await chromium.launch({headless:true,args:['--no-sandbox']});
try {
 const page=await browser.newPage({viewport:{width:1920,height:1080}});
 await page.goto(process.env.TRACE_URL??'http://host.docker.internal:5092/');
 const ready=()=>page.locator('[data-routing-state=ready]').waitFor({timeout:20000});
 await ready();await page.getByRole('button',{name:'Minimize ledger',exact:true}).click();
 const snapshot=()=>page.evaluate(()=>window.ceRoutingTrace.snapshot());
 async function move(id,dx=20,group=false){const generation=await page.locator('.canvas').getAttribute('data-routing-generation');const node=page.locator(`.react-flow__node[data-id="${id}"]`),box=await node.boundingBox();assert.ok(box);const x=box.x+box.width/2,y=group?box.y+14:box.y+box.height/2;await page.mouse.move(x,y);await page.mouse.down();await page.mouse.move(x+dx,y+6,{steps:3});await page.mouse.up();return generation;}
 const initial=await move('repository_0.n1');await page.locator(`.canvas:not([data-routing-generation="${initial}"])`).waitFor({timeout:15000});await ready();assert.equal((await snapshot()).records.length,0,'disabled diagnostics collect nothing');
 await page.getByRole('switch',{name:'Routing diagnostics',exact:true}).check();
 await page.evaluate(()=>window.ceRoutingTrace.reset());
 await move('repository_0',20,true);
 // An unrelated pointer release during the snapshot delay must not replace the drag release.
 await page.locator('.current-viewpoint').click();
 await page.waitForFunction(()=>window.ceRoutingTrace.snapshot().records.some(r=>r.kind==='group'&&r.status==='complete'),{},{timeout:15000});
 let records=(await snapshot()).records;
 const group=records.find(r=>r.kind==='group'&&r.status==='complete');assert.equal(group.events.filter(e=>e.name==='pointer.release').length,1);
 await move('repository_0.n1');await move('repository_0.n1',-20);
 await page.waitForFunction(()=>window.ceRoutingTrace.snapshot().records.filter(r=>r.kind==='node').some(r=>r.status==='complete'),{},{timeout:15000});
 records=(await snapshot()).records;
 assert.ok(records.some(r=>r.kind==='node'&&r.status==='cancelled'),'superseded gesture recorded');
 assert.equal(records.at(-1).status,'complete','latest gesture completes');
 await move('repository_0.n1');
 const navigating=(await snapshot()).records.at(-1).id;
 await page.locator('[data-view-id="index"]').first().click();await ready();
 assert.equal((await snapshot()).records.find(r=>r.id===navigating).status,'cancelled','navigation cannot finish a drag');
 await page.locator('[data-view-id="flow"]').first().click();await ready();
 // Disable routing while the worker is active; a late result must never complete this generation.
 await move('repository_0.n1');
 await page.waitForFunction(()=>window.ceRoutingTrace.snapshot().records.at(-1)?.events.some(e=>e.name.endsWith('worker.construct')),{},{timeout:15000});
 await page.getByRole('switch',{name:'Orthogonal',exact:true}).uncheck();
 await page.locator('[data-routing-state=off]').waitFor();
 assert.equal((await snapshot()).records.at(-1).status,'cancelled');
 await page.getByRole('switch',{name:'Orthogonal',exact:true}).check();await ready();
 await move('repository_0.n1',-20);
 await page.waitForFunction(()=>window.ceRoutingTrace.snapshot().records.at(-1)?.events.some(e=>e.name.endsWith('worker.construct')),{},{timeout:15000});
 await page.getByRole('switch',{name:'Routing diagnostics',exact:true}).uncheck();await ready();
 assert.equal((await snapshot()).records.at(-1).status,'cancelled','disable finalizes pending trace');
 await page.getByRole('switch',{name:'Routing diagnostics',exact:true}).check();
 // Fail worker script loading on a fresh layout, exercising the real worker.onerror boundary.
 await page.route('**/routing.worker-*.js',route=>route.abort());
 await move('repository_0.n1',-20);
 await page.locator('[data-routing-state=error]').waitFor({timeout:15000});
 assert.equal((await snapshot()).records.at(-1).status,'error');
 await page.unroute('**/routing.worker-*.js');
 await page.getByRole('button',{name:'Reset layout',exact:true}).click();await ready();
 await page.getByRole('switch',{name:'Routing diagnostics',exact:true}).uncheck();
 const count=(await snapshot()).records.length;
 const disabled=await move('repository_0.n1');await page.locator(`.canvas:not([data-routing-generation="${disabled}"])`).waitFor({timeout:15000});await ready();assert.equal((await snapshot()).records.length,count);
 console.log('PASS: default off, group, unrelated release, rapid drags, cancellation, worker failure, reset and disabled tracing');
}finally{await browser.close();}
