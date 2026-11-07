import assert from "node:assert/strict";
import { test } from "node:test";
import playwright from "../spikes/likec4/node_modules/playwright/index.js";
import type { Page } from "../spikes/likec4/node_modules/playwright/index.js";
import { createServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";

const capability = {
  id: "orders", name: "Orders", analysed: 0,
  repositories: [{ id: "orders-api", kind: "github", location: "https://github.com/acme/orders", ref: "HEAD", analysed: false, analysedRevisions: [] }],
};

async function withIngestionPage(run: (page: Page, origin: string) => Promise<void>) {
  const server = await createServer({
    root: new URL("../platform/src/CapabilityExplorer.Explorer/client", import.meta.url).pathname,
    server: { host: "127.0.0.1", port: 0 }, logLevel: "error",
  });
  await server.listen();
  const browser = await playwright.chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.clock.install();
    await page.route("**/api/**", async (route) => {
      const path = new URL(route.request().url()).pathname;
      if (path === "/api/analysis-status") {
        await route.fulfill({ json: { orders: { state: "idle", blocked: false } } });
      } else if (path.endsWith("/analyze")) {
        await route.fulfill({ json: { state: "idle", blocked: false } });
      } else if (path === "/api/meta") {
        await route.fulfill({ json: {
          generation: 0, capabilities: [{ id: "orders", name: "Orders", repositories: ["orders-api"] }],
          githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
          similarity: { database: false }, revisions: [], defaultView: {}, artifacts: 0, problems: [],
          hasSource: false, hasEvaluation: false, nodeKinds: [],
        } });
      } else if (path === "/api/capabilities") {
        await route.fulfill({ json: [capability] });
      } else if (path === "/api/generation") {
        await route.fulfill({ json: { generation: 0 } });
      } else if (path === "/api/github-accounts") {
        await route.fulfill({ json: { available: true, accounts: [] } });
      } else {
        await route.fulfill({ json: {} });
      }
    });
    await run(page, server.resolvedUrls!.local[0]);
  } finally {
    await browser.close();
    await server.close();
  }
}

test("Given slow ingestion status, when polling ticks, then requests never overlap and the form stays usable", async () => {
  await withIngestionPage(async (page, origin) => {
    let statusRequests = 0;
    let release: () => void = () => undefined;
    const barrier = new Promise<void>((resolve) => { release = resolve; });
    await page.route("**/api/analysis-status", async (route) => {
      statusRequests++;
      if (statusRequests > 1) await barrier;
      await route.fulfill({ json: { orders: { state: "running", runId: "run-orders", blocked: false } } });
    });
    await page.goto(`${origin}#/capabilities`);
    await page.getByRole("button", { name: "Analyzing · Stop" }).waitFor();
    await page.clock.runFor(2100);
    await page.waitForRequest("**/api/analysis-status", { timeout: 100 }).catch(() => undefined);
    assert.equal(statusRequests, 2, "one initial read and one pending poll");
    await page.clock.runFor(6100);
    assert.equal(statusRequests, 2, "a pending ingestion-status request must not be duplicated by later polling ticks");
    await page.getByRole("textbox", { name: "Name", exact: true }).fill("Next capability");
    assert.equal(await page.getByRole("textbox", { name: "Name", exact: true }).inputValue(), "Next capability");
    release();
  });
});

test("Given a transient list failure, when the next poll succeeds, then the capability remains visible and recovers", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.goto(`${origin}#/capabilities`);
    await page.getByRole("heading", { name: "Orders", exact: true }).waitFor();
    let listRequests = 0;
    await page.route("**/api/capabilities?*", async (route) => {
      listRequests++;
      await route.fulfill(listRequests === 1
        ? { status: 503, json: { error: "Temporary journal outage" } }
        : { json: [{ ...capability, name: "Recovered orders" }] });
    });
    await page.clock.runFor(2100);
    await page.getByText("Temporary journal outage", { exact: true }).waitFor();
    assert.equal(await page.getByRole("heading", { name: "Orders", exact: true }).count(), 1, "a transient refresh failure must retain the last successful capability list");
    await page.clock.runFor(2100);
    await page.getByRole("heading", { name: "Recovered orders", exact: true }).waitFor({ timeout: 1000 });
  });
});

