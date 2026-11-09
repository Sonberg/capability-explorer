# Require measurable objectives for performance and operational-quality issues

* Status: accepted
* Scope: repository-wide SLO decision policy; workload targets and compliance require separate evidence
* Previous scope (historical): operator-directed workstream rule; repository-wide adoption pending review
* Date: 2026-11-04
* Decision owner: repository maintainers
* Coordination: issues #13, #43, #63, #60 and #81-#84
* Related: [issue governance](0024-issue-labels-and-dependencies.md), [measured optimization](0029-frugal-ci-and-executable-parity.md), [behavioral validation](0032-tdd-bdd-delivery.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: repository-wide SLO decision policy; workload targets and compliance require separate evidence.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:876fe9a67c7bc1766847a4262199978c9b840c28be69755f242d350183b098d1`.
Previous decision status (historical): `proposed`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem statement

"Make it faster" has no verifiable completion or optimization stop condition.
The operator requires SLOs for every new performance issue and for other issues
whose user-facing operational quality can be measured meaningfully. Existing
targets in #13, #43 and #63 and the integration gate in #60 are internal policies,
not measured compliance or promises to external consumers.

An SLI is the measured indicator; an SLO is its quantitative target within a
declared workload and observation window. An SLA is a separate external
agreement. A passing unit test or a single fast sample is not SLO compliance.

## Decision drivers

Testable acceptance, bounded cost, reproducible measurements, explicit
uncertainty and a clear point at which optimization stops. Correctness,
security, accessibility and evidence integrity must not be traded for speed.

## Considered options

1. Use qualitative performance goals and optimize until reviewers are satisfied.
2. Require scoped quantitative SLOs and executable evidence before claiming success.
3. Assign the same latency or availability target to every issue.

## Decision outcome

Apply option 2 in this operator-authorized workstream and propose it for all
contributors. Every newly created or materially refined issue concerning
latency, responsiveness, throughput, capacity, availability, freshness,
recovery/cancellation time or resource consumption **must include an SLO
section**, or link an existing policy whose workload and boundaries genuinely
match. Do not copy a search budget onto a layout worker or use server response
time as a proxy for final painted UI settling.

At creation/triage, document the measurement contract below. A missing baseline
does not justify inventing measured numbers: label initial targets **proposed,
unmeasured**, name their rationale and accountable reviewer, and schedule
bounded baseline collection. Before implementation is marked ready, settle the
target and measurement contract. A bounded baseline/profiling investigation may
proceed with explicit provisional targets; broad optimization may not.

| Required field | What the issue records |
|---|---|
| Scope and owner | User operation, affected execution path, accountable acceptance owner and exclusions. |
| Indicator and target | Units and numeric bound, start/end events, percentile or good/eligible-event ratio, and permitted error rate. |
| Workload | Versioned fixture/source identity, data size/shape, interaction sequence, concurrency and offered load. |
| Resource envelope | CPU, memory, platform/runtime, engine/browser/backend and relevant storage/network assumptions. |
| Sampling | Warm/cold distinction, preparation, number of independent runs/events, window, percentile method and repeatability rule. |
| Failures and cancellation | Include timeouts, errors, dropped requests, incomplete/partial results and cancellations; state exclusions explicitly. |
| Guardrails | Correctness, completeness, determinism, security, accessibility and evidence/provenance checks that must still pass. |
| Evidence and status | Executable command, exact source/build/configuration/fixture identity, raw results and verdict: unmeasured, met, breached or inconclusive. |
| Stop/reopen rule | Stop when the scoped SLO and guardrails pass; reopen for a reproduced breach or a reviewed change in workload/target. |

Percentiles need enough declared samples; a timeout must not vanish from the
denominator or be reported as a successful low-latency result. Cold starts,
warm steady state and cancellation are separate indicators where applicable.
Define an error budget when using event-ratio objectives; do not fabricate one
for an unrelated resource ceiling. A policy or workload change needs a rationale
and acceptance-owner review, not post-hoc relaxation to make a run pass.

### Applicability and delivery

Classify each issue at triage. Pure semantic correctness, missing data,
permissions, security invariants or visual taste use explicit functional or
review gates, not an arbitrary latency SLO. Mixed issues retain those gates
**and** an SLO for the measurable operational part. Record why an SLO does not
apply instead of creating a meaningless target.

Reassess existing issues when they are touched; this decision does not reopen
closed issues or authorize an unrelated mass edit. The #81–#84 assessment is
the current bounded application. Existing matching issue policies remain the
source of their numeric targets; this record creates no universal 200 ms rule.

Before optimization, reproduce a breach or demonstrate the missing measurement.
Use the smallest existing benchmark/test path; no new paid service, daemon or
heavy CI job follows automatically. Before review/closure, link source-bound
measurement evidence and passing guardrails. If the required workload or
environment is unavailable, state the gap: implementation may be reviewed
separately, but SLO acceptance remains unmet. Synthetic results qualify only
their declared tier, not private-corpus or enterprise capacity.

## Consequences

Issues become verifiable and engineering effort stops at an agreed bar.
Measurement design and maintaining representative fixtures have a cost.
Targets can become stale, so workload and policy revisions must remain visible.
No benchmark results, automation, release clearance or external service
guarantee are established by adopting this policy.

## Validation and acceptance

Review a matching-policy reuse, an issue-specific quantitative contract and
an explicit non-applicable disposition. Check each required field, run the
declared measurement commands at the named source state and inspect failures
as well as percentiles. Keep the decision index and generated ledger consistent.
The explicit 2026-11-09 operator approval above accepts this decision policy;
it does not establish measured compliance or change another MADR's status.

## References

* [Google SRE: Service Level Objectives](https://sre.google/sre-book/service-level-objectives/)
  defines SLIs as quantitative indicators and SLOs as target values or ranges.
  Its examples inform terminology, not this repository's numeric targets.
