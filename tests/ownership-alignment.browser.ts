import assert from "node:assert/strict";
import { before, after, test } from "node:test";
import playwright from "../spikes/likec4/node_modules/playwright/index.js";
import { createServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import type { ViteDevServer } from "../platform/src/CapabilityExplorer.Explorer/client/node_modules/vite/dist/node/index.js";
import { listenBrowserServer } from "./browser-server.ts";

let server: ViteDevServer;
let origin: string;
before(async () => {
  server = await createServer({
    root: new URL("../platform/src/CapabilityExplorer.Explorer/client", import.meta.url).pathname,
    server: { host: "127.0.0.1", port: 0, fs: { allow: [new URL("..", import.meta.url).pathname] } },
    logLevel: "error",
  });
  origin = await listenBrowserServer(server);
});
after(async () => { await server?.close(); });

const finding = {
  findingId: "of_leanix_link", repositoryId: "svc-a",
  checkKey: { type: "identity", system: "leanix" }, kind: "missing-source-record", status: "action-needed",
  evidence: [], observedRoles: [], expectedRoles: [], fingerprint: "a".repeat(64),
  reason: "No reviewed LeanIX entity link exists for svc-a.",
  nextAction: "Review and record the exact LeanIX entity link for svc-a.",
  resolutionOwner: null, triage: "needs-triage",
  verification: "A reviewed entity link and a current full LeanIX snapshot establish the record.",
};

for (const mode of ["files", "database"] as const) {
for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
  for (const theme of ["light", "dark"] as const) {
    test(`Ownership report ${mode} ${viewport.width} ${theme}: both audiences keep the same actionable LeanIX finding`, async () => {
      const browser = await playwright.chromium.launch({ headless: true });
      try {
        const page = await browser.newPage({ viewport, colorScheme: theme });
        page.setDefaultTimeout(10_000);
        const errors: string[] = [];
        const writes: string[] = [];
        const audiences: string[] = [];
        page.on("pageerror", error => errors.push(error.message));
        await page.route("**/api/**", async route => {
          const request = route.request();
          const url = new URL(request.url());
          if (request.method() !== "GET") writes.push(request.method());
          if (!["/api/meta", "/api/search", "/api/view", "/api/revisions", "/api/embeddings/status", "/api/generation", "/api/health", "/api/ownership-alignment"].includes(url.pathname)) {
            await route.fulfill({ status: 404, json: { error: "Unexpected fixture API request" } }); return;
          }
          let payload: unknown = {};
          if (url.pathname === "/api/meta") payload = mode === "database" ? {
            source: "postgres", snapshotId: "ownership-ui", generation: 1, capability: "*",
            selection: { synthetic: "a".repeat(40) }, capabilities: [], supported: ["search"], status: "ready",
          } : {
            generation: 1, snapshotId: "ownership-ui", capabilities: [], revisions: [], defaultView: {},
            githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
            similarity: { database: false }, artifacts: 1, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: [],
          };
          if (url.pathname === "/api/search") payload = mode === "database" ? { items: [], nextCursor: null, snapshotId: "ownership-ui" } : [];
          if (url.pathname === "/api/revisions") payload = { items: [], nextCursor: null, snapshotId: "ownership-ui" };
          if (url.pathname === "/api/embeddings/status") payload = {
            snapshotId: "ownership-ui", generation: 1, capability: "*", selection: { synthetic: "a".repeat(40) },
            state: "not-configured", configuredModel: null, coverageModel: null, coverageScope: "selected-view",
            similarityAvailable: false, snapshotArtifactCount: 1, selectedArtifactCount: 1, coveredArtifactCount: 0,
            snapshotArtifactsSha256: null, countsExact: false, counts: null,
            lastSuccessfulCompletion: null, reasonCode: null,
          };
          if (url.pathname === "/api/generation") payload = { generation: 1, snapshotId: "ownership-ui" };
          if (url.pathname === "/api/health") payload = { status: "healthy" };
          if (url.pathname === "/api/ownership-alignment") {
            const audience = url.searchParams.get("audience") ?? "engineering";
            audiences.push(audience);
            payload = {
              schema: "ce.ownership-alignment.v1", status: "partial", alignment: "action-needed", audience,
              inputVector: "b".repeat(64), generatedAt: "2026-11-09T00:00:00Z",
              coverage: { systems: ["github", "leanix", "servicenow", "backstage"].map(system => system === "servicenow"
                ? { system, state: "unknown", coverage: "full" }
                : { system, state: "current-full", snapshotId: "synthetic-v1", capturedAt: "2026-11-09T00:00:00Z" }) },
              findings: [finding], groups: [{
                key: audience === "engineering" ? "svc-a" : "unmapped",
                findingIds: [finding.findingId],
              }],
              diagnostics: [{ code: "manual-trust", message: "Review fields are declared by configured files, not verified signatures." }],
            };
          }
          await route.fulfill({ json: payload });
        });
        await page.goto(`${origin}/#/explore`);
        await page.getByRole("navigation", { name: mode === "database" ? "Database views" : "Views", exact: true }).waitFor();
        await page.getByRole("region", { name: "Symbol results" }).locator('[role="list"][aria-busy="false"]').waitFor({ state: "attached" });
        assert.deepEqual(errors, [], "The retained Explore fixture must be healthy before checking report discovery.");
        const reportLink = page.getByRole("link", { name: "Ownership alignment", exact: true });
        assert.equal(await reportLink.count(), 1, "The ownership report must be discoverable in current Explorer navigation.");
        await reportLink.click();
        await page.getByRole("heading", { name: "Ownership alignment", exact: true }).waitFor();
        const card = page.locator(`[data-finding-id="${finding.findingId}"]`);
        await card.waitFor();
        assert.ok((await card.innerText()).includes(finding.nextAction));
        assert.ok((await card.innerText()).includes(finding.verification));
        assert.ok((await card.innerText()).includes("Needs triage"));
        assert.equal(await page.getByLabel("Ownership findings").locator("[data-finding-id]").count(), 1);
        const audience = page.getByLabel("Report audience", { exact: true });
        const switched = page.waitForResponse(response => response.url().includes("/api/ownership-alignment?") && response.url().includes("architecture"));
        await audience.selectOption("architecture");
        await switched;
        await card.waitFor();
        assert.ok((await card.innerText()).includes(finding.nextAction));
        assert.equal(await page.getByLabel("Ownership findings").locator("[data-finding-id]").count(), 1);
        assert.deepEqual([...new Set(audiences)].sort(), ["architecture", "engineering"]);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        const coverage = await page.getByLabel("Ownership source coverage").innerText();
        assert.ok(coverage.toLowerCase().includes("leanix"));
        assert.ok(coverage.includes("Observed coverage: full") && coverage.includes("Limits alignment"), "Unknown freshness must still show observed coverage and limit alignment.");
        assert.deepEqual(errors, []);
        assert.deepEqual(writes, []);
      } finally { await browser.close(); }
    });
  }
}
}

