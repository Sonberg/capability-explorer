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

## Integration evidence

Implementation source checkpoint: `6eb958d` (before final documentation/browser
harness refinements). Baseline stage `94213b1` passed 105 Contracts, 89 Explorer,
28 Interpretation and 28 Capabilities tests. Candidate solution build passed with
zero warnings/errors; 138 Explorer and 37 Store tests passed with zero skips.
Store checks ran actual PostgreSQL containers, including 22 glossary cases.

Commands used through the existing sandbox:

```sh
make test-contracts test-explorer
scripts/sandbox --offline dotnet sh -c 'cd platform && dotnet build --no-restore -nologo CapabilityExplorer.slnx && dotnet test --no-build tests/CapabilityExplorer.Explorer.Tests'
# On Linux (and inside the existing act runner on macOS):
scripts/sandbox --docker dotnet dotnet test platform/tests/CapabilityExplorer.Store.Tests --no-build -nologo
```

On this OrbStack host the pinned existing act-24.04 runner mounted the host socket
as `/var/run/docker.sock` and the worktree at its same absolute path, then invoked
the unmodified sandbox. No test/gate was disabled and no additional CI image was
introduced. Local preview was started with `make explore PORT=5094 GITHUB_AUTH=none`
and a dedicated loopback PostgreSQL instance; port 5095 verifies database absence.

Representative new assertions:

- `GlossaryFlowTests`: 21 cases for exact occurrence/revision/unit identity,
  deterministic order and culture, cycles, limits, missing/ambiguous boundaries,
  service bindings, calculation input references and distinct human memberships.
- `GlossaryFlowFixtureTests`: two real locked demand-pricing cases preserve the
  original REQUESTS/binding evidence: forecast v1 resolves and v2 is broken, with
  exact selected revisions disclosed and no claim of field-level propagation.
- `GlossaryReviewStateTests`: exact fingerprint/current, changed content/stale,
  excluded scope, missing symbol and multiple-role ambiguity without rebinding.
- `GlossaryReviewApiTests` and write-guard/database-failure tests: deterministic
  source-only operation, invalid scope/revision/limits, observed provenance,
  local same-origin JSON/header requirements and unavailable configured DB.
- `GlossaryHttpJourneyTests`: real HTTP + PostgreSQL CRUD/retries, version conflicts,
  host restart persistence, scope rejection, and same-commit reanalysis rejecting
  the old content fingerprint before accepting explicitly reviewed current inputs.
- `GlossaryStoreTests`: concurrency, parent/child transactions, forced audit-error
  rollback, cancellation, soft deletion, source artifact immutability and bounded
  lists. Deletion retries require the same supplied reviewer.

Independent read-only review found and resolved: missing configured-DB HTTP tests,
permissive revision fallback, mismatched issue length limit, delete-retry attribution,
editable immutable parent choices, hidden exact edge provenance and directional
arrows on symmetric human associations. Browser testing also found and fixed UUID
creation on local HTTP aliases using cryptographic bytes, never `Math.random`.

The optional Ruflo heuristic scan returned 28 SQL-interpolation warnings in
unchanged extractor/parser/test/spike text and skipped dependency scanning because
there is no root package manifest. Inspection showed these findings describe text
parsing/rendering, not executed SQL. It is not a dependency/security pass. New Store
queries parameterize values; identifiers come only from a private enum. The required
Trivy and complete act gate remain authoritative pre-push checks.

## Compatibility and boundaries

MADR-0006 Unreleased notes describe the additions; MADR-0007 classifies these as
additive endpoints, UI and migration without changing ce.v1 or publishing a version.
Migration 0004 does not rewrite imported artifacts. A rollback to earlier application
code leaves the additive review tables in place; dropping review data is not an
implicit rollback action. Review writes need a writable PostgreSQL connection.

This package does not establish field-level propagation, semantic equality or
runtime traffic. Graph limits bound returned traversal, not total artifact indexing
cost. Large real-world corpora, authenticated remote multi-user access, database
administrator tamper resistance and physical-display performance are untested and
outside scope. Existing #6 gaps, #12 owner decisions and #13 performance stay open.
Live LikeC4 integration and inferred/LLM proposals are not delivered here.

Native worker spend cannot be measured by this session; no savings figure is
claimed. Additional provider inference calls: zero. Ruflo's automatic turn recorder
reported readback failure; the manually stored project checkpoint was retrieved by
exact key and is the recovery receipt, not evidence that automatic recording works.

Completion requires a clean final commit, the ordinary push hook's full local
act/security success, then fresh `make pr-check` against stage. Exact final source,
receipt and owner-review PR are recorded in the GitHub handoff; no merges, releases
or auto-merge are authorized by this execution record.

## Served browser proof and phase assessment

Final functional/UI source: `dc88c0a`; served bundle `index-6xsL7eNY.js`.
The checked-in `scripts/test-glossary-flow.mjs` passed 52 assertions against
PostgreSQL preview 5094 and 34 against database-disabled preview 5095. Both covered
1440×900, 5120×2160 and 390×844. Actual CRUD, term update, issue resolution, deletion,
reviewed-edge occurrence provenance and symmetric-arrow rendering passed. Script-like
content remained text. Browser 409/503 simulations test draft/error handling only;
real conflict/outage behavior is independently exercised by HTTP/PostgreSQL tests.

```sh
CE_GLOSSARY_BASE_URL=http://host.docker.internal:5094 \
SANDBOX_PASS_ENV=CE_GLOSSARY_BASE_URL \
SANDBOX_NODE_IMAGE=mcr.microsoft.com/playwright:v1.64.1-noble@sha256:47b510dd9230bf0b9bfc9b20102241e577ea7a99334ab4c3ca19f19770e9b8ea \
scripts/sandbox node node scripts/test-glossary-flow.mjs
# Repeat with CE_GLOSSARY_BASE_URL=http://host.docker.internal:5095.
```

The isolated UI worker used the integration worktree's existing Playwright package
through a read-only `SANDBOX_SOURCES` mount. Receipts/screenshots are ignored local
artifacts under `.sandbox-cache/glossary-browser/{5094,5095}/`. Both the worker and
integration owner inspected screenshots. The canvas retains pan/zoom on resizing;
checks explicitly click its existing Fit View control. Narrow screens require
pan/zoom for graph text and horizontal scrolling for revision coverage.

Self-assessment against the Brain Prompt rubrics (judgment, not an external audit):

| Phase | Score /100 | Evidence and deductions |
|---|---:|---|
| Specification | 98 | MADR-0008 + mapped assertions + unchanged baseline; product traversal caps are not production-corpus capacity claims. |
| Pseudocode | 98 | 21 deterministic flow cases plus two locked binding cases and concurrency tests; indexing still visits selected artifacts before bounded traversal. |
| Architecture | 98 | Isolated workers, existing canvas/store/sandbox, additive migration and full Store suite; serialization deliberately favors low-volume review simplicity. |
| Refinement | 97 | 138 Explorer + 37 Store + 86 browser assertions; local Chromium/fixtures only, no remote authenticated deployment or physical-display claims. |
| Completion | pending gate | Documentation and focused evidence complete; exact clean-HEAD act/security receipt and fresh stage alignment must pass before PR handoff. Final completion score belongs in that handoff. |

All mandatory functional gates above passed after the listed fixes. Completion is
not pre-graded from focused tests. The PR handoff records its actual final gate result.
