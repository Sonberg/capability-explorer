# Analysis performance report

Report date: 2026-11-05. Evidence: eight successful local analysis runs from
2026-11-04, using Podman with package acquisition enabled and no PostgreSQL
publication. This is a read-only retrospective investigation, not a new benchmark
or a pipeline change.

## Executive conclusion

**Dependency preparation is the largest cost, not downloading source, building
the target application, or saving artifacts.** Across this sample, approximately
71% of active analysis time was before extraction in package preparation, 25%
was offline extraction, and 4% was source acquisition, final integrity checking,
publication and cleanup.

The eight runs consumed **76 minutes 26.6 seconds** of cumulative active time.
They were not one uninterrupted batch: gaps, failed attempts, prior retries and
operator interventions are excluded.

| User's category | Finding |
| --- | --- |
| Downloading source | About 34 seconds total, 0.75%. These were existing-cache runs, not first-time clones. |
| Restoring dependencies | The main bottleneck. NuGet preparation took about 18 minutes; the subsequent npm/install/dependency-hashing window took about 36 minutes. |
| Building | No normal build or restore of the analyzed applications. In-memory Roslyn/TypeScript compiler work belongs to extraction. Building Capability Explorer itself via Make is separate and not measured here. |
| Extracting | About 19 minutes total, 24.89%. Significant for ASQ, but not the largest aggregate cost. |
| Saving | Local publication plus cleanup took about 60 seconds total, 1.32%. Database saving was not exercised. |

## Stage breakdown

All durations below are seconds, rounded to one decimal. These are **historical
filesystem-derived estimates**, not independently instrumented stage stopwatches.
The windows are non-overlapping; see the methodology before interpreting them as
pure network, CPU or disk time.

| Capability | Source | NuGet window | npm + dependency hashing | Extraction + merge | Final integrity checks | Publication + cleanup | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| weekly-sales | 1.1 | 51.3 | 3.7 | 43.1 | 1.4 | 0.7 | 101.3 |
| demand-pricing | 1.2 | 35.9 | 36.3 | 67.9 | 8.1 | 10.3 | 159.6 |
| kraken | 3.8 | 133.8 | 2.8 | 48.6 | 3.7 | 2.7 | 195.3 |
| apt | 5.2 | 155.0 | 736.2 | 133.9 | 7.6 | 5.1 | 1,043.0 |
| sales-plan-repository | 3.5 | 101.1 | 2.2 | 47.6 | 2.9 | 1.3 | 158.6 |
| article-reference-repository | 2.1 | 44.3 | 2.5 | 18.2 | 1.0 | 0.7 | 68.7 |
| asq | 12.3 | 453.3 | 542.5 | 631.1 | 54.4 | 15.3 | 1,709.0 |
| squid | 5.2 | 111.1 | 839.8 | 151.4 | 19.2 | 24.4 | 1,151.1 |
| **Total** | **34.4** | **1,085.7** | **2,166.0** | **1,141.7** | **98.3** | **60.4** | **4,586.6** |
| **Share** | **0.75%** | **23.67%** | **47.23%** | **24.89%** | **2.14%** | **1.32%** | **100%** |

Combined package-preparation windows: **54 minutes 11.7 seconds, 70.90%**.
The npm-related column includes installation, sandbox startup and hashing installed
dependencies; it must not be described as 36 minutes of pure network downloading.

### The three slowest capabilities

- **ASQ: 28 minutes 29 seconds.** NuGet 7:33, npm-related preparation 9:03,
  extraction/merge 10:31. Both dependencies and semantic extraction matter.
- **Squid: 19 minutes 11 seconds.** npm-related preparation 14:00, versus
  extraction/merge 2:31. Package preparation is clearly dominant.
- **APT: 17 minutes 23 seconds.** npm-related preparation 12:16, versus
  extraction/merge 2:14. Package preparation is clearly dominant.

Together these three account for approximately 85.1% of the sample's active time.

## Why dependency preparation is expensive

### 1. Failed npm installations consume time without full dependency coverage

The resolved manifests retain these aggregate coverage receipts:

| Capability | npm projects installed | npm projects unavailable | npm-related window |
| --- | ---: | ---: | ---: |
| weekly-sales | 1 | 0 | 3.7 s |
| demand-pricing | 1 | 0 | 36.3 s |
| kraken | 0 | 0 | 2.8 s |
| apt | 0 | 2 | 736.2 s |
| sales-plan-repository | 0 | 0 | 2.2 s |
| article-reference-repository | 0 | 0 | 2.5 s |
| asq | 0 | 1 | 542.5 s |
| squid | 1 | 2 | 839.8 s |

All three slowest runs have unavailable npm projects. That is a strong reason to
investigate package-feed access and installation failures first. It does **not**
prove that every second in those windows was a failed HTTP request.

