# Native Docker and Podman interchangeability

Date: 2026-11-04. Base: `89196a140c5c9846566caed92c7653ebf69ae1f2`.
Scope: native development and host-managed analysis, not nested sandbox CI.

## Accepted Behavior

The operator requested the same native commands and UI for Docker and Podman,
then explicitly approved removing concurrency after live macOS Podman showed
intermittent filesystem read failures under concurrent extractors.

- `make dev` starts the same trusted host worker with either selected engine.
- `analyze`, `analyze-all` and `analyze-worker` use the shared runtime adapter.
- Selection is `auto`, `docker` or `podman`. Auto selects an installed
  executable; failures after selection never fall back to another engine.
- Before admission/queue consumption, the worker captures one qualified local
  runtime. Sandboxes and exact-label cleanup retain that captured executable,
  endpoint/identity arguments and runtime fingerprint, refusing drift.
- The runtime snapshot remains host-only. Offline extraction retains network
  isolation, non-root UID/GID, read-only root, dropped capabilities,
  no-new-privileges and explicit read-only source/runtime mounts.
- Both engines execute one capability and one extractor at a time. New
  settings record one worker/job; old frozen parallel settings remain immutable
  but are executed serially. Invalid old option values are still rejected.
- Uncertain execution is still unknown; label cleanup is not termination proof.
- Full sandbox CI, Testcontainers and legacy security collection remain
  Docker-only and fail closed. No Docker-compatible Podman alias is introduced.

Operating guidance is in [the lifecycle guide](../local-analysis-lifecycle.md),
[the Podman profile](../podman-core.md) and
[MADR-0022](../adr/0022-local-analysis-lifecycle.md).

## Scenario And Test Mapping

| Acceptance criterion | Executable scenario | Observed pre-change assertion |
| --- | --- | --- |
| Both wrappers reach the host CLI | `Given_a_native_engine_choice_when_the_worker_starts_then_both_engines_reach_the_same_host_cli` | Podman refused while Docker passed |
| Runtime preflight precedes admission | `Given_a_native_engine_when_analysis_starts_then_runtime_is_frozen_before_admission` | Probe marker absent for both engines |
| Preserve inherited frozen session | Same scenario with inherited snapshot assertion | Expected snapshot, received empty environment value |
| Native development starts the worker | `native make dev starts the host worker with docker/podman` | Docker passed; Podman worker never started |
| Native analysis goals are accepted | `Make permits native service and analysis goals with Podman but keeps sandbox dev refused` | Analysis goal refused by Make allowlist |
| Snapshot restore never follows changed aliases or backends | `Given qualified ... when captured and restored then only its frozen command runs` and drift cases | Six initial shell snapshot cases failed before implementation |
| Offline source has explicit read-only mount | `Given_offline_extraction_when_a_source_is_selected_then_the_host_exports_it_as_a_readonly_mount` | Expected owned source, received empty mount metadata |
| Input overlays are immutable for both engines | `docker/podman frozen extraction mounts source and runtime trees read-only` | Missing `/work/...:ro` source overlay |
| Runtime helper changes invalidate identity | `Given_runtime_bridge_code_when_it_changes_then_the_analysis_runtime_identity_changes` | Runtime hashes unchanged for both changed bridge files |
| Extractors run serially for old settings | `Offline_jobs_run_serially_and_merge_isolated_outputs_even_for_older_parallel_settings` | Peak activity reached 2/4 rather than 1 |
| Capabilities run serially for old settings | `Background_scheduler_runs_serially_even_for_older_parallel_worker_settings` | Started count reached 2/3 rather than 1 |
| New admission records serial settings | `tests/native-analysis-runtime.live.ts --live` | Completed isolated run recorded 2 instead of 1 |

The scenarios live in
[AnalysisParallelTests](../../platform/tests/CapabilityExplorer.Store.Tests/AnalysisParallelTests.cs),
[scheduler tests](../../platform/tests/CapabilityExplorer.Store.Tests/AnalysisCapabilityConcurrencyTests.cs),
[engine core tests](../../tests/container-engine-core.test.mts),
[snapshot tests](../../tests/analysis-runtime-shell.test.ts) and
[the opt-in live harness](../../tests/native-analysis-runtime.live.ts).
Legacy overlap-specific fixtures were intentionally revised for the newly
authorized serial contract, retaining failure, cancellation, no-merge,
unknown-state, unique-attempt and queued-work-preservation assertions.

