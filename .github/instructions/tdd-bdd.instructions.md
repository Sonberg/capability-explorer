---
applyTo: "**"
---

Follow [MADR-0032](../../docs/adr/0032-tdd-bdd-delivery.md) when adding or changing
behavior. Define acceptance-linked Given/When/Then scenarios before production
changes, then implement through focused red-green-refactor tests in the existing
runner. Each scenario must map to an executable test; no new BDD framework is
required.

Observe a meaningful assertion failure on pre-change behavior, then green with
the same test and directly affected regressions. Record exact source identities,
commands and results. Tooling, compilation or infrastructure failure is not red;
do not fabricate history or weaken tests to claim green. Report already-covered
behavior honestly and test only genuine new/fixed behavior.

Use deterministic barriers for concurrency and real boundary tests where mocks
cannot prove the contract. Keep workers in isolated scopes and serialize heavy
test batches. Preserve complete local/hosted gates under MADR-0029 and the
JavaScript-to-TypeScript 7 rule under MADR-0031. Documentation-only changes need
no artificial red-green test; run existing documentation validators.
