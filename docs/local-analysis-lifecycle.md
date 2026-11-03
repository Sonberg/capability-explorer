# Local analysis lifecycle

Analysis uses one trusted host worker and a local run journal under
`.sandbox-cache/requests`. Explorer admits requests and displays status; the
host CLI runs credential-scoped export, package fetching, offline extraction
and guarded publication. PostgreSQL remains optional artifact storage.

Source Check/Create-and-check now admits asynchronous requests for that same
exclusive host owner. Explorer neither resolves acquisition credentials nor
clones/fetches sources. Supported launchers strip acquisition credentials and
mask legacy package settings; offline extraction receives neither credentials nor
engine sockets. This is a process/application boundary, not OS isolation between
arbitrary same-user programs. Database/model credentials remain separate.

## Host requirements and entrypoints

Install the **.NET 10 runtime on the host**, including when building with Docker.
The launcher executes the built `ce.dll` on the host; Docker-only SDK builds do
not provide that runtime. Native development builds also require the existing
host SDK/toolchain. The scripts check for `dotnet` and the built CLI; they do not
install a runtime or download dependencies automatically.

When `PACKAGES=1`, host Node.js 22 also resolves host `.npmrc` credentials before
containerized package fetching. `PACKAGES=0` avoids that resolution step. No
repository lifecycle scripts are enabled by package acquisition.

Worker database and model endpoints must be reachable from the host. Do not
reuse a container-only `host.docker.internal` address for a native client. The
sandbox `make dev` launcher uses `DEV_WORKER_DATABASE` (default loopback) for
its native worker and `DEV_DATABASE` for container clients. Override both when
using a different database. Saved model settings must also suit the worker's
host context; optional embedding failure remains separate from core success.

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
Direct `ce export` is disabled; it cannot bypass this owner.

For an existing run, `ce reconcile-publication --run <run-id> --attempt
<attempt-id>` reports PostgreSQL attempt evidence and the matching host journal.
It is a read-only report, not a recovery command.

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
every access. Its complete contents are flushed in a private temporary file
before atomic publication without overwrite; concurrent cold starts cannot
observe a partially initialized canonical marker. Existing corrupt or incomplete
markers still require reconciliation. Native macOS records `macos-native`. Linux records its
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

Source checks have their own operational request/attempt IDs, not AnalysisRun or
ce.v1 identities. POST `/api/capabilities/{id}/check` returns `202 Accepted` and a
`Location` at `/api/source-checks/{requestId}`; clients poll it and may POST its
`/cancel` subresource. Duplicate active admission returns the same request ID
with `duplicate: true`. Create also returns 202 with separate `created`/`queued`
flags. If a definition persisted but admission failed, the error preserves its
ID and `created: true, queued: false`: retry Check, not Create.

Checks freeze the effective shared/local definition and GitHub auth policy.
Status distinguishes queued/working/cancelling/done/failed/cancelled/unknown.
Only the latest completed matching check supplies current revision locks;
configuration or policy changes mark prior results stale. Queued work is not
proof that a worker is alive. Started cancellation/restart uncertainty becomes
unknown and blocks checks and analyses together; completion after cancellation
is forbidden. Private frozen paths, credentials and remote diagnostic text are
not projected in public status. `LOCKED=1` analysis requires a matching completed
host check.

### Package-feed compatibility

Explorer Settings is now a read-only, non-secret catalogue. Credential-bearing
PUT requests return 410; there is no silent compatibility path that reads the
legacy `.sandbox-cache/package-feeds/settings.json`. The host worker publishes
`.sandbox-cache/package-feed-catalog/catalog.json` using atomic replacements.
The directory, not an individual file, is mounted read-only for Explorer.

Migrate existing settings explicitly on the trusted host, from the project root:

```bash
dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll package-feeds \
  --import .sandbox-cache/package-feeds/settings.json \
  --catalog .sandbox-cache/package-feed-catalog/catalog.json
```

The host default is `~/.config/capability-explorer/package-feeds/settings.json`;
`--host-file` during import and `CE_PACKAGE_FEEDS_FILE` during worker execution
must identify a path outside the workspace. The host retains verified private
backup/settings copies before sanitizing legacy credentials; failed persistence
does not erase the only copy. Keep backups private. Existing NuGet/npm feed and
host `.npmrc` policies remain acquisition-only. Unmigrated legacy settings,
unsafe permissions or in-workspace secret overrides fail explicitly. Never
configure the public catalogue path as a secret settings path.

### Analysis identities

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

Before attempting the snapshot compare-and-swap, the host records the exact
run/attempt, frozen input hashes, scope, expected generation, selected artifact
hashes and candidate identity in PostgreSQL. Each successful import outcome is
recorded alongside its immutable artifact transaction. A successful pointer
update and its run/attempt-specific publication result commit in the same
PostgreSQL transaction. A stale-generation rejection is durably recorded
without moving the pointer. A lost commit response is not converted into
failure: the exact attempt result determines whether this attempt committed;
no result remains unresolved.

The report distinguishes three separate facts: whether this exact attempt
committed or was durably rejected; whether its immutable candidate has ever
been published; and which snapshot/generation is current now. Thus a historical
published candidate remains evidence after another writer advances the pointer,
while a matching content hash or current pointer alone never attributes a
publication to this run. Database publication and host-journal confirmation are
also separate: reconciliation never repairs a missing host confirmation.

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

`ce reconcile-publication` reads the attempt, per-artifact outcomes, candidate
history and current pointer in one PostgreSQL `REPEATABLE READ`, read-only
transaction. It does not run migrations, import artifacts, normalize search
data, write a receipt, update the host journal, publish, retry, archive or clear
markers. Imported, reused, conflicting, missing, invalid and uncertain artifact
outcomes retain their exact analysis/content identities. Pending evidence,
missing attempt/candidate receipts, conflicting values, an unavailable database
or a contradictory host receipt are reported as unresolved. Candidate identity
and artifact membership must agree with the durable attempt; reconciliation
never repairs a mismatch. Artifact rows include extractor/version,
configuration and original byte hashes as well as repository/revision/content.
A missing or pending host confirmation remains a separate axis and cannot
negate an exact committed database result. A local directory
move is not evidence that PostgreSQL succeeded, and a different current pointer
does not prove an attempt never published.

Read-only report outcomes are `confirmed-published`, `confirmed-not-published`
or `unresolved`. The first requires the matching attempt-specific committed
result; the second requires a durable terminal rejection for that exact
attempt. Absence of a receipt, a candidate still marked `candidate`, process
exit status, cancellation, local effect confirmation or current-generation
comparison cannot establish nonpublication. The report conveys no permission
to retry, reset, repair, archive or clear unknown evidence. Preserve it and
follow the operator procedure above.

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
