# Issue #28 PostgreSQL foundation: execution and review boundary

This package starts from owner-merged main `d9421fa` and implements the first
substantial vertical slice of the [brain brief](postgres-explorer-brain-prompt.md).
It does not close issue #28. Main remains the only PR base and the owner merges.

## Implemented behavior

* The CLI imports UTF-8 JSONL with incremental byte hashing, an 8 MiB record cap,
  shared contract validation, transaction-local staging and existing binary COPY.
  Invalid/truncated records, references, counts, duplicate identities, cancellation
  and final-table errors roll back the artifact. Imported facts can remain after
  another artifact fails; the published selection does not advance.
* `ce publish` imports a selected set, persists service-map binding results without
  loading the graph, creates an immutable artifact/default-revision selection, then
  advances the scope pointer using expected-generation CAS. Identical retries are
  idempotent. Multi-revision repositories require explicit defaults; hash order
  and import time never stand in for chronological source evidence.
* Binding edge identity includes artifact occurrence in the database key. The same
  local derived ID at two revisions no longer silently drops one provenance row.
  Imported binding identities are versioned and analysis identities include their
  actual binding snapshot. Upgrades therefore create complete new snapshots instead
  of reusing an incomplete legacy binding; retained historical snapshots stay unchanged.
* PostgreSQL mode registers no ExplorerState or whole-corpus file index. Core reads
  use SQL projections and stable keyset pages: metadata, revisions, artifacts,
  search, symbol, evidence, adjacency and calculation metadata/body.
* SQL suppresses projected rows above 8 KiB before transfer to .NET; oversized rows
  fail explicitly. Serialized HTTP output is capped at 1 MiB before success headers
  or partial JSON. Pages contain at most 100 rows; the UI uses 50 and retains one
  page plus at most 20 back tokens. Large source bodies and calculation bodies are
  fetched separately with their own budgets.
* Case matching and ordering use versioned .NET invariant folds and UTF-8 scalar
  sort bytes in a separate rebuildable table. PostgreSQL locale casing was proven
  incorrect for Greek/Turkish/Kelvin/sharp-S cases; even the initial UTF-16 sort
  hypothesis failed the supplementary-character oracle and was corrected before
  delivery. Per-artifact completion markers keep request readiness metadata-only.
  Equal-name results use an explicit unique symbol-key order under PostgreSQL `C`
  collation. File search had no explicit key tie-breaker; filesystem insertion order
  is deliberately not a pagination contract.
* Requests/pages pin an immutable snapshot, selection, filters and query version.
  Signed cursors expire after one hour and after a server key change. Retained
  snapshots remain usable after publication; current capability definitions are
  checked on every request. Query commands have 5-second limits and the request
  query/source work has a 10-second cancellation budget.
* The UI preserves the existing theme/full width, cancels obsolete requests,
  exposes explicit pagination and permits loading a newer snapshot deliberately.
  Failed navigation preserves the successful page, counter and history, disables
  advancement and offers an explicit retry of the same failed cursor.
  Unsupported database views return visible `501 pending`, never a file fallback.
  File mode retains existing behavior and remains the default.

## Try the bounded mode

Use a dedicated development database; do not replace a shared or production
database. Set `CE_DATABASE` through the existing approved local environment.
The following commands use the project's sandbox build and trusted import path:

```sh
make EXECUTION=sandbox build fixtures
SANDBOX_PASS_ENV=CE_DATABASE scripts/sandbox dotnet \
  dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll migrate
find fixtures/expected -path '*/v1-base/*.jsonl' -print0 | \
  xargs -0 env SANDBOX_PASS_ENV=CE_DATABASE scripts/sandbox dotnet \
    dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll \
    publish --scope '*' --bindings bindings.json
make EXECUTION=sandbox explore EXPLORER_SOURCE=postgres PORT=5116
```

