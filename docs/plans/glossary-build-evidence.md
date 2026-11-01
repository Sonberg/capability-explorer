# Deterministic glossary build evidence

Base: `f2b3e6037a067f2dc1a8935aa67d1c5934fd8fc4`, PR #5 hosted CI successful.
Branch: `feat/deterministic-glossary`; initially stacked on PR #5, now based on
main after PR #5 merged as98be7a4. No merge performed by this build.
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

## Architecture

Frozen GlossaryDtos and mirrored TypeScript contracts; read-only API scopes via
existing ViewIndex and CapabilityService. MADR-0004 distinguishes candidates from
reviewed equivalence. No new dependency/service/schema/fixture/threshold change.
`make test-explorer` exited0: Explorer88, Interpretation28, Capabilities28 passed,
zero failed/skipped; UI and solution build0 warnings/errors. Architecture rubric:
contracts40/40, context/evidence isolation40/40, compatible integration20/20;
A grade100/100, iteration3. Log /private/tmp/ce-glossary-a-3.log.
Iterations fixed private-helper return types and test analyzer compliance (no
suppression). API enum decoding explicitly matches the existing wire serializer.
Independent Astra review found same-symbol calculation first-winner ambiguity and
missing nested JSON property-order coverage. Commits27bb122/192e997 preserve every
record with stable record-ID identity and add meaningful regressions; independent
re-review found no remaining blockers. All88 tests execute these assertions.
Brain closeout: sparc/capability-cards.md#sparc; no additional runtime dependency.
Browser behavior and full cross-project CI remain pending. No provider API spend
invoked; native worker monetary cost unavailable, no numeric savings claimed.

## Refinement

User-requested conflict resolution for PR #5 reconciled upstream924b32b (analysis
job cancellation). PR #5 merge66571bc preserves analyze-worker and all local-CI
Makefile targets; glossary branch incorporated it cleanly as2149b51.
Post-merge `make explore GITHUB_AUTH=none` built the UI/solution successfully, then
focused no-build sandbox tests passed Explorer89, Interpretation28, Capabilities28,
zero failures/skips. Log /private/tmp/ce-glossary-r-tests.log.

A real Chrome156 browser via CDP exercised the running localhost5080 feature:
weekly-sales scope31 candidates; search narrows average labels; active-week detail
opens actual exact-revision source; api-cs v1 switches to a distinct calendar-week
candidate; demand-pricing scope54 candidates with no stale previous source panel;
search-empty and empty capability states; unknown types; simulated network failure
and recovery. Metadata:18 artifacts, problems[], interpretation disabled, no
similarity database. Browser result log /private/tmp/ce-glossary-browser-results.json;
screenshot /private/tmp/ce-glossary-browser.png. The network failure was simulated
in the browser fetch boundary, not an asserted production outage.

R rubric: acceptance40/40, determinism20/20, failure/boundaries20/20, interactive
UI20/20; grade100/100, iteration1. Independent review blockers resolved and
re-reviewed before this gate. Brain closeout: sparc/capability-cards.md#sparc.
Not tested: every browser/device, concurrent live artifact mutations, or performance
at production-scale datasets. No paid API calls; dollar spend/savings unknown.
Full cross-project CI on the final glossary commit remains the completion gate.


## Completion

The full installed pre-push act gate passed on clean source
`fc81e7f0939c408c02172f11ad0caccfbeaf99f9`, then pushed successfully.
Log: /private/tmp/ce-glossary-push.log. It ran Trivy, action audit, fixture builds,
contract/evaluation/extractor checks, all golden thresholds, UI build and store
integration. Node tests108; .NET Contracts105, Evaluation25, Extractors7,
Explorer89, Interpretation28, Capabilities28, Store15; zero failed/skipped.
PR #5 hosted CI also passed; a contributor merged it as98be7a4. Integrating that
main commit changed no files relative to the tested glossary source. The final
documentation commit is subject to the same mandatory pre-push gate.

Traceability is the acceptance matrix above plus GlossaryTests/GlossaryApiTests
and the Refinement browser observations. MADR-0004 and HANDOFF describe the actual
read-only boundary. Issue #4 explicitly distinguishes the project's own ubiquitous
language from the analyzed-repository vocabulary shown by the product tab.
No migration, model, database configuration, or deployment is required for this
slice; rollback is reverting the feature commits. No production deployment made.
Completion rubric: full suite40/40, running UI20/20, docs/traceability20/20,
independent review20/20; C grade100/100, iteration1. This is the scoped build
rubric, not a claim of exhaustive production quality or hosted CI success.
Brain closeout: sparc/docs/API.md and sparc/docs/tutorial.md describe evidence
and traceability boundaries; no additional runtime primitive is needed here.
Not tested: all browsers/devices, concurrent live artifact mutation, or
production-scale performance. Native worker spend remains unknown; no paid
provider routing or runtime model calls were invoked. Remaining issue #4 layers
(persistence, reviewed links, definitions and flows) are deferred.
