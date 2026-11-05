# Analysis performance report

Report date: 2026-11-05. Evidence: eight successful local analysis runs from
2026-11-04, using Podman with package acquisition enabled and no PostgreSQL
publication. The initial investigation was read-only, not a new benchmark.
The follow-up below records subsequent timing instrumentation and npm diagnosis.

**Measured improvement, 2026-11-05:** verified NuGet package reuse reduced a real
Kraken analysis from 179.8 s to 111.7 s (37.9%). A same-build fresh-attempt control
took 171.9 s, making the directly comparable warm-run reduction 35.0%. All five
artifact files were byte-for-byte identical. See the measured optimization below.

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
The initial run receipts did not establish whether these failures were authentication,
package availability, TLS, retry exhaustion or timeout failures. A later sanitized
inspection of retained npm debug logs confirms authentication failures and one
lockfile mismatch; see the follow-up below. Package helper output is deliberately
not copied into the analysis log because remote errors may expose secrets.
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

Validation for the initial documentation-only investigation checked all eight terminal
states and local effect confirmations, ordered boundaries, stage-sum agreement,
coverage totals and the report's local Markdown links. No production behavior was
changed in that initial investigation; its checks are not red/green tests.

## First implementation increment

Implemented on 2026-11-05, following approval to start with measurement and npm
failure diagnosis. No cache, concurrency, retry, credential or target-lockfile
policy was changed. No new private capability analysis was launched.

### Confirmed npm findings

A read-only, sanitized inspection found these records in the existing sandbox
npm debug cache. Only fixed error codes, registry families, durations and a
lockfile-mismatch flag were extracted. Raw errors, package names, source paths,
URLs and credentials were not copied into this report.

| Capability | npm log start (UTC, 2026-11-04) | Observed log window | Confirmed finding |
| --- | --- | ---: | --- |
| APT | 20:17:50.822 | 731.7 s | `E401`, six HTTP 401 results from GitHub Packages |
| APT | 20:30:02.745 | 0.4 s | `EUSAGE`, package manifest and lockfile out of sync |
| Squid | 21:01:40.076 | 680.6 s | `E401`, six HTTP 401 results from GitHub Packages |
| Squid | 21:13:46.251 | 86.6 s | `E401`, two HTTP 401 results from GitHub Packages |
| ASQ | 21:35:05.329 | 537.5 s | `E401`, two HTTP 401 results from GitHub Packages |

Log windows are filename start time to final file modification, not a new npm
benchmark. Individual denied requests in these logs lasted about 1.6-6.1 seconds;
they do not explain every second of the much longer installation windows.
The logs prove authentication failure, not whether the token was missing, expired,
insufficiently authorized or overridden by another credential source.

The immediate operational correction is to verify the configured GitHub Packages
credential and any matching host npm token override, including package access and
organization authorization where applicable. Secret values must be entered via
the existing private credential configuration, not chat or durable analysis logs.
APT's manifest/lockfile mismatch needs correction in its owning repository;
silently switching from `npm ci` to an unlocked install would weaken reproducibility.

### Durable measurements

[The telemetry scope](../../platform/src/CapabilityExplorer.Cli/AnalysisTelemetryStage.cs)
uses a monotonic stopwatch and appends one `-- telemetry: ` JSON record to the
existing private run log. Records have schema `ce.analysis.telemetry.v1`, run and
attempt IDs, stage, optional repository/language, elapsed milliseconds and outcome.
Package records also contain coverage counts and fixed failure categories.

Instrumented stages:

- `analysis.total`: execution through cleanup and the enrichment check, excluding
  admission, queue waiting and Make startup.
- `sources.check` and per-repository `sources.export`.
- `hashing.sources.before` and `hashing.dependencies.before`.
- Per-repository `packages.nuget` and aggregate `packages.npm`.
- `extraction` and per-repository/language `extraction.job`.
- `integrity.final`, `publication`, `cleanup` and `enrichment`.

Timers default to `failed` until completion is recorded. Package coverage with
unavailable dependencies is `partial`, not a false complete-coverage result.
An enrichment failure does not alter the already-published core analysis state.
Completed timing scopes can include partial analysis artifacts; timing outcome
does not replace artifact coverage or the journal's publication state.

Nested scopes overlap: do not sum `analysis.total`, `extraction` and extraction
jobs as independent costs. Logging retains the existing 16 MiB run-log bound and
0600 creation mode. No timings enter deterministic artifact/config identities.

