import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, readFile } from "node:fs/promises";
import { after, before, test } from "node:test";
import playwright from "../spikes/likec4/node_modules/playwright/index.js";
import { preview } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import type { PreviewServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";

// Synthetic transport qualifies current-client rendering, not model/retriever
// correctness, real-corpus quality, native tooltip pixels or browser performance.
// The integration owner must build this exact source into wwwroot before running.
const client = new URL("../platform/src/CapabilityExplorer.Explorer/client/", import.meta.url);
const built = new URL("../wwwroot/", client);
const repository = "synthetic-orders";
const capability = { id: "synthetic-orders-capability", name: "Synthetic orders", repositories: [repository] };
const latest = "a".repeat(40);
const selected = "b".repeat(40);
const snapshotId = "synthetic-chat-browser";
const question = "Which synthetic method creates an order?";
const steps = [
  'search_symbols({"query":"CreateOrder"})',
  'get_symbol({"key":"synthetic:Orders.CreateOrder"})',
];
const sources = [
  {
    n: 1, kind: "Method", key: "synthetic:Orders.CreateOrder", name: "CreateOrder",
    repository, found: "name search",
    text: "Synthetic source only: Orders.cs:12–18\nCreateOrder records the supplied order.",
  },
  {
    n: 2, kind: "Concept", key: "synthetic:concept:order", name: "Synthetic order concept",
    repository, found: "glossary",
    text: "Synthetic concept summary, not a single extracted symbol.",
  },
];
const citationHref = "#/explore?key=synthetic%3AOrders.CreateOrder";
let server: PreviewServer | undefined;
let origin: string;
let entrySha256: string;

before(async () => {
  const html = await readFile(new URL("index.html", built), "utf8");
  assert.match(html, /<script\b[^>]*\bsrc="\/assets\/app\.js"/, "Build the current client first; do not substitute a component/dev entry.");
  assert.doesNotMatch(html, /(?:\/src\/|\/@vite\/client)/, "Only built application assets qualify this check.");
  entrySha256 = createHash("sha256").update(await readFile(new URL("assets/app.js", built))).digest("hex");
  // Preview serves the existing output without transforming source or loading
  // the production Vite config (which also contains API proxy/build hooks).
  server = await preview({
    configFile: false,
    root: client.pathname,
    build: { outDir: built.pathname },
    preview: { host: "127.0.0.1", port: 0 },
    logLevel: "error",
  });
  const url = server.resolvedUrls?.local[0];
  assert.ok(url);
  origin = url.replace(/\/$/, "");
}, { timeout: 15_000 });

after(async () => {
  const httpServer = server?.httpServer;
  if (httpServer) await new Promise<void>((resolve, reject) => {
    httpServer.close(error => error ? reject(error) : resolve());
  });
});

test("Built current chat: synthetic scoped evidence, opt-in reload and request isolation", { timeout: 45_000 }, async t => {
  t.diagnostic(`Synthetic transport; built assets/app.js sha256=${entrySha256}. No model, retriever or real-corpus acceptance.`);
  const browser = await playwright.chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, serviceWorkers: "block" });
    page.setDefaultTimeout(10_000);
    const errors: string[] = [];
    const unexpected: string[] = [];
    const scripts: string[] = [];
    let chatRequests = 0;
    const controlled: { release?: () => void; ready?: () => void; delay: boolean } = { delay: false };
    const holdAnswer = () => {
      controlled.delay = true;
      return new Promise<void>(resolve => { controlled.ready = resolve; });
    };
    page.on("pageerror", error => errors.push(error.message));
    page.on("response", response => {
      if (response.request().resourceType() === "script") scripts.push(new URL(response.url()).pathname);
    });
    await page.route("**/*", async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) {
        unexpected.push(`External ${request.method()} ${url.origin}${url.pathname}`);
        await route.abort("blockedbyclient");
        return;
      }
      if (url.pathname === "/api/chat" && request.method() === "POST") {
        chatRequests++;
        if (controlled.delay) {
          const gate = new Promise<void>(resolve => { controlled.release = resolve; });
          controlled.ready?.();
          await gate;
        }
        await route.fulfill({ json: {
          answer: "Synthetic answer: CreateOrder records an order [1–2].",
          sources, steps, retrieval: ["semantic search: synthetic fixture only"],
        } });
        return;
      }
      let payload: unknown;
      if (request.method() === "GET") {
        switch (url.pathname) {
          case "/api/meta": payload = {
            generation: 1, snapshotId, capabilities: [capability],
            githubAuth: { source: "none", hostManaged: true },
            interpretation: { enabled: true, chat: true, provider: "synthetic-no-model" },
            similarity: { database: false }, artifacts: 1, problems: [],
            hasSource: true, hasEvaluation: false, nodeKinds: ["Method"],
            defaultView: { [repository]: latest },
            revisions: [latest, selected].map((commitSha, index) => ({
              repositoryId: repository, commitSha, tags: [], label: index === 0 ? "Synthetic latest" : "Synthetic selected",
            })),
          }; break;
          case "/api/generation": payload = { generation: 1, snapshotId }; break;
          case "/api/view": {
            const view = url.searchParams.get("view");
            const cap = url.searchParams.get("cap");
            if (cap !== null && cap !== capability.id) break;
            if (view === "") payload = { [repository]: latest };
            else if (view === `${repository}:${selected}` || view === `${repository}:${latest}`) {
              payload = { [repository]: view.split(":")[1] };
            }
            break;
          }
          case "/api/search": payload = { items: [], nextCursor: null, total: 0, snapshotId }; break;
          case "/health": payload = {
            status: "healthy", uptime_seconds: 1, version: "synthetic",
            timestamp: "2026-11-10T00:00:00Z",
          }; break;
          default:
            if (!url.pathname.startsWith("/api/")) {
              await route.continue();
              return;
            }
        }
      }
      if (payload === undefined) {
        unexpected.push(`${request.method()} ${url.pathname}`);
        await route.fulfill({ status: 404, json: { error: "Unexpected synthetic fixture request" } });
        return;
      }
      await route.fulfill({ json: payload });
    });

    await page.goto(`${origin}/#/explore`);
    await page.getByRole("region", { name: "Symbol results" }).locator('[role="list"][aria-busy="false"]').waitFor({ state: "attached" });
    assert.ok(scripts.includes("/assets/app.js"), "Exercise the actual built application entry.");
    assert.ok(scripts.every(path => path.startsWith("/assets/")), "No source/dev scripts may replace built assets.");
    const scope = page.getByRole("region", { name: "Scope", exact: true });
    const capabilityResolved = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === "/api/view" && url.searchParams.get("cap") === capability.id;
    });
    await scope.getByLabel("Capability", { exact: true }).selectOption(capability.id);
    await capabilityResolved;
    const revisionResolved = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === "/api/view" && url.searchParams.get("cap") === capability.id
        && url.searchParams.get("view") === `${repository}:${selected}`;
    });
    await scope.getByLabel(`Revision of ${repository}`, { exact: true }).selectOption(selected);
    await revisionResolved;
    await page.waitForFunction(({ repository, selected }) =>
      document.querySelector<HTMLSelectElement>(`select[aria-label="Revision of ${repository}"]`)?.value === selected,
    { repository, selected });

    const open = page.getByRole("button", { name: /^Ask (?:⌘J|Ctrl J)$/ });
    await open.click();
    const panel = page.getByRole("complementary", { name: "Ask about capabilities", exact: true });
    const remember = panel.getByRole("checkbox", { name: "Remember chat in this tab", exact: true });
    const retained = () => page.evaluate(() => sessionStorage.getItem("ce.chatHistory"));
    const reopenAfterReload = async () => {
      await page.reload();
      await page.getByRole("region", { name: "Symbol results" }).locator('[role="list"][aria-busy="false"]').waitFor({ state: "attached" });
      await open.click();
      await remember.waitFor();
    };
    const exchange = async () => {
      await panel.getByRole("textbox", { name: "Question", exact: true }).fill(question);
      await panel.getByRole("button", { name: "Ask", exact: true }).click();
      await panel.getByText("semantic search: synthetic fixture only", { exact: true }).waitFor();
    };
    await panel.waitFor();
    await panel.getByRole("textbox", { name: "Question", exact: true }).waitFor();
    assert.equal(await remember.count(), 1, "The ready chat must offer an explicit opt-in retention control.");
    assert.equal(await remember.isChecked(), false, "Retention must be opt-in.");
    assert.equal(await retained(), null);
    assert.equal(await open.getAttribute("aria-pressed"), "true");
    const capabilityChip = panel.getByRole("button", { name: `Capability: ${capability.name}`, exact: true });
    await capabilityChip.waitFor();
    assert.equal(await capabilityChip.getAttribute("aria-pressed"), "true");
    assert.equal(await panel.getByRole("button", { name: "View: explore", exact: true }).getAttribute("aria-pressed"), "true");
    assert.equal(chatRequests, 0, "Opening chat must not send a question.");
    const ask = panel.getByRole("button", { name: "Ask", exact: true });
    assert.equal(await ask.isDisabled(), true);
    await panel.getByRole("textbox", { name: "Question", exact: true }).fill(`  ${question}  `);
    const sent = page.waitForRequest(request => new URL(request.url()).pathname === "/api/chat");
    await ask.click();
    const request = await sent;
    assert.equal(request.method(), "POST");
    assert.equal(new URL(request.url()).search, "");
    assert.equal(request.headers()["content-type"], "application/json");
    assert.deepEqual(request.postDataJSON(), {
      messages: [{ role: "user", content: question }],
      view: `${repository}:${selected}`, cap: capability.id,
      focus: { capability: capability.name, view: "explore" },
    }, "The request must carry the actual UI selection, not the latest/default revision.");

    await panel.getByText("semantic search: synthetic fixture only", { exact: true }).waitFor();
    assert.equal(await panel.getByRole("textbox", { name: "Question", exact: true }).inputValue(), "");
    const lookups = panel.locator("details").filter({ has: page.locator("summary", { hasText: "2 lookups the model made" }) });
    assert.equal(await lookups.evaluate(element => (element as HTMLDetailsElement).open), false);
    await lookups.locator("summary").click();
    assert.deepEqual(await lookups.locator("li").allTextContents(), steps);
    const sourceList = panel.locator("details").filter({ has: page.locator("summary", { hasText: "2 sources · AI-written, unreviewed" }) });
    assert.equal(await sourceList.evaluate(element => (element as HTMLDetailsElement).open), false);
    await sourceList.locator("summary").click();
    assert.equal(await sourceList.locator("li").count(), sources.length);
    for (const source of sources) {
      const row = sourceList.locator("li").filter({ hasText: source.name });
      assert.equal(await row.innerText(), `[${source.n}] ${source.name} ${source.kind} · ${source.repository} · ${source.found}`);
      const metadata = row.locator("span[title]");
      await metadata.hover();
      assert.equal(await metadata.getAttribute("title"), source.text, "Native hover text must preserve the supplied source excerpt.");
    }
    assert.equal(await sourceList.getByRole("link", { name: sources[0].name, exact: true }).getAttribute("href"), citationHref);
    assert.equal(await sourceList.getByRole("link", { name: sources[1].name, exact: true }).count(), 0, "A concept summary must not masquerade as a symbol.");
    const citation = panel.getByRole("link", { name: "1", exact: true });
    await citation.hover();
    assert.equal(await citation.getAttribute("href"), citationHref);
    assert.equal(await citation.getAttribute("title"), "Method CreateOrder");
    assert.equal(await panel.getByRole("link", { name: "2", exact: true }).count(), 0);
    assert.match(await panel.innerText(), /Synthetic answer: CreateOrder records an order \[1, 2\]\./);
    assert.equal(chatRequests, 1, "Expanding or hovering evidence must not invoke chat again.");
    assert.equal(await retained(), null, "A completed default-off exchange must stay memory-only.");
    assert.deepEqual(errors, []);
    assert.deepEqual(unexpected, [], "No live/private API or external service may participate.");
    await panel.getByRole("button", { name: "Close chat", exact: true }).click();
    await panel.waitFor({ state: "detached" });
    assert.equal(await open.getAttribute("aria-pressed"), "false");

    await reopenAfterReload();
    assert.equal(await remember.isChecked(), false);
    assert.equal(await panel.getByText(question, { exact: true }).count(), 0);
    assert.equal(chatRequests, 1, "Reloading and opening must not call chat.");
    assert.match(await panel.innerText(), /20 whole turns/);
    assert.match(await panel.innerText(), /512 KiB/);
    await remember.check();
    await exchange();
    assert.ok(await retained());
    await reopenAfterReload();
    assert.equal(await remember.isChecked(), true);
    await panel.getByText(question, { exact: true }).waitFor();
    assert.equal(await panel.getByRole("link", { name: "1", exact: true }).getAttribute("href"), citationHref);
    assert.equal(chatRequests, 2, "Restoration must not replay a model request.");
    await panel.getByRole("textbox", { name: "Question", exact: true }).fill("Synthetic discarded draft");
    await panel.getByRole("button", { name: "Clear chat", exact: true }).click();
    assert.equal(await panel.getByRole("textbox", { name: "Question", exact: true }).inputValue(), "");
    assert.equal(await retained(), null);
    assert.equal(await panel.getByText(question, { exact: true }).count(), 0);
    await reopenAfterReload();
    assert.equal(await remember.isChecked(), false);
    assert.equal(await panel.getByText(question, { exact: true }).count(), 0);
    await remember.check();
    await exchange();
    await remember.uncheck();
    assert.equal(await retained(), null);
    assert.equal(await panel.getByText(question, { exact: true }).count(), 1, "Turning retention off must keep the in-memory conversation usable.");
    await reopenAfterReload();
    assert.equal(await remember.isChecked(), false);
    assert.equal(await panel.getByText(question, { exact: true }).count(), 0);
    assert.equal(chatRequests, 3);

    await remember.check();
    await exchange();
    const changed = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === "/api/view" && url.searchParams.get("view") === `${repository}:${latest}`;
    });
    await scope.getByLabel(`Revision of ${repository}`, { exact: true }).selectOption(latest);
    await changed;
    await remember.waitFor();
    await panel.getByText(/Saved chat.*(?:different|changed|scope)/).waitFor();
    assert.equal(await retained(), null);
    assert.equal(await panel.getByText(question, { exact: true }).count(), 0, "Different revisions must never replay old evidence.");

    const heldForClear = holdAnswer();
    await remember.check();
    await panel.getByRole("textbox", { name: "Question", exact: true }).fill("Synthetic pending request to clear");
    const pending = page.waitForRequest(request => new URL(request.url()).pathname === "/api/chat");
    await panel.getByRole("button", { name: "Ask", exact: true }).click();
    await pending;
    await heldForClear;
    const released = page.waitForEvent("requestfailed", {
      predicate: request => new URL(request.url()).pathname === "/api/chat",
    });
    await panel.getByRole("button", { name: "Clear chat", exact: true }).click();
    assert.equal(await retained(), null);
    // Controlled transport, not a sleep: clear must abort the pending request.
    assert.ok(controlled.release);
    controlled.release();
    await released;
    controlled.delay = false;
    await exchange();
    assert.equal(await panel.getByText("Synthetic pending request to clear", { exact: true }).count(), 0);
    assert.equal(await retained(), null, "A late response must not re-enable cleared retention.");
    assert.equal(await panel.getByRole("button", { name: "Ask", exact: true }).isDisabled(), true);

    await remember.check();
    const heldForScope = holdAnswer();
    await panel.getByRole("textbox", { name: "Question", exact: true }).fill("Synthetic old-scope request");
    await panel.getByRole("button", { name: "Ask", exact: true }).click();
    await heldForScope;
    const releaseOld = controlled.release!;
    const oldAborted = page.waitForEvent("requestfailed", {
      predicate: request => new URL(request.url()).pathname === "/api/chat",
    });
    await scope.getByLabel(`Revision of ${repository}`, { exact: true }).selectOption(selected);
    await remember.waitFor();
    await panel.getByText(/Saved chat.*(?:different|changed|scope)/).waitFor();
    const heldForNew = holdAnswer();
    await panel.getByRole("textbox", { name: "Question", exact: true }).fill("Synthetic new-scope request");
    await panel.getByRole("button", { name: "Ask", exact: true }).click();
    await heldForNew;
    const releaseNew = controlled.release!;
    releaseOld();
    await oldAborted;
    assert.equal(await panel.getByText("Thinking…", { exact: true }).count(), 1, "An old finally must not reset the newer scope's pending request.");
    assert.equal(await panel.getByText("Synthetic old-scope request", { exact: true }).count(), 0);
    assert.equal(await retained(), null);
    releaseNew();
    await panel.getByText("semantic search: synthetic fixture only", { exact: true }).waitFor();
    assert.equal(await panel.getByText("Synthetic new-scope request", { exact: true }).count(), 1);
    assert.equal(await panel.getByText("semantic search: synthetic fixture only", { exact: true }).count(), 1);

    const heldForClose = holdAnswer();
    await panel.getByRole("textbox", { name: "Question", exact: true }).fill("Synthetic request to close");
    await panel.getByRole("button", { name: "Ask", exact: true }).click();
    await heldForClose;
    const closedAborted = page.waitForEvent("requestfailed", {
      predicate: request => new URL(request.url()).pathname === "/api/chat",
    });
    await panel.getByRole("button", { name: "Close chat", exact: true }).click();
    controlled.release!();
    await closedAborted;
    controlled.delay = false;
    await open.click();
    await remember.waitFor();
    assert.equal(await panel.getByText("Synthetic request to close", { exact: true }).count(), 0);
    assert.equal(await retained(), null);
    await panel.getByRole("button", { name: "Close chat", exact: true }).click();
    await page.evaluate(() => localStorage.removeItem("ce.overrides"));
    await reopenAfterReload();
    const defaultSent = page.waitForRequest(request => new URL(request.url()).pathname === "/api/chat");
    await exchange();
    assert.equal((await defaultSent).postDataJSON().view, `${repository}:${latest}`,
      "A mutable default selection must send the effective revisions used to bind retained evidence.");
    const screenshots = new URL("../.sandbox-cache/chat-history-layout/", import.meta.url);
    await mkdir(screenshots, { recursive: true });
    for (const viewport of [
      { name: "desktop", width: 1440, height: 1000 },
      { name: "mobile", width: 390, height: 844 },
      { name: "compact", width: 320, height: 568 },
      { name: "landscape", width: 844, height: 390 },
    ]) {
      await page.setViewportSize(viewport);
      const bounds = await panel.boundingBox();
      const log = await panel.getByRole("log", { name: "Conversation", exact: true }).boundingBox();
      const composer = await panel.getByRole("textbox", { name: "Question", exact: true }).boundingBox();
      await page.screenshot({ path: new URL(`${viewport.name}.png`, screenshots).pathname });
      t.diagnostic(`Chat layout ${viewport.name}: ${JSON.stringify({ bounds, log, composer })}`);
      assert.ok(bounds && log && composer);
      assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= viewport.width, "Chat must fit the viewport horizontally.");
      assert.ok(composer.y >= 0 && composer.y + composer.height <= viewport.height, "The question must remain on screen.");
      assert.ok(log.height >= 80, "Retention controls must leave a readable, scrollable conversation in short viewports.");
    }
    assert.deepEqual(errors, []);
    assert.deepEqual(unexpected, [], "Reload/retention checks remain synthetic-only.");
  } finally {
    await browser.close();
  }
});