test("Given a background tab, when it becomes visible, then capability polling resumes", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.goto(`${origin}#/capabilities`);
    await page.getByRole("heading", { name: "Orders", exact: true }).waitFor();
    let listRequests = 0;
    await page.route("**/api/capabilities?*", async (route) => {
      listRequests++;
      await route.fulfill({ json: [capability] });
    });
    await page.evaluate(() => Object.defineProperty(document, "hidden", { configurable: true, value: true }));
    await page.clock.runFor(4100);
    assert.equal(listRequests, 0, "background tabs must not poll capability lists");
    await page.evaluate(() => Object.defineProperty(document, "hidden", { configurable: true, value: false }));
    await page.clock.runFor(2100);
    assert.equal(listRequests, 1, "polling must resume after returning to the tab");
  });
});

test("Given a pending analysis admission, when Analyze is clicked twice, then only one request is sent", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.goto(`${origin}#/capabilities`);
    const analyze = page.getByRole("button", { name: "Analyze code", exact: true });
    await analyze.waitFor();
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((button) => button.textContent === "Analyze code" && !button.disabled));
    let posts = 0;
    let release: () => void = () => undefined;
    const barrier = new Promise<void>((resolve) => { release = resolve; });
    await page.route("**/api/capabilities/orders/analyze", async (route) => {
      if (route.request().method() !== "POST") return route.fallback();
      posts++;
      await barrier;
      await route.fulfill({ status: 202, json: { state: "queued", runId: "run-orders", blocked: false } });
    });
    await analyze.dblclick();
    assert.equal(posts, 1, "a pending analysis admission must disable duplicate submissions");
    release();
    await page.getByRole("button", { name: "Queued · Stop" }).waitFor();
  });
});

test("Given a stale status response, when analysis admission succeeds first, then the queued run is not replaced", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.goto(`${origin}#/capabilities`);
    const analyze = page.getByRole("button", { name: "Analyze code", exact: true });
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((button) => button.textContent === "Analyze code" && !button.disabled));
    let release: () => void = () => undefined;
    const barrier = new Promise<void>((resolve) => { release = resolve; });
    await page.route("**/api/{analysis-status,capabilities/orders/analyze}", async (route) => {
      if (route.request().method() === "POST") {
        await route.fulfill({ status: 202, json: { state: "queued", runId: "run-orders", blocked: false } });
      } else {
        await barrier;
        await route.fulfill({ json: { orders: { state: "idle", blocked: false } } });
      }
    });
    const pending = page.waitForRequest("**/api/analysis-status");
    await page.clock.runFor(2100);
    await pending;
    await analyze.click();
    await page.getByRole("button", { name: "Queued · Stop" }).waitFor();
    const stale = page.waitForResponse((response) => response.url().endsWith("/analysis-status") && response.request().method() === "GET");
    release();
    await stale;
    await page.clock.runFor(50);
    assert.equal(await page.getByRole("button", { name: "Queued · Stop" }).count(), 1, "an older poll must not overwrite a successful admission");
  });
});

test("Given an unavailable status endpoint, when it recovers, then analysis becomes usable without reloading", async () => {
  await withIngestionPage(async (page, origin) => {
    let reads = 0;
    await page.route("**/api/analysis-status", async (route) => {
      reads++;
      await route.fulfill(reads === 1
        ? { status: 503, json: { error: "Temporary status outage" } }
        : { json: { orders: { state: "idle", blocked: false } } });
    });
    await page.goto(`${origin}#/capabilities`);
    await page.getByText(/Analysis status unavailable:/).waitFor();
    assert.equal(await page.getByRole("button", { name: "Analyze code", exact: true }).isDisabled(), true);
    await page.clock.runFor(2100);
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((button) => button.textContent === "Analyze code" && !button.disabled));
    assert.equal(await page.getByText(/Analysis status unavailable:/).count(), 0);
  });
});