Tooling/analyzer failures were not counted as red evidence. Test fixtures also
needed recursive Make overrides, an explicit Unix guard and isolation from
inherited `MAKEFLAGS`. An old bare-`keep-id` assertion was updated to require
the stronger explicit UID/GID mapping; no hardening assertion was dropped.

## Live Podman Evidence

Opt-in command:

```sh
CE_CONTAINER_ENGINE=podman node --experimental-strip-types tests/native-analysis-runtime.live.ts --live
```

The final source-bound probe passed in
`.sandbox-cache/runtime-analysis-probes/1eceb8f0-d9ec-4c65-924e-43deb4a1b56e`.
Summary: `.sandbox-cache/runtime-analysis-podman.json`.
macOS host: Node `26.10.0`, .NET SDK `10.0.401`, TypeScript `7.0.2`;
Podman client `6.0.2`, server `6.1.2`. Images retain the repository's pinned
multi-platform digests; no image override was used.

- Single public-fixture analysis: exit 0, approximately 26.6 seconds, run
  `273736cc32c24a4085cc2f7441fa9e2b`.
- Aggregate analysis: exit 0, approximately 45.2 seconds, runs
  `151292b4c63644629e74c83ef81e6d27` and
  `cff38ab23e044735890a8f660ce859e2`.
- Four canonical artifacts per capability, validated through real `ce validate`.
- All three runs completed execution and local publication with confirmed
  effects; aggregate receipts bind the new run/attempt/input/artifact hashes.
- Final exact-owned-label listings were empty. This is cleanup evidence for
  these successful runs, not permission to clear any unknown execution.

Earlier isolated probes are retained. The first exposed Podman's unmapped
macOS GID 20; explicit `keep-id:uid=<host>,gid=<host>` fixed it. Later concurrent
probes showed C# permission and Node module-resolution failures while files
remained present and readable on the host. Explicit read-only mounts did not
alone eliminate the concurrency failure. Serial execution passed subsequent
single/aggregate probes. Kernel audit access was unavailable; no SELinux policy
or host file permissions were weakened, and no kernel root cause is claimed.

The operator explicitly approved one evidence exception: the pre-fix UID/GID
unit-test tool output was lost to interruption. Its source snapshots and the
original live mapping error are retained; final mapping assertions and all
runtime/live gates pass. This missing historical unit output was not recreated.

All live probes use copied compiled/extractor inputs and isolated journals,
public local fixtures, empty host-feed settings, `GITHUB_AUTH=none`, packages
disabled, no database and no models. Existing ASQ and weekly-sales production
requests remain queued with no new attempt. No production recovery/reset,
source cleanup, publication or queue consumption was performed.

## Gates And Limits

```sh
make EXECUTION=native CE_CONTAINER_ENGINE=podman test-container-engine-core
cd platform
TMPDIR=/private/tmp dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter 'FullyQualifiedName~AnalysisParallelTests|FullyQualifiedName~AnalysisCapabilityConcurrencyTests|FullyQualifiedName~AnalysisDependencyTests|FullyQualifiedName~AnalysisPackageTests' --verbosity minimal
TMPDIR=/private/tmp dotnet test tests/CapabilityExplorer.Capabilities.Tests/CapabilityExplorer.Capabilities.Tests.csproj --verbosity minimal
```

Results: 86/86 engine/security scenarios plus strict TypeScript 7 checking;
32/32 focused worker scenarios; 134/134 capability/journal tests; no skips.

```sh
CE_CONTAINER_ENGINE=docker node scripts/ci/check-frugal-ci.mjs
CE_CONTAINER_ENGINE=docker node --test tests/frugal-ci.test.mjs tests/fixture-concurrency.test.mjs
node scripts/decisions/check.mjs all
CE_CONTAINER_ENGINE=docker make EXECUTION=sandbox ci
```

