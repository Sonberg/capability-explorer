# PostgreSQL snapshots and bounded Explorer reads

* Status: accepted
* Date: 2026-11-01
* Related: issue #28, [main-only delivery](0016-main-only-pr-integration.md), [SemVer](0007-semantic-versioning.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for PostgreSQL snapshots and bounded Explorer reads; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:c507ac489e2a980aadbfbf68baad7d02494418a899947c1e842acb03a5c0196e`.
Previous decision status (historical): `proposed; implementation authorized for review, owner retains final architecture decision`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem statement

Explorer loads complete JSONL artifacts, repeats the load on refresh and caches
indexes for revision selections. PostgreSQL already stores normalized facts,
bindings, glossary review records and embeddings. Moving unbounded result sets
into SQL would retain the original scalability problem.

## Decision drivers

Bounded interactive memory and responses, deterministic evidence, atomic last-good
publication, reuse of existing storage, frugal local operation and owner-reviewed
incremental delivery.

## Considered options

1. Keep whole-corpus JSONL indexes.
2. Use existing PostgreSQL with immutable selected snapshots and bounded queries.
3. Replace PostgreSQL with SQLite, an analytical or dedicated graph store.

## Proposed decision

Implement option 2 for review. JSONL remains sandbox transport and fixtures; no
database credentials enter analyzed repositories. The existing binary COPY importer
gains streamed parsing and staged validation with identical contract semantics.

A candidate contains exact winning artifact IDs for repository/revision/unit.
Publication uses a short compare-and-swap generation transaction; an older writer
cannot replace a newer generation without detecting the conflict. Each request
pins an immutable snapshot once. Pagination binds snapshot, selection, filters,
query version and a unique ordering tuple. Retained snapshots remain pageable
after publication, subject to current authorization and cursor expiry.

Required publication inputs and bindings must succeed. Optional derived models
can remain pending/failed with explicit status. Their absence cannot falsely
invalidate valid core facts or promote inferred links into extracted evidence.

The initial opt-in database mode serves bounded metadata, search, symbol, evidence,
adjacency and calculation projections without constructing ExplorerIndex/ViewIndex.
Remaining views stay on the existing file mode until migrated; database mode must
report unsupported views explicitly rather than silently loading JSONL. The first
package does not complete issue #28 or switch the project default prematurely.

Search folds and sort bytes are versioned derived metadata, separate from raw
observations. They are computed using the pinned .NET invariant runtime, not
PostgreSQL locale casing. A per-artifact completion marker makes startup/request
readiness a metadata check; rebuilds never scan all nodes in an HTTP request.
Trusted migration/import work performs bounded resumable backfill from stored
facts without requiring original JSONL. Runtime/casing upgrades require parity
validation and a deliberate normalizer version decision.

Pagination changes API contracts. Document compatibility in the changelog and use
the existing version policy; do not invent a release number during development.
Keep the original file-mode behavior until the full cutover gate passes.

## Validation

[Contract](../../config/issue28/contract.json) and
[execution brief](../plans/postgres-explorer-brain-prompt.md) define limits and
acceptance. Validate invalid/truncated streams, cross-record references, count
mismatches, retries/conflicts, cancellation, competing publishers, retained pages,
search rank parity and a database runtime with artifact directories unavailable.
Measure application and database resources, disk/WAL and query plans. Latency and
10M-node memory budgets are provisional, not achieved results.

## Consequences

The initial package is substantial but deliberately leaves the endpoint inventory
unfinished. Full completion requires all endpoint families, derived read models,
bounded retention/recovery and database-only runtime proof. Main remains the only
PR target; the owner merges. Alternative engines require measured benefit sufficient
to justify migration and operations cost, not ecosystem benchmark claims.