test("Given a blocked terminal run, when polling repeats, then metadata is not continuously reloaded", async () => {
  await withIngestionPage(async (page, origin) => {
    let metadataReads = 0;
    await page.route("**/api/meta", async (route) => { metadataReads++; await route.fallback(); });
    await page.route("**/api/analysis-status", async (route) => {
      await route.fulfill({ json: { orders: { state: "unknown", blocked: true, blockReason: "Host reconciliation required" } } });
    });
    await page.goto(`${origin}#/capabilities`);
    await page.getByText("Host reconciliation required", { exact: true }).waitFor();
    const initialMetadataReads = metadataReads;
    await page.clock.runFor(6500);
    assert.equal(metadataReads, initialMetadataReads, "a blocked run must not repeatedly abort and reload global metadata");
    assert.equal(await page.getByRole("button", { name: "Analyze code", exact: true }).isDisabled(), true);
  });
});

for (const state of ["unknown", "interrupted"]) {
  test(`Given an ${state} run, when retry is confirmed twice, then one fresh run is queued with the observed identities`, async () => {
    await withIngestionPage(async (page, origin) => {
      const observed = { state, runId: "old-run-orders", attemptId: "old-attempt-orders", blocked: true };
      await page.route("**/api/analysis-status", (route) => route.fulfill({ json: { orders: observed } }));
      await page.goto(`${origin}#/capabilities`);
      await page.getByText(`Analysis ${state}.`, { exact: false }).waitFor();
      const retry = page.getByRole("button", { name: "Retry interrupted run", exact: true });
      assert.equal(await retry.count(), 1, "an identified interrupted execution must offer explicit retry");
      assert.equal(await page.getByRole("button", { name: "Analyze code", exact: true }).isDisabled(), true);
      let confirmations = 0;
      page.on("dialog", async (dialog) => {
        confirmations++;
        assert.match(dialog.message(), /old evidence.*preserved/i);
        assert.match(dialog.message(), /termination.*not verified/i);
        assert.match(dialog.message(), /authoriz.*fresh run/i);
        assert.ok(dialog.message().includes(observed.runId));
        assert.ok(dialog.message().includes(observed.attemptId));
        await dialog.accept();
      });
      let posts = 0;
      let release: () => void = () => undefined;
      const barrier = new Promise<void>((resolve) => { release = resolve; });
      await page.route("**/api/capabilities/orders/retry", async (route) => {
        posts++;
        assert.equal(route.request().method(), "POST");
        assert.deepEqual(route.request().postDataJSON(), {
          runId: observed.runId, attemptId: observed.attemptId, acknowledgeUnverifiedExecution: true,
        });
        await barrier;
        await route.fulfill({ status: 202, json: { state: "queued", runId: "fresh-run-orders", blocked: false } });
      });
      const pending = page.waitForRequest("**/api/capabilities/orders/retry");
      await retry.dblclick();
      await pending;
      assert.equal(posts, 1, "double clicking must not duplicate retry admission");
      assert.equal(confirmations, 1, "only one explicit authorization is requested while admission is pending");
      release();
      await page.getByRole("button", { name: "Queued · Stop", exact: true }).waitFor();
      await page.getByText("fresh-run-orders", { exact: true }).waitFor();
      assert.equal(await page.getByRole("button", { name: "Retry interrupted run", exact: true }).count(), 0);
    });
  });
}

