import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { access, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';
import { gzipSync } from 'node:zlib';
import { build, preview } from '../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js';
import { buildModelFrame } from '../platform/src/CapabilityExplorer.Explorer/client/vite.model-frame.ts';

const root = resolve(import.meta.dirname, '..');
const client = resolve(root, 'platform/src/CapabilityExplorer.Explorer/client');
const artifacts = resolve(root, '.sandbox-cache/issue78-evidence');
await mkdir(artifacts, { recursive: true });
const fixtureDirectory = resolve(root, 'tests/fixtures/issue78');
const staticOutput = resolve(artifacts, 'issue78-ui-production-browser');
const browserModuleCandidates = [
  resolve(root, 'spikes/likec4/node_modules/playwright/index.mjs'),
  resolve(root, '../bpmn-dmn-plan/spikes/likec4/node_modules/playwright/index.mjs')
];
let browserModulePath: string | undefined;
for (const candidate of browserModuleCandidates) {
  try {
    await access(candidate);
    browserModulePath = candidate;
    break;
  } catch {
    continue;
  }
}
assert(browserModulePath, 'Playwright must be available from an existing browser tool installation');
const { chromium } = await import(pathToFileURL(browserModulePath).href);
const bpmnXml = await readFile(resolve(fixtureDirectory, 'bpmn-representative.xml'), 'utf8');
const dmnXml = await readFile(resolve(fixtureDirectory, 'dmn-representative.xml'), 'utf8');
const lock = JSON.parse(await readFile(resolve(client, 'package-lock.json'), 'utf8'));
const lockedPackages = {
  bpmnJs: lock.packages['node_modules/bpmn-js'],
  dmnJs: lock.packages['node_modules/dmn-js']
};
assert.equal(lockedPackages.bpmnJs.version, '18.31.1');
assert.equal(lockedPackages.dmnJs.version, '18.0.1');
const sourceFiles = [
  'tests/issue78-viewer-boundary.ts',
  'platform/src/CapabilityExplorer.Explorer/client/src/models/viewer-sandbox.ts',
  'platform/src/CapabilityExplorer.Explorer/client/src/models/viewer-frame-entry.ts',
  'platform/src/CapabilityExplorer.Explorer/client/src/models/viewer-frame-entry.css',
  'platform/src/CapabilityExplorer.Explorer/client/src/models/contracts.ts',
  'platform/src/CapabilityExplorer.Explorer/client/src/views/ProcessesDecisions.tsx',
  'platform/src/CapabilityExplorer.Explorer/client/src/views/ModelEvidence.tsx',
  'platform/src/CapabilityExplorer.Explorer/client/src/App.tsx',
  'platform/src/CapabilityExplorer.Explorer/client/vite.model-frame.ts',
  'platform/src/CapabilityExplorer.Explorer/client/vite.config.ts',
  'tests/fixtures/issue78/viewer-shell-entry.ts',
  'tests/fixtures/issue78/viewer-shell.html',
  'tests/fixtures/issue78/bpmn-representative.xml',
  'tests/fixtures/issue78/dmn-representative.xml',
  'platform/src/CapabilityExplorer.Explorer/client/package-lock.json',
  'platform/src/CapabilityExplorer.Explorer/client/public/licenses/bpmn-io-viewers.txt'
];
const sourceHashes = Object.fromEntries(await Promise.all(sourceFiles.map(async file => [
  file,
  createHash('sha256').update(await readFile(resolve(root, file))).digest('hex')
])));
const bpmnHash = createHash('sha256').update(bpmnXml).digest('hex');
const dmnHash = createHash('sha256').update(dmnXml).digest('hex');
const assertions: string[] = [];
const browserRequests: string[] = [];
const externalRequests: string[] = [];
const childFrameRequests: string[] = [];
const explicitPopupNavigations: string[] = [];
const consoleErrors: string[] = [];

await build({
  configFile: false,
  root: fixtureDirectory,
  publicDir: resolve(client, 'public'),
  resolve: {
    alias: [
      { find: 'bpmn-js', replacement: resolve(client, 'node_modules/bpmn-js') },
      { find: 'dmn-js', replacement: resolve(client, 'node_modules/dmn-js') }
    ]
  },
  build: {
    outDir: staticOutput,
    emptyOutDir: true,
    sourcemap: false,
    assetsDir: 'assets',
    rollupOptions: {
      input: {
        shell: resolve(fixtureDirectory, 'viewer-shell.html')
      },
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name][extname]'
      }
    }
  },
  logLevel: 'warn'
});
await buildModelFrame(staticOutput);
const assetDirectory = resolve(staticOutput, 'assets');
const assets = await readdir(assetDirectory);
assert(assets.includes('model-viewer-frame.js') && assets.includes('model-viewer-frame.css'), `expected production child assets, got ${assets.join(', ')}`);
const javascriptAssets = await Promise.all(assets.filter(name => name.endsWith('.js')).map(name => readFile(resolve(assetDirectory, name))));
const cssAssets = await Promise.all(assets.filter(name => name.endsWith('.css')).map(name => readFile(resolve(assetDirectory, name))));
const bundle = {
  javascriptBytes: javascriptAssets.reduce((sum, item) => sum + item.byteLength, 0),
  javascriptGzipBytes: gzipSync(Buffer.concat(javascriptAssets)).byteLength,
  cssBytes: cssAssets.reduce((sum, item) => sum + item.byteLength, 0)
};

