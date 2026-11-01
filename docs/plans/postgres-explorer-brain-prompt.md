GOAL

Implement <issue>https://github.com/Sonberg/capability-explorer/issues/28</issue>: serve analyzed facts through bounded PostgreSQL queries and immutable published snapshots, preserving deterministic evidence and existing behavior; independently evaluate PostgreSQL + RuVector against PostgreSQL + pgvector before any extension adoption.

ASSUMPTIONS

- <repository>Sonberg/capability-explorer</repository>; authenticated account Tigon32; main is the only PR base. Planning baseline is merged main d9421fa046c870df484927c97b786afcbb7d48bb (PR #27). Fetch main again when execution begins.
- This is an execution brief, not evidence that implementation, benchmarks or an architecture decision have passed. Owner issue #28 and the independent review remain inputs; proposed changes must be explicit in a proposed MADR, not silently treated as accepted policy.
- PostgreSQL remains canonical for immutable facts. JSONL remains the credential-free sandbox transport, contract fixture and optional recovery archive. Retain existing binding/glossary/review/interpretation semantics.
- PostgreSQL + pgvector is the baseline. PostgreSQL + RuVector is an explicit challenger in disposable databases. Standalone RuVector replacement is deferred unless the experiment establishes a concrete need and equivalent integrity/recovery evidence; do not build another production store adapter now.
- RuVector investigation may proceed independently of the core migration. An unavailable, incompatible or unhelpful extension produces a documented result, not a blocker for PostgreSQL delivery.
- No new paid provider calls, generated embeddings or external inference are required. Reuse fixed synthetic/precomputed embeddings where vector evaluation is relevant.
- Ordinary CI remains small; larger performance and extension experiments are opt-in. Private Glufs access is not a prerequisite for synthetic correctness tests or PR publication; disclose that acceptance gap under #23.
- The separate declared-contract catalog branch is not part of this package. Coordinate shared Explorer routes with that workstream through GitHub claims.

STACK

- Existing .NET/Npgsql Store, normalized PostgreSQL migrations, binary COPY importer, TypeScript/React client and shared LikeC4 renderer. Inspect actual merged source and docs/HANDOFF.md before editing; reuse existing boundaries rather than introducing an ORM, message broker or generic multi-database framework.
- SPARC phases: Specification, Pseudocode, Architecture, Refinement, Completion. Grounded Brain source: sparc/CARD/sparc-card (methodology summary, not an installed execution service).
- RuVector PostgreSQL extension candidate: grounded Brain sources ruvector/crates/ruvector-postgres/docs/GRAPH_IMPLEMENTATION.md and ruvector/docs/postgres/zero-copy/zero-copy-operators.md. These establish documented interfaces, not production fitness. Resolve and pin the actual implementation commit/package/image, inspect its matching code and tests, and verify compatibility before running it. Official entry: https://github.com/ruvnet/ruvector/tree/main/crates/ruvector-postgres . The inspected graph storage module uses DashMap; investigate its actual durability and transaction boundaries rather than generalizing this finding to all RuVector modules.
- Use the existing Ruflo coordination ledger and repository harness under applicable AGENTS.md; native Codex subagents execute assigned tasks. Registration does not prove tool health. Never describe coordination records as executed work or invent model-routing/savings receipts.
- Existing sandbox build/test profile, pinned runtime images, local act gate, dependency/security checks and owner-reviewed main PR workflow. Reuse current OTel conventions; add only measurements needed for decisions.

PHASES

1. SPECIFICATION — define the workload, API and decision contract before query migration.

Read issue #28 and its review, current ADRs, HANDOFF, Store schema/importer, Explorer queries and client call sites. Inventory every collection/detail/graph endpoint, source authorization path and current rank/filter behavior. Define bounded DTOs before implementing adapters: default search 50/max100; edge preview25/direction; explicit per-route node/edge/byte limits, truncation and pending states. Define stable rank tiers, unique tie-breakers, casing/collation, null handling and cancellation semantics.

Create proposed MADRs for (a) canonical PostgreSQL facts, publication and bounded reads and (b) RuVector evaluation/adoption boundaries. Select unused ADR numbers at execution time. Record compatibility impact under existing SemVer/Keep a Changelog policies; keep project ubiquitous language distinct from analyzed-repository glossary concepts.

Define deterministic seeded workloads: realistic artifacts, multiple versions of the same unit, repeated names, punctuation/short identifiers, high-degree/skewed graphs, cross-repository edges, invalid and truncated imports. Preserve protected fixtures/revisions. Generate bulk data in ignored storage. Scale through small fixtures,100K,1M and an opt-in10M-node tier only when resources allow. Specify node/edge/revision distributions, hardware, image digests, app/source SHA, concurrency, warm/cold conditions and sample counts.

Treat issue budgets (250MB app memory at10M; p95 search200ms/graph500ms) as provisional product targets. Measure total app+DB memory, OS-cache accounting, cold start, import peaks, disk/index/WAL bytes, query plans, response bytes and CI time. Do not claim stable latency independent of all corpus size or O(page-size) query work.

Rubric /100: endpoint/compatibility inventory30; snapshot/cursor/publication contract30; reproducible workload and resource budgets25; ownership, exclusions and security boundaries15.
GATE: proposed `make EXECUTION=sandbox issue28-gate PHASE=specification` must validate a machine-readable endpoint/budget/workload manifest and reject missing limits, sort tie-breakers, snapshot fields or unspecified provisional budgets. Implement this validation entry point before marking the phase passed; it does not exist at planning time.

2. PSEUDOCODE — prove state transitions and failure behavior.

Specify candidate -> validated -> required-bindings-ready -> published; optional derived models independently pending/ready/failed. Immutable snapshot identity includes exact winning artifact IDs, revision selection, binding/config versions and relevant algorithm versions. Publish using a short transaction and an expected-generation check so an older concurrent job cannot overwrite newer publication. Readers resolve the published pointer once and use its immutable ID throughout requests.

Specify a streaming JSONL state machine with incremental hash, transaction-local staging, duplicate/reference/count/terminal-complete validation and bounded record sizes. Reuse existing binary COPY. Preserve analysis-key/content conflict detection and identical retry idempotency. Distinguish a valid partial artifact from an incomplete transport. No DB credentials or Docker socket enter untrusted analysis.

Cursors bind immutable snapshot, capability/revision selection, filters, query/order version and unique last-key tuple. Validate integrity, length/expiry and authorization on every request; signed cursors do not grant access. Pages may continue on a retained immutable snapshot after publication. Expired/unavailable snapshots yield a documented restart response; align cursor lifetime with retention. Avoid long-held database transactions across user pagination.

Describe database restart, reader/import cancellation, worker crash, duplicate/conflicting import, competing publishers, stale cursor, optional derivation failure and notification loss. Poll durable generation initially; notifications remain optional hints.

Rubric /100: atomicity/idempotency35; request/page determinism25; failure/recovery cases25; bounded memory/security15.
GATE: `make EXECUTION=sandbox issue28-gate PHASE=pseudocode` runs executable state-machine/contract tests including competing publishers and publication during pagination; invalid imports must leave the published snapshot unchanged.

3. ARCHITECTURE — choose boundaries and design the RuVector experiment.

Introduce an async/cancellable IExplorerQueries boundary with small projections, explicit snapshot context and timeouts. PostgreSQL endpoints must not construct ExplorerIndex/ViewIndex at startup or refresh. Keep file implementation temporarily as a fixture-sized comparison oracle; never run whole-corpus compare in production requests. SQL values are parameterized; source access remains authorized host-side.

Assign one integration owner for migrations/manifests/locks, endpoint contracts, imports and publish integration. Coordinate isolated worktrees and exact path claims through GitHub before writing. A minimal queen-led topology uses root integrator plus Store/query worker, API/UI worker and independent experiment/reviewer, within actual session capacity. Read-only review may share source; writers never share a worktree. Do not pretend configured eight slots override a smaller live limit. Scale only where work is independent.

RuVector protocol:
- Inspect and pin a candidate implementation, extension install/upgrade SQL, PostgreSQL-major/CPU compatibility, licenses, security inventory, rollback and .NET/Npgsql parameter/result support. Use isolated disposable databases and names/volumes. Verify pgvector coexistence/type/operator conflicts; if incompatible, compare separate instances. No existing developer database conversion.
- Baseline and candidate use identical hardware/resource limits, corpus/snapshot, query set and settings, with repeated warm/cold runs and reported variability. Record source/image/config hashes, counts, plans, elapsed time and memory/disk. Separate installation cost from steady-state results.
- Test deterministic exact lookup, filtering, bounded adjacency/traversal, cancellation and publication semantics. Test rollback, clean restart and abrupt termination/recovery; establish which graph/vector data persists and which index/projection must rebuild. Measure rebuild time and peak memory.
- For vector tests use identical fixed embeddings, distance metric and ground truth; report recall@k, latency and index size at comparable quality. Approximate retrieval parity is not exact fact-query parity. Disable adaptive learning for repeatability; evaluate learning separately only with fixed training history and an explicit versioned state.
- Promotion needs all correctness/recovery/security gates, compatible packaging and a materially useful measured benefit. Propose a20% p95 improvement on a predeclared relevant workload, larger than run variance, with <=10% total-memory/disk regression unless the owner accepts a documented tradeoff. These thresholds are proposed decision criteria, not measured results or silently approved policy. If evidence is inconclusive, retain pgvector and record why.

Rubric /100: source/query boundaries25; experiment fairness/recovery35; compatibility/cost/security25; isolated ownership15.
GATE: `make EXECUTION=sandbox issue28-gate PHASE=architecture` checks manifest/adapter contracts and runs the small baseline integration suite. Candidate install failure must be recorded explicitly as not evaluated; it cannot produce a passing candidate report. Owner approval is required for extension adoption; core implementation can continue independently.

4. REFINEMENT — implement reviewable vertical slices.

Deliver core work in dependency order, with owner-merged main checkpoints:
A. Foundation: approved contracts/MADR direction, seeded baseline tooling, streaming validated imports, immutable snapshots/publication and bounded metadata/search/symbol APIs plus matching React pagination. This is the first substantial end-to-end package; do not call the whole issue complete here.
B. Remaining bounded runtime reads: calculations/comparison, integrations/LikeC4 neighborhoods, diagnostics/history/changes and glossary. Preserve reviewed assertions, cross-repository selection, provenance, source guards and live UI behavior. Required bindings are snapshot-scoped; optional derived summaries remain rebuildable/versioned.
C. Cutover: PostgreSQL runtime default, prove startup/refresh works without JSONL artifact-body access, remove whole-corpus caches/polling, add bounded retention/recovery and finish documentation.
R. Independent optional RuVector experiment report and proposed decision; adoption is a later owner-approved change. Its outcome must not block A–C.

Use query-plan-driven indexes rather than speculative indexes on every column. Reserve interactive connection capacity; bounded background batches/checkpoints must not starve reads. Report partial/truncated/pending states explicitly. Cancel obsolete UI requests and retain the visible page during navigation.

Parity tests compare canonical DTOs on fixed fixtures including search rank tiers and graph evidence. Integration tests cover streamed large artifacts, invalid references/counts/terminal records, retries/conflicts, concurrent read/import/publication, restart and cursor retention. Browser tests cover actual served pages and network limits, not only mocked adapters. For DB-only proof use a test runtime where artifact transport directories are unavailable, with only committed test inputs pre-imported.

Rubric /100: correctness/recovery35; bounded API/UI and deterministic parity30; source-bound performance20; security/compatibility15.
GATE: `make EXECUTION=sandbox issue28-gate PHASE=refinement` runs focused Store/Explorer/browser tests and the small benchmark tier; existing `make EXECUTION=sandbox test-store test-explorer golden-check` must pass. An opt-in benchmark runs larger tiers outside ordinary CI and emits explicit pass/fail/not-run states.

5. COMPLETION — verify the exact deliverable and publish for review.

Fetch and integrate current main before final local CI. Commit each coherent package, then run ordinary `make EXECUTION=sandbox local-check` and `make EXECUTION=sandbox pr-check` for the clean exact head/base; if main or source changes, renew evidence. Create PRs explicitly against main, describe scope/limitations, attach redacted benchmark/recovery receipts and notify issue #28. Owner merges; never auto-merge or push directly to main. Do not batch unrelated catalog changes into this package.

Update HANDOFF, changelog, MADRs and issue acceptance checklist. Report full issue completion only after all endpoint families use bounded DB reads, DB-only startup/refresh and recovery pass, and the actual running build/browser is verified. RuVector report status is independent of adoption. Clean up inactive worktrees under MADR-0014 after preserving recoverable evidence; retain active previews until handoff.

Rubric /100: full issue acceptance and running-build proof40; exact-source local/hosted evidence30; owner handoff, docs and recovery20; truthful limitations/cleanup10.
GATE: `make EXECUTION=sandbox issue28-gate PHASE=completion`, exact-head local/pr checks and actual hosted CI must pass for the declared scope. A first-slice PR cannot pass the full-issue gate by marking remaining endpoints skipped.

STANDING RULES

- Before scoring, cite evidence and justify every deduction, then give the score /100. Below95: fix and regrade, at most five iterations; unresolved criteria remain failed/not-run. Grades never replace executable gates or owner authority.
- Plan changes that materially alter owner scope, cursor behavior or adoption policy remain explicit proposals until accepted. Batch indispensable questions once with recommended defaults; otherwise execute reversible authorized work without repeated confirmations.
- Maintain source-bound resumable checkpoints in the existing canonical project memory store, using approved tools and exact-key readback. Read current checkpoint first, reconcile against GitHub/source, write evidence and next action last. Do not add a competing memory database or assume a checkpoint script exists.
- Keep deterministic facts, reviewed assertions and inferred/approximate outputs separate. Never regenerate protected fixtures or weaken security gates to conceal failures.
- READY requires actual running-version verification. Status requests return a compact table of scope, source, completed evidence, pending work and blockers. Updates must disclose unavailable tests and inputs.

COST GUARDRAILS

- Use deterministic scripts for mechanical work; use the smallest capable available subscription-backed model for bounded tasks, and stronger review for architecture, recovery and final integration. Use a real installed router only after verifying its interface; print actual routing receipts if available. No invented route-cheap script, savings or dollar totals.
- No external paid model execution is authorized. If a proposed operation exceeds $1 or20 paid calls, disclose the estimate and wait for explicit spending approval. Report paid spend as zero only when verified; otherwise unknown. Native subscription usage is not a measured dollar saving.
- Reuse pinned cached images; ordinary CI needs existing SDK/Node/PostgreSQL images only. Browser, RuVector and large benchmarks are targeted/opt-in, with no cloud services or model downloads added to the default gate.
- Limit the RuVector spike initially to one pinned candidate, two local hours and one rerun for an actionable packaging failure. Record incomplete evidence when the budget expires; do not start a multi-engine rewrite. Monitor disk before each larger tier and stop before exhausting the configured safety reserve. Preserve logs and remove disposable experiment data safely.
- CI checks the small seeded corpus and regressions every PR. Larger tiers run once for a changed query/schema/candidate, not repeatedly after unchanged passing evidence. Never reuse receipts across changed source/config/image inputs.

DONE CRITERIA

The following issue28 targets are PROPOSED implementation deliverables, not existing commands or completed checks. Implement each real failing validator/test before claiming the corresponding gate:

    make EXECUTION=sandbox issue28-gate PHASE=specification
    make EXECUTION=sandbox issue28-gate PHASE=pseudocode
    make EXECUTION=sandbox issue28-gate PHASE=architecture
    make EXECUTION=sandbox issue28-gate PHASE=refinement
    make EXECUTION=sandbox issue28-gate PHASE=completion

Overall acceptance:

    make EXECUTION=sandbox issue28-acceptance

That final target must execute the full endpoint inventory, DB-only runtime proof, snapshot/import/cursor recovery, declared benchmark tier and actual served-UI checks. It must fail on missing mandatory evidence; optional RuVector evidence is reported separately and must fail its own adoption gate if unavailable. Exact-head ordinary local CI/pr checks and hosted CI remain mandatory before owner review. No benchmark or implementation result is claimed by this prompt.
