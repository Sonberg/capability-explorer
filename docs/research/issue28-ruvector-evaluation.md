# Issue #28: bounded RuVector PostgreSQL experiment

Status: **retain PostgreSQL + pgvector; candidate fails adoption prerequisites**.
This is an independently measured challenger report, not a decision to abandon
RuVector generally. No production database, migration, dependency or CI change
was made. Issue #28's canonical relational migration can continue independently.

## Inputs and scope

- Project baseline: merged main `d9421fa046c870df484927c97b786afcbb7d48bb`.
- Run: 2026-11-01, Apple arm64 host, disposable Docker PostgreSQL instances;
  one CPU, 512 MiB memory, 32 MiB shared buffers and ten connections each.
- Baseline: existing pinned `pgvector/pgvector:0.8.6-pg17` image,
  digest `sha256:cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f`;
  local architecture arm64, PostgreSQL 17.11, pgvector 0.8.6.
- Challenger: `ruvnet/ruvector-postgres`, resolved from `latest` and then pinned
  exclusively by digest
  `sha256:7fb09d439d82fccbe6e0035d09f91c9dce456bb2d70cb2e8d6afb12a6afa2051`.
  Image configuration: amd64 only, PostgreSQL 17.9, OCI version 2.0.5;
  installed extension reports **0.3.0**. Expanded size 627,042,582 bytes.
- Current source inspected separately at
  `cf357d8121f9def5bc9d2db512a47f61c62b0651`.
  The image has no source revision label. These source findings are **not proof
  that this binary was built from that source**.
- The documented `ruvnet/ruvector-postgres:2.0.0-pg17` and checked `2.0.6`
  tag did not resolve. The alternate documented `ruvector/ruvector-postgres`
  repository/tag denied pull access. This does not establish that no other
  valid packaging exists.

Grounding used Brain `search_ruvnet`, specifically
`crates/ruvector-postgres/docs/GRAPH_IMPLEMENTATION.md`,
`crates/ruvector-postgres/docker/README.md` and
`crates/ruvector-postgres/docs/INSTALLATION.md`; live implementation and OCI
metadata were then inspected because documentation disagrees on version,
repository, type and access-method names.

## Results actually observed

[Machine-readable receipt](issue28-ruvector-smoke.json) records expected and
actual output, exact image identities, source hashes and explicit omissions.

| Check | pgvector baseline | RuVector candidate |
|---|---|---|
| Exact bounded filtered lookup; adjacency | Pass | Pass (ordinary relational SQL) |
| Ordinary relational rollback | Pass | Pass |
| Fixed three-dimensional L2 distance | Pass | Pass using `ruvector`, not `vector` |
| Ordinary facts survive clean restart and SIGKILL recovery | Pass | Pass |
| Graph survives a new backend, clean restart and SIGKILL recovery | Not applicable | Pass (one committed node) |
| Existing application's `real[]::vector` SQL | Existing baseline | **Fail: type `vector` does not exist** |
| Available pgvector `vector` extension | Present | **Absent** |
| Same-backend graph rollback | Not applicable | **Fail: cached node count 2; expected 1** |
| Fresh-backend graph count after rollback | Not applicable | Pass: committed count 1 |
| ANN recall, latency, scale and adoption benefit | Not evaluated | Not evaluated |

Rollback reproduction, in one backend after committing one graph node:

```sql
BEGIN;
SELECT ruvector_add_node('probe', ARRAY['Symbol'], '{}'::jsonb) IS NOT NULL;
ROLLBACK;
SELECT ruvector_graph_stats('probe')->>'node_count';
-- observed: t, 2; expected: t, 1
```

A new backend returns 1. The persistence layer rolls back, but the existing
backend continues exposing a rolled-back node. A connection pool may reuse
that backend, making this relevant to Npgsql even though no .NET client test
was run. This is a demonstrated graph cache isolation problem in this image;
it is not evidence that ordinary PostgreSQL tables or all vector operations
share that defect.