test("Given an interrupted run, when retry confirmation is cancelled, then no mutation is sent", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.route("**/api/analysis-status", (route) => route.fulfill({ json: {
      orders: { state: "unknown", runId: "old-run-orders", attemptId: "old-attempt-orders", blocked: true },
    } }));
    const mutations: string[] = [];
    page.on("request", (request) => { if (request.method() !== "GET") mutations.push(request.url()); });
    await page.goto(`${origin}#/capabilities`);
    await page.getByText("Analysis unknown.", { exact: false }).waitFor();
    const retry = page.getByRole("button", { name: "Retry interrupted run", exact: true });
    assert.equal(await retry.count(), 1, "an identified interrupted execution must offer explicit retry");
    let confirmations = 0;
    page.on("dialog", async (dialog) => { confirmations++; await dialog.dismiss(); });
    await retry.click();
    assert.equal(confirmations, 1);
    assert.deepEqual(mutations, [], "cancelling authorization must leave the journal and queue untouched");
    assert.equal(await retry.isEnabled(), true);
    assert.equal(await page.getByRole("button", { name: "Analyze code", exact: true }).isDisabled(), true);
  });
});

test("Given a rejected retry, when the operator retries successfully before a stale poll returns, then errors recover and the fresh run remains visible", async () => {
  await withIngestionPage(async (page, origin) => {
    const observed = { state: "unknown", runId: "old-run-orders", attemptId: "old-attempt-orders", blocked: true };
    await page.route("**/api/analysis-status", (route) => route.fulfill({ json: { orders: observed } }));
    await page.goto(`${origin}#/capabilities`);
    const retry = page.getByRole("button", { name: "Retry interrupted run", exact: true });
    await retry.waitFor();
    page.on("dialog", (dialog) => dialog.accept());
    let posts = 0;
    await page.route("**/api/capabilities/orders/retry", async (route) => {
      posts++;
      assert.deepEqual(route.request().postDataJSON(), {
        runId: observed.runId, attemptId: observed.attemptId, acknowledgeUnverifiedExecution: true,
      });
      await route.fulfill(posts === 1
        ? { status: 409, json: { error: "Retry rejected: journal temporarily unavailable" } }
        : { status: 202, json: { state: "queued", runId: "fresh-run-orders", blocked: false } });
    });
    await retry.click();
    await page.getByText("Retry rejected: journal temporarily unavailable", { exact: true }).waitFor();
    assert.equal(await retry.isEnabled(), true, "failed admission must release the pending guard");
    assert.equal(await page.getByRole("button", { name: "Analyze code", exact: true }).isDisabled(), true);
    let release: () => void = () => undefined;
    const barrier = new Promise<void>((resolve) => { release = resolve; });
    await page.route("**/api/analysis-status", async (route) => {
      await barrier;
      await route.fulfill({ json: { orders: observed } });
    });
    const pending = page.waitForRequest("**/api/analysis-status");
    await page.clock.runFor(2100);
    await pending;
    await retry.click();
    await page.getByRole("button", { name: "Queued · Stop", exact: true }).waitFor();
    const stale = page.waitForResponse("**/api/analysis-status");
    release();
    await stale;
    await page.clock.runFor(50);
    assert.equal(posts, 2);
    assert.equal(await page.getByRole("button", { name: "Queued · Stop", exact: true }).count(), 1);
    assert.equal(await page.getByText("fresh-run-orders", { exact: true }).count(), 1);
    assert.equal(await page.getByText("Retry rejected: journal temporarily unavailable", { exact: true }).count(), 0);
  });
});

test("Given an ineligible state or missing execution identity, when status refreshes, then interrupted retry is not offered", async () => {
  await withIngestionPage(async (page, origin) => {
    let observed: { state: string; runId?: string; attemptId?: string; blocked: boolean; log: string } = {
      state: "unknown", runId: "old-run-orders", blocked: true, log: "Eligibility case 0",
    };
    await page.route("**/api/analysis-status", (route) => route.fulfill({ json: { orders: observed } }));
    await page.goto(`${origin}#/capabilities`);
    const ineligible = [
      { state: "unknown", runId: "old-run-orders" },
      { state: "interrupted", attemptId: "old-attempt-orders" },
      { state: "partial", runId: "old-run-orders", attemptId: "old-attempt-orders" },
      { state: "failed", runId: "old-run-orders", attemptId: "old-attempt-orders" },
    ];
    for (const [index, job] of ineligible.entries()) {
      observed = { ...job, blocked: true, log: `Eligibility case ${index}` };
      if (index > 0) await page.clock.runFor(2100);
      await page.getByText(observed.log, { exact: true }).waitFor();
      assert.equal(await page.getByRole("button", { name: "Retry interrupted run", exact: true }).count(), 0);
      assert.equal(await page.getByRole("button", { name: "Analyze code", exact: true }).isDisabled(), true);
    }
  });
});

