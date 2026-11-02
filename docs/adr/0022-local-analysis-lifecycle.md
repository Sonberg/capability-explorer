# MADR-0022: Single-host analysis ownership and publication

- Status: Proposed
- Date: 2026-11-02
- Related: issue #37; MADR-0011, MADR-0017, MADR-0020, MADR-0021

## Context

Capability-named request/running/cancel/exit markers cannot distinguish accepted
intent from an execution attempt. Checking status separately from enqueueing
races admission. Rewriting old running markers as stopped cannot establish that
processes or containers ended. Shared repository/artifact paths also require
exclusion across Explorer requests, direct analysis and aggregate analysis.

The existing snapshot generation CAS orders database snapshots; it does not
establish analysis attempt ownership. Optional PostgreSQL artifact storage is
not consent to require a central lifecycle service.

## Decision

Use the shared .NET `FileRunStore` for a bounded, host-local lifecycle journal.
A Capability selects repositories, an AnalysisRun records accepted intent, and
an Attempt identifies execution. Admission and cancellation use a short
cross-process metadata lock. One worker acquires a separate exclusive guard
before recovery, admission from direct commands, preparation or shared writes.
The host CLI owns publication; extraction sandboxes produce attempt staging.
The `analyze`, `analyze-worker` and `analyze-all` scripts delegate to that CLI.

Persistent lock files use `FileStream` with `FileShare.None`; never unlink or
replace them. Independent .NET process tests exercised exclusion and owner
termination on native macOS and in the pinned Linux .NET sandbox. Those tests
establish behavior in the tested kernel domains, not cross-VM locking.

A persistent coordination-domain marker is created with `CreateNew` and never
overwritten. Every store access validates it. Native macOS uses `macos-native`;
Linux requires the actual `/proc/sys/kernel/random/boot_id`, with no fallback.
A different domain, Linux boot, partial marker or symlink refuses access while
preserving evidence. A macOS host worker and Linux Docker-VM Explorer cannot
share this journal. Same-kernel Linux paths can identify the same domain;
shared/network filesystems and distributed workers remain unsupported.

Run/configuration/bindings records are private and bounded. Accepted settings
and frozen file hashes identify accepted inputs. Resolved manifests bind exact
repository revisions, exported source, extractors/runtime, package locks/cache
and staged artifacts. Run/attempt identities and manifest hashes bind receipts.
Frozen paths are confined relative paths on disk, resolved beneath the current
journal root when read; filesystem aliases do not grant a new ownership domain.

The state machine is:

```text
queued -> running -> publishing -> done
queued -> cancelled
running -> cancelling -> cancelled (only with confirmed termination)
running -> failed (only with confirmed termination)
started execution/publication without sufficient confirmation -> unknown
```

Successful extraction alone does not mean done. Publication requires confirmed
successful execution, matching immutable inputs and no cancellation intent.

The current host adapter treats cancellation of a live child and nonzero sandbox
exit as unknown even after exact-label cleanup. It cannot prove that no late
Docker request or escaped descendant exists. This intentionally blocks new work
until offline reconciliation; ordinary live Stop does not provide a retry-ready
terminal state. Queued and pre-spawn cancellation can be confirmed.
`BeginPublication` durably records publishing under the metadata lock and holds
that lock through publication effects and completion. Cancellation accepted
before this boundary prevents publication. Once publishing is observed,
cancellation returns too-late without waiting for a slow database operation.
Terminal core outcomes cannot be overwritten by stale completion; optional
embedding outcome metadata is recorded separately after core success.

Each local artifact destination and optional PostgreSQL publication has a
pending intent and confirmed evidence hash. A crash or failure during
publication remains unknown, with per-effect coverage retained. A committed
database operation without confirmation is not blindly repeated. Multiple
repository directory changes and database effects are not one atomic action.

Recovery preserves legacy markers, cancellation intent and all started attempt
evidence. Unresolved started attempts become unknown and globally block further
execution. There is no automatic retry, PID-based takeover or claim of confirmed
stop from killing a controller. Reconciliation is evidence-preserving operator
work; no recovery/reset command currently automates it.

## Alternatives

A PostgreSQL lifecycle ledger, broker and workflow engine are deferred until
shared admission, remote recovery or independently operated consumers require
them. Optional `CE_DATABASE` continues to select artifact storage. A shell-only
marker handshake cannot enforce the shared typed state machine. Directory or
PID existence alone cannot establish live ownership.

## Consequences and validation

Analysis now requires a trusted host .NET 10 runtime even when builds use Docker.
Explorer remains unable to start engines or access their socket. Existing
credential/export/package/offline-extraction separation remains in place.

The owning host publishes a bounded private defaults profile before claiming
work. Explorer freezes those non-secret settings into admission, preserving
package/auth/private-feed policy without receiving credential values. A missing
profile uses standard defaults; a stale profile is only last configured policy,
never liveness or execution authority. Already admitted settings remain immutable.
Podman full analysis remains unqualified and is explicitly refused.

Issue #37 A9's literal broader prohibition on Explorer credentials is not met:
the existing private-source Check workflow still uses an Explorer GitHub token,
and main also added credential-bearing package-feed Settings.
This slice introduces no analysis credential values or engine socket into
Explorer; offline extraction receives neither. Moving that existing check
behind another boundary requires separate owner review. Keep #37 open; this
decision and its tests do not establish complete A1–A11 acceptance.

The default retention ceiling is 1,000 run records. Admission refuses at the
ceiling rather than deleting active, unknown or historical evidence. Individual
journal/input files are bounded at 1 MiB. Logs are bounded separately; the UI
reads only a bounded tail. Operator storage management remains necessary.

The focused store suite passed 23 tests with no skips in both native macOS and
the pinned Linux .NET sandbox, including independent-process lock/death tests,
concurrent admission, cancellation/publication ordering, immutable binding,
coordination-domain refusal, path confinement and incomplete metadata. These
are store-level results, not full analysis/container/PostgreSQL qualification,
performance evidence or release approval. Integrated validation is recorded by
the implementation handoff.

Atomic replacement and file flushing do not establish power-loss durability or
an atomic global filesystem/database commit. No distributed fencing, automatic
rollback, multiworker throughput or central last-good policy is claimed.

## Implementation and operating guidance

- [Local lifecycle guide](../local-analysis-lifecycle.md)
- [FileRunStore](../../platform/src/CapabilityExplorer.Capabilities/Analysis/FileRunStore.cs)
- [CoordinationDomain](../../platform/src/CapabilityExplorer.Capabilities/Analysis/CoordinationDomain.cs)
- [PublicationPermit](../../platform/src/CapabilityExplorer.Capabilities/Analysis/PublicationPermit.cs)
- [Host worker](../../platform/src/CapabilityExplorer.Cli/AnalysisWorkerCommand.cs)
- [Publication effects](../../platform/src/CapabilityExplorer.Cli/AnalysisPublication.cs)
