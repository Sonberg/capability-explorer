// Actual served retirement journey; API/export behavior remains covered by BindingPreviewTests.
import { chromium } from '../spikes/likec4/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
const base = process.argv[2] ?? 'http://localhost:5091';
assert.equal(new URL(base).hostname, 'localhost', 'use a loopback URL for the actual browser security boundary');
const browser = await chromium.launch({headless:true,args:['--no-sandbox','--host-resolver-rules=MAP localhost host.docker.internal']});
const output = '.sandbox-cache/binding-preview-browser';
await mkdir(output,{recursive:true});
try {
  const page = await browser.newPage({viewport:{width:1600,height:1000}});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`${base}/#/binding-preview`);
  await page.getByRole('textbox', {name:'Search symbols',exact:true}).waitFor();
  assert.equal(await page.getByRole('heading', {name:'Review integration bindings'}).count(), 0);
  assert.equal(await page.getByRole('navigation', {name:'Views',exact:true}).getByRole('link', {name:'Binding review',exact:true}).count(), 0);
  assert.equal(new URL(page.url()).hash, '#/binding-preview', 'retain existing unsupported-route behavior, not a redirect');
  const response = await page.request.get(`${base}/api/binding-preview`);
  assert.equal(response.status(), 200, 'retiring the page must preserve the read-only binding API');
  assert.ok(await response.json());
  assert.deepEqual(errors, []);
  await page.screenshot({path:`${output}/retired-route.png`,fullPage:true});
  await writeFile(`${output}/receipt.json`,JSON.stringify({
    base, retiredRoute:'binding-preview', bindingApiStatus:response.status(), databaseWrites:false,
  },null,2)+'\n');
  console.log('binding preview retirement browser passed; served Explore fallback and preserved GET API; no writes');
} finally { await browser.close(); }
