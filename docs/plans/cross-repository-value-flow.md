# Brain Prompt: cross-repository flow and reviewed glossary relationships

Proposed implementation package, not an implementation claim or reservation of
code paths. Prepared against stage `db18165` on 2026-11-01. Coordination:
[issue #3](https://github.com/Sonberg/capability-explorer/issues/3);
product scope: [issue #4](https://github.com/Sonberg/capability-explorer/issues/4),
layers 4 and 3. Refresh issue comments, claims and stage before starting.

## GOAL

Let a user navigate revision-bound connections for a glossary occurrence across
repositories and persist reviewed term/relationship assertions, with evidence,
uncertainty and missing coverage visible, without any model calls.

## ASSUMPTIONS

<execution_inputs>
repository: Sonberg/capability-explorer
target_branch: stage-1-contract-and-fixtures
planning_base: db18165
operator_account: Tigon32
max_concurrent_agents_including_integration_owner: 4
external_provider_budget_usd: 0
implementation_status: proposed; not started by this planning PR
</execution_inputs>

- Deliver one substantial product package in three independently reviewable
  increments: read-only flow, reviewed persistence/API, integrated review UI.
  Default to sequential owner-reviewed PRs, not a new overlapping stack.
- The existing glossary candidates and source/evidence viewer are the starting
  point. `Glossary.cs` and `GlossaryDtos.cs` preserve occurrence repository,
  commit, unit, symbol and role. The Explorer currently exposes `/api/glossary`
  but has no glossary flow or mutation endpoint. Store has migrations 0001–0003
  and no GlossaryStore. Recheck these facts before allocating migration numbers.
- Use the existing Architecture canvas. Pending LikeC4 PRs #14/#15 are not a
  dependency; live LikeC4 model integration and issue #13 performance remain
  separately scoped. Do not describe its fixture snapshot as live input.
- #6 extractor gaps remain visible as partial/unresolved evidence; do not hide
  them or opportunistically rewrite extractors. #12 BPMN/DMN implementation
  remains blocked on the owner's separate scope decision.
- No LLM definitions, synonym inference, work-item integration, IaC extraction,
  process reconstruction, auth-platform redesign or ce.v1 schema redesign.
- Product Glossary terms describe analyzed repositories. They do not modify the
  project's own engineering ubiquitous language (MADR-0002/0004).

## STACK

- Existing .NET Explorer/Binding/Store, PostgreSQL/Npgsql, React/TypeScript and
  shared Architecture components. Inspect `ExplorerApi.cs`, `GlossaryDtos.cs`,
  `Architecture.cs`, `BindingStore.cs` and `Migrator.cs` before introducing types.
- Existing Docker sandbox, Makefile, Testcontainers and local act/security gate.
  Reuse images, package caches and fixture inputs. No new hosted CI service.
- SPARC phases below: grounded in RuvNet's `sparc/CARD/sparc-card` capability
  summary (retrieved 2026-11-01); this supports a methodology, not executable API
  claims. No RuVector capability is needed for deterministic graph traversal.
- Native Codex subagents execute; Ruflo may record existing coordination/memory
  per AGENTS.md. Discover live tools and validate configuration before use;
  registration or a coordination entry is not proof that work executed.
  Do not add a vector store or provider-backed swarm for this package.

## PHASES

### Specification — identity, evidence and review contract

Inventory current code and issue updates, then propose an implementation MADR
before schema/API integration. Specify a graph with two distinct edge classes:
source/binding-derived navigation and human assertions. A call, common channel
name, table read or endpoint relationship is context, not proof of field-level
value propagation. Only emit field/value mappings when explicit evidence supports
them. Stop at unsupported boundaries and explain why; never fabricate a continuous
value-flow path from coarse architecture relations. Human `same` means a reviewed
assertion of intended equivalence, not an observed runtime fact. `alias` and
`derived_from` remain distinct; direction is never silently inverted.

Define canonical occurrence/selection identities, revision-aware evidence links,
cycle handling, deterministic ordering and bounded traversal. Start with maximum
8 hops, 500 nodes and 1,000 edges; return limit/truncation reasons and configurable
validated limits. These are proposed product bounds, not established benchmarks.
An all-capabilities view must disclose which repositories/revisions were included.
An empty result must distinguish no evidence from incomplete/unsupported analysis.

Reviewed links need persistent logical symbol references plus provenance of the
exact occurrence the reviewer saw. Reanalysis may resolve a reference in another
revision but must not silently carry forward semantic approval. Missing, renamed
or ambiguous symbols become stale/orphaned; no name-based rebinding. Identify
reviewer attribution honestly under the current deployment model: a supplied
name is not authenticated identity. Define mutation access/CSRF boundaries and
validate identifiers/lengths before exposing writes; do not invent authentication.

Rubric: scope/identity 30, evidence boundaries 30, failure/limits 20, acceptance
mapping 20. Gate: `make test-contracts test-explorer` establishes the unchanged
baseline; every requirement above must map to a named new assertion in the
implementation test plan before proceeding. Baseline success alone is insufficient.

### Pseudocode — deterministic traversal and lifecycle

Specify stable adjacency ordering and visited identities, multiple seed occurrences,
forward/backward traversal, binding provenance and truncation. Never collapse
separate revisions or repository-scoped resources merely because labels match.
Specify create/update/delete/review transactions, duplicate request semantics,
optimistic concurrency and explicit stale-version conflicts. Human review data
must not mutate imported artifact rows or extraction certainty.

Prepare executable fixtures for missing mappings, cycles, shuffled input, identical
labels in different repositories, revision changes and conflicting edits. Keep
fixture source revisions/goldens and existing thresholds intact. New flow fixtures
belong in existing test projects; use no network/model-generated oracle.

Rubric: determinism 35, identity isolation 25, boundedness 20, concurrency 20.
Gate: `make test-explorer` with the new traversal contract tests present and passing;
record test names/counts and exact inputs. Test-only reference examples may precede
production code, but do not grade unimplemented behavior as passing.

### Architecture — isolated worker ownership

The integration owner owns shared API/DTO decisions, migrations numbering,
`ExplorerApi.cs`, manifests/locks, Makefile, changelog and handoff. Writers each
receive an isolated worktree and exact claims on GitHub before starting.

| Worker | Exclusive initial scope | Deliverable |
|---|---|---|
| Flow | New Explorer flow projection files and dedicated Explorer tests | Deterministic evidence graph and limits |
| Persistence | New Store glossary files, assigned new migration and Store tests | Reviewed data transactions, stale references and concurrency |
| UI/reviewer | New client flow/review components and focused browser checks | Existing canvas integration and evidence/review distinctions |

Maximum four active sessions including the integration owner; stage API/DTO
contracts first, then run independent workers in parallel. No two writers in one
worktree. UI worker initially reviews DTOs while contracts settle. Integration
owner incorporates shared-file changes and reconciles inbox/claims before testing.
Use a reviewer who did not author the relevant change at integration boundaries.

Rubric: ownership 30, existing architecture fit 30, contract consistency 20,
reversible migration/deployment boundary 20. Gate: `make build test-store` with
new store tests covering fresh/existing DB migration, rollback on failure,
concurrent updates and artifact immutability. Record nonzero test counts.

### Refinement — one complete user journey

Implement layers in order: flow available without a database; reviewed terms,
links and mismatch issues when PostgreSQL is configured; then integrated UI for
review/create/update/delete and resolving an issue. Keep deletion semantics and
audit/provenance explicit. A human issue resolution does not change source facts.

Reuse the current source viewer and canvas rather than a new visualization stack.
Show exact selection coverage, edge provenance, candidate/unknown/stale states and
partial/truncated paths. No database means a clear read-only state; a configured
but unavailable database produces an actionable error, not fake successful writes.
Test restart persistence, optimistic conflict responses, invalid/cross-scope
references, script-like labels, cancellation and repeated requests. Demonstrate
`demand-pricing` bound service navigation without claiming an unsupported field
mapping; keep calendar-week/active-week candidates distinct unless a human records
an explicitly scoped assertion. Add a negative example where no value mapping
exists despite services being connected.

Rubric: backend behavior 30, persisted lifecycle 25, usable evidence UI 25,
negative/security checks 20. Gate: `make test-explorer test-store` plus a focused
sandbox browser command added to the plan by the UI worker before its first run.
That command must fail on missing expected UI/state, not merely capture screenshots.
Report its exact invocation, nonzero assertions and source SHA. No broad paid or
hosted browser matrix. Run desktop/ultrawide and one narrow viewport locally.

### Completion — reviewable package and release notes

Refresh stage, integrate all completed worker commits and reconcile claims. Update
HANDOFF and Unreleased notes under MADR-0006; classify compatibility under
MADR-0007. Describe new endpoints/persistence as additions only after validating
existing clients and artifact behavior. Record limitations and the next scope.

Rubric: full gate 40, source-bound functional evidence 30, documentation/compatibility
20, ownership handoff 10. Gate: a clean commit and ordinary `git push` must run the
installed complete local act/security hook, then `make pr-check` immediately before
PR creation/review handoff. If stage advances, integrate and rerun; no hook bypass.
Use `--base stage-1-contract-and-fixtures`. Owner reviews/merges; no auto-merge.
Do not publish a release/tag or claim release readiness from a passing PR gate.

## STANDING RULES

Before each phase score, provide evidence and explain deductions; score /100.
Below 95: fix and regrade, maximum five iterations. A failed/missing mandatory
check blocks progress regardless of score. Report final score, fixes and proof;
never manufacture a score from unrun commands. Reason through design privately,
then expose decisions/evidence rather than a chain-of-thought transcript.
Batch genuinely blocking product decisions once with recommended defaults;
continue independent authorized work. A status request gets a concise status table.
Persist a checkpoint containing source SHA, dirty-state identity, claims, command
results and next action through the existing project mechanism; verify readback.
Do not assume a named checkpoint script exists or install one just for this plan.
Only describe the running feature as verified after checking the actual served
build. All installs/builds/tests go through `scripts/sandbox`/Makefile.

## COST GUARDRAILS

Deterministic projections/validation run without model inference. Use scripts for
mechanical inventories; use the smallest available model that can handle each
bounded worker task. Reserve frontier reasoning for semantic-contract review and
integration decisions. Record actual selected model, effort, scope and result per
assignment; do not claim savings or invent prices. Use native subscription workers,
not additional paid provider execution. If an existing `scripts/route-cheap.mjs`
router is available, inspect it and print its real receipt; otherwise record native
routing explicitly rather than creating a new harness dependency.

External provider budget is zero. Any proposal over $1 or 20 paid calls must state
its estimate and wait for authorization; this plan does not grant it. Report real
metered spend during long runs, or “unavailable; no additional provider calls” when
that is the truth. One integration owner, up to three workers; keep only useful
parallel work active. Reuse caches and existing CI images; run focused tests during
iteration and one complete gate for each final immutable PR head.

## DONE CRITERIA

1. Specification baseline: `make test-contracts test-explorer` plus the named
   requirement/assertion map reviewed by an independent worker.
2. Traversal: `make test-explorer` including new deterministic, boundary and
   truncation assertions, with nonzero test counts recorded.
3. Persistence: `make build test-store` including new upgrade, transaction,
   concurrency, stale-reference and artifact-immutability assertions.
4. Integrated journey: `make test-explorer test-store` and the recorded sandbox
   browser command proving the actual served build and database-disabled behavior.
5. Overall: clean committed source, complete pre-push local CI/security success,
   fresh `make pr-check`, stage-targeted owner-review PR, changelog/compatibility
   notes, implementation evidence and explicit claim release. No release/merge.

This plan is ready for scope review, not evidence that these gates have run or
that the feature exists. Run this now with /brain-build semantics?
