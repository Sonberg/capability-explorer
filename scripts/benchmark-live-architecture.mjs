// Run through scripts/sandbox with the pinned Playwright image. The preview
// processes belong to the caller; this script starts or publishes no server.
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {chromium} from '../spikes/likec4/node_modules/playwright/index.mjs';

const usage = `Usage: node scripts/benchmark-live-architecture.mjs [--candidate-only]

Environment:
  CE_LIVE_BENCHMARK_BASELINE_URL   Accepted spike; default http://host.docker.internal:5093/likec4/index.html?embed=1
  CE_LIVE_BENCHMARK_CANDIDATE_URL  Explorer; default http://host.docker.internal:5096/#/architecture?level=system
  CE_LIVE_BENCHMARK_SAMPLES        Integer from 1 to 100; default 30

The candidate receives the exact tracked historical fixture as its architecture
API response. Compilation, rendering, pointer drags and routing workers are real.
Results measure routing and a DOM status transition, never final browser paint.`;

const arguments_ = process.argv.slice(2);
if (arguments_.includes('--help')) {
  console.log(usage);
  process.exit(0);
}
assert.ok(arguments_.every(argument => argument === '--candidate-only'), usage);
const candidateOnly = arguments_.includes('--candidate-only');
const sampleText = process.env.CE_LIVE_BENCHMARK_SAMPLES ?? '30';
assert.match(sampleText, /^[1-9]\d{0,2}$/, 'CE_LIVE_BENCHMARK_SAMPLES must be an integer from 1 to 100');
const sampleCount = Number(sampleText);
assert.ok(sampleCount <= 100, 'CE_LIVE_BENCHMARK_SAMPLES must be at most 100');

function previewUrl(value, candidate) {
  const url = new URL(value);
  assert.ok(['http:', 'https:'].includes(url.protocol), 'Preview URL must use HTTP or HTTPS');
  assert.ok(!url.username && !url.password, 'Preview URL must not contain credentials');
  if (candidate && !url.hash) url.hash = '/architecture?level=system';
  return url.href;
}

const baselineUrl = previewUrl(process.env.CE_LIVE_BENCHMARK_BASELINE_URL
  ?? 'http://host.docker.internal:5093/likec4/index.html?embed=1', false);
const candidateUrl = previewUrl(process.env.CE_LIVE_BENCHMARK_CANDIDATE_URL
  ?? 'http://host.docker.internal:5096/#/architecture?level=system', true);
const fixturePath = new URL('../spikes/likec4/test/all-repositories.graph.json', import.meta.url);
const fixtureBody = await readFile(fixturePath, 'utf8');
const fixture = JSON.parse(fixtureBody);
const sha256 = value => createHash('sha256').update(value).digest('hex');
const output = new URL('../.sandbox-cache/live-architecture-benchmark/', import.meta.url);
await mkdir(output, {recursive:true});

const scenarios = [
  ...(!candidateOnly ? [{name:'accepted', url:baselineUrl, viewport:{width:1086, height:718}}] : []),
  {name:'candidate', url:candidateUrl, viewport:{width:1440, height:900}},
];
const receipt = {
  version:2,
  startedAt:new Date().toISOString(),
  sampleCount,
  fixture:{path:'spikes/likec4/test/all-repositories.graph.json', sha256:sha256(fixtureBody),
    sourceNodes:fixture.nodes.length, sourceEdges:fixture.edges.length},
  benchmarkScriptSha256:sha256(await readFile(new URL(import.meta.url))),
  contract:{sourceNodeId:'repo:api-cs', acceptedRenderedNodeId:'repository_0.n1',
    movement:{x:40, y:20, units:'model coordinates', alternating:true}},
  simulation:'Only the candidate system architecture API response is replaced with the exact historical fixture bytes. Browser compilation, rendering, pointer drags and routing workers execute normally.',
  domTimingMethod:'An in-page MutationObserver timestamps data-routing-state=ready for each new generation. Locator polling only waits; its delay is excluded.',
  limitations:[
    'Routing-ready is a React DOM status attribute, not proof of native diagram application or final browser paint.',
    'This finite local sample is not a general latency guarantee. CPU contention and browser scheduling can affect it.',
    'Viewport sizes preserve the accepted embedded spike and integrated Explorer comparison; they are not identical full-page sizes.',
    'Candidate-only runs do not refresh the accepted baseline. Compare only receipts with the same fixture and method.',
  ],
  samples:[],
  qualityChecks:[],
  procedurePassed:false,
  comparison:null,
  finalPaint:{measured:false, parity:'unverified'},
};

