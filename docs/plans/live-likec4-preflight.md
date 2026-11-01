# Live LikeC4 replacement: approved build preflight

User authorized Brain Build on 2026-11-01 for replacement of the production
Architecture canvas with the LikeC4 spike, driven by live selected artifacts.
The shared glossary canvas from PR #20 is included in parity requirements.

## Dependency and source state

PR #20 merged into `stage-1-contract-and-fixtures` at
`9aa05475848d27328a2253cb66db87fb77975ffe` on 2026-11-01. The approved prerequisite
is satisfied. Integration branch `feat/live-likec4-architecture` starts from that
commit with this preflight cherry-picked. Three isolated writing worktrees cover
model projection, shared renderer and browser parity; the coordinator owns wiring,
shared manifests and documentation. Issue #3 records ownership and preview port 5096.
No main changes, merge or auto-merge are authorized.

## Runtime proof and remaining choice

Pinned package: LikeC4 1.60.0. Its public root API exposes `LikeC4.fromSource`, with
`graphviz: 'wasm'`, and the returned instance provides `layoutedModel`.
The sandboxed offline Node probe performed canonical source export, runtime
compilation and layout of the five-repository fixture, then repeated with a changed
node label. It asserted no compiler errors, nonempty layout views, changed label
presence and distinct model hashes. Shuffling input nodes/edges preserved source.

| Probe | Views | Serialized model bytes | Single-call elapsed |
|---|---:|---:|---:|
| Existing fixture | 18 | 299927 | 467 ms |
| Changed label | 18 | 299975 | 285 ms |

These are two feasibility samples, not a latency distribution, browser result,
paint measurement or proof of the 200 ms settling target. No application rebuild
occurred between the inputs. Probe/result are local ignored artifacts in
`.sandbox-cache/live-feasibility/` in the research worktree. Execution used
`scripts/sandbox --offline node`, mounting existing dependencies read-only via
`SANDBOX_SOURCES`; no dependency installation or analyzed-code execution.

Do not directly move the root compiler into a browser worker: the pinned root
entry imports Node-specific modules. Public model/provider APIs can accept live
layouted data, but this alone does not prove a complete browser pipeline.

Candidate to test after the prerequisite lands:

1. Validate dynamic graph input and construct typed parsed model data.
2. Use the public `@likec4/core/compute-view` entry to compute views.
3. Implement a typed layout adapter using an already installed layout dependency,
   then feed `LikeC4Model.create` and the existing React provider/renderer.
4. Retain the spike's orthogonal refinement worker and exact source-ID mapping.

The public Builder exists, but its `ValidId` type expects literal IDs; do not hide
arbitrary API IDs behind unchecked casts. Public typed parsed model data is an
alternative worth testing. Existing Dagre is a candidate, not an approved layout
replacement: hierarchy, ports, labels, routing quality and performance must pass
parity tests. The installed package has no public Graphviz layout export; private
bundled implementation paths are not acceptable production dependencies. A Node
compiler companion or justified package change remains an alternative if the
browser route fails, with explicit operational/image cost comparison.

Installed source evidence:

- `likec4/package.json` exports and `dist/index.d.mts`: public compiler options/API.
- `likec4/dist/model/builder.d.mts`: public Builder re-export.
- `@likec4/core/dist/compute-view/index.d.mts`: parsed-model compute exports.
- `@likec4/core/dist/chunks/_types.d.mts`: Builder and literal-ID constraints.
- `@likec4/core/dist/chunks/index.d.mts`: DiagramNode/DiagramEdge/LayoutedView types.
- `@likec4/core/dist/chunks/LikeC4Model.d.mts`: create overload for layouted data.

## Required parity and acceptance mapping

