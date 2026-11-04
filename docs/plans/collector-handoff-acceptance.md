# Collector handoff acceptance and measurable issue objectives

## Scope and ownership

Base: main `96360296b6ee3b7877f4d0ec157c76665411d18c`, including owner-merged
#85. Deliver only missing #33 collector acceptance proof or demonstrated fixes.
Existing independent .NET/TypeScript validation and file/PostgreSQL browser
consumption are not missing functionality. Central middleware, accepted
business semantics, enterprise aggregation and #59 are outside this slice.

The queen owns documentation, decision-ledger enrollment, schemas/build/CI and
integration in `feat/collector-handoff-acceptance`. One isolated native worker
owns collection source/tests in `work/collector-handoff-acceptance`. A separate
worker assesses GitHub #81-#84 without source writes. Both Ruflo swarm and
hive-mind were initialized before workers, max four including queen,
autoscaling disabled, no daemon. Native workers execute; local registration
does not establish distributed consensus or grant authority.

The operator additionally requested MADR-0035: require scoped SLOs for new
performance and other measurable operational-quality issues. Pure correctness
still requires functional gates, not an arbitrary latency target.

## Acceptance criteria

1. Required missing/invalid input cannot yield a successful completed export;
   interruption/cancellation evidence must match the actual supported boundary.
2. Optional failure stays explicitly partial; existing immutable last-good
   content is neither corrupted nor silently replaced.
3. Attempt-only metadata changes do not change immutable content identity;
   conflicting bytes under an existing identity are refused.
4. Reconcile each criterion against existing tests first; add only genuinely
   missing focused proof and fix only demonstrated defects.
5. MADR-0035 and its source-bound decision navigation stay consistent.
   #81-#84 each receive an applicability disposition with quantitative policies
   where meaningful, separating proposed targets from measured compliance.
6. Existing targeted tests, actual collection dogfood, full local CI and
   exact-head hosted PR checks pass without weakened coverage.

## Specification gate

`make EXECUTION=sandbox test-ts test-contracts` passed at the baseline:
208 TypeScript tests, 244 contract tests, build zero warnings/errors.
`make test-decision-ledger` passed 29 tests after explicit enrollment and
generation of MADR-0035's source receipts. `git diff --check` passes.
The ownership acknowledgement is on #3; there were no open PRs at claim time.

Specification grade: **96/100**, one iteration. Four points retain external
consumer acceptance, unavailable private-corpus/recovery and environment/SLO
qualification beyond this synthetic slice; these are not baseline failures.
The measurable criteria above are frozen, but test additions still require
source inspection to distinguish missing proof from already-correct behavior.
No new dependency/service is needed. Brain pass:
`sparc/capability-cards.md#sparc`, methodology only.

What was not tested: private input/recovery, operational SLO compliance,
production release, enterprise aggregation or downstream business approval.
Cheap-routing/checkpoint scripts are absent; session tracking is used instead.
No paid external calls were added; native agent spend is not exposed and no
monetary savings are claimed.

## Pseudocode gate

```text
map criterion to current producer/consumer assertion:
  passing assertion -> retain and cite it, do not implement a second consumer
  missing proof -> add a focused assertion using real canonical bundle bytes
  reproduced defect -> preserve failing test, apply the narrow fix, rerun it

consumer acceptance examples:
  same content, different valid attempt metadata -> same content identity
  missing optional artifact -> explicit partial disposition, never full success
  required invalid input or cancellation -> no completed export
  conflicting/corrupt target -> reject, preserve prior immutable content

issue acceptance:
  measurable operational behavior -> scoped SLO or matching policy link
  pure correctness/research -> explicit functional gate, no invented latency
  unavailable baseline/workload -> proposed/unmeasured, not compliance
```

Producer publication remains staged and validated before rename. Pre-cancelled
export is not proof of arbitrary mid-export interruption/crash recovery; the
final evidence matrix must preserve that distinction. No change to source
acquisition, authority, canonicalization, identity or schema is planned.
Acceptance uses the current linear artifact scan within existing byte/count
budgets; this test-only slice adds no new runtime complexity.

Pseudocode grade: **96/100**, one iteration. The four-point deduction is remaining
integration/negative-path qualification, not an unexplained error path.
Baseline `test-ts` (208) and `test-contracts` (244) remain green; no source code
has changed in the integration checkout. New worker assertions still need
validation before Refinement. Brain pass clean:
`sparc/capability-cards.md#sparc`; no new stack primitive is justified.