[The npm helper](../../scripts/fetch-npm-packages.mjs) discovers every directory
containing a package manifest and lockfile, then runs `npm ci` sequentially in
each one, with lifecycle scripts disabled and a **15-minute timeout per project**.
That discovery does not apply the extractor's production/test exclusion policy.
Nested test or tooling projects can therefore incur dependency installation even
when they do not contribute production analysis units.

Unavailable installations are counted but do not by themselves fail the entire
analysis. A run can finish successfully while producing partial semantic coverage.
The retained evidence does not establish whether these failures were authentication,
package availability, TLS, retry exhaustion or timeout failures. Package helper
output is deliberately not persisted because remote errors may expose secrets.
Do not treat the near-minute-scale durations as proof of a particular timeout.

### 2. NuGet packages are downloaded into fresh attempt-owned directories

[Preparation](../../platform/src/CapabilityExplorer.Cli/AnalysisPreparation.cs)
creates a new `packages` directory inside each attempt.
[Package acquisition](../../platform/src/CapabilityExplorer.Cli/AnalysisPackagePreparation.cs)
passes that directory to every repository's fetch operation. Packages can be
reused between repositories **within that attempt**, but the fetcher is not
pointed at a persistent shared NuGet package directory across attempts.

The receipts report **1,877 downloaded package events, 844 already-present
events and 22 unavailable events** across the eight runs. These are summed
fetcher coverage counts, not counts of globally unique package versions or bytes.
ASQ alone reports 815 downloaded and 833 already-present events, consistent with
substantial reuse between its repositories inside one attempt.

[The fetcher](../../platform/src/CapabilityExplorer.Extractors.CSharp/PackageFetcher.cs)
awaits package resolution/downloads sequentially and tries feeds in order. It
downloads package data and unpacks selected assets; it does not execute package
build logic or perform normal NuGet restore. Serial feed/network latency can
therefore accumulate across many packages.

Fresh source exports also mean npm starts from a fresh dependency tree each time.
An npm download cache may reduce transfer work, but does not eliminate `npm ci`
installation and filesystem work. Its hit rate was not measured here.

## Extraction, building and filesystem costs

### No target application build

[The C# extractor](../../platform/src/CapabilityExplorer.Extractors.CSharp/CSharpExtractor.cs)
reads project XML and source files, selects reference assemblies, creates Roslyn
compilations, queries compiler diagnostics and traverses semantic models. It does
not invoke MSBuild, run source generators or emit/build the target applications.
The TypeScript extractor similarly uses compiler APIs rather than building or
executing the target application.

This work still includes real compiler CPU and memory costs. Calling it "no
build" does not mean semantic extraction is free. ASQ's extraction window alone
is about 10.5 minutes. No CPU profile was collected, so the individual costs of
Roslyn compilation, calculation analysis, rule traversal and validation cannot
be ranked from these receipts.

### Extraction is deliberately serialized

[The extraction coordinator](../../platform/src/CapabilityExplorer.Cli/AnalysisParallelExtraction.cs)
runs one job at a time, despite its historical class name. It constructs a job
for every repository for C#, TypeScript and SQL, including combinations that
produce only a "no units found" message. Sandbox startup, engine checks and
language startup are therefore included in extraction time even for empty jobs.

The worker also clamps capability and extraction concurrency to one. This is
intentional current behavior, not an accidental missing parallel loop. Increasing
concurrency would require explicit agreement and memory/isolation qualification;
it should not be the first response to npm failures consuming most of a run.

### Integrity checks and publication are measurable but secondary

Preparation hashes sources and installed dependencies before and after extraction,
then hashes artifacts, package locks, package-cache files and runtime inputs.
The final integrity-check window totals about **98 seconds**; ASQ contributes
about **54 seconds**. Earlier hashing also occurs inside the preparation windows.
These are correctness and isolation checks, not redundant work that can simply
be removed without replacing their guarantees.

[Publication](../../platform/src/CapabilityExplorer.Cli/AnalysisPublication.cs)
validates artifacts, verifies hashes, copies to a same-filesystem preparation
directory, rechecks bytes and swaps the canonical directory. The worker then
deletes exported source/dependency trees and records enrichment status. The
combined tail totals about **60 seconds**, with the longest tail about **24
seconds** for Squid. This is not a measurement of file writing alone.

These runs have only confirmed `local-artifacts` effects. PostgreSQL migration,
import, binding, snapshot publication and optional embeddings were **not
measured**. A database-backed run may have materially different saving costs.

`make analyze` and `make analyze-all` also depend on Capability Explorer's own
`build` target. That startup build/restore is outside these run timestamps and
must be timed separately when explaining the full Make command's wall time.

## Recommended priorities

1. **Diagnose the unavailable npm projects first.** Collect sanitized per-project
   elapsed time, success/failure category, registry host and retry/timeout counts.
   Keep credentials and remote response bodies out of durable logs. Establish
   whether package roots are needed by selected analysis units before excluding
   any test/tooling roots.
2. **Add safe cross-attempt NuGet reuse.** Use verified immutable cached assets
   with attempt-owned materialization and preserved content identities. Current
   evidence supports repeated downloads as a cost; it does not quantify the
   speedup of a proposed cache implementation.
3. **Persist complete stage timings.** Record source sync versus export, package
   resolution/download/unpack, each npm project, dependency hashing, each
   extraction job, validation, artifact copy, PostgreSQL and embeddings separately.
   Record queue time independently. Preserve existing log redaction.
4. **Avoid provably empty extractor launches.** Consider trusted discovery that
   skips languages without applicable units while preserving artifact/coverage
   contracts. Container overhead has not been isolated, so benchmark this before
   promising a specific saving.
5. **Profile ASQ's offline extraction.** After fixing dependency stalls, profile
   compiler/semantic/rule/validation work and allocation or filesystem pressure.
   Do not assume CPU, Podman mounts or hashing is the largest internal cost
   without a profile.

`PACKAGES=0` is a possible diagnostic comparison, **not an equivalent faster
analysis**: removing dependency references can reduce semantic coverage and
change artifacts. It should not be presented as a free optimization.

## Evidence and methodology

Selected run identities, all with final `done` state:

| Capability | Run ID |
| --- | --- |
| weekly-sales | `1dbcc9a0e3b540ffa1f6417deae5e522` |
| demand-pricing | `e43c5d57639d4395adc2c53e307b496e` |
| kraken | `0a2ca5b1b7d64e6a9a0a7d01b7a6e13f` |
| apt | `757b015df5734698a12062c68f0e56d5` |
| sales-plan-repository | `ecc4b0d5cf1545ec8aa888852a113aa0` |
| article-reference-repository | `b620c3079ec9447ab66d8d4c62622dca` |
| asq | `210482a45d884a07ba5bed627cd10aad` |
| squid | `aefa1bc3aa2d49d38c7e2b8c303c8289` |

Evidence resides under each run's local `.sandbox-cache/requests/runs/<run-id>`
directory. It is intentionally not copied into version control. The calculation
uses Node's structured JSON and filesystem APIs (`JSON.parse`, `statSync`,
`readdirSync`) over run records, resolved manifests, lockfiles and staging trees.
No configuration credentials, package-feed settings or target source were exported.