function statistics(values) {
  const ordered = [...values].sort((left, right) => left - right);
  return {n:ordered.length, min:ordered[0], median:ordered[Math.floor(ordered.length / 2)],
    p95:ordered[Math.ceil(ordered.length * 0.95) - 1], max:ordered.at(-1)};
}

function compareTimings(samples) {
  const accepted = samples.find(sample => sample.name === 'accepted');
  const candidate = samples.find(sample => sample.name === 'candidate');
  if (!accepted || !candidate) {
    return {available:false, status:'not_compared', sampledTimingParity:null,
      reason:'A completed accepted baseline and candidate are both required; candidate-only runs make no timing-parity claim.'};
  }
  const metrics = Object.fromEntries(['preview', 'refinement', 'releaseToRoutingReady'].map(metric => {
    const baseline = accepted[metric], value = candidate[metric];
    return [metric, {
      accepted:{medianMs:baseline.median, p95Ms:baseline.p95},
      candidate:{medianMs:value.median, p95Ms:value.p95},
      medianDeltaMs:value.median - baseline.median,
      p95DeltaMs:value.p95 - baseline.p95,
      p95Ratio:baseline.p95 === 0 ? null : value.p95 / baseline.p95,
      medianRegressionObserved:value.median > baseline.median,
      p95RegressionObserved:value.p95 > baseline.p95,
    }];
  }));
  const sampledTimingParity = Object.values(metrics).every(metric => !metric.medianRegressionObserved && !metric.p95RegressionObserved);
  return {available:true, status:sampledTimingParity ? 'no_sampled_timing_regression' : 'timing_regression_observed',
    sampledTimingParity, rule:'Each candidate median and p95 must be no slower than the accepted sample; no tolerance is applied.',
    scope:'These finite routing and DOM-status samples do not establish final-paint parity.', metrics};
}

async function assetHashes(page, urls) {
  return Promise.all([...urls].sort().map(async url => {
    const response = await page.request.get(url);
    assert.ok(response.ok(), `Served JavaScript asset unavailable: ${new URL(url).pathname}`);
    return {url, sha256:sha256(await response.body())};
  }));
}

async function sourceExport(page) {
  await page.getByRole('button', {name:'Diagram source', exact:true}).click();
  const source = await page.locator('pre').last().textContent() ?? '';
  for (const record of [...fixture.nodes, ...fixture.edges]) {
    assert.ok(source.includes(record.id), `Source export omitted fixture ID ${record.id}`);
  }
  const commits = new Set(fixture.edges.flatMap(edge => edge.facts ?? [])
    .flatMap(fact => fact.evidence ?? []).map(evidence => evidence.commitSha).filter(Boolean));
  for (const commit of commits) assert.ok(source.includes(commit), 'Source export omitted a fixture evidence revision');
  await page.getByRole('button', {name:'Diagram source', exact:true}).click();
  // The accepted spike schedules an automatic fit 200 ms after a resize.
  // This pause precedes measurements and lets the closed source pane settle.
  await page.waitForTimeout(250);
  return sha256(source);
}