For multiple revisions add `--view repo:full-sha,repo:full-sha` to declare defaults.
`--expected-generation N` prevents publishing over another writer; the CLI otherwise
reads the generation before importing and still detects competing publication.
Each capability has its own pointer (`--scope capability-id`); `*` is the workspace.
Publishing a capability does not silently republish the workspace or other scopes.

`scripts/analyze` publishes the successfully analyzed capability when storage is
configured. Sandbox recipes keep the trusted storage step sandboxed too. The
untrusted export/package/extraction stages receive no database credentials.
Embedding generation remains optional and is not needed for this package.

## Evidence and executable gates

The specification manifest is [config/issue28/contract.json](../../config/issue28/contract.json).
The initial specification gate is 95/100: the bounded contract, failure cases,
scope inventory and proposed decisions are explicit and its nine checks pass;
five points remain for provisional budgets and owner architecture review. This
score is not an implementation or performance score.

Focused PostgreSQL tests cover all protected golden artifacts, normalized-row
parity with the original importer, invalid streams, late transaction failure,
CAS publication, retained pages, scope/filter tampering, expiry, source authorization,
oversized valid fields and independent response serialization limits. CLI subprocess
tests cover publication, bindings, defaults, stale generations and interruption
while an actual database insert is blocked. Gzip/Brotli and live scope revocation
are exercised through the real HTTP application.

```sh
make EXECUTION=sandbox issue28-gate PHASE=specification
make EXECUTION=sandbox issue28-gate PHASE=pseudocode
make EXECUTION=sandbox issue28-gate PHASE=architecture
make EXECUTION=sandbox issue28-foundation-check
```

Docker-backed gates run in the Linux runner (including local act), as with the
existing Store suite. Direct macOS test execution can use the same pinned act
runner wrapper; do not grant the analysis sandbox a Docker socket. Ordinary CI
adds only 16 small Node contract/tool tests; the PostgreSQL tests reuse its existing
Store test container. No browser, RuVector or model image is added to default CI.

Actual browser proof uses `scripts/test-issue28-browser.mjs` against the served
seeded database mode, with 1920/5120 viewports, native request cancellation cleanup,
pagination, filters and detail/edge/evidence pages. It records actual served asset
hashes and screenshots privately; no API responses or model calls are fabricated.
Final exact-candidate local/hosted CI and browser receipts are recorded in the PR
handoff, not inferred from earlier worktree checks.

`scripts/test-issue28-pager-failure.mjs` separately injects one clearly labelled
HTTP 503 to exercise navigation recovery. Its successful initial/retry pages use
the actual application/database; it is not substituted for the uninjected browser
journey or performance measurements.

The seeded generator and [benchmark protocol](issue28-benchmark-protocol.md) support
1K/100K and explicitly opt-in larger tiers. Generated facts are synthetic and have
no real Git source contents. Their HTTP timing alone proves neither source browsing
nor a 10M-node memory target. Query plans, cold cache, database/OS memory and import
peaks must be named explicitly when not measured.

## RuVector result

The [source-bound experiment](../research/issue28-ruvector-evaluation.md) found
persistence and restart checks passing for the tested image, but pgvector type/SQL
compatibility and same-backend graph rollback failed. Current source does persist
graphs; an in-memory cache is not proof that all graph data is ephemeral. Source
and installed image versions were not falsely equated. No performance win, CVE
clearance, Npgsql parity or production adoption is claimed.

## What remains unverified or unimplemented

* Full issue #28: architecture/integration read models, changes/history, glossary
  and review/binding-preview workflows, model/chat/similarity parity, production
  compare mode, PostgreSQL default cutover and retention/recovery jobs.
* Exhaustive testing of every Unicode string is not claimed. Representative case,
  supplementary-character and mixed-order tests compare the actual pinned runtime;
  future runtime/normalizer upgrades require their own parity and migration proof.
* 1M/10M-node latency, application/import peaks, controlled cold-cache benchmarks,
  total database/OS cache memory and production backup/restore.
