# Evaluate RuVector separately from canonical fact storage

* Status: accepted
* Date: 2026-11-01
* Related: issue #28, [PostgreSQL snapshots](0017-postgres-explorer-snapshots.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: RuVector evaluation boundary only; extension adoption requires a separate owner decision.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:db065517e315495d69ffd8ea5d2b3af063e4a24df6309df71a7a8e1fa1dba278`.
Previous decision status (historical): `proposed; experiment authorized, adoption requires owner decision`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem statement

RuVector is an explicit candidate for accelerating graph/vector workloads inside
PostgreSQL. Documentation and an in-memory cache implementation alone establish
neither production suitability nor lack of durability. Current implementation,
the exact installed binary and actual recovery behavior must be distinguished.

## Decision drivers

Frugality, measured workload benefit, repeatable results, recovery, compatible
PostgreSQL packaging and preservation of deterministic artifact evidence.

## Considered options

1. Retain PostgreSQL + pgvector.
2. Add or substitute a pinned RuVector PostgreSQL extension after evaluation.
3. Replace canonical storage with standalone RuVector now.

## Proposed decision

Retain option 1 while evaluating option 2 independently. Defer option 3 until
equivalent integrity/recovery and a concrete workload need are demonstrated.
Installation or compatibility failures are not passing results and do not block
the core PostgreSQL migration.

Use disposable isolated databases, pinned versions and image digests, fixed
hardware limits, corpus, embeddings and query inputs. Investigate rollback,
restart, abrupt termination, multi-session visibility and bounded memory, including
cache rebuild cost. Record source/image provenance gaps explicitly. Reuse
precomputed embeddings; no paid inference or automatic model downloads.

Exact fact queries require parity; approximate retrieval requires separately
measured recall/latency tradeoffs. Freeze adaptive learning during repeatable
experiments. Learned/inferred output remains distinct from deterministic facts.
No test may rewrite facts to make vector results appear equivalent.

Proposed promotion criterion: all correctness/recovery/security gates plus a
relevant p95 improvement of at least 20%, exceeding run variance, with no more
than 10% total resource regression unless the owner accepts a documented tradeoff.
These figures remain proposed thresholds. No speedup is claimed.

## Validation and consequences

Publish a reference log, executable bounded probe and source-bound report including
not-run cases. Keep the candidate out of default CI/runtime images until adoption.
An inconclusive or failed experiment leaves pgvector in place. The root integration
owner controls shared dependencies, migrations and delivery; the repository owner
retains the extension adoption and merge decisions.
