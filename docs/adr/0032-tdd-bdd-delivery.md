# Drive implementation with behavior scenarios and red-green-refactor tests

* Status: accepted
* Date: 2026-11-03
* Decision owner: repository contributors
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [MADR-0029](0029-frugal-ci-and-executable-parity.md), [MADR-0031](0031-typescript7-on-touch.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: repository-wide TDD/BDD decision policy; no new framework mandated or feature-test evidence established.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:c0e006c0df4b5d8df48a69b9e27f61ef5aae955d6b8355b497b3abdac9bda934`.
Previous decision status (historical): `proposed`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem statement

Passing tests after implementation does not prove that the tests detect the
requested behavior or that acceptance criteria are shared and observable.
Concurrent workers can also implement incompatible interpretations of an issue.
The operator requests TDD and BDD in the next implementation plan and a durable
decision recording that practice.

## Decision drivers

Observable acceptance criteria, meaningful regression detection, deterministic
failure-path coverage, isolated parallel work and frugal existing CI.

## Considered options

1. Implement first and add tests afterward: rejected as the default workflow.
2. Introduce a separate BDD framework and duplicate all existing tests:
   rejected without a concrete integration need.
3. Use BDD examples to define behavior and TDD to implement it in existing
   runners, preserving source-bound evidence: selected for review and this work.

## Decision outcome

Before changing production behavior, translate each new or changed acceptance
criterion into a uniquely identified Given/When/Then scenario in domain
language. Include relevant success, invalid-input, failure, cancellation and
concurrency cases. State observable outcomes rather than internal method calls.
Map each scenario to an executable test name/selector and its acceptance
criterion; a prose scenario alone is not executable BDD evidence.

Use the existing test project and runner where suitable. Scenarios can live
alongside the executable tests or in an existing maintained behavior contract.
Gherkin files and a new framework are not required. Do not copy the same
behavior across unit, integration and end-to-end suites merely to label it BDD.
Use the lowest layer that proves the contract; use real database/API boundary
tests when mocks alone cannot prove persistence or externally visible behavior.

For each behavioral increment, use red-green-refactor:

1. Add a focused executable test first. Run it against the pre-change production
   behavior and observe a failure for the intended missing/incorrect behavior.
   Missing tools, compilation failures, unavailable infrastructure and arbitrary
   injected failures are not a valid red result. Establish required interfaces
   or a compiling test seam explicitly where necessary, without implementing
   the behavior under test.
2. Make the smallest production change that satisfies that test and the
   acceptance scenario. Rerun the same selector to green.
3. Refactor only while retaining green behavior, then run directly affected
   regressions. Reuse an existing test that demonstrably fails for the defect
   rather than writing a duplicate just to obtain a red receipt.

Record the scenario/test selector, command, exit result, meaningful assertion
failure and exact test/production source identities for red and green.
Use clean commits or immutable dirty-tree snapshots, including new test files.
The test must remain materially the same; report intentional corrections rather
than presenting a weakened assertion as a successful implementation. Never
reconstruct a fictitious red run after implementing the feature. If behavior
already passes, report existing coverage and implement only genuine gaps.

Use deterministic barriers, controlled clocks and bounded timeouts to test
concurrency and cancellation. A timeout is a deadlock safety bound, not proof
that workers overlapped. Do not replace real capability-scheduler tests with
tests of an unrelated within-run extraction pool. Keep database and heavy
container batches bounded separately from the number of coding agents.

## Parallel delivery and validation

The integration owner freezes acceptance scenarios and shared contracts before
workers implement in isolated worktrees. Each worker owns its scenario tests,
production paths and red/green receipts. The lead reconciles boundary scenarios
after integration and reruns tests invalidated by source or interface changes.
An independent review checks behavior/evidence when warranted; a vote, score,
or worker assertion is not a substitute for executing the tests.

Run the smallest relevant selectors during development. Follow MADR-0029 for
the complete local/hosted gate before publication; do not add redundant CI jobs,
skip baseline gates or reuse cached test-success receipts. Follow MADR-0031
when modifying maintained JavaScript tests or tooling. No new test package or
paid testing service is mandated by this decision.

The next-phase plan must make missing scenario mappings, invalid red evidence,
failed green tests, stale source receipts and unexecuted boundary scenarios
blocking. A future gate helper must validate these records and fail closed.
This documentation change does not implement that helper or claim that the
planned feature tests already exist.

Documentation-only changes do not require artificial red-green tests. Existing
documentation/ledger validators still apply. For other cases where a valid red
run cannot be obtained safely, report the reason and obtain a scoped explicit
owner exception; do not silently relabel test-after development as TDD.

## Consequences

BDD supplies the shared behavioral contract; TDD supplies the feedback loop
that implements it. Review gets evidence that tests detect missing behavior,
not only a green final suite. Test seams and source receipts add some work,
but small selectors and reuse of existing runners bound that cost. This is not
a coverage-percentage target, a requirement for browser tests everywhere, or
repository-wide acceptance by implication.