Npm stderr is captured privately and bounded in memory. Only allowlisted error-code
categories reach durable telemetry: authentication, authorization, package missing,
TLS, timeout/network, dependency conflict, usage/lockfile, or unknown. Unknown
errors never fall back to logging their messages. These are diagnostic hints from
untrusted helper output, not proof of a particular credential defect.

This increment does not yet provide per-npm-project timing, failed-helper stderr
for a nonzero helper exit, HTTP retry counts, download-byte/cache-hit statistics,
or separate database import and embedding substage profiles. It does not claim a
measured speedup; it establishes the evidence needed for the next optimization.

### Acceptance and test evidence

| Scenario | Executable test | Observed pre-change failure | Green |
| --- | --- | --- | --- |
| Given npm fails, when package acquisition ends, then timings and safe failure categories persist without secrets or raw output | `AnalysisPackageTests.Package_credentials_and_untrusted_fetch_output_do_not_enter_run_evidence_or_offline_children`, seven error-code cases | All seven failed `Assert.Contains`: telemetry collection empty | 7/7 |
| Given serial extractor jobs, when they complete or the first fails, then each started job and the aggregate have truthful timings | `AnalysisParallelTests.Offline_jobs_run_serially_and_merge_isolated_outputs_even_for_older_parallel_settings` and `Failing_first_serial_job_leaves_later_jobs_unstarted_without_merging_or_erasing_uncertainty` | All four failed `Assert.Single`: telemetry collection empty | 4/4 |
| Given a complete local analysis, when it finishes, then each major stage has durable private timing | `AnalysisParallelTests.Given_a_complete_local_analysis_when_it_finishes_then_each_major_stage_has_private_durable_timing` | Actual local export/publication completed; `Assert.Single` found only extraction records, not lifecycle stages | 1/1 |

Commands run from the repository root, each with `cd platform` first:

```sh
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter FullyQualifiedName~AnalysisPackageTests --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter 'FullyQualifiedName~Offline_jobs_run_serially|FullyQualifiedName~Failing_first_serial_job' --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter FullyQualifiedName~Given_a_complete_local_analysis --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter 'FullyQualifiedName~Given_a_complete_local_analysis|FullyQualifiedName~Offline_jobs_run_serially|FullyQualifiedName~Failing_first_serial_job|FullyQualifiedName~AnalysisPackageTests' --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter 'FullyQualifiedName~AnalysisParallelTests|FullyQualifiedName~AnalysisPackageTests|FullyQualifiedName~AnalysisDependencyTests|FullyQualifiedName~AnalysisCapabilityConcurrencyTests|FullyQualifiedName~LocalPublicationTests' --no-restore --verbosity minimal
```

The first three selectors each had an assertion-failing red run, then passed after
their corresponding production change. Combined focused verification passed
12/12; directly affected regressions passed 40/40, exit 0. Test editor discovery
returned no tests, so the existing .NET/VSTest runner was used. Earlier fixture
compilation and permission errors were corrected before the meaningful red runs;
they are not counted as behavioral failures.

Immutable source snapshots are retained locally under
`.sandbox-cache/analysis-performance-evidence`, with these SHA-256 identities:

| Snapshot | SHA-256 |
| --- | --- |
| Package red: `red-source.tgz` | `3f29ffc1faff5e33a32aa03db280f92497dc25a9a42b57af38dd2d1e2b1db372` |
| Extraction red: `extraction-red-source.tgz` | `e0311037a9a2c7d555e3d30cdf8ed14aa5b2921191bf451c7c30a2a1a6b56925` |
| Lifecycle red: `lifecycle-red-source.tgz` | `270b708f7ad639a5f44d8b6433f901236b3faa3b94eae754634812fac9e63352` |
| Final implementation/tests: `green-source.tgz` | `d924567e0a88638a326320a8e69f8766c22608fc4b2804f0a3069cfa561017fb` |

The package regression source is unchanged between its assertion-red and final
green (`d9fe226e037502adb9c6999eb554ae39fa69c3c5a59a84b9efee6d1c5450cc00`).
Extraction and lifecycle regression versions are preserved in their respective
snapshots; subsequent additions did not weaken their assertions.

