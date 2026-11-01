# Cross-repository flow execution record

Execution authorized 2026-11-01 from the Brain Prompt in
[cross-repository-value-flow.md](cross-repository-value-flow.md).
Base: stage `94213b1` (PR #17 merged); integration branch `feat/glossary-flow-review`.
Deliver as one owner-review PR with independently reviewable commits to avoid a
pending stack. No automatic merges, releases, main changes or provider calls.

## Ownership and routing

Integration owner: API/shared registration, source validation, write boundary,
MADR-0008, docs/changelog, integration tests and local preview port 5094.

| Worker | Native model / effort | Isolated branch | Owned work |
|---|---|---|---|
| Flow | gpt-6.1-sol / high | work/glossary-flow-kernel | New flow projection/DTOs and focused Explorer tests |
| Store | gpt-6.1-sol / high | work/glossary-review-store | New store/models, assigned migration 0004, Store tests |
| UI | gpt-6.1-sol / high | work/glossary-flow-ui | Glossary UI/modules, minimal existing-canvas export, browser harness |

Ruflo CLI initialized a hierarchical coordination ledger with maximum four agents:
`swarm-1793528010719-qamt8j`. Native Codex workers execute; this ledger is not proof
of execution or distributed locking. No paid-provider router/checkpoint script is
present in this repo. Native costs are not metered here; additional provider calls: 0.
Brain grounding: `sparc/capability-cards.md#sparc` supports phase discipline only;
no vector capability is needed for this bounded deterministic graph.

## Acceptance map

- Exact revision/unit occurrence identity; stable graph under shuffled inputs/culture;
  cycles, missing mapping and same-name cross-repo negatives: GlossaryFlow tests.
- Bounded traversal with explicit incomplete/truncated reasons; source vs binding
  vs human assertion separation: GlossaryFlow tests and API response checks.
- No database read-only; valid selected occurrence required for writes; stale,
  orphaned, ambiguous and excluded references: review service/API tests.
- Real persisted CRUD, conflict/retry semantics, transaction rollback, soft delete,
  immutable artifacts and audit attribution: GlossaryStore tests.
- JSON/custom-header/local-host/origin rejection, configured-DB failure: API tests.
- Actual served flow selection/evidence and persisted review, 409 recovery,
  database-disabled state at desktop/ultrawide/narrow sizes: focused browser script.

Specification and architecture are recorded in MADR-0008 and frozen worker DTOs.
No phase is reported passing solely because a document or baseline test exists.
Final per-phase evidence, scores, limitations and handoff will be appended after
implementation and independent review.