test("Ownership request failures, invalid envelopes and denied grants never retain an earlier aligned verdict", async () => {
    const browser = await playwright.chromium.launch({ headless: true });
    try {
      const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
      const errors: string[] = [];
      page.on("pageerror", error => errors.push(error.message));
      let response: "ok" | "denied" | "invalid" | "failure" | "malformed" | "slow" = "ok";
      await page.route("**/api/**", async route => {
        const url = new URL(route.request().url());
        assert.equal(route.request().method(), "GET");
        if (url.pathname !== "/api/ownership-alignment") {
          const payload = url.pathname === "/api/meta" ? {
            generation: 1, snapshotId: "ownership-ui", capabilities: [], revisions: [], defaultView: {},
            githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
            similarity: { database: false }, artifacts: 1, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: [],
          } : url.pathname === "/api/search" ? [] : url.pathname === "/api/generation"
            ? { generation: 1, snapshotId: "ownership-ui" } : { status: "healthy" };
          await route.fulfill({ json: payload }); return;
        }
        if (response === "failure") { await route.fulfill({ status: 503, json: { error: `Synthetic unavailable ${"e".repeat(300)}` } }); return; }
        if (response === "malformed") { await route.fulfill({ status: 500, contentType: "text/html", body: "<html>Synthetic</html>" }); return; }
        if (response === "slow") { await new Promise(resolve => setTimeout(resolve, 500)); response = "invalid"; }
        if (response === "invalid") { await route.fulfill({ json: { schema: "wrong", alignment: "aligned" } }); return; }
        await route.fulfill({ status: response === "denied" ? 403 : 200, json: {
          schema: "ce.ownership-alignment.v1", status: response === "denied" ? "denied" : "ok",
          alignment: response === "denied" ? "not-evaluable" : "aligned", audience: url.searchParams.get("audience"),
          inputVector: "c".repeat(64), generatedAt: "2026-11-09T00:00:00Z",
          coverage: { systems: [] }, findings: [], groups: [], diagnostics: [],
          ...(response === "denied" ? { reason: "ownership-repository-not-granted", nextAction: "Request only a granted repository." } : {}),
        } });
      });
      await page.goto(`${origin}/#/ownership-alignment`);
      await page.getByText("Ownership verdict: aligned.", { exact: true }).waitFor();
      response = "denied";
      await page.getByLabel("Granted repository", { exact: true }).fill("svc-private");
      const scoped = page.waitForRequest(request => request.url().includes("repository=svc-private"));
      await page.getByRole("button", { name: "Apply repository scope", exact: true }).click();
      await scoped;
      await page.getByText("ownership-repository-not-granted", { exact: true }).waitFor();
      assert.equal(await page.getByText("Ownership verdict: aligned.", { exact: true }).count(), 0);
      response = "invalid";
      await page.getByRole("button", { name: "Refresh ownership evidence", exact: true }).click();
      await page.getByText("The ownership report response is invalid. No alignment is established.", { exact: true }).waitFor();
      response = "failure";
      await page.getByRole("button", { name: "Refresh ownership evidence", exact: true }).click();
      await page.getByRole("alert").getByText(/^The ownership report request failed \(HTTP 503\): Synthetic unavailable e+\. No alignment is established\.$/).waitFor();
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, "Long errors must wrap.");
      response = "malformed";
      await page.getByRole("button", { name: "Refresh ownership evidence", exact: true }).click();
      await page.getByRole("alert").getByText("The ownership report response could not be read. No alignment is established.", { exact: true }).waitFor();
      response = "ok";
      await page.getByLabel("Granted repository", { exact: true }).fill("");
      await page.getByRole("button", { name: "Apply repository scope", exact: true }).click();
      await page.getByText("Ownership verdict: aligned.", { exact: true }).waitFor();
      response = "slow";
      await page.getByRole("button", { name: "Refresh ownership evidence", exact: true }).click();
      assert.equal(await page.getByText("Ownership verdict: aligned.", { exact: true }).count(), 0, "Refresh must hide the earlier verdict immediately.");
      await page.getByText("The ownership report response is invalid. No alignment is established.", { exact: true }).waitFor();
      assert.equal(await page.getByText("Ownership verdict: aligned.", { exact: true }).count(), 0);
      assert.deepEqual(errors, []);
    } finally { await browser.close(); }
});

