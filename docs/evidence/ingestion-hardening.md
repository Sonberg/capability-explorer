# Capability ingestion hardening

Date: 2026-11-04. Base: `cc0e96ecffe78956cf992bb98fd942fd3517b006`.
Status: focused native validation passed; full production qualification blocked.

## Retained Changes

- Completion-driven list/status polling, 15-second read deadlines, automatic
  retry, preserved last successful list, and suspended background-tab polling.
- One batch status request instead of one request per capability. Global
  reconciliation checks, per-run identity, bounded logs and path privacy remain.
- Pending Analyze/Stop guards and version checks against stale polling results.
- Pending removal disables competing mutations; mobile action controls wrap.
- Interrupted-attempt diagnostics identify the capability, run and execution or
  publication boundary instead of the previous opaque global message.
- Native and sandbox Make gates include TypeScript 7 client/test checks and the
  browser suite. The reviewed plan preserves all 53 existing commands and adds
  three checks, for 56 total.

## Acceptance Scenarios

The executable Given/When/Then names in
[the browser suite](../../tests/capability-ingestion.browser.ts) are the
acceptance scenarios. They cover:

| Criterion | Executable name prefix | Observed pre-fix failure |
| --- | --- | --- |
| Slow requests do not overlap | Given slow ingestion status | Five status reads rather than two |
| Refresh failure retains data and recovers | Given a transient list failure | Capability heading disappeared |
| Hidden tabs resume polling | Given a background tab | Zero resumed list reads rather than one |
| Admission is not duplicated | Given a pending analysis admission | Two POSTs rather than one |
| Older reads cannot erase new admission | Given a stale status response | Queued run disappeared |
| Status errors recover without reload | Given an unavailable status endpoint | Already passed after initial polling change |
| Blocked status does not reload metadata repeatedly | Given a blocked terminal run | Already passed after initial polling change |
| Invalid input and partial creation remain honest | Given invalid repositories | Already-covered behavior, passed on first run |
| Hung reads time out and recover | Given a hung capability list / analysis status | No timeout reported for either request |
| Status traffic does not scale per capability | Given twenty capabilities | Twenty individual reads rather than zero |
| All controls fit narrow screens | Given a narrow viewport | 453px content in a 390px viewport |
| Removal excludes competing mutations | Given pending capability removal | Analyze remained enabled during DELETE |

[AnalysisApiTests](../../platform/tests/CapabilityExplorer.Explorer.Tests/AnalysisApiTests.cs)
adds `Given_multiple_capabilities_when_status_is_batched_then_identities_and_global_safety_blocks_are_preserved`
for healthy, legacy and malformed journals. Red: missing API returned
`text/html` instead of `application/json`. Green: all three cases pass.

[AnalysisRunTests](../../platform/tests/CapabilityExplorer.Capabilities.Tests/AnalysisRunTests.cs)
adds `Given_an_interrupted_attempt_when_recovery_is_required_then_the_blocker_identifies_the_exact_run_and_boundary`.
Red: both execution/publication diagnostics omitted the run ID. Green: both
identify it, disclose no host path, retain unknown evidence and refuse dispatch.

## Security Correction

An intermediate change incorrectly treated successful exact-label container
cleanup plus drained pipes as authoritative termination proof. Fake-runtime
tests initially passed, but they do not prove the real Docker daemon boundary.
[MADR-0022](../adr/0022-local-analysis-lifecycle.md) explicitly warns that late
engine requests can outlive a controller. That implementation was withdrawn;
`AnalysisProcess` retains its original conservative behavior.

The added
[worker tests](../../platform/tests/CapabilityExplorer.Store.Tests/AnalysisParallelTests.cs)
now require unknown execution even when label cleanup reports success, and
verify a real detached Unix descendant cannot be reported stopped. The stronger
cleanup-only assertion failed against the intermediate implementation and
passed after withdrawal. This is a corrected acceptance contract, not evidence
that automated reconciliation was implemented. Existing uncertainty and
capability-isolation fixtures were restored unchanged.

