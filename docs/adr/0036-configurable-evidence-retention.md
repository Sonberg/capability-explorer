# Configure evidence retention and age-based historical resolution

* Status: proposed
* Scope: operator-requested design; no retention executor or interface implemented
* Date: 2026-11-04
* Decision owner: repository maintainers and designated data-policy owners
* Coordination: issue #33 collector and downstream ownership boundary
* Related: [collector evidence](0033-bounded-model-and-collector-evidence.md), [cache lifecycle](0030-dependency-cache-lifecycle.md), [measurable objectives](0035-measurable-service-level-objectives.md)

## Context and problem statement

Evidence must remain available long enough for authorized central collection,
operational use and historical comparison without retaining every byte forever.
Retention serves legal, regulatory, business and operational requirements;
none of those requirements supplies a universal duration for this repository.
Age alone is neither permission to delete nor a reason to keep data indefinitely.

The operator requests easily configurable interface settings, an enforced
minimum and a recommended default. Older history may have coarser resolution,
eventually monthly or quarterly, while preserving honest time-travel views.
Policy, data ownership, custody transfer and historical resolution are different
concerns and must be explicit.

## Decision drivers

Reliable central handoff, useful historical comparison, bounded storage,
purpose/rights restrictions, understandable controls and verifiable deletion.
Preserve immutable identities and dependencies; do not disguise missing detail
as an exact historical reconstruction.

## Considered options

1. Retain every original and derived artifact indefinitely.
2. Apply one fixed age cutoff to every data type and delete older history.
3. Configure purpose-specific retention, verified custody and progressively
   coarser historical resolution with explicit loss-of-detail metadata.

## Decision outcome

Propose option 3. This record defines future requirements only. It authorizes
no cleanup, service, UI implementation, central enrollment or legal conclusion.

### Policy model and interface

Each versioned policy identifies its owner, scope, purpose, applicable data
classes, age clock, minimum duration, recommended default, selected duration,
any maximum expiry, archival destination and historical resolution schedule.
Separate source/model content, native facts, candidates, collection manifests,
attempt logs, derived projections/embeddings and disposable caches. Related
data need not have identical retention, but dependencies must remain valid.

The interface must show the effective policy, its inherited organization/
repository defaults, minimum, recommendation, current choice and exceptions.
Authorized users can choose supported settings by use case without editing
files or database rows. A minimum is an enforced lower bound, not an automatic
deletion trigger; a recommended default is the initial reviewed choice, not
proof that it meets every legal obligation. Organization policy owners govern
the floor; an ordinary local preference cannot lower it or override rights.

Before applying a change, preview retained/deleted/archived record and byte
counts, affected date ranges, historical resolution, dependencies, central
handoff blockers and irreversible consequences. Counts with unknown size stay
unknown. Validate the same constraints at the API boundary, audit the policy
revision/actor/action and require explicit authorization for destructive apply.
Increasing retention cannot restore detail already deleted. Lowering a default
must not silently purge existing history.

Minimum and recommended durations must be concrete per data class before
enabling enforcement. **Numeric values are unresolved**, not invented here:
baseline actual collection delays/outages, offline operation, storage growth,
time-travel needs and applicable owner-approved requirements first. The minimum
must cover the agreed handoff/retry envelope; the default must be at least that
minimum and within applicable expiry limits. If constraints conflict, reject the
policy and escalate to its owner; do not silently keep or delete contrary to it.
Authorized holds and their release rules must be explicit and rechecked.

### Collector and central custody

Local retention and central retention are separately owned policies. A local
export, HTTP upload or `received`/`validated` response is not proof of durable
central custody or publication. Before deleting the only retained copy of data
needed for handoff, require a verified destination receipt bound to exact
content identity, permitted scope, acceptance status and a declared retention/
recovery commitment. The future contract must define that receipt; no current
collector flag supplies it.

Unacknowledged, failed, partial or offline handoffs remain visible and retryable
within policy. A backlog approaching its retention/space limit raises an
explicit alert and owner decision; do not silently erase it or retain it
forever by accident. Mandatory expiry still applies: an unavailable recipient
does not grant unlimited retention. Reject incompatible configurations and
record unresolved custody/expiry conflicts before authorizing a destructive run.
Central deduplication is not proof that local deletion is safe.

### Historical resolution and time travel

Configure increasing age bands with non-overlapping boundaries and non-increasing
temporal detail: recent full-resolution evidence, then coarser retained points,
eventually monthly or quarterly where the use case permits. Exact age cutoffs
and whether intermediate daily/weekly tiers are needed remain policy decisions.
Monthly/quarterly cadence does not specify how many years history is retained;
the total horizon is a separate setting.

Define time zone, calendar boundaries, observation/as-of clock, bucket selection
rule and behavior for late/out-of-order observations. Use explicit collection/
observation time for aging unless a reviewed class-specific clock says otherwise;
source commit time must not silently determine expiry.

Distinguish two products:

1. A retained representative snapshot is an actual source-bound snapshot chosen
   by a declared deterministic rule, such as the last complete observation in a
   calendar bucket. It is not evidence that the whole bucket had that state.
2. An aggregate is a separate derived product with a versioned recipe, covered
   interval, source references, coverage/uncertainty and partial/missing inputs.
   Never relabel an average or merged record as an original snapshot.

Do not rewrite old immutable manifests or hash different bytes under an existing
identity. Coarsening must preserve valid dependency closure and the anchors
needed to explain the retained product. Where raw detail expires, the interface
must disclose the available resolution and explicitly refuse unsupported exact
time travel or drill-down. Missing periods stay gaps, not interpolated facts.
Pinned baselines, retained decisions and active investigations need explicit
owner-approved policy treatment; a pin cannot override mandatory expiry.

### Archive, deletion and recovery

Archival is a custody/access/storage change, not deletion or proof of recovery.
Verify destination integrity, permissions and an actual bounded restore before
treating an archive as satisfying the relevant retention requirement.

Plan using an immutable policy/source inventory; recheck current policy,
holds, active uses, dependency reachability and destination receipts immediately
before apply. Use resumable, idempotent, exact-scope operations; interruption
cannot turn a partial deletion into success. Respect shared blobs: expiry of one
reference does not authorize removal while another permitted retained reference
still needs it.

Deletion must account for source copies, indexes, embeddings, caches, exports,
replicas and backups under their respective owners. State backup expiry and
restore-time suppression explicitly; do not promise instant physical erasure
across unqualified storage. Prevent deleted data from silently reappearing after
restore. Keep a minimal, access-controlled deletion/exception receipt with its
own bounded retention; it must not preserve the deleted payload indefinitely.

## Consequences

Storage and historical fidelity become deliberate, reviewable tradeoffs.
Coarsening reduces exact historical detail and cannot be reversed without an
authorized surviving source. UI controls and policy enforcement add maintenance
cost; legal applicability, numeric durations, custody authority and archival
qualification require accountable owners before destructive automation.
MADR-0030 continues to govern dependency-cache ownership; worktree cleanup is
not application-evidence deletion. This record supersedes neither.

## Validation and acceptance

Future implementation must test minimum/default/maximum conflicts, inheritance,
unauthorized overrides, holds, missed acknowledgements/offline retries, exact
age boundaries, calendar/time-zone transitions, late observations, empty/partial
buckets, shared dependencies, idempotent interrupted apply, restore suppression
and explicit coarse-resolution time travel. Verify a real archive restore and
source-bound deletion receipt. Measure handoff, enforcement and restore
objectives under MADR-0035 where applicable.

This documentation establishes no measured retention/SLO compliance, lawful
duration, verified central receipt, implemented UI or permission to delete.
