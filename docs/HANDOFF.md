# Handoff: remaining work

## Portable decision navigation and branch lifecycle

[MADR-0028](adr/0028-portable-decision-ledger.md) proposes a bounded portable
typed graph and faceted taxonomy, not a full ontology reasoner. The
[generated index](research/decision-ledger-preview/index.md) covers every local
decision and records reviewed-proposed topic groups and cited navigation
relationships. These are not adopted classifications or inferred dependencies.
Canonical sources remain authoritative; collections may use Markdown or JSON.

`make test-decision-ledger` validates source receipts and checked-in projections
offline, with 29 focused tests. Explicitly refresh changed sidecar receipts and
run `node scripts/decisions/check.mjs generate` to update views; read-only checks
never repair stale output. See the [contract evaluation](research/decision-ledger-architecture.md)
for bounded adapters and unresolved semantic/authority questions.

An authorized second workspace was assessed read-only: the new explicit
single-object JSON scalar profile parsed 75/75 standalone records, and the
array profile parsed 13 archive records. Nested relationship objects and other
metadata remain uninterpreted, not qualified typed edges or adoption evidence.
No private record content was copied into this repository.

[MADR-0014](adr/0014-pr-publication-worktree-retention.md#remote-topic-branches-after-owner-merge)
now distinguishes authorized merged-topic remote deletion from local worktree
retention. All 17 audited candidates remain retained: protection visibility was
incomplete and Tigon32 lacked admin permission to enable automatic deletion.
The setting remains false; an authorized repository administrator must resolve
that gap. No branches or other-workspace content were changed.

## Issue #53: business-object and event assessment research

[Specification](research/domain-object-specification.md), proposed
[MADR-0027](adr/0027-domain-object-event-assessment.md), source-bound gap analysis,
draft schema/rubric and nine synthetic cases define separate object, domain-event
and integration-event profiles. `make test-domain-object-contracts` runs 35
focused contract tests offline as part of sandbox CI. Exposure verdicts stay
independent, unknown is not bronze, and current reviews bind assessed inputs.

This is not a semantic extractor, production grader, exposure-policy enforcement
or measured real-corpus accuracy. ce.v1/runtime/dependencies remain unchanged.
The [gold-set and pilot protocol](research/domain-object-completion.md) requires
authorized frozen sources, domain/context/glossary and policy owners,
independent reviewers, thresholds and budgets before a pilot can start.
MADR-0027 remains proposed; #23, #24, #43 and #54 retain their separate gates.

## Issue #42: language-neutral frontend and discovery research

[Research and acceptance](research/language-neutral-frontends.md), proposed
[MADR-0025](adr/0025-language-neutral-frontends-and-discovery.md), draft schemas
and sealed synthetic inventories define a bounded adapter/discovery direction.
`make test-frontend-contracts` checks the package offline and is part of sandbox
CI. The fixture oracle is not a filesystem scanner or compiler adapter; passing
it does not establish semantic accuracy, arbitrary-layout support, inventory
precision/recall or source-span conversion correctness. ce.v1 is unchanged.

A future pilot requires actual language/unit demand, approved frozen sources,
pinned toolchain/offline dependencies, independent gold inventories and explicit
thresholds, budgets and rollback. No language/parser adoption, schema migration,
analyzed build execution or new service follows from this proposal. #23's corpus
gap and #24's release/security gate remain. Business-object/domain/integration
event maturity and exposure research is separately described above for #53.

## Issue #37: local analysis lifecycle

The host CLI owns the run journal and execution/publication handles; extraction
containers produce attempt-scoped artifacts. Explorer admits and cancels through
the shared journal. See [MADR-0022](adr/0022-local-analysis-lifecycle.md) and the
[lifecycle guide](local-analysis-lifecycle.md) for recovery and qualification.
Host .NET 10 is required even with sandbox builds. macOS host workers cannot
share a coordination domain with a Linux-VM Explorer; that combination refuses
journal access rather than assuming cross-kernel locks. File mode remains valid.
Unknown runs and legacy markers require evidence-preserving reconciliation;
do not delete markers or infer termination from a missing PID. No Kafka,
automatic retries or distributed worker authority is introduced.
Live-child cancellation and nonzero sandbox exit currently remain `unknown`
after cleanup, blocking subsequent work pending offline reconciliation. This
package does not provide a live Stop-and-retry UX.
Issue #37 stays open: A9's literal Explorer credential prohibition is broader
than this slice. Existing private-source checks still use an Explorer GitHub
token, and the package-feed Settings workflow also handles credentials. The new admission profile adds no credential values or engine socket;
moving the existing check boundary requires owner-reviewed follow-up work.

## Issue #33: draft platform exchange contracts

The [platform schema guide](../schema/platform/README.md),
[seven-boundary catalogue](../config/platform/exchange-contracts.json) and
[proposed MADR-0020](adr/0020-platform-exchange-contracts.md) define a contract-first
review slice. Enrollment, evidence-bundle and intake-receipt shapes are tested
with existing JsonSchema.Net; a cheap Node plan checker preserves draft scope.
No central server, authorization, byte/digest verifier, publication state machine,
product API or production deployment is delivered. RFC 8785 content hashing is a
proposed later choice, not implemented. ce.v1 and protected goldens are unchanged.
Podman portability remains separate issue #34; no runtime support follows from
these shape checks. Later provider/consumer slices wait for owner decisions and
target fresh main with normal local gates. This does not complete issue #33.

## Issue #31: tool-free research foundation

The [execution record](plans/issue31-foundation-execution.md) and
[foundation guide](research/code-insights/README.md) describe bounded evidence
validation, sealed synthetic source anchors and independent per-tool councils.
Proposed [MADR-0019](adr/0019-independent-tool-evaluation.md) requires one accepted
tool/mode per introduction PR. No candidate dependency or adapter is introduced
by the foundation. Its passing checks do not complete the broader research study.
All PRs target main and owner merges remain required; dependent introduction PRs
wait for the foundation merge. Private-corpus and image-audit gaps remain #23/#24.

## Issue #28: PostgreSQL foundation in review

Main reconciliation PR #27 merged at `d9421fa046c870df484927c97b786afcbb7d48bb`.
The isolated `feat/postgres-explorer-snapshots` package implements streamed imports,
immutable published snapshots with explicit default revisions, bounded core SQL
queries and an opt-in paged UI. It does **not** complete issue #28 or change the
default file source. See [execution](plans/issue28-foundation-execution.md),
[MADR-0017](adr/0017-postgres-explorer-snapshots.md) and
[RuVector experiment](research/issue28-ruvector-evaluation.md).

No direct main push or owner merge is authorized. Deliver this package against
current main and reconcile after owner review before the next endpoint families.
The declared-contract catalog remains separate. The full issue acceptance gate
fails while architecture/integrations/history/glossary/model/review workflows
remain unmigrated. Do not substitute fixture parity for large-corpus performance.

Post-merge `make EXECUTION=sandbox dev` built main successfully but stopped before
analysis because the corporate proxy blocked Ollama's model download. No complete
shared-analysis receipt exists. Private Glufs and image-audit gaps remain separate
issues #23/#24; this package does not claim they are resolved.

## Main-only integration: current directive

[MADR-0016](adr/0016-main-only-pr-integration.md) supersedes the earlier stage-only policy.
All PRs target main. Owner PR #27 merged stage into main at `d9421fa`; preserve
both histories and require ordinary exact-main local CI and owner review.
Do not merge or enable auto-merge. Earlier stage receipts below are historical.
The post-merge sandbox development attempt and its model-download limitation are
recorded above. On a successful rerun, compare the private analysis receipt
(application/repository SHAs and artifact hashes), not just localhost URLs.
The declared-contract catalog is committed separately on feat/declared-integration-contracts
at 8a8a6b9 and preview port5110; it is excluded from reconciliation and must be ported
to merged main before a later main-targeted PR.


## Security and binding batch (in progress)

`feat/security-corpus-binding` targets stage only. Source-bound audit collection,
security fixes, locked corpus replay and binding preview/export are implemented
locally. [MADR-0011](adr/0011-source-bound-security-validation.md) and
[MADR-0012](adr/0012-revision-bound-binding-preview.md) document the boundaries.
Live binding review passed nine browser assertions on loopback port 5108.
The [execution record](plans/security-corpus-binding-execution.md) records passed
focused checks and failed/incomplete gates. After Docker restoration, full local
act CI and nine binding browser checks passed exact source `09737b0`.
Subsequent commits need their own exact-source receipts. The complete security
audit remains incomplete. The operator clarified that unavailable private inputs
must not block a review PR; MADR-0011 now permits draft publication with scoped
evidence and disclosed findings, while strict security verification remains failed. Container findings
remain tracked in [issue #24](https://github.com/Sonberg/capability-explorer/issues/24);
private Glufs validation remains blocked in [issue #23](https://github.com/Sonberg/capability-explorer/issues/23).
Do not mistake passing dependency scans or synthetic replay for those missing gates.

New proposed policy: [MADR-0013 integration contract standards](adr/0013-integration-contract-standards.md)
records OpenAPI/AsyncAPI version and evidence boundaries; document parsers are not
implemented by this decision. [Code-analysis research](research/code-analysis-options.md)
compares existing tooling and alternatives with a weighted rubric and source log;
candidate tools have not been benchmarked or adopted.

Status: reconciliation PR #11 merged into `stage-1-contract-and-fixtures` at `db18165`.
Historical policy at that time targeted stage; the current main-only directive above supersedes it.
#5/#7/#8 landed on main; #9 landed only on `feat/main-aligned-delivery` after #8.
This package includes both histories and corrected #10 lifecycle documentation.
The merge was verified on 2026-11-01; it does not imply owner validation of every feature.
The operator reports notifying the owner; subsequent validation is unconfirmed.
See [reconciliation and source SHAs](plans/stage-branch-reconciliation.md).
Environment/IaC binding remains open.
Shared terminology: [ubiquitous language](ubiquitous-language.md),
[MADR-0002](adr/0002-ubiquitous-language.md). Issue #6 tracks mapping defects;
those fixes are separate from synthetic test coverage.

PR updates and newcomer workflow: [MADR-0005](adr/0005-pr-lifecycle-and-conflict-prevention.md).
Disk retention: [MADR-0014](adr/0014-pr-publication-worktree-retention.md) makes
verified PR publication the cleanup checkpoint, without waiting for merge.
Preserve private evidence outside the checkout and defer active worktrees until handoff.

Release notes: [changelog](CHANGELOG.md), [MADR-0006](adr/0006-keep-a-changelog.md).
Compatibility/versioning: [MADR-0007](adr/0007-semantic-versioning.md).
Current owner-review package: [cross-repository flow and reviewed glossary links](plans/cross-repository-value-flow.md), issue #4 layers 4 and 3. Implementation and validation are recorded in [the execution receipt](plans/glossary-flow-execution.md); see [MADR-0008](adr/0008-glossary-flow-and-reviewed-assertions.md). BPMN/DMN issue #12 still requires an owner scope decision. LikeC4 PRs #14/#15 are now merged into stage.

## Mapping fidelity workstream

The independent stage-targeted mapping package is recorded in
[the execution plan](plans/mapping-fidelity-execution.md) and
[MADR-0010](adr/0010-deterministic-mapping-fidelity.md). It improves static C#
reference selection, Next app-router endpoints and HTTP identities. It preserves
uncertain bindings and does not implement full MSBuild/Next configuration execution.
The synthetic replay is separate from the locked product fixtures and private-corpus
validation. `make test-mapping-replay` runs harness failure-path tests in ordinary CI;
`scripts/mapping-replay` compares explicitly sealed baseline/candidate bundles.
Owner review remains required; issue #6 is not automatically closed by synthetic proof.
The [project/workflow evaluation](plans/project-metaharness-evaluation.md) separates
measured CI/replay and memory persistence from unproven routing savings and learning uplift.

## LikeC4 spike delivery

PR #14 merged at `87d310d` and PR #15 at `daf02df` on stage (verified 2026-11-01).
The live replacement PR #21 merged into stage at `5ce7f21` on 2026-11-01.
It replaces the production fixture iframe and shares the renderer with glossary
connections. See [MADR-0009](adr/0009-live-likec4-architecture.md), the
[preflight contract](plans/live-likec4-preflight.md) and
[execution handoff](plans/live-likec4-execution.md). Its sampled routing-ready DOM
p95 of 148.2 ms is narrower than the final-paint/physical-display target below;
merge status does not establish owner acceptance of every feature.
The standalone spike retains its
captured five-repository fixture as a regression harness.
See [spike scope](spikes/likec4.md). Strict TypeScript 7.0.2 checks cover the spike;
the extractor compiler API remains unchanged.

[Routing diagnostics and measurements](spikes/likec4-routing-diagnostics.md) record
600 warm drags: release-to-second-frame-opportunity p50/p95 is 130/208 ms at 1920
and 184/269 ms at 5120. These are not exact-paint or physical-display measurements.
Immediate completed-edit capture preserves native undo/redo; exact A* scratch
reuse reduces allocation. The evaluated RuVector WASM adapter was slower and is
not shipped. Opt-in local tracing supports a host-supplied OTel tracer without a
mandatory SDK/collector. [Issue #13](https://github.com/Sonberg/capability-explorer/issues/13)
remains open: the 200 ms p95 target and final-paint validation are not achieved.

## What exists

| Area | Where | State |
|---|---|---|
| ce.v1 contract | `schema/v1/`, [schema/README.md](../schema/README.md) | Stable. JSON Schema, the relation catalogue and the key grammar. |
| Fixtures + golden set | `fixtures/` (5 repos, locked revisions), `fixtures/golden/facts/*.mjs` → `fixtures/expected/` | Hand-written expected facts for every node, edge and calculation, anchored to text in the source. `make golden` regenerates them. |
| Validators | `platform/src/CapabilityExplorer.Contracts`, `extractors/ts/src/contracts` | Both stacks validate the same contract. |
| Scorer | `platform/src/CapabilityExplorer.Evaluation`, `ce evaluate` | Measures recall, accuracy, evidence, precision on exhaustive categories, calculations, attributes, irHash and coverage. Can be limited with `--repository` and `--language`. |
| C# extractor | `platform/src/CapabilityExplorer.Extractors.CSharp` (`ce-cs`) | 100% on graph metrics and on calculations (`Calculations*.cs`, gated by `fixtures/evaluation.csharp.json`). |
| TS/JS extractor | `extractors/ts/src/extract` (`ce-ts extract`) | 100% on graph metrics and on calculations (`calculations.ts`; gated in `fixtures/evaluation.extractors.ts.json`). |
| SQL extractor | `extractors/ts/src/sql` (`ce-ts extract-sql`) | 100% on graph metrics for all 5 SQL golden artifacts (`make evaluate-sql`). SQL calculations/attributes/IR hashes pass 5/5, 52/52 and 5/5; see the build evidence. |
| Analyse pipeline | `scripts/analyze`, `make analyze CAPABILITY=<id>` | Three sandboxed steps: export → packages → offline extract (C#, TS/JS, SQL), written to `.sandbox-cache/artifacts`. |
| Explorer | `platform/src/CapabilityExplorer.Explorer` (+ React client) | Live reload. Capabilities, architecture canvas, optional LLM layer. |
| Store | `platform/src/CapabilityExplorer.Store` | Migrations, an idempotent importer and binding snapshots, tested with Testcontainers. Fed by `make analyze` when `CE_DATABASE` is set. |
| Binding | `platform/src/CapabilityExplorer.Binding`, `bindings.json` | Service-map binding of `@unbound` hints into `human` edges; shown in the explorer as bound/broken links. |

Ground rules that apply to all work:

- Trusted project restore, build, tests, Explorer and Vite run on the host through `make`.
- Capability source acquisition and extraction use `scripts/sandbox`; extraction is `--offline` and never builds,
  restores or executes the analysed code.
- Evaluation thresholds are only ever raised.

## Local checks before pushing

Install the repository hook with `make install-git-hooks`; run `make local-check`
for the full project gate. Fetch/integrate `origin/stage-1-contract-and-fixtures` first; local-check enforces
that alignment and rejects changes to stage-1-contract-and-fixtures during CI. Run `make pr-check` immediately
before PR creation, base changes or review handoff; if stage-1-contract-and-fixtures advanced, integrate and rerun. See [MADR-0003](adr/0003-local-ci-before-push.md).
The local hook is installed in this clone; other contributors must install it.
The tracked `out/evaluation.json` is a historical missing-actual report, not current
acceptance evidence. Current reports are `.sandbox-cache/extracted/{cs,ts,sql}/evaluation.json`.

## Remaining work (independent workstreams)

Each workstream owns its own directories so they can run in parallel. The shared files are
`fixtures/evaluation.extractors.json` (gates), `Makefile`, `scripts/analyze` and `README.md`. Keep edits
to these minimal and rebase before merging.

### 1. C# calculation extraction — DONE

Implemented in `Calculations.cs`, `CalculationAttributes.cs`, `CalculationIr.cs`; `make evaluate-cs` gates the three calculation metrics at 1.0 via `fixtures/evaluation.csharp.json`. Fold the thresholds into `evaluation.extractors.json` once workstream 2 lands. The original brief follows.

- **Goal:** emit `calculation` records (IR `expr`, `irHash`, `inputs`, `output`, `attributes` with
  evidence) for the methods the golden set marks as calculations. Start with `fixtures/golden/facts/{api-cs,worker-cs,forecast-cs}.mjs`
  (`a.calc(...)`). They cover 11 calculations and 112 attributes.
- **IR:** the grammar is in `schema/v1/record.schema.json` (`$defs/expr`). `irHash` is the sha256 of the canonical
  JSON (sorted keys, see `canonicalJson` in `fixtures/golden/lib.mjs`).
- **Scorer:** see `CompareAttribute` in `Evaluator.cs`. `unknown` in the golden set must stay `unknown`, because a
  guessed value is `AttributeFabricated`. Planted special cases are in `ServiceFixtureScenarioTests.cs`: zero
  denominators, overflow, rounding, and the constant change between v1 and v2 that must change `irHash` but not
  the attributes.
- **Where:** `platform/src/CapabilityExplorer.Extractors.CSharp` (new `Calculations*.cs`). `ArtifactBuilder.ToJsonl`
  currently hard-codes `calculation: 0`.
- **Done when:** `make evaluate-cs` passes with `calculationRecall`, `attributeAccuracy` and `irHashMatch`
  gated at 1.0 for csharp. Add these thresholds to a csharp-specific config, or to
  `evaluation.extractors.json` once TS is done too.

### 2. TypeScript/JavaScript calculation extraction — DONE

- **Goal:** the same as workstream 1 for `web-ts` (`averagePerActiveWeek`) and `pricing-node`
  (`priceForDemand`, `weeklyRevenueSek`). Watch for:
  - JSDoc-typed JS
  - `Math.round` as `halfTowardPositiveInfinity`
  - `toFixed`
  - `Math.trunc`
  - the moved function in `v2-move`, which keeps the same `irHash` under the new key.
- **Where:** `extractors/ts/src/extract/` (new `calculations.ts`). The shared `builder.ts` already emits calculation records and computes their hashes.
- **Done when:** `make evaluate-ts` passes with the calculation metrics gated at 1.0.

### 3. SQL extractor: calculation integration

- **Done:** `ce-ts extract-sql` (`extractors/ts/src/sql/`: `lexer`, `ddl`, `refs`, `project`, `extractor`) is a
  dependency-free hand parser for T-SQL and PostgreSQL. It runs offline, needs no database and executes nothing.
  `make evaluate-sql` passes the graph thresholds at 1.0 for all 5 golden artifacts, and `scripts/analyze` step 3
  runs it. Unit tests: `extractors/ts/test/sql.test.ts`.
- **Fixture gap:** the `api-cs` fixture has no `.sqlproj` (the golden unit `SalesDb` is just `database/`), and
  `pricing-db` has no project file either. With a `.sqlproj` the extractor takes the name from it; without
  one it infers the name (connection-string database for T-SQL, `<package>-db` for PostgreSQL) and says so in an
  info diagnostic. Adding a `.sqlproj` to the fixture would be cleaner but changes every locked revision.
- **Current package:** bounded expression parsing, typed IR and conservative attributes for routine
  calculations, emitted through the existing `ArtifactBuilder.calculation` API. SQL calculation
  recall, attribute accuracy and IR-hash thresholds are 1.0; all graph thresholds are preserved.
  The PostgreSQL golden currency/output unit was explicitly corrected to unknown because names
  and scaling do not establish monetary semantics. The subsequent security scan found
  CVE-2026-109461; the operator approved upgrading the pricing fixture to Azure Identity
  4.14.1 (locked MSAL 7.1.2), regenerating both pricing revisions and four goldens.
  Independent comparison verified unchanged semantic facts.
  Unsupported calculations retain graph facts and emit `SQL_CALCULATION_UNSUPPORTED`; dynamic
  SQL remains partial. See [the execution contract](plans/deterministic-sql-calculations.md).
- **Known gaps:** no `CREATE TRIGGER`, `SYNONYM` or cross-database references (they are `unresolved`); DML in
  `INSERT … ON CONFLICT` and `MERGE` is covered by simple rules only.

### 4. Deterministic package resolution + private feeds (C#) — DONE

- **Problem:**
  - `ce-cs fetch-packages` and `PackageResolver` choose versions from whatever the shared
    `.sandbox-cache/analyze-packages` folder holds. Re-analysing the same commit can give different edges
    into package code (api-cs gave 82 and then 67 edges).
  - The importer rejects different bytes under the same analysis key.
- **Do:**
  - Resolve one version set per project, written as a lock file next to the export.
  - Use only those exact versions when extracting.
  - Include the resolved set in `header.configHash`.
  - Optional: support private feeds from `nuget.config` with credentials passed only to step 2.
- **Where:** `PackageFetcher.cs`, `PackageResolver.cs`, `scripts/analyze`.
- **Done when:** running analyze twice on different cache states gives byte-identical artifacts. Add a test.

### 5. Persist analyses + bind unbound resources (stage 3): done for the service map

- `make analyze` imports into PostgreSQL when `CE_DATABASE` is set; `BINDINGS=` also saves a binding snapshot.
- Remaining: environment/IaC files as a second binding source (Kubernetes/compose/appsettings), a UI to edit the map,
  and reading analyses from the database in the explorer (it still reads artifact files).

## Glossary: issue #4 candidates, connections and reviewed assertions

Read-only deterministic candidates and the Glossary tab are implemented in the
stacked glossary workstream. Names group only by explicit normalization; calendar
and active qualifiers remain distinct. Source occurrences retain revision/unit/record
identity, unknowns remain unknown, and comparison completeness is explicit.
Glossary concepts (follow-up): `GET /api/glossary/concepts` ranks declarations into concepts
with an explainable score and a Concepts/Candidates toggle in the tab. See
[MADR-0005](adr/0005-glossary-concepts.md) for semantics, measured precision/recall and known
limits (about 60% precision on a fresh blind sample; TypeScript members are not in ce.v1).
Weights live in `glossary-weights.json`; the fixture gold set is a regression test.

See [MADR-0004](adr/0004-glossary-candidates.md) and
[candidate build evidence](plans/glossary-build-evidence.md).

The current package adds bounded exact-occurrence source/binding navigation and
separately attributed human term assertions. PostgreSQL migration 0004 persists
terms, links and mismatch issues with version conflicts and transactional audit
writes. Links retain the exact observed commit, unit, occurrence and artifact hash;
changed inputs require explicit re-review. The Glossary tab uses the existing
Architecture canvas and source viewer. Without `CE_DATABASE`, source navigation
works and review is read-only; a configured but unavailable database is an error.

Review terms are global. Capability/revision selection constrains source references
and their state; it is not an access-control boundary. Term deletion retires its
links/issues across repositories. The trusted-local write guard is browser request
protection, not authentication. Audit history is append-only through the application;
database administrators can still alter storage. No release version is assigned.

Optional `glossary/v1` proposals now use the configured interpretation model only
after an explicit request. Exact source inputs, model and prompt/schema identify
the cache; invented aliases, occurrences and evidence are rejected. Proposals
remain `llm`/`unreviewed` until a human explicitly accepts selected occurrence
roles or rejects the proposal. Migration 0006 persists that decision atomically
with accepted terms/links and existing audit writes; relationships and ambiguities
remain proposals, not source facts. Reviewer names are self-reported.

Recorded-model tests cover grounding and source/scope invalidation; PostgreSQL
HTTP tests cover acceptance, rejection, restart and conflicting/idempotent review.
Browser checks exercise both fixture capabilities, exact source evidence,
recorded proposal rendering, capability-change reset and database-disabled review.
No live model or paid inference is needed. Browser database-backed review is not
part of this check; persistence is covered by the HTTP integration tests.

Field-level value propagation, runtime traffic and inferred equivalence remain
unimplemented. Synonym embeddings and work-item integration are deferred.
Calls, bindings and shared term hubs must not be
reported as proof of value equality. #6 extractor gaps, live LikeC4 integration,
#13 settling performance and owner-controlled #12 BPMN/DMN scope remain separate.
The broad #4 acceptance is not closed by the optional proposal layer alone.
See [execution evidence and commands](plans/glossary-flow-execution.md).

## Smaller follow-ups

- C# synthetic package and calculation tests now cover locked packages, explicit division IR, unsupported-loop graph retention and generated-source exclusion. Broader synthetic graph/route coverage remains open; issue #6 fixes are separate.
- `scripts/extract-fixtures.sh` re-parses the lock file with sed. A `ce` subcommand would be sturdier.
- The explorer polls every 2 s. Fine locally, but worth making configurable.
- The TypeScript 7 native compiler API: evaluate before upgrading beyond `typescript@5.9`.


## C# transitive project visibility (issue #6, defect 1)

The transitive-reference workstream propagates cached compilation references
through acyclic project graphs and deduplicates by compilation identity. Direct
DEPENDS_ON edges still describe only declared references. Synthetic chain,
diamond/repeat-byte, missing-project and unrelated-project controls pass; the
C# golden gate remains unchanged. See [execution evidence](plans/transitive-project-references.md).
This does not fix framework selection, package version conflicts, older framework
fallback, conditional MSBuild references, or other issue #6 mapping gaps.

## Issue #34: first Podman core slice

The separate `feat/podman-core` PR introduces explicit engine selection, sanitized
doctor output, rootless direct sandbox execution and the pinned lockfile scanner.
See [MADR-0021](adr/0021-explicit-container-engine-core.md),
[guide](podman-core.md) and [acceptance plan](plans/podman-core-execution.md).
Docker remains the full local/hosted gate. Explicit Podman requests for nested
act/Testcontainers, Make pipelines, dev, analysis and full audit fail early.
Those are follow-on slices after owner review, not completed support. Preserve
the independent issue #33 contract draft and PR #32 research foundation.