No JavaScript file was modified, no new test framework or dependency was added,
and CI commands were not changed. Full hosted/local Docker CI and database-backed
publication/embedding qualification were not run for this increment. The focused
local-worker test uses real fixture Git export and local artifact publication,
with a fake extractor/runtime so it does not depend on private feeds or Docker.

## Measured package-reuse optimization

Following the request to run, measure and adjust until speed improves, three real
Kraken analyses were completed on 2026-11-05. All used Podman, `PACKAGES=1`, Git
authentication, the same capability/bindings configuration, offline extraction
and confirmed local publication. PostgreSQL publication and embeddings were
disabled for these controlled runs. This was not a reduced-coverage comparison.

### Observed result

| Run | Total active execution | NuGet acquisition | Verified reuse/copy | Downloaded / already present / unavailable |
| --- | ---: | ---: | ---: | --- |
| Original baseline | 179.842 s | 108.739 s | Not implemented | 197 / 0 / 0 |
| Optimized build, fresh attempt | 171.939 s | 107.021 s | 0.066 s, miss | 197 / 0 / 0 |
| Optimized build, verified reuse | 111.740 s | 12.865 s | 36.824 s, hit | 0 / 197 / 0 |

- Against the original baseline: **68.103 s saved, 37.868% less active time**.
- Against the same optimized build's fresh-attempt control: **60.199 s saved,
  35.012% less active time**.
- Package preparation, including reuse verification and copying, fell from
  approximately 107.1 s to 49.7 s in the same-build comparison. The 36.8 s spent
  verifying/copying retained packages is included, not hidden from the result.
- The supervised optimized runs also recorded whole-process wall times:
  **175.025 s fresh versus 114.658 s warm**. The original baseline's outer wall
  time was not independently recorded, so its comparison uses persisted active
  execution timers consistently.

The cold control means a fresh attempt-owned package directory, not purged machine,
image, DNS or registry caches. Extraction also varied between runs (57.5, 51.8
and 45.8 seconds). Therefore do not attribute every second of total improvement
solely to package copying. The measured package-stage reduction itself is about
57.4 seconds in the same-build comparison.

### Equivalence and publication proof

| Run | Run ID |
| --- | --- |
| Original baseline | `20a854fccc9043cba688606ac0339923` |
| Optimized fresh attempt | `15f68bcdbfe34ed3aa0f7194fc5b84e8` |
| Optimized verified reuse | `139794ac6fb14af6957bc6e5d0b1ddeb` |

A read-only verification of the three run directories checked:

- Every run ended `done`, with only confirmed local-artifact publication effects.
- Each resolved manifest's bytes match its bound SHA-256 journal receipt.
- Source revision, source content identity, feed policy, npm coverage and package
  content identity are equal across all three runs.
- Every staged artifact's bytes match its bound hash, and all **five artifact
  hashes are exactly equal across all three runs**, including their coverage
  diagnostics and configuration identities.
- The optimized control and warm runs have the same frozen runtime identity.
- Warm-run provenance identifies the optimized control as its package source,
  and dependency coverage is `0:197:0`, versus `197:0:0` in the control.

All assertions passed. No source, package coverage, extraction rule or artifact
validation was skipped to obtain the speedup.

### Implementation and safety boundary

[Verified package reuse](../../platform/src/CapabilityExplorer.Cli/AnalysisPackageReuse.cs)
seeds a new attempt's empty package directory from existing retained evidence.
Only a completed run of the same capability with confirmed effects can qualify.
Its source membership/revisions/content, include-tests policy, allowed NuGet
origins, public feed policy and full runtime identity must match. The resolved
manifest is size-bounded, read once, hashed against the completed journal receipt
and parsed from those same bytes. Its recorded package-tree identity must match
the retained files before copying.

Packages are ordinary independent copies into the new attempt, not symlinks or
shared writable trees. Both the new copy and retained source are rehashed after
copying; a mutation during materialization fails rather than silently accepting a
partially seeded tree. Normal NuGet resolution and fetching still execute, so
missing packages can be downloaded and floating versions retain their existing
resolution behavior. Npm installation behavior is unchanged.

There is no new machine-global cache, cache purge or automatic retirement policy.
Reuse only consumes already-retained, workspace-owned attempt evidence; each new
attempt still owns its package files, as before. Existing rollback/evidence data
is not deleted or rewritten. Runtime/source/feed changes cause a cold miss. The
first run after this code change was deliberately cold because old runtime
identities are not accepted as compatible.

