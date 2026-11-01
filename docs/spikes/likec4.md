# LikeC4 architecture visualization spike

This isolated experiment renders a deterministic architecture graph snapshot with the real LikeC4 renderer. It does not replace the production React Flow view or introduce process/decision extraction. Work and the eventual PR target `stage-1-contract-and-fixtures`; publication waits for reconciliation PR #11 to merge and the stage-based local CI gate to pass.

## Run locally

```sh
make explore PORT=5090 ARTIFACTS=fixtures/expected INTERPRETATION=none CE_DATABASE= GITHUB_AUTH=none
# In another terminal:
make likec4-preview
```

Open the Explorer at http://127.0.0.1:5090/#/architecture and the spike at http://127.0.0.1:5091/. Both commands use repository sandbox containers. The Explorer shell uses the available viewport width; narrow screens stack the architecture panels.

The isolated package pins LikeC4 1.60.0. `make likec4-check` installs the lockfile, checks deterministic export and runs parser/grouping tests and the production build. It is also a `make ci` prerequisite. `make likec4-browser-check` uses a separately pinned Playwright image for optional local browser validation; it does not add a hosted browser job or service.

## Structure, viewpoints and interaction

The reference HTML's `genC4()` nests elements in model containers and defines collapsed landscape, expanded flow and scoped views. This spike now follows that structure rather than relying only on display rectangles.

The downloaded `.c4` includes eighteen named viewpoints for this fixture: landscape; horizontal/vertical resource overview; SQL-object detail; applications, data and messaging; five repository scopes; and six SQL-resource scopes. Views are compiled from source, not just UI filters. SQL resources collapse at overview level and objects appear only in detail views. See [the SME council findings](likec4-sql-council.md) for the identity/shape decisions.

- **Viewpoints** stays visible in a dedicated left column, with favorites first and clustered navigation below. It cannot cover canvas controls. On narrow screens navigation occupies a bounded row above the diagram. Star/unstar favorites; choices are remembered for this snapshot.
- **Ledger** starts expanded and can be minimized or expanded. Its twelve category swatches use the exact colors supplied in `ColorLedger.drawio.html`. Applications map to Application, channels to Interface, SQL objects to Data Object, and SQL resources to IT Component. These are visual conventions, not inferred business classifications. Repository/presentation boundaries remain neutral. LikeC4 derives contrasting strokes and translucent boundary tints from these base colors; swatches retain the exact hex values.
- Explicit repository membership automatically creates initial repository groups. Missing ownership is not guessed. Separate SQL resources are generated from exact scoped store identities; they are not automatically proven physical databases.
- In the resource overview, click a group to rename it, edit membership or ungroup it using controls in the canvas. In membership editing, click nodes to add/remove them. Ungrouping removes the boundary, not its artifacts.
- Shift-click nodes and choose **Group selection** to create a custom group. Moving a member into another group removes its prior presentation membership. Drag a group header to move its members together.
- Group names, membership and deletions persist in browser storage for the exact generated source snapshot and survive viewpoint changes. A changed source snapshot restores identified defaults rather than reusing stale IDs. Manual positions remain temporary; reset layout restores the selected view's generated positions while retaining group definitions.
- Source-oriented/scoped views retain the evidence hierarchy. Presentation groups are editable in the horizontal/vertical resource overview, not by rewriting source ownership in a drilldown. Nested custom groups are not supported.
- Dark mode starts on. Animation starts off; reduced motion disables it. Decorative particles/highlights do not represent measured traffic or execution.
- Source inspection/download preserves the original generated model, all named viewpoints and evidence. Browser presentation edits are saved separately; they do not rewrite the downloaded `.c4`.

## Evidence and determinism

`spikes/likec4/test/all-repositories.graph.json` is the unscoped architecture API response for `level=system` (omit `cap`), captured from the stage-based Explorer with `ARTIFACTS=fixtures/expected`. It contains 22 source artifact nodes and 31 relationships across all five loaded repositories. `all-repositories.selection.json` records the corresponding `/api/view` revision selection. The original Weekly sales snapshot remains a focused regression fixture. The generated model adds explicit repository and SQL-scope containers without inventing physical resources. It is not a live connection to an analyzed user repository.

The exporter sorts identities, canonicalizes evidence object keys, validates identifiers/endpoints/certainty, quotes source text and retains the complete original node/edge records in LikeC4 metadata. Semantic arrays retain their order. Candidate, unbound and unresolved relationships remain visibly uncertain. Name associations retain an undirected dashed style. No AI, remote data source or business inference is involved.

Regenerate through the sandbox:

```sh
scripts/sandbox --offline node sh -c 'cd spikes/likec4 && node scripts/export-graph.mjs test/all-repositories.graph.json src/model/architecture.c4'
```

