# Issue #28: sequential foundation benchmark result

The measured **1K and 100K foundation workloads meet the proposed p95 targets**
on clean application commit `0720d597b94f8a965c2a37b306060fc181fe3876`.
This is a bounded HTTP foundation result, not full issue #28 acceptance,
large-corpus qualification or a RuVector adoption decision. Important query-work
and resource measurements remain open below.

These sequential runs supersede the briefly overlapping runs in the directory
named `final` at commit `5f05302321b1a9b2f6f622dfa4df2d08a1dcf3b9`.
That directory name never established acceptance. Workspace background services
remained running and OS caches were uncontrolled in every run.

## Exact measured inputs

- Served application source: clean `0720d597b94f8a965c2a37b306060fc181fe3876`;
  Explorer DLL SHA-256
  `edfc73c77334d6c499771369179046a8553630588030694cc2390753cdf9e3b4`.
- HTTP runner SHA-256:
  `8d95e6796c5a8a4dab16a283cede3ceeab3028b2329fac6383cc42f58ef8e13a`.
  Both source and runner identities are recorded in each receipt. The current
  checkout DLL and runner hashes independently matched the receipt values.
- Existing pgvector image:
  `pgvector/pgvector:0.8.6-pg17@sha256:cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f`.
  Disposable database container limited to two CPUs / 1 GiB, hosting separate
  small and medium databases. Application containers had no explicit CPU or
  memory cap; their visible memory ceiling was the 15.66 GiB Docker VM.
- Client: Apple M3 Pro, arm64 macOS, 12 logical CPUs, 36 GiB host memory.
  Sequential local HTTP; five warmups followed by 30 measured requests for
  each workload; pages limited to 50 rows and 1 MiB. Initial UI empty search,
  four nonempty terms, symbol, hub adjacency and three continuations included.
- 100K receipt finished `2026-11-01T22:53:30.452Z`; 1K receipt finished
  `2026-11-01T22:53:36.973Z`. No additional benchmark load was introduced by
  this report review. Later plan replay was separate from latency measurements.

## Corpus verification

The HTTP runner checks snapshot and revision selection; its `nodesGenerated`
field comes from the operator-provided manifest. That field alone is not proof
of database population: these generated tiers deliberately share a revision SHA.
Independent read-only SQL checked the selected artifacts, counts and complete
artifact content hashes against the manifests for the exact published snapshots:

| Tier | Snapshot, generation 1 | Verified nodes / edges | Artifact content SHA-256 |
|---|---|---:|---|
| 1K | `d4cb9730-f469-484a-aad1-ccd8cfaf0868` | 1,000 / 999 | `6ea8272866be3b30b395a8f6f87cb2de4406cdd7c5bfab7272b0d5f4c7bea557` |
| 100K | `b928d845-8986-4f82-93ae-756beb74263d` | 100,000 / 99,999 | `06259f81ef6ccfc8dfb4bf477db3451f4a42ca0b480c4fbd1de413408cc20a2d` |

Source/build metadata is operator supplied; the HTTP endpoint does not attest
its own loaded assembly bytes. The launch evidence, matching file hashes and
snapshot-scoped SQL proof complement the report rather than implying that the
HTTP runner independently verifies every input.

## Observed client latency

| Workload | 1K p95 ms | 100K p50 ms | 100K p95 ms | 100K p99 ms |
|---|---:|---:|---:|---:|
| Empty search, initial UI | 15.89 | 92.59 | **123.75** | 158.62 |
| Search Repeated | 10.65 | 57.35 | 89.05 | 92.71 |
| Search x | 7.64 | 61.69 | 75.82 | 78.06 |
| Search get.foo | 12.17 | 61.73 | 77.17 | 99.85 |
| Search Order-28 | 15.40 | 44.83 | **150.91** | 151.93 |
| Symbol detail | 105.71 | 7.40 | 14.99 | 20.24 |
| Hub outgoing adjacency | 6.34 | 30.05 | 36.49 | 37.75 |
| Empty-search continuation | 10.28 | 94.36 | 108.05 | 113.13 |
| Repeated-search continuation | 7.42 | 55.44 | 102.46 | 141.23 |
| Hub adjacency continuation | 8.35 | 4.41 | 4.86 | 4.92 |

All measured responses passed row/byte bounds, stable row identity, within-page
uniqueness and first/continuation disjointness. Largest responses were 16,065
bytes at 1K and 16,262 bytes at 100K; symbol responses contained one row and
collection pages contained 50. These warm foundation samples satisfy the
proposed p95 search 200 ms / adjacency 500 ms targets. Those are provisional
product targets, not adopted universal release thresholds.