Scope limitation: these results establish faster repeated Kraken analysis with
matching inputs. They do not prove a speedup for first-time analysis, changed
sources, different runtimes or npm-authentication failures. The unrelated GitHub
Packages credential and APT lockfile issues remain unresolved.

### Executable verification

Acceptance scenario:
`AnalysisPackageTests.Given_retained_packages_when_another_attempt_starts_then_only_bound_matching_completed_content_is_reused`.
Eight cases cover matching evidence, changed runtime, changed source, changed
feeds, altered manifest bytes, altered package bytes, package symlinks and an
unknown prior run. The matching case also proves writes to the new copy do not
change the retained package source.

Run from the repository root:

```sh
cd platform
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter FullyQualifiedName~Given_retained_packages --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter 'FullyQualifiedName~AnalysisParallelTests|FullyQualifiedName~AnalysisPackageTests|FullyQualifiedName~AnalysisDependencyTests|FullyQualifiedName~AnalysisCapabilityConcurrencyTests|FullyQualifiedName~LocalPublicationTests' --no-restore --verbosity minimal
```

Before implementation the matching case failed with expected `0:1:0`, actual
`1:0:0`; the seven rejection cases already passed. After implementation all eight
passed, and the directly affected regression batch passed **48/48**, exit 0.
The same reuse test source was used for red and green, SHA-256
`d84dc2e4b24d839031e79b91f9c59da86e1850c91cb8285c50bc432b3d50e63b`.

Real-run command, from the repository root, executed once for the original
baseline and twice serially after the optimized build:

```sh
CE_CONTAINER_ENGINE=podman CE_DATABASE= PACKAGES=1 GITHUB_AUTH=git BINDINGS=bindings.json scripts/analyze kraken capabilities.json
```

The initial baseline launch was blocked by the idle development worker's exclusive
lock, not an analysis failure. That idle worker was stopped for the controlled
runs; Explorer was left running. The background analysis worker was restored
after verification with the development PostgreSQL settings. No active or unknown
run was reset or reconciled, and no benchmark request remains queued.

Local source-bound evidence under `.sandbox-cache/analysis-performance-evidence`:

| Snapshot | SHA-256 |
| --- | --- |
| `reuse-red-source.tgz` | `3cbb1df457826987e95fc534b9cc2d0c26a43f4311263dd62bc6ed742ac1b271` |
| `reuse-green-source.tgz` | `3cfbe1793e236dea4d71326a7c897725f3499048a473b56277d7f7adcfcdff11` |

Editor diagnostics and focused build/tests passed. Full Docker CI and
database-backed performance qualification remain unexecuted; no such pass or
database speedup is claimed.

## Second iteration: reuse verification overhead

On 2026-11-05 the next requested iteration split package reuse into initial
verification, copying, destination verification and source re-verification, then
measured a real Kraken cold/warm pair before changing the algorithm.

### Profile and adjustment

The profiled warm run spent 38.949 s in reuse:

| Reuse substage | Profiled control | Optimized warm | Optimized warm repeat |
| --- | ---: | ---: | ---: |
| Initial verification | 20.709 s | 9.647 s | 4.350 s |
| Copying | 7.497 s | 7.798 s | 3.796 s |
| Destination verification | 6.257 s | 10.847 s | 6.444 s |
| Source re-verification | 4.446 s | 3.757 s | 2.992 s |
| **Total reuse** | **38.949 s** | **32.065 s** | **17.593 s** |
| **Total active analysis** | **122.635 s** | **111.415 s** | **101.559 s** |

Verification was the largest initial reuse cost. The code first enumerated and
validated the package tree for the copy list, then enumerated and validated it
again inside the hashing function. The optimization returns the hashing pass's
verified file inventory for copying, removing that duplicate full tree walk.

No hash, link rejection, file-size/entry bound, source matching, runtime matching
or post-copy integrity pass was removed. Copied files remain independent. Normal
package acquisition, all extractor jobs and publication remain enabled.

The first optimized warm run records one initial inventory taking 3.926 s; its
repeat records one taking 2.354 s. Initial inventory is nested inside verification
and must not be added again when totaling the stages.