test("Given invalid repositories or a saved capability with queue failure, when creating, then input and partial success are handled honestly", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.goto(`${origin}#/capabilities`);
    const name = page.getByRole("textbox", { name: "Name", exact: true });
    const repositories = page.getByRole("textbox", { name: /Repositories/ });
    const create = page.getByRole("button", { name: "Create and queue check", exact: true });
    await name.fill("New orders");
    await repositories.fill("https://gitlab.com/acme/orders");
    let creates = 0;
    await page.route("**/api/capabilities", async (route) => {
      creates++;
      await route.fulfill(creates === 1 ? {
        status: 400, json: { error: "Invalid repositories", lines: [{ line: 1, input: "https://gitlab.com/acme/orders", message: "Only GitHub URLs are supported" }] },
      } : {
        status: 503, json: { created: true, queued: false, capability: { id: "new-orders" }, error: "Saved, but the source check could not be queued. Retry Check; do not recreate." },
      });
    });
    await create.click();
    await page.getByText("Invalid repositories", { exact: true }).waitFor();
    assert.equal(await name.inputValue(), "New orders");
    assert.equal(await repositories.inputValue(), "https://gitlab.com/acme/orders");
    await repositories.fill("https://github.com/acme/orders");
    await create.click();
    await page.getByText("Saved, but the source check could not be queued. Retry Check; do not recreate.", { exact: true }).waitFor();
    assert.equal(await name.inputValue(), "");
    assert.equal(await repositories.inputValue(), "");
    assert.equal(creates, 2, "partial success must never automatically recreate a capability");
  });
});

for (const endpoint of ["capability list", "analysis status"]) {
  test(`Given a hung ${endpoint} request, when its deadline expires, then polling retries and recovers`, async () => {
    await withIngestionPage(async (page, origin) => {
      await page.goto(`${origin}#/capabilities`);
      await page.waitForFunction(() => [...document.querySelectorAll("button")].some((button) => button.textContent === "Analyze code" && !button.disabled));
      let reads = 0;
      let release: () => void = () => undefined;
      const barrier = new Promise<void>((resolve) => { release = resolve; });
      const pattern = endpoint === "capability list" ? "**/api/capabilities?*" : "**/api/analysis-status";
      await page.route(pattern, async (route) => {
        reads++;
        if (reads === 1) await barrier;
        await route.fulfill({ json: endpoint === "capability list" ? [capability] : { orders: { state: "idle", blocked: false } } });
      });
      const pending = page.waitForRequest(pattern);
      await page.clock.runFor(2100);
      await pending;
      await page.clock.runFor(15100);
      assert.equal(await page.getByText(/timed out/i).count(), 1, "a stalled refresh must surface a bounded timeout rather than wait forever");
      const retried = page.waitForResponse(pattern);
      await page.clock.runFor(2100);
      await retried;
      assert.equal(reads, 2, "polling must retry after a timed-out request");
      await page.waitForFunction(() => !document.body.innerText.toLowerCase().includes("timed out"));
      release();
    });
  });
}