Variance remains visible: the 1K symbol p95 was 105.71 ms versus a 9.14 ms
median and 149.74 ms p99; the 100K symbol p95 was 14.99 ms. Do not infer that
larger datasets make symbol lookup faster. Thirty observations provide weak
tail confidence. The 100K initial metadata request was 307.42 ms; that single
observation is neither process startup time nor a metadata p95 measurement.

## Comparison with the initial dirty-build diagnostic

The original dirty-build 100K Order-28 p95 was **240.16 ms**; the sequential
clean-build observation is **150.91 ms**. Repeated search changed from 153.64
to 89.05 ms, x from 175.41 to 75.82 ms, get.foo from 158.12 to 77.17 ms, and
Repeated continuation from 184.77 to 102.46 ms. These are observed changes on
the same synthetic corpus, not a controlled causal speedup estimate: source,
query structure, workload coverage, caches and background contention changed.
The empty initial search had no measurement in the first diagnostic; it cannot
be assigned a baseline speedup. The intermediate 55.11 ms Order-28 observation
belongs to the overlapping run and is **not the final result**.

Hub first-page p95 changed from 30.88 to 36.49 ms, while continuation changed
from 36.37 to 4.86 ms. Do not claim every operation improved or attribute these
individual differences solely to an index change.

## Current exact SQL-plan replay and remaining work

After timing, actual expanded application SQL and logged positional parameters
were captured from the disposable database. The prior logging setting was
restored to `-1`. Existing replay tooling ran read-only
`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` with recorded inferred parameter types:

| Actual captured projection | Separate replay execution ms | Relevant evidence |
|---|---:|---|
| Empty search | 101.968 | Sort/merge still writes 1,443 temporary blocks |
| Order-28 search | 53.257 | No temporary blocks in this replay |
| Symbol | 0.131 | Indexed symbol lookup |
| Hub outgoing | 29.283 | **90,000 edge rows scanned before producing 51 rows** |

Live index inspection confirms the current migration definition is installed:

```sql
CREATE INDEX edge_artifact_from_idx ON public.edge
  USING btree (artifact_id, from_key, local_id COLLATE "C", relation);
CREATE INDEX edge_artifact_to_idx ON public.edge
  USING btree (artifact_id, to_key, local_id COLLATE "C", relation)
  WHERE (to_key IS NOT NULL);
```

Despite those matching definitions, the selected-artifact join and planner still
produce a nested loop followed by incremental sorting over the high-degree
neighborhood. This is **not stale schema** and the latency target passing does
not prove O(page-size) database work. Preserve it as the next measured query
optimization: evaluate an ordered bounded scan per selected artifact followed
by a bounded merge, maintaining cross-artifact cursor order and winner semantics.
No query or schema was changed by this report review.

These are separate custom-plan replays, not the original Npgsql execution
plans or exact components to subtract from HTTP latency. The application query
is preserved verbatim; inferred `PREPARE` types and logged values are recorded
so differences remain inspectable.

## Resource snapshots and explicit omissions

Post-run Docker snapshots: 1K application **63.6 MiB**, 100K application
**49.53 MiB**; shared database container **171.8 MiB / 1 GiB** after both tiers.
These are container diagnostic snapshots, **not peak memory, process RSS,
whole-stack isolation or a 10M-node memory proof**. SDK image identity is retained
in the receipts; this run does not compare minimal runtime images.

Not measured: process startup, app/DB peak and OS-cache memory, import peak,
WAL/table/index sizes, pool wait, concurrent client load, cold cache, equivalent
file baseline, 1M/10M tiers, full endpoint coverage, or final release readiness.
No RuVector performance or security conclusion follows from these PostgreSQL
foundation measurements. Existing independent RuVector correctness failures
remain a separate experiment result.

## Evidence handoff

Private raw sequential receipts in integration checkout:

- `.sandbox-cache/issue28-bench/sequential/small.json`, SHA-256
  `79a25bc765c1c02212af2db5f659498d5cb3a4c6df40b712d7f7847c8281dbd0`.
- `.sandbox-cache/issue28-bench/sequential/medium.json`, SHA-256
  `196a168584e1939f436159e3e95acb75d31343af79d916d82663e2f5cd2a29d3`.

Independent corpus proof is in the experiment worktree
`.sandbox-cache/issue28-bench/final-review/{small,medium}-corpus-verification.json`;
actual current query log/plans and index definitions are under
`.sandbox-cache/issue28-bench/sequential-review/`. Preserve those ignored private
artifacts before worktree cleanup. A changed source, build, schema, image,
configuration or corpus requires fresh relevant evidence.
