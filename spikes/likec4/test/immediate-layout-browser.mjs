import assert from 'node:assert/strict';
import {mkdir,writeFile} from 'node:fs/promises';
const {chromium}=await import(process.env.PLAYWRIGHT_MODULE??'playwright');
const browser=await chromium.launch({headless:true,args:['--no-sandbox']});
try {
  const page=await browser.newPage({viewport:{width:1920,height:1080}}),errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  await page.goto(process.env.LIKEC4_PREVIEW_URL??'http://host.docker.internal:5092');
  const canvas=page.locator('.canvas');
  await page.locator('[data-routing-state=ready]').waitFor({timeout:30000});
  await page.getByRole('button',{name:'Minimize ledger',exact:true}).click();
  const node=page.locator('.react-flow__node[data-id="repository_0.n1"]');
  const position=()=>node.evaluate(element=>{
    const transform=new DOMMatrixReadOnly(element.style.transform);
    return {x:transform.e,y:transform.f};
  });
  const initial=await position(),generation=await canvas.getAttribute('data-routing-generation');
  const box=await node.boundingBox();assert.ok(box);
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();
  await page.mouse.move(box.x+box.width/2+48,box.y+box.height/2+24,{steps:4});await page.mouse.up();
  await page.locator(`.canvas:not([data-routing-generation="${generation}"])`).waitFor({timeout:5000});
  await page.locator('[data-routing-state=ready]').waitFor({timeout:30000});
  const moved=await position();assert.notDeepEqual(moved,initial,'drag must change actual node geometry');
  const samples=[];
  async function noDeferredReroute(operation) {
    const settled=await canvas.getAttribute('data-routing-generation');
    // Native snapshot scheduling is 50ms + 1000ms; outwait both after settling.
    await page.waitForTimeout(1500);
    assert.equal(await canvas.getAttribute('data-routing-generation'),settled,`${operation}: delayed native snapshot must not reroute`);
    assert.equal(await canvas.getAttribute('data-routing-state'),'ready');
    samples.push({operation,generation:settled,position:await position()});
  }
  await noDeferredReroute('drag');
  async function history(shortcut,expected,operation) {
    const before=await canvas.getAttribute('data-routing-generation');
    await page.keyboard.press(shortcut);
    await page.locator(`.canvas:not([data-routing-generation="${before}"])`).waitFor({timeout:5000});
    await page.locator('[data-routing-state=ready]').waitFor({timeout:30000});
    const actual=await position();
    assert.ok(Math.abs(actual.x-expected.x)<=1&&Math.abs(actual.y-expected.y)<=1,`${operation}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
    await noDeferredReroute(operation);
  }
  // The Linux container uses Ctrl for LikeC4's public mod-key shortcuts.
  await history('Control+z',initial,'undo');
  await history('Control+Shift+z',moved,'redo');
  assert.deepEqual(errors,[]);
  const result={initial,moved,samples,note:'Real pointer drag and native history shortcuts; no generation change for 1500ms after each operation settles. Frame/paint latency is not asserted.'};
  await mkdir('/work/.sandbox-cache/likec4-browser',{recursive:true});
  await writeFile('/work/.sandbox-cache/likec4-browser/immediate-layout-history.json',JSON.stringify(result,null,2));
  console.log(JSON.stringify(result,null,2));
} finally {await browser.close();}
