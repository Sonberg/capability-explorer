# Local analysis lifecycle

Analysis uses one trusted host worker and a local run journal under
`.sandbox-cache/requests`. Explorer admits requests and displays status; the
host CLI runs credential-scoped export, package fetching, offline extraction
and guarded publication. PostgreSQL remains optional artifact storage.

This slice does not complete the literal Explorer credential prohibition in
issue #37 A9. The existing private-source Check workflow still uses an Explorer
GitHub token. Analysis admission introduces no credential values or engine
socket into Explorer, and offline extraction receives neither. Moving existing
private-source checks behind a credential-free boundary requires separate owner
review; do not close #37 or describe all A1–A11 as complete on this package alone.

## Host requirements and entrypoints

Install the **.NET 10 runtime on the host**, including when building with Docker.
The launcher executes the built `ce.dll` on the host; Docker-only SDK builds do
not provide that runtime. Native development builds also require the existing
host SDK/toolchain. The scripts check for `dotnet` and the built CLI; they do not
install a runtime or download dependencies automatically.

Use the existing commands from the project root:

```bash
scripts/analyze <capability-id> [capabilities.json]
scripts/analyze-worker [capabilities.json]
scripts/analyze-all [capabilities.json]
```

Their managed commands are `ce analysis-run`, `ce analysis-worker` and
`ce analysis-all`. All acquire the same singleton guard before touching shared
analysis state. A worker already running for Explorer also excludes direct
`analyze` and `analyze-all`; stop that controller normally before changing modes.
A second worker refuses before recovery changes live records.

Full analysis requires the qualified Docker host profile. Explicit Podman full
analysis, running ownership inside an extraction container and runtime image
overrides are refused. This does not broaden the separately qualified direct
Podman sandbox profile.

## Qualified coordination domains

Use a local filesystem and one kernel coordination domain. Persistent
`worker.lock` and `metadata.lock` files use OS-backed `FileShare.None` exclusion;
file presence is not ownership. Never delete or replace the lock files while
any process might hold them.

The persistent `coordination-domain` marker is created once and validated on
every journal operation. Native macOS records `macos-native`. Linux records its
actual kernel boot ID, including when accessed through a same-kernel container.
Partial, symlinked or mismatched markers fail closed. An already open store also
refuses a missing marker. New-store initialization creates an absent marker
atomically; removing a marker is never an authorized recovery operation. There is no
fallback, environment override or automatic marker replacement.

A native macOS worker and an Explorer inside a Linux Docker VM are therefore
**unsupported together for analysis**. Use a native Explorer with the native
worker. Reading artifacts separately is distinct from participating in the
analysis journal. Same-kernel Linux marker equality is necessary; it does not
qualify NFS, SMB, other shared filesystems or a new host/container deployment.
A Linux reboot changes the domain and requires operator reconciliation of the
preserved journal before reuse. Tests do not establish cross-VM lock behavior.

## Run records and status

Each accepted request receives a 32-character hexadecimal run ID; each executing
attempt receives a different ID. Requests, logs, staging, cancellation and
container labels identify that run/attempt. Container labels also include the
project identity. Prior runs are retained independently of the capability's
latest status.

Admission freezes configuration, optional bindings and analysis settings in
private run files. Preparation resolves exact repository SHAs and records
runtime/extractor, exported-source, package and artifact identities. Completion
and publication evidence must match the run, attempt and immutable manifest.
Changing the original configuration after admission does not change the frozen
request; changing its frozen copy prevents publication. Frozen-input paths are relative
on disk and resolved beneath the current journal root.

Before claiming work, `analysis-worker` publishes its validated non-secret
defaults: package/locked flags, GitHub auth mode, private-feed credential variable
names, authorized feed origins and an optional `INCLUDE_TESTS` override. Explorer
copies this profile into each new admission; it receives no credential values or
engine socket. Without a profile, standard defaults and the capability's
`includeTests` apply. A retained profile means last configured defaults, not a
live worker or permission to execute. Changing it cannot alter queued runs.
Restart the worker to publish changed defaults before admitting new work.

`queued` means accepted but unclaimed. `running` means claimed preparation;
`stopping` in Explorer means cancellation intent, not confirmed termination.
`stopped` projects a confirmed cancelled run. `publishing` means the publication
boundary won; `done` means required core effects were confirmed. `failed` means
execution failed with confirmed termination. `unknown`, `interrupted` and
`partial` are blocking uncertainty states; current recovery conservatively
records interrupted execution/publication as `unknown` and retains individual
effect coverage.

Legacy `.request`, `.running` and `.cancel` files are preserved and block
execution. Malformed or incomplete admission records also block execution;
they are not silently skipped or converted to success.

## Stop and publication ordering