The existing `EmbeddingStore.cs` uses `real[]::vector`, cosine `<=>` and
`CREATE EXTENSION vector` in migration `0003_embeddings.sql`. A functioning
`ruvector` distance operator does not establish drop-in compatibility. Separate
instances were used; coexistence with pgvector was not tested. The inspected
image SQL also registers access method `hnsw`, making namespace/operator/type
coexistence a required explicit test, not an assumption.

No meaningful speedup claim can follow from these runs: CPU architecture and
PostgreSQL patch version differ, and the workload is only 1,000 relational
nodes, 999 edges and one persisted extension graph node. Docker idle memory
snapshots are retained for diagnostics, not compared as steady-state budgets.
Because correctness and compatibility already failed, an ANN benchmark of this
binary would consume resources without making it eligible for adoption.

## Source inspection changes the earlier assessment

Current `src/graph/mod.rs` is **not memory-only**. It persists graph names,
nodes and edges to PostgreSQL tables via SPI, then lazily loads nodes and edges
into a backend-local `GRAPH_REGISTRY` / `DashMap`. The runtime persistence tests
confirm that this image also retains the tested committed graph across restart.
The original in-memory-only concern must therefore be corrected.

However, current lazy loading selects **all nodes and all edges of a named
graph** before caching it. Merely wrapping `ruvector_get_neighbors` in an outer
`LIMIT` does not bound graph materialization. This conflicts with #28's desired
bounded per-request database resource usage if the entire analyzed corpus is
represented by one extension graph. Per-snapshot graphs would still require
bounded size and lifecycle accounting. Backend cache invalidation, transaction
abort handling and concurrent writer isolation require dedicated tests.

The selected current graph functions mutate the cache then persist via SPI.
Several SPI results are discarded with `.ok()`. This warrants review of error
propagation and transaction callbacks; it does not justify claiming every
persistence failure can be silently recovered from PostgreSQL errors.

## Existing upstream evidence relevant to this binary