The supplied six-language reference HTML informed the comparison. Its LikeC4-looking diagram was hand-rendered SVG; this experiment actually compiles LikeC4 source. The private reference HTML and its generated experiments are not committed.

## Validation and limits

Focused tests cover deterministic output, malformed identities/endpoints, certainty handling, source-text escaping with the actual LikeC4 parser, and group CRUD/bounds invariants, exact SQL scope grouping and declared-versus-referenced object distinctions. Browser checks cover 5120×2160, 2560×1080, 1920×1080 and 390×844; full-width layout, source download, motion preferences, third-party requests, dark mode, group creation, equal member movement during group dragging, in-canvas create/read/update/delete, snapshot-bound persistence, named viewpoints and resource/object-level detail; non-overlapping permanent navigation, favorite persistence, and default-open/minimizable ledger with all twelve exact color swatches. Screenshots and measurements are local under `.sandbox-cache/likec4-browser`.

The renderer's default remote font injection is disabled. The package audit reported zero vulnerabilities at the initial locked installation. The renderer adds roughly 785 KB gzip of JavaScript, so production adoption needs a deliberate lazy-loading/bundle decision. This is not a physical-monitor validation, compatibility proof for arbitrary architecture graphs, or a full-project local `act` result. The full stage-aligned CI/security gate must run after #11 merges and before publication.

LikeC4 is useful for architecture views and compound grouping. BPMN/DMN semantics and bpmn.io evaluation belong to issue #12, not this visualization adapter. Keep the existing evidence contract authoritative regardless of renderer.

Settings stay at the bottom of the viewpoint menu: Orthogonal defaults on, Animation defaults off, and Dark mode defaults on. Toggles apply across viewpoints for the current page session. Reduced-motion preferences disable animation. The viewer defaults every viewpoint to orthogonal routing. Build-time source retains spline geometry so switching orthogonal off can restore curves without relayout; the runtime switch preserves node positions and group membership.

Orthogonal routing uses a deterministic 12-unit grid and treats parallel segments less than 24 diagram units apart as clearance collisions. The apparent gap scales with zoom. It spaces and aligns ports, chooses sides from free space between node boundaries, reserves connection exits with clearance in every direction, avoids nodes, and penalizes occupied lanes/crossings. Three fixed routing orders are compared, preferring fewer fallback routes, clearance collisions and crossings before fewer bends/shorter paths. Labels are placed beside segments with node/label collision checks. There are no fixture-specific names, identities or positions in the router.

The generic crossing causes were incompatible far-end bend choices for opposing connections, missing destination approach direction, and paths passing too close to another connection's reserved exit. The router now prefers a common corridor for turning lanes, requires the correct destination approach, and protects future exits. Each connection releases its own reservations while searching, avoiding self-induced zigzags. A clear, aligned connection produces one straight segment. Backward drawing direction preserves semantic arrow endpoints.

Node positions and evidence are preserved; geometry is recomputed after group/layout snapshots. The 24-unit clearance is a scored routing constraint, not a universal guarantee: overlapping nodes, crowded ports, non-planar graphs, unroutable connections and very large layouts can retain collisions or native fallback routing. Relationships are never discarded to improve the score. Orthogonal mode owns route control points; source geometry remains the basis for the initial curved mode.

The browser comparison against `cdde13d` (same fixture, node layout, LikeC4 1.60.0 and 1920×1080 viewport) measured actual rendered SVG straight segments:

| View | Shared segment pairs | Parallel pairs closer than 24 units | Proper crossings | Straight segments |
|---|---:|---:|---:|---:|
| Resource overview | 3 → 0 | 3 → 0 | 2 → 0 | 36 → 20 |
| Vertical resource overview | 5 → 0 | 2 → 0 | 0 → 0 | 39 → 18 |
| SQL object detail | 3 → 0 | 7 → 0 | 8 → 2 | 54 → 32 |

The comparison excludes rounded corner arcs and endpoint touches; it does not claim universal planar routing. Nineteen focused tests cover evidence/geometry preservation, obstacle avoidance, opposing lanes, deterministic ordering, repeated snapshot bounds, fallback, straight routes, backward arrow direction, mirrored/rotated offset connections with changed identities, and wide nodes with narrow side gaps. The normal browser suite checks four screen sizes, routing toggles and group CRUD/dragging.

The optional `test/routing-benchmark.mjs` compares a separately built baseline supplied through `ROUTING_BASELINE_URL` with the local preview, asserts no shared or too-close parallel segments and no extra crossings for these three fixture views, and writes screenshots and measurements under `.sandbox-cache/likec4-browser`. The baseline was built from the spike at `cdde13d`, with unchanged source/model and dependencies; its entry point was restored into a temporary copy. No additional hosted CI job or dependency was introduced.

