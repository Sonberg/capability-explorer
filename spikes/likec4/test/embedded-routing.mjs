import {chromium} from 'playwright';
import assert from 'node:assert/strict';
import {mkdir,writeFile} from 'node:fs/promises';
const browser=await chromium.launch({headless:true,args:['--no-sandbox']});
try {
 const page=await browser.newPage({viewport:{width:1920,height:1080}}),errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.goto('http://host.docker.internal:5090/#/architecture');
 await page.getByRole('tab',{name:'LikeC4 · experiment',exact:true}).click();
 const frame=page.frameLocator('iframe[title="LikeC4 architecture experiment"]');
 await frame.locator('[data-routing-state=ready]').waitFor({timeout:30000});
 await frame.getByRole('button',{name:'Minimize ledger',exact:true}).click();
 assert.equal(await frame.locator('.spike-heading').isVisible(),false);
 assert.equal(await frame.locator('.spike').getAttribute('data-theme'),'light');
 await page.emulateMedia({colorScheme:'dark'});
 await frame.locator('[data-theme=dark]').waitFor();
 assert.equal(await frame.getByRole('switch',{name:'Site theme · dark',exact:true}).isDisabled(),true);
 const samples=[];
 for(let i=0;i<12;i++){
   const node=frame.locator('.react-flow__node[data-id="repository_0.n1"]'),box=await node.boundingBox();
   const before=await node.getAttribute('style');
   const generation=await frame.locator('.canvas').getAttribute('data-routing-generation');
   console.log(`Drag ${i+1}`);
   await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
   const start=performance.now();
   await page.mouse.move(box.x+box.width/2+(i%2?-30:30),box.y+box.height/2+8,{steps:3});await page.mouse.up();
   await frame.locator(`.canvas:not([data-routing-generation="${generation}"])`).waitFor({timeout:10000});
   await frame.locator('[data-routing-state=ready]').waitFor({timeout:30000});
   assert.notEqual(await node.getAttribute('style'),before,'benchmark must move a node');
   samples.push({previewMs:Number(await frame.locator('.canvas').getAttribute('data-preview-ms')),workerMs:Number(await frame.locator('.canvas').getAttribute('data-refinement-ms')),settledMs:performance.now()-start});
 }
 const preview=samples.map(s=>s.previewMs).sort((a,b)=>a-b);
 const result={samples,previewP95:preview[Math.ceil(preview.length*.95)-1],previewMax:preview.at(-1),note:'Preview computation only; worker and settled timings include full refinement separately. Headless container browser, 1920x1080, five-repository fixture.'};
 assert.ok(result.previewMax<5,`preview target missed: ${result.previewMax}ms`);
 await frame.getByRole('button',{name:'Reset layout',exact:true}).click();
 await frame.locator('[data-routing-state=ready]').waitFor({timeout:30000});
 assert.equal(await frame.locator('body').evaluate(e=>getComputedStyle(e).backgroundColor),'rgb(9, 9, 11)');
 await page.screenshot({path:'/work/.sandbox-cache/likec4-browser/embedded-likec4.png'});
 await page.getByRole('tab',{name:'Architecture',exact:true}).click();
 assert.equal(await page.getByRole('tab',{name:'Architecture',exact:true}).getAttribute('aria-selected'),'true');
 assert.deepEqual(errors,[]);
 await mkdir('/work/.sandbox-cache/likec4-browser',{recursive:true});
 await writeFile('/work/.sandbox-cache/likec4-browser/interaction-performance.json',JSON.stringify(result,null,2));
 console.log(JSON.stringify(result,null,2));
} finally {await browser.close();}
