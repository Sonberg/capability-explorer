# MADR-0023: Host-owned source checks and acquisition credentials

- Status: Proposed; implementation contract authorized by the operator
- Date: 2026-11-02
- Related: issue #37 A9, MADR-0007, MADR-0022
- Baseline: `46104456754474fd792a38ebbd14cc964fe1e6e2` (owner-merged PR #39)

## Decision

Explorer admits asynchronous source checks, including Create-and-check, and reads
their bounded, non-secret results. It neither instantiates acquisition credentials
nor performs clone/fetch. The existing exclusive trusted host owner processes
checks and analyses serially; no additional worker, broker, database or image is
introduced. Direct check entrypoints acquire the same guard or refuse.

Check requests are operational records, not ce.v1 facts or AnalysisRuns. Each has
an immutable accepted capability configuration/authentication-policy binding,
request ID and distinct execution attempt ID. Admission and cancellation share
the analysis metadata lock. Acquisition shares its worker ownership and recovery
block. An unknown started check prevents conflicting source-cache work, including
analysis. A retained defaults profile is not worker liveness.

```text
queued -> working -> done | failed
queued -> cancelled
working -> cancelling -> unknown
interrupted working/cancelling -> unknown (no automatic retry/reset)
```

Cancellation records intent. No result may publish after accepted cancellation.
Terminal records cannot be replaced by stale controllers. A request result binds
the accepted configuration, attempt and exact resolved repository revisions.
Completion validates repository membership/ref and revision syntax, rejects
mismatches, and never reconstructs success from current mutable refs.

Current status must not silently project an old result onto a changed capability.
It exposes request/config identity and staleness. Results remain request-scoped;
the latest matching result is the only source of the current check snapshot.
Cancellation after completed publication reports too-late. Retention and record
size reuse the bounded local journal policy; unreconciled evidence is retained.

## HTTP and UI compatibility

Check and Create-and-check return `202 Accepted` with a request identity and
status location. Duplicate active check admission returns the same request with
an explicit duplicate response. Missing worker leaves work queued, not successful.
Status distinguishes queued/working/cancelling/done/failed/cancelled/unknown.
The existing synchronous behavior is intentionally changed and documented rather
than secretly preserved as a second acquisition path. Clients migrate to polling.
Capability creation persists its definition separately; failed admission must
explicitly distinguish a created definition from a queued check.

Package-feed Settings becomes a non-secret host configuration/status projection.
Explorer rejects credential-bearing updates, never loads the old secret file and
does not silently ignore old secret fields. Host configuration retains existing
GitHub auth modes and NuGet/npm policies. A host-only migration moves legacy
secrets outside the Explorer workspace; the sanitized public catalogue contains
only descriptors and configured credential references/status. Missing migration
or credentials is visible; no success-shaped fallback.

Supported launchers remove acquisition credential forwarding/inheritance from
Explorer. This is a process/application boundary, not a claim of OS isolation
between arbitrary programs running as the same user. Database/model credentials
are separate and remain supported. Offline extraction still receives neither
acquisition credentials nor engine sockets.

## Executable acceptance

- Barrier-synchronized duplicate admission yields one request without lost history.
- Second-owner refusal preserves live check and analysis records.
- Cancellation before result publication prevents later publication; completion
  first yields too-late. Stale/repeated/wrong-attempt completion fails.
- Configuration mutation after admission preserves accepted inputs, marks stale
  status and cannot replace a newer snapshot.
- Interrupted acquisition becomes unknown and blocks conflicting work. Recovery
  never deletes markers or asserts descendant termination.
- Path traversal, malformed records and mismatched revision/membership receipts
  fail closed.
- A controlled credential-required fixture proves host-only acquisition access;
  Explorer, offline children, journals, errors and logs contain no fixture secret.
- Host-only feed migration preserves supported feed policies; Explorer never reads
  the legacy file and rejects old secret-bearing Settings requests explicitly.
- UI and API exercise queued Create/Check/status/error flows without a real token
  or inaccessible private repository.
- Existing goldens, package tests, security gates and full fresh-main local act
  gates remain mandatory; skips/mock success are not qualification.

Commands and actual counts are recorded with the implementation handoff. Initial
planning is not a passing receipt. No new release, remote authentication,
distributed ownership, automatic retry or repair of unknown analysis runs follows
from this decision.

## Shared interface and execution plan

The Capabilities library owns `SourceCheck`, `SourceCheckAdmission` and
`SourceCheckCancellation`. `FileRunStore` exposes `AdmitCheck(capabilityId,
configurationPath)`, `ReadCheck(requestId)`, `ReadLatestCheck(capabilityId)`,
`CancelCheck(requestId)`, `ClaimCheck(owner)` and guarded completion/failure
methods. Admission freezes merged local/shared definitions and the existing
host authentication policy. The DTO sent over HTTP omits private frozen paths.

The worker claims checks and analyses under the same ownership handle. It resolves
credentials only for acquisition, produces bounded sanitized snapshots, then
validates identity/cancellation under the metadata gate before completing.
Queries are linear in the bounded retained record count; admission/transition
writes are constant-sized apart from the bounded frozen definition/snapshot.
Duplicate admission returns the existing active immutable intent, not a second
execution. Started uncertainty blocks both operation families.

`PackageFeedCatalog` is the shared versioned public descriptor projection.
Credential-bearing host settings are not that catalogue. Explorer's old input
shape is rejected with a migration explanation; only the host imports legacy
secrets. The catalogue's feed ID is the non-secret credential/configuration
reference. A catalogue is configured status, never host liveness.

## Implementation verification

The implementation at `c8fe85927a02971635d20aa1e3ecb56fe891555d` passed
`make local-check` and `make pr-check` against fresh main
`46104456754474fd792a38ebbd14cc964fe1e6e2` on 2026-11-02. The full local
Actions job used the pinned runner and unchanged security/golden gates.
The eventual review head must have its own exact-source receipt; this is not
a reusable release permission.

| Acceptance boundary | Executable evidence |
|---|---|
| Duplicate admission, history, shared owner and recovery block | `SourceCheckTests`: barrier admission, second-owner refusal and symmetric check/analysis uncertainty tests |
| Attempt/config/ref/SHA fencing and cancellation ordering | `SourceCheckTests`: wrong attempt, terminal replay, changed frozen inputs and mismatched membership/revision cases |
| Current versus frozen auth policy | `Accepted_inputs_are_frozen_and_configuration_auth_policy_and_integrity_are_bound`; Locked preparation explicitly supplies the run's accepted auth |
| Async HTTP, stale locks, completion reload and persisted-but-not-queued errors | `SourceCheckApiTests` and `CapabilityApiTests`, including malformed-record 503 |
| Host-only credentials and sanitized operational evidence | `HostAcquisitionTests`: controlled credential-required Git executable, local real Git, cancellation and journal secret-absence checks |
| Feed migration, private backups, public catalogue and unsafe paths | `HostAcquisitionTests` and `PackageFeedSettingsTests`; invalid URL/credential, symlink and in-workspace override refusals |
| Supported Explorer environments and offline mounts | `tests/explorer-environment.test.mjs`, `tests/container-engine-core.test.mjs`; actual native child plus Docker legacy-mask/read-only-catalogue probe |
| Analysis acquisition cancellation and direct-entrypoint refusal | `AnalysisExecutionTests.Cancellation_during_host_git_interrupts_acquisition_and_preserves_unknown_execution`; `Direct_export_cli_refuses_before_creating_shared_cache` |
| Existing behavior and extraction quality | Full local Actions job; protected C#/TS/SQL golden precision and coverage remain 100%, with no golden or threshold edits |

Native validation passed 92 Capabilities, 182 Store and 269 Explorer tests before
the final corruption regression; the final Linux job passed 92 Capabilities,
181 Store, 270 Explorer and 37 Interpretation tests. The Store profile difference
is the existing native-only `HostDocker` probe, which passed natively, not an
unavailable test counted as successful. The standard native solution build
reported zero managed warnings/errors; 78 affected Node tests passed.

A separate running fixture exercised the supported native Explorer launcher and
host worker: Check/Create returned 202 and reached done with the exact local Git
SHA; Settings read the public catalogue and rejected PUT with 410. After both
processes stopped, a real host analysis exported that local source, performed
offline Docker C# extraction and guarded local publication with database and
package acquisition explicitly disabled. No fixture secret appeared in its
journals or captured logs.

The full gate exposed an unrelated, already failing main-branch EF model-builder
query-classification defect, independently reproduced on pristine main and hosted
CI run `41531133368`. The operator separately authorized its two-file root-cause
repair (`0ba53a9`, integrated as `4edbcfb`). Its new regression failed before the
repair; 171 extractor tests and unchanged protected C# goldens passed afterward.
The final integration also gives glossary HTTP fixtures their own journal rather
than reading an interactive host's incompatible kernel-domain marker.

Not tested: authentication against real private GitHub/package services, browser
visual automation, power-loss durability, or distributed/cross-kernel operation.
The credential-required fixture verifies the process boundary, not a remote
provider's availability. Independent review identified frozen-auth matching and
bounded-corruption-response defects; both have focused regressions. None of this
authorizes automatic merge, deployment, recovery reset or publication.
