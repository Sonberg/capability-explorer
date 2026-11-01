// Optional browser benchmark; baseline URL must serve a separately built revision.
// Measures actual SVG straight segments, excluding rounded corner arcs and shared endpoints.
import {chromium} from 'playwright';
import {writeFile,mkdir} from 'node:fs/promises';
import assert from 'node:assert/strict';
const baseline=process.env.ROUTING_BASELINE_URL;
if(!baseline)throw Error('Set ROUTING_BASELINE_URL to the baseline build URL');
function segments(d,id) {
  const tokens=d.match(/[A-Za-z]|-?(?:\d*\.)?\d+(?:e[-+]?\d+)?/g)??[],result=[];
  let current=[0,0],i=0;
  while(i<tokens.length){const command=tokens[i++],count={M:2,L:2,C:6,Q:4}[command];assert.ok(count,`Unexpected SVG command ${command}`);
    const values=tokens.slice(i,i+count).map(Number);i+=count;const end=values.slice(-2);
    if(command==='L'||command==='C'&&[0,1].some(axis=>values.filter((_,j)=>j%2===axis).every(v=>v===current[axis])))result.push({id,a:current,b:end});
    current=end;
  }
  return result.filter(({a,b})=>a[0]!==b[0]||a[1]!==b[1]);
}
function measure(paths) {
  const lines=paths.flatMap(({d,id})=>segments(d,id));let shared=0,near=0,crossings=0;
  for(let i=0;i<lines.length;i++)for(let j=i+1;j<lines.length;j++){
    const {a,b,id}=lines[i],{a:c,b:d,id:other}=lines[j];if(id===other)continue;
    const vertical=a[0]===b[0],otherVertical=c[0]===d[0];
    if(vertical===otherVertical){const axis=vertical?0:1,along=1-axis;if(Math.min(Math.max(a[along],b[along]),Math.max(c[along],d[along]))>Math.max(Math.min(a[along],b[along]),Math.min(c[along],d[along]))){if(a[axis]===c[axis])shared++;else if(Math.abs(a[axis]-c[axis])<24)near++;}}
    else {const [v,h]=vertical?[[a,b],[c,d]]:[[c,d],[a,b]];
      if(v[0][0]>Math.min(h[0][0],h[1][0])&&v[0][0]<Math.max(h[0][0],h[1][0])&&h[0][1]>Math.min(v[0][1],v[1][1])&&h[0][1]<Math.max(v[0][1],v[1][1]))crossings++;}
  }
  return {sharedSegmentPairs:shared,nearParallelPairs:near,minimumGap:24,properCrossings:crossings,straightSegments:lines.length};
}
const browser=await chromium.launch({headless:true,args:['--no-sandbox']});const report=[];
try {
  for(const [name,url] of [['baseline',baseline],['candidate','http://host.docker.internal:5091/']]){
    const page=await browser.newPage({viewport:{width:1920,height:1080}});page.setDefaultTimeout(15000);
    await page.goto(url);await page.locator('.react-flow__node').first().waitFor();
    await page.getByRole('button',{name:'Minimize ledger',exact:true}).click();
    for(const view of ['flow','vertical','objects']){
      await page.locator(`[data-view-id="${view}"]`).first().click();await page.waitForTimeout(600);
      const paths=await page.locator('.react-flow__edge').evaluateAll(edges=>edges.map(e=>({id:e.dataset.id,d:e.querySelector('.react-flow__edge-path')?.getAttribute('d')})).filter(e=>e.d));
      report.push({name,view,edges:paths.length,...measure(paths)});
      await page.screenshot({path:`/work/.sandbox-cache/likec4-browser/routing-${name}-${view}.png`});
    }
    await page.close();
  }
  console.log(JSON.stringify(report,null,2));
  for(const after of report.filter(r=>r.name==='candidate')){const before=report.find(r=>r.name==='baseline'&&r.view===after.view);assert.equal(after.edges,before.edges,'all relationships remain visible');assert.equal(after.nearParallelPairs,0,`${after.view} has parallel lines within 24 units`);assert.equal(after.sharedSegmentPairs,0,`${after.view} shares a straight segment`);assert.ok(after.properCrossings<=before.properCrossings,`${after.view} adds crossings`);if(after.view!=='objects')assert.equal(after.properCrossings,0,`${after.view} has avoidable overview crossings`);}
  await mkdir('/work/.sandbox-cache/likec4-browser',{recursive:true});
  await writeFile('/work/.sandbox-cache/likec4-browser/routing-comparison.json',JSON.stringify(report,null,2)+'\n');
} finally {await browser.close();}