test("Given twenty capabilities, when status refreshes, then one batch replaces per-capability request fanout", async () => {
  await withIngestionPage(async (page, origin) => {
    const capabilities = Array.from({ length: 20 }, (_, index) => ({ ...capability, id: `orders-${index}`, name: `Orders ${index}` }));
    let batches = 0;
    let individualReads = 0;
    await page.route("**/api/capabilities?*", (route) => route.fulfill({ json: capabilities }));
    await page.route("**/api/capabilities/*/analyze", async (route) => {
      individualReads++;
      await route.fulfill({ json: { state: "idle", blocked: false } });
    });
    await page.route("**/api/analysis-status", async (route) => {
      batches++;
      await route.fulfill({ json: Object.fromEntries(capabilities.map((entry) => [entry.id, { state: "idle", blocked: false }])) });
    });
    await page.goto(`${origin}#/capabilities`);
    await page.getByRole("heading", { name: "Orders 19", exact: true }).waitFor();
    await page.clock.runFor(100);
    assert.equal(individualReads, 0, "status reads must not fan out with the number of capabilities");
    assert.ok(batches > 0, "the capability list must receive an initial status batch");
    const initial = batches;
    await page.clock.runFor(2100);
    assert.equal(batches - initial, 1, "one polling cycle must use exactly one batch request");
  });
});

test("Given a narrow viewport, when managing a capability, then every action stays inside the page", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${origin}#/capabilities`);
    await page.getByRole("heading", { name: "Orders", exact: true }).waitFor();
    const layout = await page.evaluate(() => ({ width: innerWidth, contentWidth: document.documentElement.scrollWidth }));
    assert.ok(layout.contentWidth <= layout.width, `capability actions must not overflow the viewport: ${JSON.stringify(layout)}`);
    for (const action of ["Check sources", "Analyze code", "Rebuild intelligence", "Remove"]) {
      const bounds = await page.getByRole("button", { name: action, exact: true }).boundingBox();
      assert.ok(bounds && bounds.x >= 0 && bounds.x + bounds.width <= layout.width, `${action} must stay on-screen`);
    }
  });
});

test("Given a long repository identity, when managing ingestion on mobile, then revision controls stay inside the page", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.setViewportSize({ width: 390, height: 844 });
    const repository = "apd-asq-assortmentengagementservice-api";
    const revision = "a".repeat(40);
    await page.route("**/api/meta", (route) => route.fulfill({ json: {
      generation: 0, capabilities: [{ id: "orders", name: "Orders", repositories: [repository] }],
      githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
      similarity: { database: false }, revisions: [{ repositoryId: repository, commitSha: revision, label: "a18999ab245f", tags: [] }],
      defaultView: { [repository]: revision }, artifacts: 0, problems: [], hasSource: false, hasEvaluation: false, nodeKinds: [],
    } }));
    await page.route("**/api/view?*", (route) => route.fulfill({ json: { [repository]: revision } }));
    await page.goto(`${origin}#/capabilities`);
    await page.getByLabel(`Revision of ${repository}`, { exact: true }).waitFor();
    const layout = await page.evaluate(() => ({ width: innerWidth, contentWidth: document.documentElement.scrollWidth }));
    assert.ok(layout.contentWidth <= layout.width, `repository scope controls must not overflow: ${JSON.stringify(layout)}`);
  });
});