For each run, derive these ordered boundary timestamps in milliseconds:

1. `t0`: birth time of the run's `analysis.log`, created for its first helper.
2. `t1`: modification time of the attempt's `manifest.tsv`, written after export.
3. `t2`: latest modification time of files in the attempt's `locks` directory.
4. `t3`: birth time of the attempt's `out/tasks` directory, created for extraction.
5. `t4`: latest modification time of the immediate repository/revision staging
   directories, identified using `manifest.tsv`, excluding `out/tasks`.
6. `t5`: birth time of the run's `resolved.json`, bound after preparation.
7. `t6`: modification time of the final `run.json`, including enrichment status.

Each stage is the difference of adjacent boundaries divided by 1,000. Total
active time is `(t6 - t0) / 1000`. Coverage counts come from resolved `inputs`
keys `nuget-coverage:*` and `npm-coverage`. Never use copied artifact file birth
or modification dates as merge-completion timestamps: macOS file copying can
preserve them. Directory mutation timestamps avoid the observed misattribution
of final empty-language jobs to post-extraction hashing.

Limitations:

- These are wall-clock estimates from retained filesystem metadata, not precise
  monotonic timings. Copies/restores of the evidence or clock adjustments could
  invalidate the reconstruction; no sub-second accuracy is claimed.
- The NuGet window also contains initial source hashing and process startup.
  The npm window also contains host credential resolution, sandbox overhead and
  dependency hashing. Extraction includes validation, file writing and merge.
- The last directory mutation and receipt write are proxies for stage completion;
  small boundary overheads belong to the neighboring window.
- `analysis.log` does not retain the coordinator's existing `-- timing:` output:
  [process logging](../../platform/src/CapabilityExplorer.Cli/AnalysisProcess.cs)
  captures child streams, while those stopwatch messages are host console output.
- Total active time excludes admission/runtime checks before the first helper,
  queue delay and Make's own startup build. For weekly-sales, acceptance preceded
  execution by about **4 hours 46 minutes 25 seconds**. That is queue/wait time,
  not source download or extraction time. Other selected starts were within
  about a quarter second of acceptance.
- No fresh cold-cache run, controlled A/B experiment, network trace, CPU profile,
  database benchmark or comparison with Docker was performed. The sample does
  not establish performance for first-time clones or other machines.
- Glufs and unsuccessful/reconciled attempts are excluded. This report does not
  quantify total time lost to earlier failures and operator recovery.

Validation for this documentation-only investigation checks all eight terminal
states and local effect confirmations, ordered boundaries, stage-sum agreement,
coverage totals and the report's local Markdown links. No production behavior was
changed, so no artificial red/green test is claimed.