## System-wide scope

The default **All repositories · integrations** viewpoint spans api-cs, forecast-cs, pricing-node, web-ts and worker-cs. Repository viewpoints remain available for drill-down. Expand **Snapshot coverage** in the left menu to inspect each selected revision, partial-analysis badges and binding caveats. The default revision selection belongs to the loaded fixture artifacts, not necessarily a repository's current branch tip.

This reuses the existing unscoped Architecture API; no alternate graph service, extraction rules or resource identity scheme were introduced. HTTP, data and messaging integrations are included where supported by the selected evidence. This is not a claim of every code dependency or every repository in an organization. Same-name resources remain distinct; broken bindings and candidate matches are preserved. Evidence lists may be capped at 40 facts per edge by the API while the edge count remains larger.

The earlier routing benchmark applies only to the retained three-repository Weekly sales snapshot at commit d6a7c2f. It does not certify zero crossings or clearance in this larger system snapshot. Browser regression checks now use the five-repository snapshot; no full-project act gate or PR publication is claimed while #11 remains pending.

## Long associations and label clearance

Aligned endpoints separated by other nodes now use a common outside corridor with side-entry ports. Same-side ports use a stable nested ordering, avoiding unnecessary wraparound paths at shared endpoints. This is geometry-based and applies after rotation; it does not inspect repository names, resource labels or fixture IDs. For the all-repository overview, the forecast/worker and pricing/worker channel associations decrease from seven/nine straight segments to three each, with node positions and relationship metadata unchanged.

Label placement searches along segments at text-sized intervals, reserves eight model units of padding, and checks every routed line as well as nodes, group headers and previous labels. Retained native labels/routes are included when a route falls back. Browser checks inspect actual rendered label rectangles at all four supported viewport sizes. Dense layouts may still exhaust available label space; this is bounded placement, not a universal collision-free layout guarantee.

The three `article-stats-updated` nodes are three source references, not three proven deployed topics:

| Repository | Evidence establishes | Namespace identity |
|---|---|---|
| forecast-cs | Topic reference with subscription `forecast` | Unbound configuration reference |
| worker-cs | Sender to a queue-or-topic reference | Separate unbound configuration reference |
| pricing-node | Queue reference | Explicit `sb-pricing.servicebus.windows.net` |

`CreateSender(TopicName)` does not establish a topic type from the variable name. `CreateProcessor(TopicName, SubscriptionName)` establishes a topic/subscription reference, while pricing uses the queue receiver overload. The candidate same-name association edges have no supporting integration facts and are undirected; they do not establish message flow or shared identity. Binding evidence is required before consolidating unresolved references. The references and their distinct scopes remain unchanged by the visual correction.

## Embedded Architecture tab and interaction performance

`make explore` now builds and serves the experiment under `/likec4/` on the Explorer's own origin. Open **Architecture → LikeC4 · experiment**; port 5091 remains optional for standalone inspection. The production UI build runs before the spike assets are copied into `wwwroot`, and the .NET build then includes both. The large LikeC4 bundle loads only when its tab is opened. No additional hosted CI job or service is introduced.

Embedded mode removes the standalone title, notice and footer, removes the frame border, and uses Explorer's zinc/blue interface colors and system font. It follows the same system light/dark preference as the Explorer, including changes while open; the embedded theme switch is informational rather than allowing an inconsistent local override. Diagram ledger colors retain their semantic values. A same-origin frame still isolates the experimental renderer/dependency tree; the surrounding page explicitly states that parent capability/revision selectors do not filter this captured fixture snapshot.

Full three-pass routing no longer runs during React rendering. The immediate update reuses existing paths, adjusts incident endpoints orthogonally, and translates internal paths/labels when a group moves together. It is explicitly provisional: moved obstacles, label clearance and global lane choices are resolved by the unchanged complete router in a worker. A new layout/view/mode cancels the previous worker; immutable layout identity guards results, and cached completed layouts avoid redundant refinement. Reset retries a failed worker.

`make likec4-interaction-check` measures twelve real node drags inside the embedded tab, checks actual movement, follows each new routing generation to completion, and records preview computation, worker computation and end-to-end settling separately in `.sandbox-cache/likec4-browser/interaction-performance.json`. Its 5 ms assertion applies only to provisional route computation. It does not assert a 5 ms paint, event-to-paint latency, serialization time or complete collision-aware optimization. The worker remains a several-hundred-millisecond operation for this fixture; worker isolation improves responsiveness rather than claiming that computation disappeared.

Measured in the final embedded dark-theme run (12 moves, headless Chromium at 1920×1080): provisional computation maximum/p95 1.6 ms. This is a local fixture result, not a universal performance guarantee. Full worker and end-to-end timings are retained separately in the interaction receipt.