## Source Identities

Immutable local Git blobs were retained with `git hash-object -w`; these are
source snapshots, not commits or reusable test-success receipts.

| Source before operator retry | Git blob |
| --- | --- |
| tests/capability-ingestion.browser.ts | fe95e7fe1c190598f28abb0e8752fdbf3d248e22 |
| client/src/views/Capabilities.tsx | a1a5b778b06868d002b5ba038f76b03b37af26ae |
| client/src/state.tsx | 862c10074098c98ae710f1419ec9260d6b731900 |
| AnalysisParallelTests.cs | a18db64375c4ead713132d62895bde50ff40e2b2 |
| FileRunStore.cs | a4daa2ab8e8e71f3dadedfb6c804af97dc468fd6 |
| AnalysisJob.cs | 8701f87e45f13850d4ffd70c617f19d26aa56b03 |
| ExplorerApi.cs | 3b731fa48b589c747ce7f14ecfb6accecc07322a |
| AnalysisApiTests.cs | 1c56f975c05f623c1355565b7033861faa2baa11 |
| AnalysisRunTests.cs | 5c379086aeb761dcdecd6d87fcd6be751168af87 |

Red snapshots: recovery/duplicate tests `05e29419f5e46a04670e40d6d4c35006e36508ce`,
Capabilities `a5b6f3ddb59e5885f78c3f5f6cead8a3eabdfd9e`, state
`d07c9a6951bd69aa73995db94be6fc694035bf40`; stale-response test
`430faea884ecb48eb6d998c1f0c6b4e0d6403003`, Capabilities
`36b0556ccb653f4352469f2c92de88edb7641b91`; timeout test
`6961a58cd23bc2b559aadd83e34ad3788712654b`, Capabilities
`ef2a0616b7f53e83e3b706d160accc2d593c2db8`, state
`087e3097d68bffd5534b4bf1ad17a90318643401`; batch request-count test
`e9c48bba19048ed74c5af30c161cc6d920045ccb`, Capabilities
`46bc096f88024749bbedcb79d4631687aa1ddd62`; mobile test
`da68b1f2d8d2dbefd1155e2fb08bb0ad2cbb04c1`, Capabilities
`7e70c77cb1033c11c33ec89f97081648986e6faf`; removal test
`fe95e7fe1c190598f28abb0e8752fdbf3d248e22`, Capabilities
`21cea41373d392833341c7e690a9c01afa2dbf3a`.

API red sources: test `1c56f975c05f623c1355565b7033861faa2baa11`,
AnalysisJob `b00d9b7b2fc6d48889c5c1d9600e4c6f5352f58c`, ExplorerApi
`d87282c7bd3364b7256558adc39056e4aabb1d71`, FileRunStore
`6bb54f80164b2ae33d63d18af6cd41db8a529e34`.
Diagnostic red sources: final AnalysisRunTests blob above, FileRunStore
`539cf15b5b926fd737b8a70dab55103fa9813103`.
Corrected security-contract red: test `350ffa27a1e8baede5824ae4168ab3fdf97d1a27`,
intermediate AnalysisProcess `a57408634b064bad7d602f9d6e18061bd1880742`.

Initial polling red/green identities were SHA-256 only: test
`4977cdd80d96ff669d681bf2ee92bf59a4ea4c844057cd0d4a932f38c31d4d7f`,
production red `693922339acf0d913065e70982a319e4807994a7539c944e6494893d56312607`,
green `e2d6807c038dc64d4dc01cec3b8ad54dba9b50ed16a3f183d52cfd3516670b65`.
No immutable initial test blob was retained before the first production edit;
that evidence limitation is not reconstructed or relabelled as complete.

Test-harness corrections: CommonJS Playwright import, explicit API content-type
assertion, metadata baseline after React development startup, and awaiting the
actual retry response before counting it. Import/cwd/JSON parse failures were
not counted as meaningful red evidence. Read routes moved to batch fixtures
without weakening overlap, timeout or stale-response assertions.

## Commands And Results

