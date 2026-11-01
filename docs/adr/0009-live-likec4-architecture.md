# Share a live LikeC4 canvas without changing evidence semantics

* Status: proposed for owner review; implementation and validation in progress
* Date: 2026-11-01
* Related: issues #3, #4, #13; MADR-0004, MADR-0005, MADR-0008

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
runtime model. Public LikeC4 model/compute APIs and the existing Dagre dependency
provide initial layout; the existing orthogonal routing worker refines interactive
geometry. The root LikeC4 compiler requires Node APIs and is not imported into the
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
Dagre is a deliberately explicit initial layout choice, not a claim of parity with
Graphviz. Layout quality, hierarchy, labels and interaction require browser checks.
No artifact schema, extraction behavior or API compatibility change is intended.
Unreleased notes follow MADR-0006; no product version is published by this change.

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
