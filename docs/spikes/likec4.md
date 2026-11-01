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

The downloaded `.c4` includes thirteen named viewpoints for this fixture: landscape; horizontal/vertical resource overview; SQL-object detail; applications, data and messaging; three repository scopes; and three SQL-resource scopes. Views are compiled from source, not just UI filters. SQL resources collapse at overview level and objects appear only in detail views. See [the SME council findings](likec4-sql-council.md) for the identity/shape decisions.

- Use **Viewpoints** to open the left overlay drawer. Favorites appear first, followed by architecture, SQL objects, repositories and SQL resources. Star/unstar views to customize favorites; pin to keep the menu open while navigating. Pinning never resizes the canvas. Close or Escape unpins and closes. Favorites and pin preference are remembered; snapshot changes reset stale favorites.
- Open **Legend** for element/boundary symbols and relationship evidence styles, matching the two-part legend in the reference HTML. It overlays the canvas.
- Explicit repository membership automatically creates initial repository groups. Missing ownership is not guessed. Three separate SQL resources are generated from exact scoped store identities; they are not three proven physical databases.
- In the resource overview, click a group to rename it, edit membership or ungroup it using controls in the canvas. In membership editing, click nodes to add/remove them. Ungrouping removes the boundary, not its artifacts.
- Shift-click nodes and choose **Group selection** to create a custom group. Moving a member into another group removes its prior presentation membership. Drag a group header to move its members together.
- Group names, membership and deletions persist in browser storage for the exact generated source snapshot and survive viewpoint changes. A changed source snapshot restores identified defaults rather than reusing stale IDs. Manual positions remain temporary; reset layout restores the selected view's generated positions while retaining group definitions.
- Source-oriented/scoped views retain the evidence hierarchy. Presentation groups are editable in the horizontal/vertical resource overview, not by rewriting source ownership in a drilldown. Nested custom groups are not supported.
- Dark/light mode initially follows the system preference. Animation starts off; reduced motion disables it. Decorative particles/highlights do not represent measured traffic or execution.
- Source inspection/download preserves the original generated model, all named viewpoints and evidence. Browser presentation edits are saved separately; they do not rewrite the downloaded `.c4`.

## Evidence and determinism

`spikes/likec4/test/weekly-sales.graph.json` is the fixture-only architecture API response for `level=system&cap=weekly-sales`, captured on stage baseline `7fd2a2fe0905f20d6da7f2006bd76de5d2197d8c`. It contains nine source artifact nodes and twelve relationships. The generated model adds explicit repository and SQL-scope containers without inventing physical resources. It is not a live connection to an analyzed user repository.

The exporter sorts identities, canonicalizes evidence object keys, validates identifiers/endpoints/certainty, quotes source text and retains the complete original node/edge records in LikeC4 metadata. Semantic arrays retain their order. Candidate, unbound and unresolved relationships remain visibly uncertain. Name associations retain an undirected dashed style. No AI, remote data source or business inference is involved.

Regenerate through the sandbox:

```sh
scripts/sandbox --offline node sh -c 'cd spikes/likec4 && node scripts/export-graph.mjs test/weekly-sales.graph.json src/model/architecture.c4'
```

The supplied six-language reference HTML informed the comparison. Its LikeC4-looking diagram was hand-rendered SVG; this experiment actually compiles LikeC4 source. The private reference HTML and its generated experiments are not committed.

## Validation and limits

Focused tests cover deterministic output, malformed identities/endpoints, certainty handling, source-text escaping with the actual LikeC4 parser, and group CRUD/bounds invariants, exact SQL scope grouping and declared-versus-referenced object distinctions. Browser checks cover 5120×2160, 2560×1080, 1920×1080 and 390×844; full-width layout, source download, motion preferences, third-party requests, dark mode, group creation, equal member movement during group dragging, in-canvas create/read/update/delete, snapshot-bound persistence, named viewpoints and resource/object-level detail; overlay geometry, pin/favorite persistence, keyboard dismissal and legend visibility. Screenshots and measurements are local under `.sandbox-cache/likec4-browser`.

The renderer's default remote font injection is disabled. The package audit reported zero vulnerabilities at the initial locked installation. The renderer adds roughly 785 KB gzip of JavaScript, so production adoption needs a deliberate lazy-loading/bundle decision. This is not a physical-monitor validation, compatibility proof for arbitrary architecture graphs, or a full-project local `act` result. The full stage-aligned CI/security gate must run after #11 merges and before publication.

LikeC4 is useful for architecture views and compound grouping. BPMN/DMN semantics and bpmn.io evaluation belong to issue #12, not this visualization adapter. Keep the existing evidence contract authoritative regardless of renderer.