async function labelOverlap(page) {
  return page.locator('.likec4-edge-label').evaluateAll(elements => {
    const boxes = elements.map(element => ({...element.getBoundingClientRect().toJSON(),
      id:element.dataset.edgeId, text:element.textContent}));
    const pairs = [];
    for (let first = 0; first < boxes.length; first++) {
      for (let second = first + 1; second < boxes.length; second++) {
        const left = boxes[first], right = boxes[second];
        if (left.width && right.width && left.x < right.right && right.x < left.right
          && left.y < right.bottom && right.y < left.bottom) {
          pairs.push({first:left.id, second:right.id, firstText:left.text, secondText:right.text});
        }
      }
    }
    return {labels:boxes.length, pairs};
  });
}

async function runScenario(browser, scenario) {
  const page = await browser.newPage({viewport:scenario.viewport});
  page.setDefaultTimeout(30_000);
  const errors = [], javascriptUrls = new Set();
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname.endsWith('.js')) javascriptUrls.add(url.href);
  });
  await page.addInitScript(() => {
    localStorage.clear();
    localStorage.setItem('ce.capability', '""');
    localStorage.setItem('ce.overrides', '{}');
    document.addEventListener('pointerup', () => {window.__ceBenchmarkRelease = performance.now();}, true);
    const NativeWorker = window.Worker;
    window.Worker = class extends NativeWorker {
      postMessage(message, ...arguments_) {
        if (message?.view) window.__ceBenchmarkBounds = message.view.bounds;
        return super.postMessage(message, ...arguments_);
      }
    };
  });
  if (scenario.name === 'candidate') {
    await page.route('**/api/architecture?**', route => {
      const url = new URL(route.request().url());
      return url.searchParams.get('level') === 'system'
        ? route.fulfill({status:200, contentType:'application/json', body:fixtureBody}) : route.continue();
    });
  }
  try {
    console.log(`Starting ${scenario.name}: ${sampleCount} source-bound drags`);
    await page.goto(scenario.url);
    await page.locator('[data-routing-state=ready]').waitFor();
    const scriptUrls = await page.locator('script[src]').evaluateAll(elements => elements.map(element => element.src));
    const build = await assetHashes(page, scriptUrls);
    const sourceSha256 = await sourceExport(page);
    const node = scenario.name === 'accepted'
      ? page.locator('.react-flow__node[data-id="repository_0.n1"]')
      : page.locator(`.react-flow__node-element[data-source-node-ids='["repo:api-cs"]']`);
    await node.waitFor();
    assert.ok((await node.textContent()).includes('api-cs'), 'The drag target must represent the API repository');
    const initialLeafPixels = (await node.boundingBox()).height;
    if (scenario.name === 'candidate') await page.getByRole('button', {name:'Fit diagram', exact:true}).click();
    await page.getByRole('button', {name:'Minimize ledger', exact:true}).click();
    const labelOverlaps = await labelOverlap(page);
    receipt.qualityChecks.push({scenario:scenario.name, phase:'before_drag', ...labelOverlaps});
    assert.ok(labelOverlaps.labels > 0, 'The benchmark must measure actual rendered labels');
    assert.deepEqual(labelOverlaps.pairs, [], 'Rendered relationship label boxes overlap');
    const canvas = page.locator('.canvas');
    await canvas.evaluate(element => {
      window.__ceBenchmarkReadyEvents = [];
      new MutationObserver(() => {
        if (element.dataset.routingState === 'ready') {
          window.__ceBenchmarkReadyEvents.push({generation:Number(element.dataset.routingGeneration), at:performance.now()});
        }
      }).observe(element, {attributes:true, attributeFilter:['data-routing-state', 'data-routing-generation']});
    });
    const measurements = [];
    for (let index = 0; index < sampleCount; index++) {
      const before = await canvas.getAttribute('data-routing-generation');
      const box = await node.boundingBox(), canvasBox = await canvas.boundingBox();
      assert.ok(box && canvasBox, 'Node and canvas geometry must be available');
      const x = box.x + box.width / 2, y = box.y + box.height / 2;
      assert.ok(x > canvasBox.x && x < canvasBox.x + canvasBox.width
        && y > canvasBox.y && y < canvasBox.y + canvasBox.height, 'Fit must leave the pointer target inside the canvas');
      const scale = await node.evaluate(element => element.getBoundingClientRect().width / parseFloat(element.style.width));
      assert.ok(Number.isFinite(scale) && scale > 0, 'The node must have a finite rendered scale');
      const direction = index % 2 === 0 ? 1 : -1;
      await page.mouse.move(x, y);
      await page.mouse.down();
      await page.mouse.move(x + 40 * scale * direction, y + 20 * scale * direction, {steps:4});
      await page.mouse.up();
      await page.locator(`.canvas:not([data-routing-generation="${before}"])[data-routing-state=ready]`).waitFor();
      const measurement = await canvas.evaluate(element => {
        const generation = Number(element.dataset.routingGeneration);
        const ready = window.__ceBenchmarkReadyEvents.findLast(event => event.generation === generation);
        if (!ready) throw new Error('No routing-ready mutation timestamp for the completed generation');
        return {generation, previewMs:Number(element.dataset.previewMs), refinementMs:Number(element.dataset.refinementMs),
          releaseToRoutingReadyMs:ready.at - window.__ceBenchmarkRelease,
          observedWaitMs:performance.now() - window.__ceBenchmarkRelease, inputBounds:window.__ceBenchmarkBounds};
      });
      for (const metric of ['previewMs', 'refinementMs', 'releaseToRoutingReadyMs']) {
        assert.ok(Number.isFinite(measurement[metric]) && measurement[metric] >= 0, `Invalid ${metric} measurement`);
      }
      measurements.push(measurement);
      if ((index + 1) % 10 === 0) console.log(`${scenario.name}: ${index + 1}/${sampleCount} drags`);
    }
    // Let native DOM layout apply before checking actual text rectangles. Frame
    // opportunities are not treated as measurements or proof of final paint.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const postDragLabelOverlaps = await labelOverlap(page);
    receipt.qualityChecks.push({scenario:scenario.name, phase:'after_final_drag', ...postDragLabelOverlaps});
    assert.equal(postDragLabelOverlaps.labels, labelOverlaps.labels, 'Dragging must retain every rendered relationship label');
    assert.deepEqual(postDragLabelOverlaps.pairs, [], 'Rendered relationship label boxes overlap after the final drag');
    assert.deepEqual(errors, [], 'The benchmark encountered browser errors');
    await page.screenshot({path:new URL(`${scenario.name}.png`, output).pathname, fullPage:true});
    return {...scenario, build, sourceSha256, javascriptAssets:await assetHashes(page, javascriptUrls),
      initialLeafPixels, labelOverlaps, postDragLabelOverlaps, measurements,
      preview:statistics(measurements.map(value => value.previewMs)),
      refinement:statistics(measurements.map(value => value.refinementMs)),
      releaseToRoutingReady:statistics(measurements.map(value => value.releaseToRoutingReadyMs))};
  } finally {
    await page.close();
  }
}

const browser = await chromium.launch({headless:true, args:['--no-sandbox']});
try {
  // Sequential runs avoid competing with each other for browser/worker CPU.
  for (const scenario of scenarios) receipt.samples.push(await runScenario(browser, scenario));
  receipt.procedurePassed = true;
} catch (error) {
  receipt.error = error instanceof Error ? error.message : String(error);
  throw error;
} finally {
  receipt.finishedAt = new Date().toISOString();
  receipt.comparison = compareTimings(receipt.samples);
  const path = new URL(candidateOnly ? 'candidate.json' : 'comparison.json', output);
  await writeFile(path, JSON.stringify(receipt, null, 2));
  console.log(JSON.stringify({...receipt, samples:receipt.samples.map(({measurements, ...sample}) => sample)}, null, 2));
  await browser.close();
}
