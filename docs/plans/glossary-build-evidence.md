# Deterministic glossary build evidence

Base: `f2b3e6037a067f2dc1a8935aa67d1c5934fd8fc4`, PR #5 hosted CI successful.
Branch: `feat/deterministic-glossary`, stacked on PR #5; no merge authorization.
Contract: [Brain Prompt](deterministic-glossary.md), issue #4 layer 1 only.

## Acceptance mapping

| Requirement | Planned verification |
|---|---|
| Explicit normalized names, no inferred equivalence | normalization positives/negatives; calendar/active and Qty/Quantity stay separate |
| Evidence/occurrence identity | artifact/revision/unit/key and source evidence tests |
| Read-only capability/revision projection | glossary API selection tests; no database/model configured |
| Factual differences, unknowns preserved | identical/hash/known-vs-unknown comparison tests |
| Deterministic bounded work | shuffled input/cultures/byte equality, pair-limit disclosure |
| Usable read-only tab | UI build plus real browser search/filter/detail/evidence checks |
| No regression or new CI resources | full installed pre-push gate and scope diff |

Routing: native Codex workers, bounded by four active slots. Sol high for implementation,
Sol medium for tests; independent consequential review reserved for gate decisions.
No provider API routing invoked; monetary spend/savings unknown. Runtime feature has
no model calls or additional services/images. Ruflo records coordination, not execution.

## Specification

`make fixtures golden-check` exited0: locked revisions verified, 18 golden artifacts current.
Scope/examples40/40, data/evidence boundaries30/30, failure/bounds cases30/30:
S grade100/100, iteration1. No product implementation claimed at this gate.
Brain closeout: sparc/capability-cards.md#sparc; no added runtime dependency.
The worktree ledger invocation refused a missing policy-anchor log. No policy was
relaxed: using the existing canonical project ledger succeeded, swarm
`swarm-1793482077261-gy7efz`, hierarchical/max4. Worker execution remains native.

## Pseudocode

Collection/identity/order/error paths and O(N log N)+bounded64-pair complexity are
recorded in the contract. Executable normalizer and DTO tests integrated from
worker commits ea826f5/c84338e. `make test-explorer` exited0: Explorer70,
Interpretation28, Capabilities28 passed with zero skips; build0 warnings/errors.
First run exposed test-only invariant globalization, corrected by enabling ICU
cultures in Explorer.Tests only; production remains invariant. No test skipped.
P rubric: acceptance40/40, deterministic order30/30, bounded/errors30/30;
grade100/100, iteration2. Log /private/tmp/ce-glossary-p-2.log.
Brain closeout: sparc/capability-cards.md#sparc; no additional dependency warranted.
Full projection/UI behavior not yet tested. No paid provider routing; spend unknown.