The two optimized warm runs averaged **106.487 s**, an observed **13.167% reduction**
against this iteration's 122.635 s profiled control. Whole-process wall times were
128.760 s for the control and 118.219/106.706 s for the optimized warm runs.

This is one control and two optimized observations, not a randomized benchmark.
Filesystem/cache warmth clearly contributes to variation, and the earlier
iteration's 111.740 s warm run was already close to the first optimized result.
Do not attribute the entire elapsed-time reduction to the removed walk or promise
a universal 13% improvement. The deterministic change is one initial inventory
instead of two per accepted candidate; the measured warm range is 101.6-111.4 s.

### Real-run identities and equivalence

| Run | Run ID | Active execution |
| --- | --- | ---: |
| Profiling build, fresh attempt | `d32f6edb0a4a4cf1bd73267ed5d0a09a` | 199.247 s |
| Profiling build, verified reuse | `af7916c175f24c7590836e84e5d2bc9e` | 122.635 s |
| Single-inventory build, fresh attempt | `6d1f61a203e44356ae7e22f7c466b8a7` | 153.965 s |
| Single-inventory build, verified reuse | `e7ef2f73f80e4fd5bee7b9cdd6ea4af8` | 111.415 s |
| Single-inventory build, warm repeat | `28bd5e617811430fb3414535903fb78d` | 101.559 s |

Both cold runs intentionally miss because the runtime identity changed. Their
different download timings are not evidence of this warm-path optimization.

Verification compared these five runs with the prior iteration's warm run
`139794ac6fb14af6957bc6e5d0b1ddeb`. Every resolved-manifest hash and staged artifact
hash matched its journal binding. All six runs had identical source revision,
source content identity, package content identity, feed policy and npm coverage;
all **five artifact files were byte-for-byte identical**. Every run ended `done`
with confirmed local publication. The two optimized warm runs had `0:197:0`
NuGet coverage and exactly one initial-inventory telemetry record each.

The background worker was stopped while idle for these controlled runs, then
restored using the development database settings. Explorer was left running.
There was no journal reset, cache purge or target lockfile/credential change.

### Tests and source evidence

The existing reuse scenario now requires all four substage timing records. It
first failed because those records were absent, then passed after instrumentation.
For the algorithmic optimization, a diagnostic-only inventory counter was added
without changing the original two-pass algorithm. The matching scenario then
failed `Assert.Single`: **two** initial-inventory records were present. Returning
the hashed inventory for copying made the same assertion pass with **one** record.
The seven existing invalid-cache cases remained passing.

Commands from `platform`, unchanged selectors:

```sh
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter FullyQualifiedName~Given_retained_packages --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter 'FullyQualifiedName~AnalysisParallelTests|FullyQualifiedName~AnalysisPackageTests|FullyQualifiedName~AnalysisDependencyTests|FullyQualifiedName~AnalysisCapabilityConcurrencyTests|FullyQualifiedName~LocalPublicationTests' --no-restore --verbosity minimal
```

Focused profiling/reuse verification passed 8/8; final directly affected
regressions passed **48/48**, exit 0. No wall-clock threshold was used as a flaky
unit-test substitute for the deterministic duplicate-work assertion. The actual
performance comparison uses the real package-enabled command recorded above.

Source snapshots under `.sandbox-cache/analysis-performance-evidence`:

| Snapshot | SHA-256 |
| --- | --- |
| Missing-substage assertion red: `reuse-profile-red-source.tgz` | `02109df47094bd13abace4fcf0bf158667e7fa25139e2bc27a84ee4ab307c97e` |
| Two-inventory assertion red: `inventory-red-source.tgz` | `d13283e791fb8d80d71cde4e57f50c7cdeb58cff8796a8c14971eba94cd56454` |
| Final implementation: `inventory-green-source.tgz` | `46af22d8d3b302e2be5d7aab1460c89d9f81557b29ffa905ee3454906ee9099c` |

The duplicate-inventory regression source was unchanged between its assertion-red
and final green, SHA-256
`d96fc7dd084751e9625dcc581eac0bf297e8690d308fcc5f8aa98dbe4367253e`.
The profiling runs predate the diagnostic inventory counter; their algorithm is
unchanged from the measured two-pass implementation, but they are not claimed
to use the exact same instrumented binary as the assertion-red snapshot.

No JavaScript, dependency, CI-command, concurrency or cache-retention policy was
changed. Full Docker CI and database/embedding performance remain unmeasured.