Results: reviewed plan parity passes with 57 commands; 16/16 CI-policy tests;
29/29 decision-ledger tests. The plan moves shared compiler preparation earlier
once, retains existing gates and adds explicit strict checking for the migrated
runtime tooling. ADR-0022 revision receipts and checked projections were
refreshed without changing decision status, authority or relationships.

Full canonical CI stops because the Docker executable is absent. Docker
behavior is fixture-tested but not newly live-qualified on this host. Private
package feeds/fetching, database publication, model enrichment and nested
Testcontainers workflows are not covered by the live Podman receipt.

Migration: `tests/container-engine-core.test.mjs` -> `.mts`, preserving ESM and
existing coverage. Generated JavaScript mock executables and unchanged legacy
tools remain intentional fixtures/untouched sources. The existing locked TS7
compiler and download/dependency caches are shared and retained; no dependency
upgrade or cache purge occurred, and reclaimed space is zero. Retained failed
probe workspaces are audit/rollback evidence, not reusable success receipts.

## Immutable Source Snapshots

Final sources retained using `git hash-object -w` (not commits):

| Source | Blob |
| --- | --- |
| AnalysisEngineRuntime.cs | 190c65400aa2fedacb91814d9f13582f2ba06150 |
| AnalysisProcess.cs | eaf4e7883efb28c47252885ab9b4774d6c048f59 |
| AnalysisWorkerCommand.cs | e87d2c7b1507be0942c35c4038dd705ec825dd42 |
| AnalysisCapabilityScheduler.cs | 264763edf4f7dcb06b843eafdfe7c39521e89220 |
| AnalysisParallelExtraction.cs | 18e0e1ebc93f36c587c3c53ce6cd93dadc2cfc3c |
| AnalysisPreparation.cs | 6fe5842a3f661fa5c221a670559a01c61aeced65 |
| AnalysisParallelTests.cs | 7a7aa3b6e6c0ece10bdc8cc8c331be10bf006b81 |
| AnalysisCapabilityConcurrencyTests.cs | be93d0f59dccccf98fd7eef01252248528c5ca75 |
| scripts/container-engine | cdb86ba40b1746f6e4fd0395efff270fd5211590 |
| scripts/lib/container-engine.sh | 68a7083cd299ad871423dc4538df27e26280ec73 |
| scripts/lib/analysis-runtime.ts | 78b9e2a7ef6bef64cc77a262c0a1fd824efecd35 |
| scripts/sandbox | ea6f82c66b9777bc5df278acb441495ff0afe315 |
| tests/container-engine-core.test.mts | 793fc3cf9cb46afe8fb7df84eedc80d648f94289 |
| tests/analysis-runtime-shell.test.ts | 553ece00072358c51d2b8715b958859711346777 |
| tests/native-analysis-runtime.live.ts | 73d14e54d0936139e716d326adf64840985ad350 |

Key red snapshots: worker launcher test `96d46105e6285018e0312f3a6ef9b78d7131d50e`
and wrapper `43dec5ee49af4d02d244858dac91a719b825c096`; preflight test
`d1411cc6fa3ce872f262132df54d21fd46a30b58` and worker
`b59831836242c5a8567b5fd65572eabdb45ba6ae`; native dev test
`412ea37aa309b712e478442a8a8121ba41dca4d7` and native Make
`c8bab3efc1c8fc39b3581e768e6ca5783488d0ca`; serial extraction test
`bbd208a6283fce57d3bbada5d4655559886388e6` and extraction dispatcher
`88d615d403840a5f7fb17f4d980cb2127028cded`; serial scheduler test
`35218e103837f442459536794bbc38bb3cbd7335` and dispatcher
`8e3be9c30eac71cda3c09a607db441afdfc9e00c`; runtime identity test
`b85b44f453182b3c56075dc1baa5ad8ba6ae30ed` and preparation
`47fa9e799e0ada5114e627ce9492fdea0a9c8346`; inherited-session test
`7a7aa3b6e6c0ece10bdc8cc8c331be10bf006b81` and bridge
`9a04d796a7cb07198e51129d545b2f3c7d7f15c9`.