Stopping queued work withdraws it atomically. Stopping executing work persists
cancellation intent separately from termination. A killed Docker client or
controller does not prove that its descendants or containers ended.

Currently, Stop during a live child and an ordinary nonzero sandbox exit both
leave the run `unknown`, even when exact-label container cleanup reports no
running container. A late Docker request or escaped descendant is still
uncertain. This blocks all subsequent analysis until offline reconciliation;
there is no automatic reset or stop-and-retry command. Confirmed cancellation
is available for queued work or cancellation detected before any child starts.
Stop after confirmed core publication remains too late.

Cancellation accepted before `BeginPublication` prevents later publication.
That method verifies confirmed successful extraction, cancellation intent and
immutable inputs while holding the metadata lock. It records publishing before
any effect and retains the lock through completion. A Stop observing publishing
or done returns too-late promptly; it cannot promise rollback. A request racing
that transition receives the serialized outcome or an explicit busy/error
response, rather than false confirmation that execution stopped.

The host validates staged artifact bytes/revisions before publication. Each
repository destination has an intent and confirmation. Optional PostgreSQL
migration/import/snapshot publication is a separate effect inside the same
publication permission. Local files may already be visible if PostgreSQL later
fails. Pending/confirmed effect entries retain this coverage; unknown is not a
claim that every destination failed or succeeded.

Embeddings run after confirmed core publication. Their separate
`EnrichmentOutcome` is done, failed or not-configured. A model failure preserves
core done; the existing `ce embed` command can retry enrichment independently.
A crash before recording enrichment leaves that metadata unresolved without
reversing confirmed core publication.

## Recovery and evidence preservation

Restarting a worker obtains the singleton guard before inspecting prior runs.
Unfinished started attempts become unknown and block all new execution, even
for another capability sharing repositories or caches. Queued work remains
queued. Ownership loss, PID absence and a stale timestamp cannot authorize
reuse. There is no automatic retry, resume or success reconstruction from shell
exit codes.

There is currently no automated reconcile, reset or archive command. Operator
reconciliation must establish the following before authorizing conflicting
execution again:

1. Stop new admission/controllers and preserve the complete journal, logs,
   resolved inputs, attempt staging, prior artifact directories and database
   evidence. Keep credentials out of exported diagnostics.
2. Identify the exact project/run/attempt and its owned processes and containers.
   Verify termination using the selected runtime and host; do not kill resources
   selected only by capability name or touch unrelated resources.
3. Compare publication intents, confirmations and destination hashes against the
   frozen/resolved manifest. Treat possible database commit without confirmation
   as ambiguous; do not blindly publish again or rediscover latest revisions.
4. Record the operator decision and retain the original evidence before any
   offline repair/archive. If execution or a publication effect remains unknown,
   keep the block and escalate rather than removing a marker to continue.

These are reconciliation requirements, not an automatic proof procedure.
Changing the coordination marker, deleting running evidence or editing unknown
to stopped merely to unblock work bypasses the protocol.

## Bounds and limits

The default store retains at most **1,000 run records**. At the ceiling it
refuses new admission; it never automatically deletes history, active work or
unreconciled evidence. The library constructor accepts an explicit record
ceiling, but the current host commands use the default. Capacity management is
an offline operator responsibility with the evidence-preservation rules above.

Journal and frozen-input files are limited to 1 MiB each and created private
(mode 0600; directories 0700). Metadata lock acquisition has a bounded retry;
worker lock acquisition refuses immediately on contention. Host log capture
stops near 16 MiB per run, allowing a final encoded output chunk at the bound;
status reads at most 64 KiB and 30 lines. Attempt sources, package caches,
staging and preserved prior artifact directories need separate disk monitoring;
metadata/log limits are not a total disk quota.

Atomic file replacement and flushing do not provide a proven power-loss
recovery guarantee. Repository directory replacements and PostgreSQL publication
are not globally atomic. This package provides no distributed lock, multiple
active workers, automatic rollback/retry, remote authentication or central
last-good policy. See [MADR-0022](adr/0022-local-analysis-lifecycle.md) for the
scope and store-level validation evidence.

## Host-specific qualification

The native store test suite includes the real Docker CLI cleanup probe. The
pinned SDK image deliberately contains no Docker CLI, so sandbox CI excludes
only tests tagged `Profile=HostDocker`; its socket-backed Testcontainers tests
still run. This is a profile boundary, not an unavailable scan treated as green.
Qualify the host adapter separately on a trusted host with .NET 10 and Docker:

```bash
dotnet test platform/tests/CapabilityExplorer.Store.Tests --filter 'Profile=HostDocker'
```

The probe starts two short-lived containers using the existing pinned PostgreSQL
image, verifies exact-label cleanup leaves the sibling running, then removes
only its own containers. It does not establish late-descendant quiescence.
