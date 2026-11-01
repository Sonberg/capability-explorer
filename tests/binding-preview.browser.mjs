// Actual served fixture UI journey; run in the existing Playwright sandbox image.
// Chromium resolves localhost to the host bridge while preserving the real loopback Host/Origin boundary.
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
const base = process.argv[2] ?? 'http://localhost:5091';
assert.equal(new URL(base).hostname, 'localhost', 'use a loopback URL for the actual browser security boundary');
const revisions = JSON.parse(await readFile(new URL('../fixtures/revisions.lock.json', import.meta.url),'utf8'));
const hint = '@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL';
const browser = await chromium.launch({headless:true,args:['--no-sandbox','--host-resolver-rules=MAP localhost host.docker.internal']});
const output = '.sandbox-cache/binding-preview-browser';
await mkdir(output,{recursive:true});
const receipt = {base, assertions:0, simulated:[], databaseWrites:false};
const check = (condition,message) => { assert.ok(condition,message); receipt.assertions++; };
try {
  const page = await browser.newPage({viewport:{width:1600,height:1000}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(({pricing,forecast}) => {
    localStorage.setItem('ce.capability','""');
    localStorage.setItem('ce.overrides',JSON.stringify({'*':{'pricing-node':pricing,'forecast-cs':forecast}}));
  },{pricing:revisions['pricing-node']['v1-base'],forecast:revisions['forecast-cs']['v1-base']});
  await page.goto(`${base}/#/binding-preview`);
  await page.getByRole('heading',{name:'Review integration bindings'}).waitFor();
  const target=page.getByLabel(`Destination for ${hint}`,{exact:true});
  await target.fill('forecast-cs/Forecast.Api');
  const card=page.locator('article').filter({has:target});
  const exportButton=page.getByRole('button',{name:'Export reviewed proposal'});
  await page.getByRole('button',{name:'Preview mappings',exact:true}).click();
  await card.getByRole('status').waitFor();
  check(await exportButton.isDisabled(),'unreviewed proposal cannot be exported');
  check((await card.getByRole('status').innerText()).includes('human assertion'),'endpoint match must remain a human assertion');
  await card.getByRole('checkbox').check();
  await page.getByRole('button',{name:'Preview mappings',exact:true}).click();
  await page.waitForFunction(()=>[...document.querySelectorAll('button')].some(b=>b.textContent==='Export reviewed proposal'&&!b.disabled));
  const downloaded=page.waitForEvent('download');await exportButton.click();
  const download=await downloaded;const path=await download.path();const document=JSON.parse(await readFile(path,'utf8'));
  check(document.version===1&&document.reviewState==='reviewed-export-not-adopted','export must be a non-adopted version1 map');
  check(document.bindings.some(b=>b.hint===hint&&b.target==='forecast-cs/Forecast.Api'),'reviewed choice must be exported');
  check(document.sources.every(s=>/^[0-9a-f]{40}$/.test(s.commitSha)&&/^[0-9a-f]{64}$/.test(s.artifactHash)),'export binds exact revisions/artifacts');
  await card.getByLabel(`Review note for ${hint}`,{exact:true}).fill('Changed rationale');
  check(!(await card.getByRole('checkbox').isChecked()),'editing invalidates review');
  check(await exportButton.isDisabled(),'editing invalidates export');
  await page.getByRole('button',{name:'Reload source',exact:true}).click();
  await target.waitFor();check(await target.inputValue()==='','reload clears proposed destination');
  check(errors.length===0,`unexpected browser errors: ${errors.join('; ')}`);
  await page.screenshot({path:`${output}/review.png`,fullPage:true});
  await writeFile(`${output}/receipt.json`,JSON.stringify(receipt,null,2)+'\n');
  console.log(`binding preview browser passed: ${receipt.assertions} actual UI/export assertions; no database writes`);
} finally { await browser.close(); }