const server = await preview({
  configFile: false,
  root: fixtureDirectory,
  publicDir: false,
  build: { outDir: staticOutput },
  preview: { host: '127.0.0.1', port: 0, strictPort: false },
  logLevel: 'warn'
});
let browser: Awaited<ReturnType<typeof chromium.launch>> | undefined;
try {
  const address = server.httpServer.address();
  assert(address && typeof address !== 'string', 'Vite preview must bind a local port');
  const baseUrl = `http://127.0.0.1:${address.port}`;
  browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  const page = await browser.newPage({ viewport: { width: 1450, height: 1150 } });
  page.on('request', request => {
    browserRequests.push(request.url());
    if (request.frame() !== page.mainFrame()) childFrameRequests.push(request.url());
    if (new URL(request.url()).origin !== baseUrl) externalRequests.push(request.url());
  });
  page.on('pageerror', error => consoleErrors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  await page.route('https://example.invalid/**', route => {
    externalRequests.push(route.request().url());
    return route.abort();
  });
  await page.context().route('https://bpmn.io/**', async route => {
    explicitPopupNavigations.push(route.request().url());
    await route.abort();
  });
  await page.goto(`${baseUrl}/viewer-shell.html`);
  await page.waitForFunction(() => window.__issue78Ready === true);
  assert.equal(await page.locator('#viewer-host iframe').count(), 0);
  assertions.push('Static built shell loaded the pinned browser bundles and trusted child entry.');

  const bpmnRequest = {
    kind: 'bpmn',
    source: bpmnXml,
    scope: 'repository-scope-A',
    generation: 7,
    sourceId: 'bpmn-source-A'
  };
  await page.evaluate(request => window.__issue78Sandbox.render(request), bpmnRequest);
  await page.waitForFunction(() => window.__issue78Events.some(event => event.status === 'rendered' && event.sourceId === 'bpmn-source-A'));
  const bpmnFrame = page.frameLocator('#viewer-host iframe');
  const bpmnEvent = await page.evaluate(() => window.__issue78Events.find(event => event.status === 'rendered'));
  assert.equal(bpmnEvent.acceptedByParent, true);
  assert.deepEqual(
    { scope: bpmnEvent.scope, generation: bpmnEvent.generation, sourceId: bpmnEvent.sourceId },
    { scope: bpmnRequest.scope, generation: bpmnRequest.generation, sourceId: bpmnRequest.sourceId }
  );
  assert(bpmnEvent.nonce.length > 10, 'child status must echo a fresh per-frame nonce');
  const bpmnFrameAttributes = await page.locator('#viewer-host iframe').evaluate(frame => ({
    sandbox: frame.getAttribute('sandbox'),
    referrerPolicy: frame.getAttribute('referrerpolicy'),
    srcdoc: frame.getAttribute('srcdoc')
  }));
  assert.equal(bpmnFrameAttributes.sandbox, 'allow-scripts allow-popups allow-popups-to-escape-sandbox');
  assert.equal(bpmnFrameAttributes.referrerPolicy, 'no-referrer');
  assert(!bpmnFrameAttributes.sandbox.includes('allow-same-origin'));
  assert(!/allow-(?:top-navigation|forms)/i.test(bpmnFrameAttributes.sandbox));
  assert.match(bpmnFrameAttributes.sandbox, /allow-popups(?: |$)/);
  assert.match(bpmnFrameAttributes.sandbox, /allow-popups-to-escape-sandbox/);
  assert.match(bpmnFrameAttributes.srcdoc, /default-src 'none'/);
  assert.match(bpmnFrameAttributes.srcdoc, /connect-src 'none'/);
  assert.match(bpmnFrameAttributes.srcdoc, /img-src data:/);
  assert.match(bpmnFrameAttributes.srcdoc, /font-src data:/);
  assert.match(bpmnFrameAttributes.srcdoc, /script-src 'nonce-[^']+'/);
  assert.match(bpmnFrameAttributes.srcdoc, /style-src 'nonce-[^']+'/);
  assert.match(bpmnFrameAttributes.srcdoc, /<script nonce="[^"]+">/);
  assert.doesNotMatch(bpmnFrameAttributes.srcdoc, /<script[^>]+src=/);
  assert.doesNotMatch(bpmnFrameAttributes.srcdoc, /\/assets\/model-viewer-frame\.(?:js|css)/);
  assert.match(bpmnFrameAttributes.srcdoc, /style-src-attr 'unsafe-inline'/);
  assert(browserRequests.some(url => url.endsWith('/assets/model-viewer-frame.js')));
  assert(browserRequests.some(url => url.endsWith('/assets/model-viewer-frame.css')));
  assert.deepEqual(childFrameRequests, [], 'opaque iframe must not request same-origin assets or API endpoints');
  assertions.push('Parent fetches bounded trusted JS/CSS; nonce-inlined assets execute in an opaque sandbox with network-deny CSP and authenticated messages.');

  const bpmnShapeCount = await bpmnFrame.locator('.djs-element[data-element-id]').count();
  assert(bpmnShapeCount > 10 && bpmnShapeCount <= 500);
  for (const id of ['Participant_A', 'Participant_B', 'Lane_Main', 'SubProcess_Check', 'Task_Nested', 'MessageFlow_A_B']) {
    assert.equal(await bpmnFrame.locator(`.djs-element[data-element-id="${id}"]`).count(), 1);
  }
  const selectedShape = bpmnFrame.locator('.djs-element[data-element-id="Task_Review"]');
  await selectedShape.click();
  assert.match(await selectedShape.getAttribute('class'), /selected/);
  assert.match(await selectedShape.textContent() ?? '', /<img src=/);
  assert.equal(await bpmnFrame.locator('img[src*="example.invalid"]').count(), 0);
  assert.equal(await bpmnFrame.locator('[contenteditable="true"], input, textarea, select, .djs-palette, .djs-context-pad').count(), 0);
  const watermark = bpmnFrame.locator('a.bjs-powered-by');
  assert.match(await watermark.getAttribute('href'), /bpmn\.io/);
  assert(await watermark.isVisible());
  assert.equal(await watermark.evaluate(anchor => getComputedStyle(anchor).opacity), '1');
  const watermarkGeometry = await watermark.evaluate(anchor => {
    const box = anchor.getBoundingClientRect();
    const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
    const viewport = { width: innerWidth, height: innerHeight };
    return {
      hit: hit === anchor || anchor.contains(hit),
      inViewport: box.left >= 0 && box.top >= 0 && box.right <= viewport.width && box.bottom <= viewport.height,
      width: box.width, height: box.height,
      color: getComputedStyle(anchor).color
    };
  });
  assert(watermarkGeometry.hit && watermarkGeometry.inViewport && watermarkGeometry.width > 0 && watermarkGeometry.height > 0);
  assert.notEqual(watermarkGeometry.color, 'rgba(0, 0, 0, 0)');
  await watermark.click();
  const lightbox = bpmnFrame.locator('.bjs-powered-by-lightbox');
  await lightbox.waitFor({ state: 'visible' });
  const popupPromise = page.context().waitForEvent('page');
  await lightbox.locator('a[href="https://bpmn.io"]').last().click();
  const popup = await popupPromise;
  assert.deepEqual(explicitPopupNavigations, ['https://bpmn.io/']);
  await popup.close();
  await page.waitForFunction(() => window.__issue78Events.some(event => event.status === 'selection' && event.elementId === 'Task_Review'));
  assertions.push(`Production BPMN renderer displayed ${bpmnShapeCount} elements; inert hostile label, in-viewport watermark and explicitly activated bpmn.io popup were verified.`);
  await page.screenshot({ path: resolve(artifacts, 'issue78-ui-production-boundary-bpmn.png'), fullPage: true });

  const replaceSource = (needle: string, replacement: string) => bpmnXml.replace(needle, replacement);
  const exactLimitPadding = 'x'.repeat(1_048_576 - Buffer.byteLength(bpmnXml) - 8);
  const exactLimitSource = `${bpmnXml}\n<!--${exactLimitPadding}-->`;
  assert.equal(Buffer.byteLength(exactLimitSource), 1_048_576);
  await page.evaluate(request => window.__issue78Sandbox.render(request), { ...bpmnRequest, source: exactLimitSource, scope: 'scope-validation', generation: 8, sourceId: 'exact-limit' });
  await page.waitForFunction(() => window.__issue78Events.some(event => event.status === 'rendered' && event.sourceId === 'exact-limit'));
  const multibyteSource = bpmnXml.replace('</bpmn:process>', `<bpmn:documentation>${'é'.repeat(524_288)}</bpmn:documentation></bpmn:process>`);
  assert(multibyteSource.length < 1_048_576 && Buffer.byteLength(multibyteSource) > 1_048_576);
  const checks: Array<{ source: string; expected: string; sourceId: string; kind?: 'bpmn' | 'dmn' }> = [
    { source: `${bpmnXml}\n<!--${'x'.repeat(1_048_576)}-->`, expected: 'too-large', sourceId: 'too-large' },
    { source: multibyteSource, expected: 'too-large', sourceId: 'utf8-too-large' },
    {
      source: replaceSource('</bpmn:process>', `${Array.from({ length: 501 }, (_, i) => `<bpmn:documentation id="overflow-${i}">x</bpmn:documentation>`).join('')}</bpmn:process>`),
      expected: 'too-many-elements',
      sourceId: 'too-many-elements'
    },
    {
      source: bpmnXml.replace('<?xml version="1.0" encoding="UTF-8"?>', '<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE bpmn:definitions [<!ENTITY probe SYSTEM "https://example.invalid/probe">]>'),
      expected: 'unsafe-source',
      sourceId: 'doctype'
    },
    {
      source: bpmnXml.replace('</bpmn:process>', '<extension:hostile xmlns:extension="urn:hostile">x</extension:hostile></bpmn:process>'),
      expected: 'unsupported-namespace',
      sourceId: 'extension'
    },
    {
      source: bpmnXml.replace('</bpmn:process>', '<bpmn:documentation href="https://example.invalid/hostile">link</bpmn:documentation></bpmn:process>'),
      expected: 'external-reference',
      sourceId: 'external-link'
    },
    {
      source: bpmnXml.replace('</bpmn:process>', '<bpmn:script>window.pwned=1</bpmn:script></bpmn:process>'),
      expected: 'unsafe-source',
      sourceId: 'script-element'
    },
    { source: '<bpmn:definitions', expected: 'invalid-xml', sourceId: 'malformed' },
    {
      source: bpmnXml.replace(/<bpmndi:BPMNDiagram[\s\S]*?<\/bpmndi:BPMNDiagram>/, ''),
      expected: 'missing-di',
      sourceId: 'no-di'
    },
    {
      kind: 'bpmn',
      source: bpmnXml.replace('http://www.omg.org/spec/BPMN/20100524/MODEL', 'urn:unsupported:bpmn'),
      expected: 'unsupported-namespace',
      sourceId: 'namespace'
    },
    {
      kind: 'dmn',
      source: '<dmn:definitions',
      expected: 'invalid-xml',
      sourceId: 'dmn-malformed'
    },
    {
      kind: 'dmn',
      source: dmnXml.replace(/<dmndi:DMNDI>[\s\S]*?<\/dmndi:DMNDI>/, ''),
      expected: 'missing-di',
      sourceId: 'dmn-no-di'
    },
    {
      kind: 'dmn',
      source: dmnXml.replace('https://www.omg.org/spec/DMN/20191111/MODEL/', 'urn:unsupported:dmn'),
      expected: 'unsupported-namespace',
      sourceId: 'dmn-namespace'
    }
  ];
  for (const item of checks) {
    const request = { ...bpmnRequest, ...item, scope: 'scope-validation', generation: 8 };
    await page.evaluate(value => window.__issue78Sandbox.render(value), request);
    await page.waitForFunction(({ sourceId, expected }) =>
      window.__issue78Events.some(event => event.sourceId === sourceId && event.status === expected),
    { sourceId: item.sourceId, expected: item.expected });
    assert.equal(
      await page.evaluate(sourceId => window.__issue78Events.some(event => event.sourceId === sourceId && event.status === 'import-started'), item.sourceId),
      false,
      `${item.sourceId} must be rejected before viewer construction/import`
    );
    const currentText = await page.frameLocator('#viewer-host iframe').locator('#state').textContent();
    assert.match(currentText ?? '', new RegExp(item.expected));
  }
  assertions.push('UTF-8 source cap, conservative XML element cap, DTD/entity, executable-script elements, unsupported namespaces/extensions/URLs, malformed XML and missing-DI fail before viewer import with explicit states.');

  const dmnRequest = { kind: 'dmn', source: dmnXml, scope: 'repository-scope-B', generation: 11, sourceId: 'dmn-source-B' };
  await page.evaluate(request => window.__issue78Sandbox.render(request), dmnRequest);
  await page.waitForFunction(() => window.__issue78Events.some(event => event.status === 'rendered' && event.sourceId === 'dmn-source-B'));
  const dmnFrame = page.frameLocator('#viewer-host iframe');
  assert.equal(await dmnFrame.locator('.djs-element[data-element-id="Decision_Eligibility"]').count(), 1);
  assert.equal(await dmnFrame.locator('input, textarea, select, [contenteditable="true"], .djs-palette, .djs-context-pad').count(), 0);
  assert(await dmnFrame.locator('.stage a.bjs-powered-by').isVisible());
  assertions.push('Production DMN DRD renders without editor controls and preserves the unobscured upstream watermark; the source-derived table is tested in the UI journey.');
  await page.screenshot({ path: resolve(artifacts, 'issue78-ui-production-boundary-dmn.png'), fullPage: true });

  await page.evaluate(() => {
    window.__issue78SwitchOnImportStarted = true;
    window.__issue78Sandbox.render({
      kind: 'bpmn',
      source: window.__issue78Fixtures.bpmn,
      scope: 'scope-stale',
      generation: 41,
      sourceId: 'stale-first'
    });
  });
  await page.waitForFunction(() =>
    window.__issue78Events.some(event => event.status === 'rendered' && event.sourceId === 'current-second') &&
    window.__issue78Sandbox.getDiagnostics().rejectedMessages > 0
  );
  const race = await page.evaluate(() => ({
    activeIdentity: window.__issue78Sandbox.getDiagnostics().activeIdentity,
    rejectedMessages: window.__issue78Sandbox.getDiagnostics().rejectedMessages,
    oldFrameCount: [...document.querySelectorAll('#viewer-host iframe')].filter(frame => frame.srcdoc.includes('scope-stale')).length,
    oldVisibleContent: document.querySelector('#viewer-host iframe')?.getAttribute('title')
  }));
  assert.equal(race.activeIdentity, 'current-second');
  assert(race.rejectedMessages >= 1, 'real stale child message must be rejected');
  assert.equal(race.oldFrameCount, 0);
  assert.equal(race.oldVisibleContent, 'BPMN read-only model');
  assert.equal(await page.frameLocator('#viewer-host iframe').locator('.djs-element[data-element-id="Task_Review"]').getAttribute('class').then(value => /selected/.test(value ?? '')), false);
  assertions.push('On scope/generation/source switch the old frame and content are removed; a real delayed message from its child WindowProxy is rejected.');

  const license = await page.request.get(`${baseUrl}/licenses/bpmn-io-viewers.txt`);
  assert.equal(license.status(), 200);
  assert.match(await license.text(), /MUST NOT be\s+removed or changed/);
  assert.match(await license.text(), /fully visible and not visually overlapped/);
  await page.evaluate(() => window.__issue78Sandbox.dispose());
  assert.equal(await page.locator('#viewer-host iframe').count(), 0);
  assert.equal(await page.locator('#viewer-host').textContent(), '');
  assertions.push('Dispose tears down the opaque iframe, clears visible source content, and preserves the exact upstream license notice.');

  assert(browserRequests.some(url => url.includes('/assets/model-viewer-frame.js')));
  assert(browserRequests.some(url => url.includes('/assets/model-viewer-frame.css')));
  assert.deepEqual(externalRequests, [], `sandboxed imports must not fetch externally: ${externalRequests.join(', ')}`);
  assert.deepEqual(consoleErrors, [], `browser must be free of console and CSP errors: ${consoleErrors.join('; ')}`);
  assertions.push('Real static browser run observed no external requests from hostile label/reference/extension cases.');

  const artifact = {
    head: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim(),
    dirtySnapshotHashes: sourceHashes,
    fixtureSha256: { bpmn: bpmnHash, dmn: dmnHash },
    packages: {
      bpmnJs: { version: lockedPackages.bpmnJs.version, integrity: lockedPackages.bpmnJs.integrity },
      dmnJs: { version: lockedPackages.dmnJs.version, integrity: lockedPackages.dmnJs.integrity }
    },
    environment: { node: process.version, platform: process.platform, architecture: process.arch, browser: browser.version(), browserImage: 'Host Playwright browser cache, pinned container image not exercised.' },
    bundle,
    assertions,
    browserRequests: browserRequests.map(url => url.replace(baseUrl, '')),
    externalRequests,
    consoleErrors,
    residualRisk: 'XML DOMParser preflight and bpmn-js/dmn-js import share the iframe renderer main thread. The opaque sandbox with network-deny CSP and teardown bounds lifetime/capabilities but provides no hard CPU deadline; destroying the iframe cannot guarantee the browser process is immediately interrupted.'
  };
  await writeFile(resolve(artifacts, 'issue78-ui-production-boundary-browser.json'), `${JSON.stringify(artifact, null, 2)}\n`);
  await writeFile(resolve(artifacts, 'issue78-ui-production-boundary-browser.log'),
    `Issue #78 production boundary passed: ${assertions.length} grouped browser assertions, ${checks.length} negative source cases; ${browserRequests.length} requests, ${externalRequests.length} external.\n`
    + `Source hashes: BPMN ${bpmnHash}; DMN ${dmnHash}\nResidual: ${artifact.residualRisk}\n`);
  console.log(`Issue #78 production boundary passed: ${assertions.length} grouped browser assertions, ${checks.length} negative source cases; ${browserRequests.length} requests, ${externalRequests.length} external.`);
} finally {
  await browser?.close();
  await server.close();
}