test("Dense ownership reports page with focus on the announced range and keep the report link discoverable", async () => {
  const browser = await playwright.chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1024, height: 768 } });
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.route("**/api/**", async route => {
      const url = new URL(route.request().url());
      assert.equal(route.request().method(), "GET");
      if (url.pathname === "/api/ownership-alignment") {
        const findings = Array.from({ length: 60 }, (_, index) => ({ ...finding, findingId: `of_dense_${index}`, status: index < 55 ? "action-needed" : "aligned" }));
        await route.fulfill({ json: {
          schema: "ce.ownership-alignment.v1", status: "partial", alignment: "action-needed", audience: url.searchParams.get("audience"),
          inputVector: "d".repeat(64), generatedAt: "2026-11-09T00:00:00Z", coverage: { systems: [] }, findings,
          groups: [{ key: "svc-a", findingIds: findings.map(item => item.findingId) }], diagnostics: [],
        } }); return;
      }
      await route.fulfill({ json: url.pathname === "/api/meta" ? {
        generation: 1, snapshotId: "ownership-ui", capabilities: [], revisions: [], defaultView: {},
        githubAuth: { source: "none", hostManaged: true }, interpretation: { enabled: false, chat: false, provider: "none" },
        similarity: { database: false }, artifacts: 1, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: [],
      } : url.pathname === "/api/search" || url.pathname === "/api/integrations" ? [] : url.pathname === "/api/generation"
        ? { generation: 1, snapshotId: "ownership-ui" } : { status: "healthy" } });
    });
    await page.goto(`${origin}/#/explore`);
    const more = page.getByRole("button", { name: "Show more views", exact: true });
    await more.waitFor();
    await more.click();
    await page.getByRole("button", { name: "Show earlier views", exact: true }).waitFor();
    for (let step = 0; step < 10 && await more.count(); step++) {
      await more.focus();
      await page.keyboard.press("Enter");
      await page.waitForTimeout(100);
    }
    assert.equal(await more.count(), 0, "The navigation must reach its trailing boundary.");
    assert.equal(await page.getByRole("navigation", { name: "Views", exact: true })
      .evaluate(element => element === document.activeElement), true, "A disappearing scroll control must transfer keyboard focus to stable navigation.");
    await page.goto(`${origin}/#/ownership-alignment`);
    const link = page.getByRole("link", { name: "Ownership alignment", exact: true });
    const [box, nav] = await Promise.all([link.boundingBox(), page.getByRole("navigation", { name: "Views", exact: true }).boundingBox()]);
    assert.ok(box && nav && box.x >= nav.x - 1 && box.x + box.width <= nav.x + nav.width + 1, "The current report link must be scrolled into view.");
    await page.locator("[data-finding-id]").first().waitFor();
    assert.equal(await page.locator("[data-finding-id]").count(), 50);
    await page.getByRole("button", { name: "Next findings", exact: true }).focus();
    await page.keyboard.press("Enter");
    const heading = page.getByRole("heading", { name: /^Findings 51–60 of 60/ });
    await heading.waitFor();
    assert.equal(await heading.evaluate(element => element === document.activeElement), true, "Paging must move focus to the findings range.");
    const top = await heading.evaluate(element => element.getBoundingClientRect().top);
    assert.ok(top >= 52 && top < 768, `The new page must start in view below the sticky header (top ${top}).`);
    assert.equal(await page.locator("[data-finding-id]").count(), 10);
    await page.getByRole("button", { name: "Previous findings", exact: true }).click();
    await page.getByRole("heading", { name: /^Findings 1–50 of 60/ }).waitFor();
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
});