## Third iteration: C# extraction

On 2026-11-05 the requested extraction investigation added safe stage profiling,
measured the real Kraken workload, and optimized its dominant package/reference
setup path. It did not assume the initially suspected rule scans or duplicate
compiler diagnostic calls were the largest cost.

### Profile and measured change

| C# stage | Detailed control | Optimized fresh attempt | Optimized warm |
| --- | ---: | ---: | ---: |
| Package/reference preparation | 25.827 s | 24.192 s | 20.106 s |
| Source parsing | 2.246 s | 0.933 s | 1.056 s |
| Assembly conflict resolution | 1.958 s | 1.486 s | 1.716 s |
| Reference fingerprints | 0.579 s | 0.642 s | 0.726 s |
| **Compilation setup, inclusive** | **30.831 s** | **27.465 s** | **23.813 s** |
| Compiler diagnostics | 1.084 s | 0.947 s | 1.008 s |
| Syntax/semantic walk | 1.441 s | 1.677 s | 1.347 s |
| Route rules | 7.445 s | 8.589 s | 7.209 s |
| **Whole C# job** | **48.460 s** | **46.383 s** | **42.325 s** |

The initial broad profile had already identified compilation setup at 30.666 s
of a 52.390 s C# job. The detailed control narrowed that to package/reference
preparation, rather than diagnostics (about one second), calculations (about
0.02 seconds), serialization or assembly-conflict handling.

The two optimized C# jobs averaged **44.354 s**, an observed **8.473% reduction**
against the detailed 48.460 s control. The warm observation alone was about 12.7%
lower. These are a small number of elapsed-time observations with cache/filesystem
variation, not a universal or statistically isolated speedup guarantee.

Compilation setup includes the reference, parsing and fingerprint stages above;
do not sum it with its children. The `cs.calculations` measurement is similarly
nested inside the semantic walk. The whole job also includes sandbox/runtime
startup, validation and writing, beyond the library's `cs.total` timer.

### The optimization

[Package path validation](../../platform/src/CapabilityExplorer.Extractors.CSharp/PackageSafety.cs)
previously called `File.Exists`, then `Directory.Exists`, then `File.GetAttributes`
for each existing directory ancestor. Existing package paths therefore required
up to three filesystem inspections per ancestor, multiplied across package
resolution and projects. On the host/VM shared filesystem this is repeated I/O,
not useful additional validation.

It now performs one attribute lookup per ancestor. Reparse-point rejection and
the cache-root containment boundary remain intact. Missing files/directories
continue to permit subsequent safe acquisition; other I/O/access errors are not
silently treated as successful validation. Tests cover existing and dangling
linked ancestors, missing package paths and path escape, alongside the full
package-security regression suite.

No package tree safety walk, fingerprint, extraction rule, coverage diagnostic,
source selection or dependency-resolution policy was removed. Shared repository
indexes, semantic-model caching, diagnostics reuse and unchanged-artifact caching
were not implemented in this increment: the measured setup bottleneck was tackled
first. Route rules remain a visible secondary target at roughly 7-9 seconds.

### Profiling contract

[Extraction timing](../../platform/src/CapabilityExplorer.Extractors.CSharp/ExtractionTiming.cs)
uses monotonic stopwatches. The CLI emits `-- cs-timing: ` JSON records with
schema `ce.csharp.timing.v1`, fixed stage names, optional numeric unit indexes,
elapsed milliseconds and completion/failure outcome. Timing records contain no
source text, source paths or credentials and stay outside artifact bytes.

Measured stages cover discovery, compilation setup, package/reference preparation,
parsing, assembly resolution, reference fingerprints, diagnostics, semantic walks,
calculation analysis, each rule family, input fingerprints, serialization,
validation and writing. The host already retains child stdout in its private run
log, so these records survive real analyses without changing artifact schemas.

### Source-bound real-run verification

| Run | Run ID | Whole C# job | Active analysis total |
| --- | --- | ---: | ---: |
| Initial broad profile | `96b9d33d995a49de95f04736cbe650cb` | 52.390 s | 152.829 s |
| Detailed setup control | `207d6d65563c4688a5b9cdd0b604eace` | 48.460 s | 146.522 s |
| Optimized fresh attempt | `a9844770d66c4733907dc5a0930e35a0` | 46.383 s | 137.002 s |
| Optimized warm | `6a63d7530fb64d1ca86dcbd1d1920c44` | 42.325 s | 101.259 s |

