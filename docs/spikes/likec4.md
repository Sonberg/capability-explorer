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

## Interaction

- Choose horizontal or vertical arrangement. Switching arrangement resets temporary layout edits and groups.
- Use **Groups & layout** to select two or more ungrouped leaf nodes, name a group, and create a visible compound group. Drag its header to move the members together. Grouping is a presentation choice, not an extracted architectural boundary.
- Align selected nodes using the same panel. Native LikeC4 editing supports manual positioning; reset restores the selected arrangement's generated layout.
- Toggle dark/light mode. Initial mode follows the system preference.
- Animation starts off. When enabled, decorative particles and highlights follow the displayed paths without replacing their solid/dashed evidence styles. They do not depict measured traffic, timing or execution. Reduced-motion preference disables animation, including when the preference changes while the page is open.
- Source inspection/download contains the original deterministic `.c4` snapshot, **not** temporary browser groups or manual positions. Layout edits do not survive reload. Nested groups are intentionally excluded from this first experiment.

## Evidence and determinism

`spikes/likec4/test/weekly-sales.graph.json` is the fixture-only architecture API response for `level=system&cap=weekly-sales`, captured on stage baseline `7fd2a2fe0905f20d6da7f2006bd76de5d2197d8c`. It contains nine nodes and twelve relationships. It is not a live connection to an analyzed user repository.

The exporter sorts identities, canonicalizes evidence object keys, validates identifiers/endpoints/certainty, quotes source text and retains the complete original node/edge records in LikeC4 metadata. Semantic arrays retain their order. Candidate, unbound and unresolved relationships remain visibly uncertain. Name associations retain an undirected dashed style. No AI, remote data source or business inference is involved.

Regenerate through the sandbox:

```sh
scripts/sandbox --offline node sh -c 'cd spikes/likec4 && node scripts/export-graph.mjs test/weekly-sales.graph.json src/model/architecture.c4'
```

The supplied six-language reference HTML informed the comparison. Its LikeC4-looking diagram was hand-rendered SVG; this experiment actually compiles LikeC4 source. The private reference HTML and its generated experiments are not committed.

## Validation and limits

Focused tests cover deterministic output, malformed identities/endpoints, certainty handling, source-text escaping with the actual LikeC4 parser, and group evidence/bounds invariants. Browser checks cover 5120×2160, 2560×1080, 1920×1080 and 390×844; full-width layout, source download, motion preferences, third-party requests, dark mode, group creation, equal member movement during group dragging, multiple groups and arrangement reset. Screenshots and measurements are local under `.sandbox-cache/likec4-browser`.

The renderer's default remote font injection is disabled. The package audit reported zero vulnerabilities at the initial locked installation. The renderer adds roughly 770 KB gzip of JavaScript, so production adoption needs a deliberate lazy-loading/bundle decision. This is not a physical-monitor validation, compatibility proof for arbitrary architecture graphs, or a full-project local `act` result. The full stage-aligned CI/security gate must run after #11 merges and before publication.

LikeC4 is useful for architecture views and compound grouping. BPMN/DMN semantics and bpmn.io evaluation belong to issue #12, not this visualization adapter. Keep the existing evidence contract authoritative regardless of renderer.
