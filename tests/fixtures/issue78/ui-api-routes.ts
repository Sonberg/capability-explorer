type UiApiState = {
  databaseMode: boolean;
  listedGeneration?: number;
  requests: string[];
  sourceRequestIds: string[];
};

type EvidenceFixture = { unitPath: string; sourceSha256?: string };

export async function installIssue78UiApiRoutes(page: any, config: {
  state: UiApiState;
  snapshot: string;
  revisionA: string;
  revisionB: string;
  selection: Record<string, string>;
  bpmnHash: string;
  items: unknown[];
  evidenceById: Record<string, EvidenceFixture>;
  xmlById: Record<string, string>;
}) {
  const { state, snapshot, revisionA, revisionB, selection, bpmnHash, items, evidenceById, xmlById } = config;
  await page.route("**/api/**", async (route: any) => {
    const url = new URL(route.request().url());
    state.requests.push(`${url.pathname}${url.search}`);
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
    if (url.pathname === "/api/generation") return json({ generation: 3 });
    if (url.pathname === "/api/revisions") return json({ items: [], nextCursor: null, snapshotId: snapshot });
    if (url.pathname === "/api/search") return json([]);
    if (url.pathname === "/api/meta") return json(state.databaseMode ? {
      source: "postgres", snapshotId: snapshot, generation: 3, capability: "cap-a",
      selection, capabilities: [{ id: "cap-a", name: "Capability A", repositories: ["repo-a"] }],
      supported: ["models"], status: "ready"
    } : {
      generation: 3,
      capabilities: [{ id: "cap-a", name: "Capability A", repositories: ["repo-a"] }],
      githubAuth: { source: "none", hostManaged: false }, interpretation: { enabled: false, chat: false, provider: "none" },
      similarity: { database: false }, revisions: [
        { repositoryId: "repo-a", commitSha: revisionA, tags: [], label: "revision A" },
        { repositoryId: "repo-a", commitSha: revisionB, tags: [], label: "revision B" }
      ],
      defaultView: { "repo-a": revisionA }, artifacts: 0, problems: [], hasSource: true, hasEvaluation: false, nodeKinds: []
    });
    if (url.pathname === "/api/view") {
      const alternate = url.searchParams.get("view")?.includes(revisionB);
      if (alternate) await new Promise((resolve) => setTimeout(resolve, 300));
      return json({ "repo-a": alternate ? revisionB : revisionA });
    }
    if (url.pathname === "/api/models") {
      if (url.searchParams.has("cursor") && !/^\d+$/.test(url.searchParams.get("generation") ?? "")) {
        return json({ detail: "continuation requires numeric generation" }, 400);
      }
      const isAlternate = url.searchParams.get("view")?.includes(revisionB);
      const currentItems = isAlternate ? [items[4]] : items.slice(0, 3);
      const continuation = url.searchParams.has("cursor") ? items.slice(3) : currentItems;
      state.listedGeneration = 3;
      return json({
        schema: "ce.model-views.v1", items: continuation, problems: [], mode: state.databaseMode ? "database" : "files",
        snapshot: isAlternate ? "snapshot-ui-b" : snapshot, generation: 3,
        selection: isAlternate ? { "repo-a": revisionB } : selection,
        nextCursor: !isAlternate && !url.searchParams.has("cursor") ? "cursor-next" : null
      });
    }
    if (url.pathname === "/api/models/evidence") {
      const id = url.searchParams.get("id") ?? "";
      if (!/^\d+$/.test(url.searchParams.get("generation") ?? "")) {
        return json({ detail: "selected model request requires numeric generation" }, 400);
      }
      if (id === "candidate-cs") await new Promise((resolve) => setTimeout(resolve, 300));
      if (id === "model-malformed") return json({ schema: "unexpected", id });
      const evidence = evidenceById[id];
      if (!evidence) return json({ detail: "not found" }, 404);
      return json({
        schema: "ce.model-evidence-view.v1", id, evidence,
        snapshot: url.searchParams.get("snapshot") === "snapshot-ui-b" ? "snapshot-ui-b" : snapshot,
        generation: Number(url.searchParams.get("generation")), selection: url.searchParams.get("snapshot") === "snapshot-ui-b" ? { "repo-a": revisionB } : selection
      });
    }
    if (url.pathname === "/api/models/source") {
      const id = url.searchParams.get("id") ?? "";
      state.sourceRequestIds.push(id);
      if (!/^\d+$/.test(url.searchParams.get("generation") ?? "")) {
        return json({ detail: "selected model request requires numeric generation" }, 400);
      }
      if (id === "model-denied") return json({ detail: "denied" }, 403);
      if (id === "oversize-bpmn") {
        return route.fulfill({
          contentType: "application/json",
          body: JSON.stringify({
            schema: "ce.model-source-view.v1", id, repositoryId: "repo-a",
            commitSha: revisionB, unitPath: "models/oversized.bpmn.json", sha256: bpmnHash,
            format: "bpmn+xml", xml: "x".repeat(8_388_609),
            snapshot: "snapshot-ui-b", generation: 3, selection: { "repo-a": revisionB }
          })
        });
      }
      const xml = xmlById[id];
      const evidence = evidenceById[id];
      if (!xml || !evidence) return json({ detail: "not found" }, 404);
      return json({
        schema: "ce.model-source-view.v1", id, repositoryId: "repo-a",
        commitSha: revisionA, unitPath: evidence.unitPath, sha256: evidence.sourceSha256,
        format: id.startsWith("model-dmn") ? "dmn+xml" : "bpmn+xml", xml,
        snapshot: url.searchParams.get("snapshot") === "snapshot-ui-b" ? "snapshot-ui-b" : snapshot,
        generation: Number(url.searchParams.get("generation")), selection: url.searchParams.get("snapshot") === "snapshot-ui-b" ? { "repo-a": revisionB } : selection
      });
    }
    return json({ detail: `unexpected API route ${url.pathname}` }, 404);
  });
}

export type { UiApiState };