* Private Glufs acceptance (#23), the broader image audit (#24) and shared post-main
  re-analysis: the earlier `make dev` stopped at a proxy-blocked Ollama download.

`make issue28-acceptance` deliberately returns nonzero while the full endpoint
inventory remains incomplete. Foundation checks cannot waive that gate.

## Coordination, cost and next integration boundary

### Foundation design gate: pseudocode 95/100

The design maps invalid imports to artifact rollback, partial multi-file failure to
an unchanged published pointer, concurrent publication to CAS rejection, and page
navigation to an immutable context and authenticated continuation. Executable proof:
`issue28-gate PHASE=pseudocode` passed 27 actual snapshot/CLI/upgrade tests.

Application import memory is bounded by record/staging budgets plus at most
100,000 artifact identities. Query/UI memory is bounded by page/row/body budgets.
Database work is not bounded by page size: empty/substring search still scans and
sorts the selected corpus, and normalization processes the stored corpus in bounded
batches. Five points are deducted because the larger-tier database time/import
resource envelope remains provisional; see the unverified-scale list above.
The closing Brain pass found relevant SPARC evidence/trace guidance, without a
source-grounded reason to add another runtime dependency.

### Foundation architecture gate: 95/100

`issue28-gate PHASE=architecture` passed 39 real PostgreSQL query/HTTP tests.
Typed `IExplorerQueries` keeps runtime reads outside file indexes; the migration,
normalizer, snapshot store, cursor and bounded serializer make their respective
trust and resource boundaries explicit. The independent RuVector experiment
failed its own adoption prerequisites and cannot alter the canonical store.
The existing PostgreSQL/Npgsql stack is retained. Five points are deducted for
the still-proposed owner architecture decision and absent larger-tier capacity
evidence described in MADR-0017/0018. Brain source review included the RuVector
graph implementation and compatibility documentation; actual image tests take
precedence over its broad compatibility claims. No new library is added.

### Foundation refinement gate: 95/100; full-issue completion remains pending

The combined final Store suite passed 131 tests with zero skips. Earlier combined
Explorer/Contracts runs passed 232/108, and the first full local act run passed at
`5f05302`. Subsequent binding-identity and pager changes have their own passing
regressions; the final pushed commit must obtain a fresh ordinary pre-push receipt.
The final served application passed 31 uninjected browser checks and a separate
15-check recovery test with exactly one injected 503. Both 1K/100K sequential HTTP
runs covered ten workloads each, including empty-query first and continuation
pages, and passed the provisional p95 targets. See the benchmark report for exact
source/build identities, variance, query plans and remaining database scan costs.

Refinement corrected Unicode casing/order, rollback/retry edge cases, immutable
default selection, legacy binding-upgrade reuse and failed-navigation page state.
Five points are deducted for the explicitly unmeasured larger-tier/import-peak
and controlled-cache evidence above. Formal phase iteration counts were not
reliably recorded and are not reconstructed from commit counts. Independent
native workers reviewed Store/CLI and query/UI; root integrated shared files.

The full `issue28-acceptance` target was actually executed and returned nonzero:
`completion: executable evidence not implemented; gate remains incomplete`.
This is a foundation checkpoint, not a completed issue or a passed full completion
gate. The closing Brain pass checked Ruflo SPARC refinement/evidence guidance;
remaining endpoint coverage cannot be waived by passing core tests.

One root integrator owns shared migrations/manifests/CLI/API registration; isolated
native workers own Store/query/UI, independent tests and the RuVector experiment.
Actual session capacity is root plus three workers; Ruflo records a hierarchical
four-slot ledger with autoscaling disabled. A ledger initialization is not an
execution receipt. No external paid model calls were made; native subscription
cost and model-routing savings were not measured.

Publish the coherent foundation against current main, leave issue #28 open, and
let the owner review/merge before dependent endpoint families under the agreed
delivery sequence. Preserve active previews until handoff and apply MADR-0014 to
inactive recoverable worktrees after verified PR publication. Keep the separate
declared-contract catalog outside this diff.