| Behavior | Current location | Required verification |
|---|---|---|
| All repositories, capability, revisions, reload generation | client `state.tsx`, `Architecture.tsx` | Switching actual served inputs changes graph; obsolete results cannot apply |
| Landscape/system/repository/component/request levels and drills | `Architecture.tsx`, `ArchitectureApiTests.cs` | Breadcrumbs, hashes and capability drill remain usable |
| Kind filters, initial declares visibility, neighborhood highlighting | `Architecture.tsx` | UI filtering never changes facts or loses source identity |
| Node/edge selection, exact source inspection | `components/source.tsx`, Architecture details | Assert repository/commit/path/span requests and original source endpoint/direction |
| Capped facts and mixed certainty | backend `Architecture.cs` | Counts/truncation survive collapsed-edge aggregation; certainty never upgraded |
| Glossary reviewed assertions and occurrence metadata | PR20 `glossary-canvas.tsx`, `glossary-flow.tsx` | same/alias undirected; derived_from occurrence to term; original/input/occurrence IDs preserved |
| Viewpoint clusters/favorites, pinned navigation and ledger | spike `main.tsx`, viewpoint/ledger components | Real browser desktop, ultrawide and narrow checks |
| Identified groups and presentation CRUD/whole-group dragging | spike grouping/controls | Source ownership immutable; snapshot changes reconcile preferences visibly |
| Orthogonal default, animations off, dark mode, reduced motion | spike routing/style/menu | Existing behavior retained and site integrated |
| Worker lifecycle, undo/redo, immediate/refined geometry | spike routing/immediate-layout modules | Latest generation wins; cancellation/failure visible; no evidence mutation |
| Diagram-as-code download | spike exporter | Source/download and displayed model share a pure canonical projection |

Concrete migration risks found by independent read-only review:

- `scripts/export-graph.mjs` rejects human certainty and only recognizes sameName
  as an association. PR20's same/alias memberships need explicit support.
- `src/grouping.ts` identities depend on sorted array positions. Stable source IDs
  and explicit mapping are required for selection, persistence and late results.
- The exporter imports Node filesystem/URL modules. Extract a shared pure semantic
  projection for browser model and DSL output; do not maintain divergent semantics.
- Architecture currently clears selected objects only by level/focus, while useApi
  retains old data during loading. Clear/hide stale graph and provenance by full
  scope and analysis generation in the replacement.
- Multiple source edges can collapse into one rendered relationship. Preserve all
  contributing IDs, direction, uncertainty and bounded evidence counts.
- SQL references and presentation groups do not establish physical resources.
  Do not merge same-name resources or invent field-level value propagation.

## Execution arrangement after merge

Use one integration owner plus at most three isolated native workers. Freeze typed
snapshot/callback contracts first: original node IDs, original edge IDs, drill
requests, association IDs, selected ID and immutable input identity.

- Projection worker: new pure contracts/model/projection modules and tests.
- Renderer worker: new shared canvas, grouping/routing lifecycle and local styles.
- Integration/reviewer worker: independent parity/browser tests, separate paths.
- Coordinator: Architecture/Glossary wiring, manifests/locks, API/Makefile, new MADR,
  changelog/HANDOFF and reconciliation. No two writers share a worktree.

Add `make live-architecture-check` and `make live-architecture-browser-check`
with nonzero executable assertions; they do not exist yet. Preserve all existing
sandbox tests, glossary journey and full ordinary pre-push act/security gate.
Fresh `make pr-check` precedes the stage-targeted owner-review PR. Retire the
experimental tab/static fixture production path only after parity passes.

Document a replacement MADR using the next free number. Follow MADR0006/0007 for
Unreleased/compatibility; do not publish a version. Keep #6 general extractor
repairs, #12 owner-controlled BPMN/DMN scope and #13 unmet final-paint target honest.
Do not close #13 using provisional routing or two-frame-opportunity measurements.

## Historical preflight receipts and limitations

Brain grounding: `sparc/capability-cards.md#sparc` describes phased methodology;
it is not executable harness evidence. Two native gpt-6.1-sol/high workers performed
read-only runtime/parity research; the coordinator ran the sandbox probe. No model
inference endpoints, new paid providers or package installations. Native spend is
not measurable here. No SPARC implementation phase is graded complete from this
preflight. The prerequisite has since merged; see [execution evidence](live-likec4-execution.md) for implementation and current gates.

Not tested at preflight time: browser runtime model construction/layout, full replacement rendering,
layout parity/performance, new acceptance targets or production migration. These
are required implementation work, not completed claims.
