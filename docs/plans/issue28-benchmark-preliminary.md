# Issue #28 preliminary foundation measurements

These are **dirty-build diagnostic results**, not final clean-commit acceptance
or a full issue performance pass. Measured 2026-11-01 against the actual served
PostgreSQL API at ports 5116 (1K) and 5118 (100K), 30 measured samples after five
warmups per query, sequential local HTTP, 50-row requests. Host/DB caches were
uncontrolled; other workspace services remained running.

Application HEAD at measurement: `97919d000a85a58401efc85652e2c1e65dd0da79`,
**dirty source**; served Explorer DLL SHA-256:
`2b795ed22410d492361a3de927c6655b084231201ed1543d8803d6e8c7544c8f`.
Database image: repository-pinned pgvector 0.8.6 PG17 digest
`cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f`.
A shared disposable database container hosted separate `ce` / `ce_issue28_scale`
databases, capped at two CPUs and 1 GiB. Applications were not explicitly memory
capped; observed container limit was the 15.66 GiB Docker VM capacity.

| HTTP workload | 1K nodes p95 ms | 100K nodes p95 ms |
|---|---:|---:|
| Search Repeated | 6.89 | 153.64 |
| Search x | 6.55 | 175.41 |
| Search get.foo | 8.46 | 158.12 |
| Search Order-28 | 26.71 | **240.16** |
| Symbol detail | 7.94 | 12.33 |
| Hub outgoing adjacency | 4.58 | 30.88 |
| Search Repeated continuation | 7.25 | 184.77 |
| Hub adjacency continuation | 4.73 | 36.37 |

Every measured response satisfied the row/byte caps, stable row identity and
nonoverlapping continuations. The 100K Order-28 query exceeds the proposed
200 ms search target; **do not mark performance passed**. Highest 100K p99 was
252.75 ms. These sample sizes do not provide strong tail confidence.

Post-run Docker diagnostic memory snapshots: 1K application 66.44 MiB, 100K
application 52.98 MiB; shared database container 152.6 / 157.6 MiB respectively.
These are neither peak/RSS measurements nor isolated total-memory comparisons;
there is no evidence that a larger corpus reduces application memory.

## Exact SQL replay findings at 100K

After HTTP timing completed, three actual projection statements and parameters
were captured from the disposable database's logs. The original logging setting
was restored to `-1`. Verbatim SQL included the application's expanded
`GuardedColumns` expressions. Read-only `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`
replay with recorded inferred parameter types showed:

- Search Order-28: **140.053 ms** database execution. Sequential scan and
  sort/unique of 100,000 nodes occur before rank filtering to 12,500 matching
  candidates and top-N. Parallel sort used external merge and wrote 1,297
  temporary blocks. Improve query structure/indexes while preserving the
  winning occurrence for each symbol; moving filters indiscriminately before
  winner selection could change behavior.
- Symbol: **0.105 ms**, uses `node_symbol_key_idx` and retrieves one row.
- Hub adjacency: **26.072 ms**. Existing adjacency index scans 90,000 outgoing
  rows, then incremental sorting produces the requested first 51 rows. The
  index's intervening `relation` key and mismatch with explicit `COLLATE "C"`
  ordering prevent the intended ordered bounded scan. An index matching the
  fixed artifact/from prefix followed by `local_id COLLATE "C"` warrants a
  measured candidate; migration changes belong to integration ownership.

Replay execution times are separate observations, not HTTP latency components
that can be subtracted precisely. Npgsql parameter typing/custom/generic plans
and contemporaneous contention can differ. No source or index was modified by
this diagnostic work, and no dataset was republished.

Raw reports/logs/plans are private ignored artifacts under the experiment
worktree `.sandbox-cache/issue28-bench/preliminary-{small,medium}`. Integration
must preserve them in its handoff and **rerun after final source changes and
clean commit**. Missing startup/peak memory/import/WAL/disk/concurrency and
larger-tier evidence remain unmeasured.
