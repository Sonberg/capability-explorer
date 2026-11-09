# Share a live LikeC4 canvas without changing evidence semantics

* Status: accepted
* Date: 2026-11-01
* Related: issues #3, #4, #13; MADR-0004, MADR-0005, MADR-0008

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for Share a live LikeC4 canvas without changing evidence semantics; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:647ae3a4f4a424b216fe1708b71465df2703f61799a32bb6dcdd50e33b58bb4f`.
Previous decision status (historical): `proposed for owner review; implementation and validation in progress`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem

The Architecture page has a live React Flow graph and a separate LikeC4 experiment
compiled from fixed fixture files. The experiment cannot answer questions about the
current capability, revisions or newly analyzed repositories. Glossary connection
navigation also depends on the original Architecture renderer. Replacing only one
canvas would leave divergent interaction, provenance and maintenance behavior.

## Decision drivers

Live selected inputs, deterministic projections, exact evidence identity, shared
interaction behavior, bounded work, responsive dragging and frugal local execution.
The existing backend contract and source inspection remain authoritative. Presentation
groups and diagrams must not create facts or infer physical database identities.

## Considered options

1. Keep the fixture iframe alongside the live graph.
2. Reuse the LikeC4 React renderer with a typed browser model/layout worker.
3. Add a Node compiler service to compile and lay out dynamic LikeC4 source.

## Decision outcome

Choose option 2, subject to the executable acceptance gates recorded in the build
receipt. Both Architecture and glossary connections use one embedded renderer.
The fixture experiment remains an isolated regression harness, not production data.

A pure canonical projection supplies both downloadable diagram source and the
runtime model. Public LikeC4 model/compute APIs and the public Graphviz WASM
package provide initial layout in the model worker; the existing orthogonal routing
worker refines interactive geometry. The initial Dagre candidate failed measured
performance parity, so it is retained only as a synchronous test adapter. The root LikeC4 compiler requires Node APIs and is not imported into the
browser. No private compiler internals, additional backend service, container image,
inference provider or vector index is required for drawing a graph.

Original source node and edge IDs survive projection. Rendered relationships that
represent multiple source edges expose all contributors for explicit inspection.
Counts, capped facts, uncertainty and original evidence remain in the source graph.
Same/alias reviewed memberships are undirected associations; derived_from retains
its occurrence-to-term direction. Neither term hubs, groups nor source navigation
establish runtime value equality. Names alone never join cross-repository resources.
The engineering ubiquitous language remains separate from analyzed glossary terms.

Scope includes capability, effective revision request and analysis generation.
Scope changes discard stale graph and selection immediately. Model identity also
binds canonical input content, associations and projection version, so reviews or
binding changes cannot silently reuse obsolete layouts. Obsolete worker work is
cancelled and late results cannot replace the current snapshot. Work and retained
state are bounded; validation, computation and routing failures remain visible.
Presentation preferences are separate from source evidence and scoped to the input.

The production UI retains evidence inspection, abstraction levels, breadcrumbs,
drill actions and relation filters. Shared interactions retain the pinned viewpoint
menu, favorites, automatic/editable groups, ledger, orthogonal routing by default,
optional animation, dark mode and reduced-motion handling. Group membership changes
do not rewrite analyzed artifacts or reviewed assertions.

## Consequences and validation

The browser bundle grows and initial layout now has a worker lifecycle to manage.
Graphviz WASM is pinned directly at the already installed transitive version
1.29.2. Its public `Graphviz.load()`/`layout()` API consumes generated DOT IDs and
attributes. Bounded display text is XML-normalized and escaped into a fixed HTML
label subset; it cannot introduce attributes, links, images or executable markup.
Public LikeC4 style measurements, hierarchy-aware ranking and source-adjacent
declarations preserve content space without a private compiler dependency.
Original strings remain intact in the model and exported source. Returned JSON
identities, counts, label measurements and geometry are checked before rendering. Layout
quality, hierarchy, labels and interaction still require browser checks. See the
[public package documentation](https://hpcc-systems.github.io/hpcc-js-wasm/packages/graphviz/README.html).
No artifact schema, extraction behavior or API compatibility change is intended.
The intended release category is a compatible feature (minor under MADR-0007),
subject to release review of the combined changes. Unreleased notes follow
Keep a Changelog 1.1.0 through MADR-0006; no product version is published here.
MADR-0005 governs contributor claims, stage alignment and the validated PR handoff.
These policies apply to this work; their adoption was pending historically.
Their explicit 2026-11-09 approval is now recorded in those MADRs, without
certifying implementation or changing MADR-0016's main-only target.

Required validation includes shuffled-input determinism, stable identity under input
changes, association direction, contributor retention, invalid/bounded input cases,
worker cancellation and actual browser journeys for both canvases. Run the served
application at ordinary, ultrawide and narrow viewport sizes, with database-backed
review and read-only review. Existing sandbox suites and full act/security gates
remain mandatory before the owner-review PR against stage-1-contract-and-fixtures.

Issue #13 stays open: frame opportunities and routing callback durations are not
physical-display paint measurements. The replacement must report observed timings
and regressions honestly rather than claiming the 200 ms p95 settling target met.
General extractor repairs and the owner's BPMN/DMN decision remain separate work.
