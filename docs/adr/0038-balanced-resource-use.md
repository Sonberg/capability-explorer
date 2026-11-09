# Balance RAM and disk use with performance and developer productivity

* Status: accepted
* Scope: resource-use decision method; no budget enforcement, monitoring or cleanup implemented
* Previous scope (historical): proposed policy; owner review pending; no budget enforcement, monitoring or cleanup implemented
* Date: 2026-11-06
* Decision owner: repository maintainers
* Coordination: [issue #108](https://github.com/Sonberg/capability-explorer/issues/108), [issue #109](https://github.com/Sonberg/capability-explorer/issues/109), [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [measurable objectives](0035-measurable-service-level-objectives.md), [frugal CI](0029-frugal-ci-and-executable-parity.md), [cache lifecycle](0030-dependency-cache-lifecycle.md), [evidence retention](0036-configurable-evidence-retention.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: resource-use decision method; no budget enforcement, monitoring or cleanup implemented.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:03f64fccf09855a2cdc6a86e1bbd588953f02a737d17390c2bf30ebe8505cb21`.
Previous decision status (historical): `proposed`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem statement

The operator requests minimal RAM and disk impact from the product and from
its development, analysis, test, container and agent workflows. The operator
also requires that latency, throughput and developer productivity do not
suffer. The goal is a practical, measured resource envelope, not the smallest
possible footprint at any cost.

Scanner qualification in #104/#107 exhausted real disk space. Sealed per-run
data and atomic moves then avoided a second copy of a database of about
1.56 GiB. This is motivation, not a universal budget.

Existing records cover parts of the problem. [MADR-0035](0035-measurable-service-level-objectives.md)
defines the measurable objective contract, but treats the resource envelope
as an assumption. [MADR-0029](0029-frugal-ci-and-executable-parity.md) bounds
CI concurrency and benchmarking. [MADR-0030](0030-dependency-cache-lifecycle.md)
governs caches and dependency hygiene. [MADR-0036](0036-configurable-evidence-retention.md)
governs evidence retention. No record says how to set resource budgets together
with performance objectives, how to account for shared memory and disk honestly,
or when to stop optimizing.

## Decision drivers

Meet agreed performance and correctness objectives. Avoid waste and host
exhaustion on shared machines. Keep engineering complexity and developer
waiting time proportional to the benefit. Preserve security, determinism,
source rights and recovery obligations.

## Considered options

1. Minimize RAM and disk everywhere, for example with fixed percentage
   reduction targets: rejected. Aggressive eviction and serialization can cause
   repeated downloads, rebuilds and slower work.
2. Ignore resource use until a failure occurs: rejected. #107 shows that
   unplanned disk exhaustion stops work.
3. Set workload-specific resource objectives together with performance
   objectives, measure them honestly, keep headroom and stop tuning when both
   pass: selected for review.

## Decision outcome

Propose option 3. This record defines a method, not numeric limits. It changes
no status, scope or authority of MADR-0029, MADR-0030, MADR-0035 or MADR-0036.

### Resource objectives with performance objectives

Use MADR-0035's contract for each workload that has material resource use or a
reported capacity risk. Its resource-envelope field then records **resource
objectives**, not only assumptions. Each resource objective states:

* the resource: host RAM, container or VM memory limit, temporary disk,
  retained disk, shared cache or image bytes, process count or concurrency;
* the unit, bound and owner;
* the workload, source identity, platform, runtime and engine;
* the measurement method; and
* the performance objectives and guardrails that must still pass.

A lower RAM or disk number does not justify an unapproved latency or
throughput regression, cache thrashing or disproportionate complexity. A faster
result does not justify disproportionate RAM or disk without review. If the
objectives conflict, give the tradeoff evidence to the acceptance owner.
Neither side wins silently. Small changes without material resource effect
need no budget; record "not material" and the reason in the review.

### Honest measurement

Report these quantities separately. Do not add them together.

| Quantity | Meaning |
|---|---|
| Baseline | Usage before the task, on the same host and state. |
| Peak | Highest value in a declared sampling interval. One sample is an observation, not a peak. |
| Temporary | Scratch disk or memory that the task releases at the end. |
| Retained | Bytes that remain after the task, with their owner and purpose. |
| Shared | Images, layers, download caches and indexes that several workloads use. Count them once, with their owner. |

Host process RSS, container memory, VM allocation and file-system cache are
different measures. Overlapping shared allocations must not be summed per
consumer. Compare baseline and candidate with the same source, host, images
and cache state. Record cold and warm runs separately, as MADR-0029 requires.
State the sampling method and limits. Label a target that has no measurement
**proposed, unmeasured**, as MADR-0035 requires.

### Budget, headroom and failure rules

Before expensive work:

1. Identify the workload, source identity, platform and owner.
2. Inspect the current free disk on the affected volume, available memory,
   VM or container limits and other active heavy work.
3. Estimate the expected peak temporary and retained growth from earlier
   evidence or a small probe. State the uncertainty.
4. Keep an explicit per-host reserve. A reserve that worked on one host is a
   floor for that host, not proof that a larger job fits.
5. Start only if the expected peak plus the reserve fits. Otherwise defer,
   serialize or reduce the scope. Bound parallelism by measured capacity, not
   by the number of agents. Run heavy tasks one at a time unless measurements
   show that more fit.
6. Monitor long-running work at a declared interval.

If headroom becomes insufficient, stop or defer safely before the reserve is
exhausted. Release only the task's own temporary data. Report the resource,
required and available amounts, partial results and the next owner action.
Silent truncation, partial results reported as success, hidden out-of-memory
termination, skipped tests and lowered security gates are not acceptable.

### Useful resources and reuse

Caches, indexes, precomputation and parallelism are legitimate when their
measured benefit justifies their footprint. Bound them by bytes, entries or
lifetime. Record their owner, purpose and recovery needs. Prefer bounded
streaming or batching, reuse of verified immutable inputs, lazy loading,
incremental work and reference or atomic-move strategies over full copies and
repeated downloads. Reuse must preserve source identity, permissions,
freshness and run isolation. Avoid duplicate writable caches and repeated
image churn. Cache retirement and dependency hygiene follow MADR-0030.
Required history, backups and recovery evidence follow MADR-0036 and are not
disposable cache.

### Opt-in services and daemons

Large services, model downloads, background agents and daemons, and large
build or test matrices are opt-in where practical. An explicit command starts
them and their owner stops them when the task ends. Disable automatic daemon
start in agent tooling unless the task needs it. Report each service that a
task started and whether it still runs. Never leave an unintended daemon.

### Optimization stop rule

Optimize only a demonstrated bottleneck, capacity risk or material waste.
Stop tuning when the agreed performance objectives, resource objectives and
headroom pass. Reopen only for a reproduced breach, a reviewed workload change
or new evidence of material waste. Do not set arbitrary percentage reduction
targets, add speculative micro-optimizations or build optimization machinery
that costs more than it saves. Include engineering, maintenance and developer
waiting time in the comparison.

### Exceptions

A necessary large resource needs measured evidence and a recorded exception:
owner, scope, reason, measured size and review trigger. A smaller
implementation is not acceptable if it corrupts results or defeats security,
determinism or recovery.

### Representative examples

The examples show what to budget. **They are not limits.** Each issue sets its
own numbers from measurement.

| Workload | Resources to budget | Useful reuse | Typical waste or risk |
|---|---|---|---|
| Repository analysis (#84) | Peak RAM per stage, temporary disk, concurrency | Unchanged-input reuse with source identity | Full repository copies per stage |
| PostgreSQL (#28, #43, #63) | Container memory, retained data, index size | Indexes that meet query objectives | Unbounded snapshots; dropping a useful index |
| Vite and browser payloads (#13, #29, #99) | Build memory, bundle and payload bytes, browser heap | Lazy loading and bounded queries | Shipping the whole model to the browser |
| Scanner databases (#104, #107) | Temporary and retained disk per run, shared database bytes | Sealed per-run data, atomic moves | A second copy of a large database |
| Containers and images | Image and layer bytes, VM memory | Pinned shared images | Repeated pulls and image churn |
| CI and fixtures (MADR-0029) | Worker count, temporary build output | Download caches | Unbounded `make -j` |
| Agents and daemons (#3) | Processes, RAM, concurrent writers | Read-only sharing of one checkout | Idle daemons; many heavy jobs at once |

### Resource-impact review

Relevant implementation plans and PRs include a short resource-impact review:
expected and observed peak RAM, temporary and retained disk, cache and
download effects, cleanup ownership and unresolved limits. Use existing
operating-system, engine and test tools. This record does not add a monitoring
platform, dependency, daemon or cleanup automation.

## Consequences

Expensive work gets a budget and a safe failure path before it starts.
Useful caches and parallelism remain available when they pay for themselves.
Measurement design and reserves have a cost, and conservative admission can
defer work on a busy host. Targets can become stale; record workload changes.

## Validation and acceptance

Review the method against one representative issue that has resource risk and
one change without material resource effect. Check that measurements separate
peak, temporary, retained and shared use. Keep the decision index and generated
ledger consistent. The decision method is accepted by the explicit 2026-11-09
operator approval above; workload budgets and operational exceptions still
require their separate reviews. This record
authorizes no cleanup, pruning, tuning, dependency installation or daemon.
