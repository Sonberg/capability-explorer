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

## Architecture gate

The existing ce.v1/ce.models.v1/ce.collection.v1 shapes and .NET/TypeScript
validators remain the contract. No production API, package, model call, source
execution or storage migration is proposed. The current diagram now makes
staging, atomic completion and separate attempt metadata explicit; these are
existing boundaries, not newly delivered runtime capabilities.

Only the collector worker writes collector tests, in its isolated worktree.
The SLO worker's GitHub-only allocation is independent; the queen alone enrolls
MADR-0035 and generates its checked projections. This avoids shared-manifest
conflicts and a second implementation of already-qualified consumers.

Architecture grade: **97/100**, one iteration. Three points retain unqualified
crash/host/environment behavior and external consumer authority, not a known
architectural defect. The unchanged-code baseline (208 TS/244 contracts) and
decision-ledger checks (29) pass; new assertions must pass at integration.
`git diff --check` passes. No full enterprise/SLO/release claim is made.
Brain pass clean: `sparc/capability-cards.md#sparc`; native workers plus the
already-enabled Ruflo bookkeeping are sufficient.

## Contract reconciliation

| #33 obligation | Current bounded evidence / remaining authority |
|---|---|
| Architecture, terminology and contract catalogue | Existing MADR-0033, model/collection guide and design-only OpenAPI profiles; central ownership/acceptance is unresolved, not a missing second collector. |
| Two-cluster central conformance, products and viewpoints | Not implemented by this slice. Offline two-repository dogfood is not central intake or viewpoint conformance. |
| Frozen collector subset and native compatibility | Existing `CollectionExporter`/`CollectionValidator`; no profile or ce.v1 shape change. |
| Identity/revisions/digests/paths/permissions/coverage | Existing .NET collection and transport tests; new TS consumer cases explicitly validate changed attempts and optional omissions. |
| Independent immutable offline consumption | Actual two-repository CLI dogfood and independent .NET/TS consumers pass; prior file/PostgreSQL/browser proof is retained rather than reimplemented. |
| Required failure / optional partial / reuse | Existing producer tests plus stronger staging-cleanup assertion; explicit optional disposition and byte-verified reuse/tamper cases. |
| Cancellation/interruption | Pre-cancelled export is tested. Mid-export cancellation and arbitrary crash recovery remain unproven; there is no deterministic public progress/stream seam. |
| Stale/out-of-order publication and enrollment | Offline freshness/rights/profile checks exist; authenticated central enrollment, read authorization and replay ordering are downstream, not delivered here. |
| Privacy, original content and source binding | Existing permission/profile denial and exact-source transport dogfood; no new arbitrary-URI acquisition or private-source publication. |
| Architecture/wiki handoff and release obligations | Current diagram, OKF article and this mapping are updated; #24/#54/#75 and operational SLO qualification remain separate. |

Focused source anchors: `extractors/ts/test/collection/validator.test.ts`
(attempt/content identity and optional omission), and
`platform/tests/CapabilityExplorer.Contracts.Tests/CollectionTests.cs`
(required failures, immutable reuse, optional input, permissions, pre-cancel).
Production cancellation checks occur during reads and immediately before the
atomic directory move; cancellation concurrent with the commit boundary is
cooperative cancellation, not a separately tested linearizable abort guarantee.
Avoid flaky timers/watchers or new production injection hooks merely to claim
that broader property.

## SLO applicability

The independent worker read back each persisted disposition:

| Issue | Disposition |
|---|---|
| #81 | Semantic/research quality: deterministic mapping and revision-bound preview gates, not an arbitrary operational latency SLO. |
| #82 | Provisional, unmeasured renderer p95 1.5 s / p99 3 s, with explicit resource/sampling/failure rules; bounded fixture and numeric node cap remain prerequisites for qualification. |
| #83 | Reuse #63's applicable endpoint policy; exact list-family mapping still required before qualification. |
| #84 | Reuse #43's sealed workload/resource/scenario policy; no competing target or compliance claim. |

MADR-0035 requires that applicable future issues include the complete measurement
contract or link a genuinely matching one. New objectives stay proposed/
unmeasured until accepted and exercised; correctness/security/completeness gates
are independent. It does not impose an SLO on semantic taste or authorize
an unrelated backlog rewrite.

## Retention design addition

The operator additionally requested MADR-0036 for interface-configurable
retention: enforced minimum, recommended default, separate local/central
custody, and progressively coarser historical resolution ending in monthly or
quarterly points where appropriate. This is documentation only, not a new
retention executor or UI. Concrete durations need policy-owner and workload/
legal requirements; none were supplied or invented. Archive/restore, expiry,
holds, shared dependencies and explicit loss-of-detail behavior are requirements,
not tested implementations. The source-bound decision ledger includes this
record with proposed status.

## Refinement gate

New acceptance commits `c41ef9f` and `f25e72f` add two independent TS cases,
explicit cleanup assertions and a deterministic .NET valid-first/required-missing
case that reaches partial staging before failure. The target and staging
directory are absent after failure. No runtime, schema, dependency or evaluator
change was needed; the inspection found existing behavior, not a reproduced
production defect.

`make EXECUTION=sandbox test-ts test-contracts test-collection-dogfood` passed:
210 TS tests, 244 contract tests and the real offline CLI dogfood. The final
partial-staging addition then passed all 17 focused .NET collection tests with
an actual build in the sandbox. `make test-decision-ledger` passed all 29 tests
after both MADRs and their exact source receipts were generated. Build output
reports zero warnings/errors; existing frontend chunk-size advisories remain.
`git diff --check` passes.

Refinement grade: **97/100**, two iterations. The second iteration replaces a
weak missing-first cleanup assertion with explicit valid-first partial-staging
proof. The three-point deduction retains untested mid-export cancellation/
crash behavior and external consumer authority, not a known runtime defect.
Real file/PostgreSQL/browser consumption was qualified in #85; no UI behavior
changed here and that prior evidence is not relabeled as a new browser run.
Complete canonical local/hosted publication gates remain required.

What was not tested: arbitrary interruption, real central custody/authorization,
retention UI or deletion/archive execution, lawful numeric retention periods,
private corpus/recovery, SLO performance/capacity or a production release.
No new paid external service/model call or daemon was used. Native agent
metering is unavailable; no savings or distributed-consensus claim is made.
Brain pass clean: `sparc/capability-cards.md#sparc`; no extra runtime primitive
is justified by these test and policy changes.