test("Given pending capability removal, when actions are shown, then competing mutations are disabled", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.goto(`${origin}#/capabilities`);
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((button) => button.textContent === "Analyze code" && !button.disabled));
    page.on("dialog", (dialog) => dialog.accept());
    let release: () => void = () => undefined;
    const barrier = new Promise<void>((resolve) => { release = resolve; });
    await page.route("**/api/capabilities/orders", async (route) => {
      await barrier;
      await route.fulfill({ status: 204 });
    });
    const pending = page.waitForRequest("**/api/capabilities/orders");
    await page.getByRole("button", { name: "Remove", exact: true }).click();
    await pending;
    assert.equal(await page.getByRole("button", { name: "Analyze code", exact: true }).isDisabled(), true, "analysis must not race pending deletion");
    assert.equal(await page.getByRole("button", { name: "Check sources", exact: true }).isDisabled(), true, "source checking must not race pending deletion");
    assert.equal(await page.getByRole("button", { name: /Remov/ }).isDisabled(), true, "removal cannot be submitted twice");
    release();
  });
});
test("Given a named host GitHub account, when analysis and source check are queued, then only the account name is sent", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.route("**/api/github-accounts", (route) => route.fulfill({ json: {
      available: true, accounts: [{ login: "synthetic-octo", active: false }, { login: "synthetic-active", active: true }],
    } }));
    await page.goto(`${origin}#/capabilities`);
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((button) => button.textContent === "Analyze code" && !button.disabled));
    const selector = page.getByRole("combobox", { name: /^GitHub account/ });
    await selector.locator("option", { hasText: "synthetic-active (active)" }).waitFor({ state: "attached" });
    await selector.selectOption("synthetic-octo");
    const analyze = page.waitForRequest((request) => request.method() === "POST" && new URL(request.url()).pathname === "/api/capabilities/orders/analyze");
    await page.getByRole("button", { name: "Analyze code", exact: true }).click();
    const analyzeRequest = await analyze;
    assert.equal(new URL(analyzeRequest.url()).search, "?githubAccount=synthetic-octo");
    assert.equal(analyzeRequest.postData(), null, "analysis admission must not carry credential material");
    const check = page.waitForRequest((request) => request.method() === "POST" && new URL(request.url()).pathname === "/api/capabilities/orders/check");
    await page.getByRole("button", { name: "Check sources", exact: true }).click();
    const checkRequest = await check;
    assert.equal(new URL(checkRequest.url()).search, "?githubAccount=synthetic-octo");
    assert.equal(checkRequest.postData(), null, "source check admission must not carry credential material");
  });
});

test("Given unavailable host account names, when analysis is queued, then automatic host credentials remain usable", async () => {
  await withIngestionPage(async (page, origin) => {
    await page.route("**/api/github-accounts", (route) => route.fulfill({ status: 503, json: { error: "GitHub CLI account list failed" } }));
    await page.goto(`${origin}#/capabilities`);
    await page.getByText("Host account names are unavailable: GitHub CLI account list failed. Automatic host credentials remain available.", { exact: true }).waitFor();
    const selector = page.getByRole("combobox", { name: /^GitHub account/ });
    assert.deepEqual(await selector.locator("option").allTextContents(), ["Automatic host credentials"]);
    assert.equal(await selector.inputValue(), "");
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((button) => button.textContent === "Analyze code" && !button.disabled));
    const analyze = page.waitForRequest((request) => request.method() === "POST" && new URL(request.url()).pathname === "/api/capabilities/orders/analyze");
    await page.getByRole("button", { name: "Analyze code", exact: true }).click();
    assert.equal(new URL((await analyze).url()).search, "", "automatic host credentials must not send an account name");
  });
});

test("Given a completed source check with a failed repository lock, when Explorer renders it, then the sanitized reason and recovery guidance are tied to the check, not an analysis run", async () => {
  await withIngestionPage(async (page, origin) => {
    const failedCapability = {
      ...capability,
      repositories: [{ ...capability.repositories[0], error: "source acquisition failed" }],
      check: {
        requestId: "check-f106-gap", capabilityId: "orders", configurationHash: "cfg", state: "done",
        acceptedAt: "2026-11-07T00:00:00Z", cancellationRequested: false, stale: false,
        statusUrl: "/api/source-checks/check-f106-gap", duplicate: false,
      },
      checkStale: false,
    };
    await page.route("**/api/capabilities?*", (route) => route.fulfill({ json: [failedCapability] }));
    await page.goto(`${origin}#/capabilities`);
    await page.getByText("source acquisition failed", { exact: false }).waitFor();
    await page.getByText("Check check-f106-gap could not lock this repository. Verify its location, ref and access, then view the host worker logs before retrying.", { exact: false }).waitFor();
    const pageText = await page.textContent("body");
    assert.ok(pageText, "page body must render");
    assert.ok(!/ghp_|github_pat_|Bearer\s/.test(pageText), "no token value or credential material may ever reach the rendered page");
    assert.equal(await page.getByText(/^Run\s+\S+\s+·/).count(), 0, "a repository check failure must not invent or render an analysis run/stage identity");
  });
});