[Issue #727](https://github.com/ruvnet/RuVector/issues/727) explicitly reports
HNSW `CREATE INDEX CONCURRENTLY` duplicate tuples on PostgreSQL 17.9 and the
**same image digest** tested here. The issue is closed and
[PR #738](https://github.com/ruvnet/RuVector/pull/738), commit
`ff4862b07d958bcf31aa323cf4ea8acb6b41e194` (tag `ruvector-postgres-v2.0.6`),
records the source fix. A closed issue does not patch an immutable older image;
this run did not reproduce the ANN bug or verify its fix.

[Issue #728](https://github.com/ruvnet/RuVector/issues/728) reports accepted but
discarded non-256-dimensional SONA learning inputs, also addressed by #738.
Learning is outside this deterministic facts experiment and was not enabled.
Never interpret a successful learning response as read-back proof.

## Weighted decision aid

Weights describe this project's requirements; ratings are qualitative evidence
maturity from 0 (unproven/failing) to 5 (strong relevant evidence). They are not
performance benchmarks or an automatic architecture decision.

| Criterion | Weight | Existing pgvector path | Tested RuVector image |
|---|---:|---:|---:|
| Exact facts, transaction/recovery correctness | 35 | 4 | 1 |
| Existing SQL/client and native arm64 packaging fit | 25 | 5 | 1 |
| Measured relevant performance/quality benefit | 20 | 0 | 0 |
| Implementation and operational frugality | 15 | 4 | 2 |
| Version provenance/security qualification | 5 | 2 | 0 |
| Weighted evidence score /100 | 100 | **67** | **18** |

The baseline score is not a security approval: large-scale bounded reads and
performance still need issue #28 work, and full image/security qualification is
outside this probe. Hard correctness gates override all weighted scores.

## Recommended next eligible experiment

Retain this negative result against its exact digest; do not change existing
CE PostgreSQL or add RuVector to default CI. Before another timed run:

1. Obtain a source-bound PG17 native arm64 and amd64 package/image containing
   the relevant source fixes, with reproducible build/extension versions.
2. Verify pgvector SQL/client coexistence or explicitly cost the adapter/migration
   needed for replacement. Run Npgsql parameter/result and cancellation tests.
3. Require same-session rollback, cross-session invalidation, concurrent writes,
   index update/delete/vacuum/rebuild and crash recovery to pass.
4. Confirm bounded graph behavior; otherwise restrict RuVector to an optional,
   rebuildable derived retrieval index that cannot replace canonical facts.
5. Only then compare identical fixed embeddings and exact ground truth,
   recall@k/latency at matched quality, total memory/disk/WAL, cold/warm repeats
   and index build/recovery costs on the same architecture and PG patch version.
   The plan's proposed 20% p95 benefit and <=10% resource regression need owner
   approval and cannot waive correctness/security gates.

## Reproduction, cost and limits

The probe is opt-in; no ordinary CI changes or new dependencies:

```sh
# Pull the two exact digest references above first; the probe never pulls.
python3 scripts/experiments/issue28-ruvector-probe.py
python3 scripts/experiments/issue28-ruvector-probe.test.py
```

The script uses only Python's standard library and Docker. It starts containers
with UUID names, no published ports, `--network none`, resource limits, and an
empty init-script directory so upstream sample roles/default credentials and
optional initialization work are not executed. Only its own containers and
anonymous experiment volumes are removed. Existing `ce-postgres` is untouched.
It refuses to start below an 8 GiB host disk reserve. Candidate emulation is
explicit. It performs no model or embedding calls; the checked image model
directory was empty. No external paid provider was invoked by this experiment.

First candidate attempt encountered the temporary initdb-server readiness race;
the one actionable rerun required PID 1 to be the final `postgres` process.
The rerun completed and reported failures correctly. Four report-gate unit
tests pass, covering empty, failed and not-run evidence plus smoke/adoption
separation. The final runner additionally extracts that tested status rule and
adds an explicit initialization timeout; the exact final file was not rerun
against Docker after that mechanical change. Receipt labels distinguish the
executed runner hash from the final reviewed runner hash.

**Not tested:** image/dependency CVE scan, Npgsql client integration, coinstalled
extensions, ANN quality/performance, large-corpus memory/disk, concurrent graph
cache behavior, live snapshot publication, cancellation, upgrade/downgrade and
production rollout. Candidate adoption is **not approved** and must fail its
own gate. This report does not claim issue #28 is complete.

## Reference log (retrieved 2026-11-01)

All source links below pin the inspected commit; image identity is separate.

- [MIT license](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/LICENSE): repository license; transitive dependencies and packaged contents not fully audited.
- [Cargo manifest](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/Cargo.toml): crate 2.0.6, default pg17, pgrx 0.12, optional feature dependencies.
- [Extension control](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/ruvector.control): SQL extension version 0.3.0, distinct from crate/image version.
- [Graph persistence/cache](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/src/graph/mod.rs): SPI persistence and whole-graph lazy loading.
- [Graph operations](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/src/graph/operators.rs): cache mutation followed by persistence; graph interfaces.
- [Graph storage](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/src/graph/storage.rs): cached node/edge indexes.
- [HNSW access method](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/src/index/hnsw_am.rs): current implementation, not matching-binary recovery proof.
- [Docker build](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/Dockerfile): PG17 build/features; floating build inputs must be pinned for a qualified rebuild.
- [Upgrade SQL](https://github.com/ruvnet/ruvector/blob/cf357d8121f9def5bc9d2db512a47f61c62b0651/crates/ruvector-postgres/sql/ruvector--2.0.0--0.3.0.sql): inspected upgrade direction, not a validated downgrade procedure.
- [pgvector upstream](https://github.com/pgvector/pgvector): existing comparison engine; this experiment uses the repository's pinned image rather than an unpinned latest release.