Node `26.10.0`, .NET SDK `10.0.401`, TypeScript `7.0.2`, Playwright `1.64.1`.
Paths below are relative to the repository unless `cd platform` is stated.

```sh
node --experimental-strip-types --test tests/capability-ingestion.browser.ts
CE_CONTAINER_ENGINE=docker make EXECUTION=native test-capability-ingestion-browser
```

Final: 13/13 browser scenarios; locked TypeScript 7 checks pass for client and
test. Development iterations used the same command with `--test-name-pattern`
selectors `hung .* request`, `twenty capabilities`, `narrow viewport` and
`pending capability removal`. Each reproduced its listed assertion before the
corresponding production change and passed afterward.

```sh
cd platform
TMPDIR=/private/tmp dotnet test tests/CapabilityExplorer.Capabilities.Tests/CapabilityExplorer.Capabilities.Tests.csproj --verbosity minimal
TMPDIR=/private/tmp dotnet test tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --verbosity minimal
TMPDIR=/private/tmp dotnet test tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter 'FullyQualifiedName~AnalysisParallelTests|FullyQualifiedName~AnalysisCapabilityConcurrencyTests|FullyQualifiedName~AnalysisDependencyTests|FullyQualifiedName~AnalysisPackageTests' --verbosity minimal
```

Final: 134/134 capability, 293/293 Explorer, 23/23 focused worker tests, no skips.
An initial broad native run failed on macOS `/var` temporary-path aliases;
using the real `/private/tmp` preserved security assertions and passed.
Focused diagnostic red/green used
`--filter FullyQualifiedName~Given_an_interrupted_attempt_when_recovery_is_required`;
API red used `--filter FullyQualifiedName~Given_multiple_capabilities_when_status_is_batched`.

```sh
CE_CONTAINER_ENGINE=docker node scripts/ci/check-frugal-ci.mjs
CE_CONTAINER_ENGINE=docker node --test tests/frugal-ci.test.mjs tests/fixture-concurrency.test.mjs
CE_CONTAINER_ENGINE=docker make EXECUTION=sandbox ci
```

Plan parity passes with 56 commands; policy/concurrency tests pass 16/16.
Full gate exits 2: `container-engine: selected docker executable is missing`.
No Podman substitution or gate omission was used. Hosted, full database-store,
container-backed extraction and complete security scanner qualification remain
unexecuted. Client `npm run build` and Explorer build pass; existing lazy-chunk
size warnings remain, initial app bundle approximately 486kB.

## Live Workspace Limits Before Operator Approval

The real batch API returned seven capability statuses in one response; one
read measured 95ms, not a statistically qualified latency benchmark. The
deterministic 20-capability browser fixture proves request-count reduction,
not extraction-throughput improvement. Read-only desktop smoke had no browser
console errors or overflow. A 390px check found the action overflow and led to
the passing regression above.

The existing ASQ run `8a76d0a4eb5c4a69a2bc26d05783d0bd`, attempt
`afcff80dd818419d8a65c4cd3bc4343b`, remains unknown, cancelled, without a resolved
manifest or recorded publication effects. The correctly computed label is
`ce-analyze=df647bdd1e7b2d3d-8a76d0a4eb5c4a69a2bc26d05783d0bd-afcff80dd818419d8a65c4cd3bc4343b`.
A read-only current-Podman listing found no matching containers; this does not
verify the original frozen endpoint or establish absence of late work. An
agent's earlier label containing literal `run`/`attempt` prefixes was incorrect
and its negative lookup was not used as evidence.

No unknown journal was reset, archived, retried or marked successful. No real
capability was added/removed and no analysis was queued during browser tests.
Production acceptance requires a qualified engine run and evidence-preserving
operator reconciliation or a separately designed authoritative execution
receipt protocol. Merely changing the warning text does not unblock this run.

## Explicitly Approved Operator Retry

The operator subsequently selected **"Yes, recover ASQ and queue a fresh run"**
after acknowledgement that the original engine endpoint and execution
termination cannot be verified. This is a scoped operator release, not an
automatic termination claim. It supersedes the earlier unchanged-live-state
paragraph only for this exact ASQ attempt.