All four runs use the same source revision/content, package content, feed policy
and npm coverage, with confirmed local publication. The repository had changed
since the older iteration's run `28bd5e617811430fb3414535903fb78d`; that mismatch
was detected and that older run was excluded from this iteration's comparisons.
The end-to-end cold/warm difference includes package reuse and is not credited
solely to faster extraction.

Changing extractor code intentionally changes its recorded build-dependent
`header.configHash`. Verification therefore checked every bound manifest and
artifact hash first, then compared all parsed artifact records while normalizing
**only `header.configHash`**. Every node, edge, calculation, diagnostic, evidence
record, input identity, count and coverage record in all five artifacts matched
across the four runs. No fact or evidence field was excluded. The two optimized
runs, using the same build, were additionally byte-for-byte identical.

Both optimized runs retained full package acquisition and extraction. The warm
run reports zero downloads, 197 already present and zero unavailable packages.
PostgreSQL and embeddings were disabled for controlled performance measurements.

### Executable verification and source identities

The CLI acceptance test
`ProjectReferencesTests.Given_project_references_when_the_cli_extracts_then_stage_timings_preserve_artifact_bytes`
first failed because timing records were absent, after its library-versus-CLI
artifact-byte checks passed. It then passed with structured timing records.
The later deeper-setup assertion failed with expected two records, actual zero,
before the corresponding stage scopes were added.

The optimization test
`PackageSecurityTests.Given_an_existing_package_path_when_checked_then_each_ancestor_requires_one_filesystem_inspection`
used a diagnostic callback to count operations in the unchanged algorithm. It
failed with **expected 3, actual 9** for three ancestors. After the change it
passed with three inspections. The test source remained unchanged; existing and
dangling link rejection and missing-path cases also passed.

Commands from `platform`:

```sh
dotnet test tests/CapabilityExplorer.Extractors.Tests/CapabilityExplorer.Extractors.Tests.csproj --filter FullyQualifiedName~Given_project_references_when_the_cli_extracts --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Extractors.Tests/CapabilityExplorer.Extractors.Tests.csproj --filter 'FullyQualifiedName~Given_an_existing_package_path|FullyQualifiedName~Given_a_linked_package_ancestor|FullyQualifiedName~Given_a_missing_package_path' --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Extractors.Tests/CapabilityExplorer.Extractors.Tests.csproj --filter 'FullyQualifiedName~PackageSecurityTests|FullyQualifiedName~ProjectReferencesTests' --no-restore --verbosity minimal
dotnet test tests/CapabilityExplorer.Extractors.Tests/CapabilityExplorer.Extractors.Tests.csproj --no-restore --verbosity minimal
```

The final focused batch passed 23/23; the **full extractor suite passed 221/221**,
exit 0. An initial missing test-helper compilation error was fixed before the
meaningful red run; it is not counted as behavioral red evidence. Test editor
discovery found no tests, so the existing .NET/VSTest runner was used.

Local snapshots under `.sandbox-cache/analysis-performance-evidence`:

| Snapshot | SHA-256 |
| --- | --- |
| Broad profiling red: `csharp-profile-red-source.tgz` | `06c729c487b992310c79f3a19e2c6e291c1b95b92e46bec26bf8c3d4ddfc8956` |
| Setup profiling red: `csharp-setup-red-source.tgz` | `a9c2c050beb31e5158f43c24b2c6cbc4ee62900523a302dda746ea50dadf4278` |
| Inspection-count red: `csharp-inspection-red-source.tgz` | `22e0dc57d6a545e0a0ec898a023bba7f4cf108bf8cafce3eeb6062bce6d51a17` |
| Final source: `csharp-green-source.tgz` | `75c3ca8f3e640cf6a8160382fda5841214ae7680eea44e7109850c8c3210216e` |

The package-security test source SHA-256 is unchanged between its assertion-red
and final green:
`88bbc88ffb129f58d4642f41468f02fde28e9fec3f3a3d270b3b8c50c01a1c33`.

The idle worker was stopped for controlled runs and restored afterward; Explorer
remained running. No active run was interrupted or reconciled. Full Docker CI,
other private capabilities and database/embedding performance were not qualified
in this increment. No JavaScript, dependency or concurrency policy was changed.