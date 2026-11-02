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