The new **Retry interrupted run** control asks for explicit confirmation and
posts the observed `runId`, `attemptId` and
`acknowledgeUnverifiedExecution:true` to `/api/capabilities/{id}/retry`.
The journal operation requires exclusive worker ownership and metadata lock,
matching immutable inputs, cancelled unknown/interrupted execution, and no
resolved manifest, publication effects or completed execution. Other cases
remain refused. A matching prior decision can resume a directory move; the
decision is never rewritten. Retirement and fresh admission are separate
operations; admission failure does not roll back or falsify archived evidence.
The owner-approved exception is documented in
[MADR-0022](../adr/0022-local-analysis-lifecycle.md) and the
[lifecycle guide](../local-analysis-lifecycle.md).

Real API response: HTTP 202, new run `ad053e3a5a7646b68881a5438f70f80f`,
`state:queued`, `blocked:false`. The old run remains `unknown` under
`.sandbox-cache/requests/reconciled/8a76d0a4eb5c4a69a2bc26d05783d0bd/evidence`.
The adjacent decision records `operator-released-for-retry` and
`terminationVerified:false`. All 6,487 entries, 5,611 files, 151,519,663 file
bytes, file/directory modes and symlink targets matched the pre-release
inventory. Ignored reports are in `.sandbox-cache/ingestion-retry-evidence/`.
All live capability status responses were unblocked after retirement.

Executable API scenarios cover acknowledgement refusal, unchanged archived
bytes, new queued identity, stale attempts, non-cancelled runs, publication
refusal and resuming a previously saved decision. Initial missing endpoint:
five meaningful HTTP status assertion failures. Interrupted retirement:
expected Accepted, observed Conflict; fixed without deleting the decision.
The complete affected API slice passed 30/30, full capabilities 134/134 and
full Explorer 299/299. The browser suite passes 19/19, adding retry confirmation,
double-submit exclusion, cancellation, stale-response/error recovery,
ineligible states and the real long-repository mobile selector case.
TypeScript 7 checks and production builds pass; full Docker CI remains blocked.

Final retry source blobs:

| Source | Git blob |
| --- | --- |
| AnalysisApiTests.cs | 04e2bf171546cfd62393b4d249cabf013b38b724 |
| FileRunStore.cs | 3dd267460c2c4531cd9b730212821569cfdc963e |
| ExplorerApi.cs | d04ff77c48fca39a5b3078a0c7d6ce4d58bf49b7 |
| AnalysisJob.cs | 46ba2332422f96d0f060c892f65518c4378e422a |
| client/src/views/Capabilities.tsx | aaa35e6ad662c91e291072d481f303ef73a40034 |
| client/src/App.tsx | ecc3a0616a276f91309d81faa036522ac07bda30 |
| tests/capability-ingestion.browser.ts | 74c1653d715a41d6a06471918c981ec12cadc45e |

Retry red snapshots: API test `cf199e4d9922bb0d36d8f1aecbe0543b557be423`,
FileRunStore `a4daa2ab8e8e71f3dadedfb6c804af97dc468fd6`, ExplorerApi
`3b731fa48b589c747ce7f14ecfb6accecc07322a`. Resumable move red: final API
test above, FileRunStore `25971231ab04db7e96ad4a671c5e2ae7c6cb944b` and
ExplorerApi `c9526fbfb8c92cac6323d5a5365c5e8e286feb98`. Frontend retry
red/green test `13cfedd69b29d2b8cd0d425aa03213a81baccc97`, red production
`a1a5b778b06868d002b5ba038f76b03b37af26ae`. Mobile scope red uses the final
browser test blob above and App `ac803e14b43a03e7c9e341022fff04976524ca07`;
the initial fixture without a revision option already passed and was corrected
to the observed live case before recording red.

The fresh run has **not executed**. A qualified analysis worker/runtime is still
needed. No claim is made about extraction success, publication, database
effects or production-grade end-to-end qualification of the queued run.