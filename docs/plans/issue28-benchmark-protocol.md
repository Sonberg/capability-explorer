# Issue #28 foundation benchmark protocol

This opt-in harness measures the implemented PostgreSQL foundation endpoints.
It cannot certify full issue #28 completion or RuVector adoption. No generated
corpus is committed and no dependency or default CI image is added.

## Deterministic corpus

```sh
node scripts/benchmarks/issue28-generate.mjs \
  --nodes 1000 --seed 28 --out .sandbox-cache/issue28-bench/small
```

The generator streams valid ce.v1 JSONL with backpressure, incremental SHA-256,
constant-size records, one evidence per artifact, deterministic revisions and
symbol IDs. Names include repeated names, one-character identifiers, punctuation
and case. Ninety percent of edges originate at a high-degree hub. Default is one
repository/revision; `--repos 2 --revisions 2` splits the **total** requested node
count across four artifacts and records the latest revision for each repository.
Symbol keys remain stable across revisions. There are no cross-repository edges
in this initial workload; that is an explicit coverage gap.

Only real directories below `.sandbox-cache/issue28-bench` are accepted. Existing
artifacts are never overwritten. A 10 GiB host reserve plus an estimated 1,200
bytes per generated node is required; database tables, indexes and WAL need a
separate disk budget. Above 100K nodes requires `--large`, with a hard 10M-node
limit. Generated file hashes/counts/selection and generator hash are in
`manifest.json`. `sourceSha` is null when sandbox Git metadata is unavailable;
this must never be presented as a verified clean source identity.

Progression: 1K -> 100K -> opt-in 1M -> opt-in 10M. Run a larger tier only after
its predecessor passes correctness and after checking Docker VM as well as host
free space. Do not run 10M merely because the CLI accepts it.

## Import and publish using the application

Use a disposable PostgreSQL database/container, the existing pinned pgvector
image and the exact current application build. Do not import benchmark data into
`ce-postgres` or the owner's shared corpus. `CE_DATABASE` is supplied through the
approved sandbox environment; do not put credentials in metadata or reports.

```sh
# Run these through scripts/sandbox, using the built CLI for the actual candidate:
dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll \
  validate .sandbox-cache/issue28-bench/small/bench-0-r1.jsonl

dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll \
  publish --scope '*' .sandbox-cache/issue28-bench/small/bench-0-r1.jsonl
```

For multiple revisions pass **all** generated paths, plus `--view` containing
`repo:sha` pairs from manifest `selection`; this explicitly selects defaults
rather than relying on lexicographic revision hashes. Launch Explorer with
`EXPLORER_SOURCE=postgres`, that disposable database and a claimed local port.
The runtime should have no JSONL transport directory access for a separate
DB-only proof; the HTTP harness alone does not establish that property.

## Measure the served build

Create a private ignored metadata JSON file containing only:

```json
{
  "appSourceSha": "<actual 40-character application source SHA>",
  "sourceState": "clean",
  "databaseImage": "pgvector/pgvector:0.8.6-pg17@sha256:cf134a767f474095eeba57e0117be8e568e011a63f33fbf252f14c9b760f8e6f",
  "applicationBuildSha256": "<optional SHA-256 of served build artifact>"
}
```

Omit the optional build field if not measured. These values are operator supplied,
not trusted proof that a process runs that build; reconcile against the actual
launched binary/container. Unknown metadata fields are rejected to reduce
accidental credential retention.

```sh
node scripts/benchmarks/issue28-http.mjs \
  --base-url http://127.0.0.1:5118 \
  --manifest .sandbox-cache/issue28-bench/small/manifest.json \
  --metadata .sandbox-cache/issue28-bench/small/build.json \
  --samples 20 --warmup 3 \
  --out .sandbox-cache/issue28-bench/small/http-report.json
```

Optionally pass `--app-container NAME --database-container NAME` for one post-run
Docker statistics snapshot and sanitized image/resource identities. No container
environment or connection string is retained. These are diagnostic snapshots,
not peak memory measurements or proof of whole-stack RSS.

The runner accepts only local HTTP addresses and rejects credentials/redirects.
It requires ready PostgreSQL metadata and exact manifest revision selection,
pins the immutable snapshot on every query, requests 50 rows and identity
encoding, and aborts each request after 10 seconds. It measures four fixed search
terms, symbol detail, hub outgoing adjacency and second pages when available.
Rows must be stable across repeats, unique within each page, disjoint from the
first page on continuation, within the row limit and below 1 MiB per response.
It reports nearest-rank p50/p95/p99, min/max, row counts and bytes. Small sample
percentiles are descriptive, not statistical confidence bounds. A sample count
of 20 has weak tail precision; use more samples for a consequential decision.

Initial metadata request time is **not process startup time**. Measured request
time includes transfer through the final body chunk and excludes JSON parsing.
Warmups warm application/DB caches; OS cache state remains uncontrolled. The
proposed p95 search 200 ms / adjacency 500 ms targets are reported separately
from correctness; they are not adopted release gates. Failed HTTP/row/determinism
contracts return nonzero rather than emitting a successful benchmark report.

## Additional mandatory evidence before a scale claim

Capture exact SQL and parameters from the running query implementation, then
`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` against the same immutable snapshot.
This harness deliberately does not substitute a simpler lookalike SQL query
and claim it represents the endpoint plan. Record cancellation, concurrent
read/import/publication, request/DB queue time, app startup/RSS/peak, DB total
memory/OS cache, import peak, table/index/WAL bytes and rebuild/recovery costs
separately. File baseline comparisons must use equivalent source, corpus,
selection, hardware, resource limits and warm/cold conditions.

The report's `notMeasured` list keeps these gaps explicit. Full issue acceptance
must combine these receipts with all remaining endpoint and DB-only/recovery/UI
gates; an HTTP foundation report cannot waive missing evidence.

## Focused validation performed

```sh
scripts/sandbox --offline node node --test scripts/benchmarks/issue28-tools.test.mjs
```

Six tests pass: repeatability/count/skew/revisions, large-tier opt-in, path and
symlink rejection, streamed file hashes/multiple revisions/overwrite rejection,
HTTP limits and local-only addressing, and the measurement runner against an
actual local HTTP **test double**. That HTTP test does not use PostgreSQL.

The 1K artifact was also accepted by the actual sandboxed `ce validate` CLI.
A first attempt exposed an invalid zero-based evidence column; the generator
was corrected to one-based span columns and the actual validation reran green.
A real imported/published PostgreSQL HTTP benchmark must be run by integration
against the exact combined application build; no performance result is claimed
by this tooling commit.
