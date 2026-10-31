Proposed next package for #4: **deterministic glossary candidates and a read-only tab**. Execution authorized by the operator via Brain Build. Stacked on PR #5 head f2b3e60; claims recorded on issue #4. Reviewed against HANDOFF, the project glossary, existing Explorer contracts and issue #4. The remaining layers stay separate.

One acceptance correction is necessary: name normalization cannot honestly merge `AveragePerCalendarWeek` / `CalendarWeekAverage` with `AveragePerActiveWeek`. Worker/web active-week names can group; calendar-week names remain separate until a later explicit human link or reviewed proposal. Multiple occurrences do not prove identical business meaning, and multiple calculations do not by themselves prove divergent derivations.

Paste-ready Brain Prompt:

## GOAL

<goal>Deliver issue #4 layer 1: a deterministic, source-backed GET /api/glossary endpoint and read-only Explorer tab that groups explicit names into term candidates, exposes evidence and actual differences, and works without a model or database.</goal>

## ASSUMPTIONS

- <repository>Sonberg/capability-explorer</repository>; GitHub account Tigon32.
- <base>Current main after PR #5 lands, or an explicitly coordinated stacked branch on its reviewed head.</base> Fetch, inspect claims and record the exact base before editing; never assume this proposal reserves files.
- Read docs/HANDOFF.md, docs/ubiquitous-language.md and MADR-0001/0002/0003 first. The project glossary defines engineering vocabulary; the product tab shows candidate domain vocabulary. Neither silently rewrites the other.
- This slice includes capability/revision filtering, evidence navigation, a searchable list/detail tab and factual differences. No flow graph, persistence, same-value links, LLM definitions, embeddings, work-item integration, IaC binding or new services.
- Preserve ce.v1, extractors, fixture source/revisions/goldens, thresholds and dependency locks. No new runtime dependencies or model calls.
- A candidate groups equal normalized labels only. Preserve calendar/active and Id qualifiers; never expand Qty to Quantity or assert business equivalence.
- Calculation output has type/unit but no name. Use its declared symbol name, explicitly identified as a calculation occurrence. Include input origins only when resolvable to existing named declarations. Endpoint parameters require explicit structured metadata; never invent them from route text.
- Missing type/evidence stays unknown. Exclude generated and external declarations. A small documented infrastructure denylist may remove obvious framework names; keep its rules reviewable and testable.
- Unknown/unavailable functionality must be visible, not silently fabricated. Existing API behavior for invalid capability/view selections remains consistent.

## STACK

- Existing .NET 10 Explorer + React/TypeScript; reuse ViewIndex, Queries DTO conventions, caps.Scope(cap), existing source viewer and UI components. Source: platform/src/CapabilityExplorer.Explorer/{ExplorerIndex.cs,Queries.cs,ExplorerApi.cs,client/src/} (resolve actual class file paths before editing).
- CalculationComparer.Compare for factual comparisons; preserve KnownOnOneSide vs ValueDiffers/DetailDiffers, and expose expression-hash inequality separately. Source: platform/src/CapabilityExplorer.Interpretation/CalculationComparison.cs. Hash inequality does not prove mathematical inequivalence.
- SPARC phase discipline, not a new runtime dependency. Brain grounding: sparc/capability-cards.md#sparc (curated methodology card).
- Native Codex subagents execute; Ruflo is the coordination ledger, as required by AGENTS.md and MADR-0001. Brain retrieval ruflo/plugins/ruflo-workflows/docs/adrs/0002-native-workflow-orchestration.md describes coordination designs, not proof that those host-specific APIs are configured here. Discover live tools before invoking; do not substitute registration for executed work.
- Existing AgentDB checkpoint via Ruflo CLI, exact store/readback, queen-only writes. Grounding: AGENTS.md Memory System and the verified project checkpoint receipt. Hook timeout advisories remain unresolved; do not claim automatic learning quality.
- Existing sandbox, local-check/pre-push, Trivy and action audit from MADR-0003. Reuse the current required image set; do not add a model container, database service or CI matrix for this feature.

## PHASES

Work through a short private design pass, then publish decisions and evidence, not hidden reasoning.

S — Specification: write acceptance examples, endpoint/DTO contract, input coverage and exclusions to docs/plans/deterministic-glossary.md. Record the calendar/active mismatch above explicitly in #4. Rubric: scope/examples40, data/evidence boundaries30, failure/bounds cases30. Gate: baseline fixtures/goldens verify and the acceptance matrix covers the tests below.

P — Pseudocode: specify explicit-name collection, normalization, occurrence identity, grouping, filtering, sorting and comparison. Bound work: collect/sort O(N log N); compare only distinct calculations inside one candidate, with a documented deterministic pair budget and explicit comparison-completeness flag when exceeded. Rubric: acceptance coverage40, deterministic ordering30, bounded/error paths30. Gate: existing Explorer suite passes and synthetic executable normalization tests cover positive and negative cases.

A — Architecture: freeze typed DTOs before parallel writing. Keep projection read-only and model/database independent. Add a small MADR for candidate semantics if this introduces vocabulary beyond MADR-0002. Rubric: contracts40, context/evidence isolation40, compatible integration20. Gate: build/UI typecheck passes and contract tests serialize the selected DTO; missing implementation tests cannot count as passing evidence.

R — Refinement: integrate the lanes below, run focused tests and independently review conservative semantics. Rubric: acceptance40, determinism20, failure/boundaries20, UI behavior20. Gate: test-explorer + UI build and an actual browser check of both existing fixture capabilities.

C — Completion: update HANDOFF and evidence, commit, run the full project pre-push gate on clean exact source, and create a reviewable PR when authorized. Rubric: full suite40, running UI20, docs/traceability20, independent review20. Gate: full local-check (or the equivalent installed pre-push run) plus live endpoint/browser verification. Hosted CI is reported separately.

Parallel execution, max four active agents including queen:

1. Queen freezes DTOs, owns shared integration and records acknowledged claims in #4.
2. Backend worker: new Glossary.cs and optional focused DTO/normalization file, isolated worktree.
3. UI worker: new client/src/views/Glossary.tsx, isolated worktree, builds against frozen DTOs.
4. Test worker: new GlossaryTests.cs and GlossaryApiTests.cs, isolated worktree; fixtures read-only.

Queen alone edits ExplorerApi.cs, client/api.ts, client/App.tsx, docs and any shared manifests. Give worker commits an interface-ready dependency barrier before parallel implementation. Independently review after integration; reuse an idle slot. Do not create redundant nested swarms. Ruflo swarm/hive entries record ownership and handoffs; native workers do the execution.

Required assertions: camel/snake/kebab equality; invariant culture/acronym rules; meaningful suffix/Id/calendar/active negatives; generated/external exclusion; explicit metadata only; exact selected capability/revision isolation; no synthesized evidence; duplicate handling; identical calculations not flagged different; unknown types not reported as mismatches; stable serialized output over repeated/shuffled input and alternate cultures; bounded comparison reporting; evidence navigation preserves artifact/revision identity. Worker/web active-week examples may group; API calendar-week examples remain separate. Test API invalid inputs and UI empty/error/unknown states.

## STANDING RULES

- Before GitHub writes verify gh api user --jq .login == Tigon32. Keep origin ownership unchanged.
- Check issue/PR claims first. Claim exact paths with owner/base/dependencies and acknowledge assignments before writing. One writer per worktree; root-only shared manifests and checkpoint writes. Release claims explicitly at handoff. Silence never releases another contributor's claim.
- Self-verify each gate with real commands. Explain evidence-cited deductions before the score /100. Below95: fix and regrade, maximum5 iterations per phase. Report the final score, fixes and proof; do not pass zero-test/skipped suites or relabel failures as success.
- All builds/restores/tests use scripts/sandbox through make. Preserve gates and locks. No runtime inference from names, no model calls, no DB mutation, no artifact rewrite.
- Stable ordinal ordering for terms, occurrences, repositories, capabilities and comparison pairs; occurrence identity includes repository/revision/unit/symbol. Partial comparison is disclosed, not presented as complete.
- Read the newest exact-key checkpoint before each phase; write goal, contract, source SHA, claims, decisions, evidence, blockers and next action after it; exact-read back. If the plugin loop-checkpoint helper exists and is verified, use its read/write/check contract. Otherwise use the existing Ruflo checkpoint path; do not invent a missing repository script.
- Commit passed phases locally. Push only under current authorization; never merge/release/deploy automatically. A future implementation needs its own execution authorization; this planning comment is not that authorization.
- Batch nonblocking questions with recommended defaults. Status requests get a done/in-flight/blocked/parked table. Say ready only after exercising the running feature; HTTP smoke alone does not verify interactive UI.

## COST GUARDRAILS

- Scripts first. Use native subscription workers: Sol medium for bounded research/test scaffolding; Sol high for backend/UI implementation; Astra only for an unresolved consequential design decision or authoritative gate review. Record actual model and task, not invented savings. If those model routes are unavailable, disclose and use the smallest available capable route.
- No automatic provider API fallback. Use route-cheap.mjs only if installed/verified and separately billed execution is authorized; otherwise stay on native workers. Never claim MetaHarness routed work that it did not execute.
- Any operation projected above $1 or above20 paid calls requires an estimate and explicit approval. No cloud models/services are required here. Report observed spend from real receipts if available; otherwise unknown, not zero.
- Parallelism is bounded by independent scopes and dependency barriers, not number of agent labels. Reuse caches and installed images. Run focused gates while changing code; run the whole gate once at the integration boundary. Do not run local-check and immediately push unchanged HEAD: the installed hook already repeats the full gate.
- Retain single-job CI and superseded-run cancellation. Do not add images, matrices, Docker archive caches or duplicate setup without a measured need. Slim-image experiments are a separate compatibility task, not a reason to weaken offline isolation.

## DONE CRITERIA

Commands run from the isolated integration worktree; future Glossary tests must exist and execute nonzero assertions. Record exit codes and counts, not just command names.

S: make fixtures golden-check
P: make test-explorer  # includes the newly added focused normalization tests, not only baseline tests
A: make test-explorer  # includes build/UI and must execute the new DTO serialization contract tests
R: make test-explorer ui
C: make local-check   # clean committed source; alternatively the installed gate during an authorized git push

Overall (reuse the just-observed successful C/pre-push run, do not execute CI again):
git diff --exit-code && test -z "$(git status --porcelain --untracked-files=all)" && test "$(sed -n 's/^head=//p' .sandbox-cache/local-ci/last-success.txt)" = "$(git rev-parse HEAD)"

The receipt comparison records that same run; it never bypasses a future pre-push check.

Running feature: make explore GITHUB_AUTH=none, then curl -fsS 'http://127.0.0.1:5080/api/glossary?cap=weekly-sales' and the demand-pricing equivalent. In a real browser, open the Glossary tab, change capabilities/revisions, search, inspect a candidate and follow evidence. Verify absence of model/database requirements. Use make dev only when the full dev environment is actually needed; the read-only feature must work without it. Capture concise observed evidence without starting paid models.

Expected output: implemented feature and focused tests; the proposed candidate-semantics MADR; per-phase evidence/scores; actual routing/spend receipts or explicit unknown; one independently reviewed PR; current HANDOFF and exact-key checkpoint; explicit list of untested behavior. Keep all deferred #4 layers open.


Implementation pseudocode (frozen before worker integration):

1. Sort selected artifacts by repository/commit/unit path, then visit explicit
   eligible declarations and raw calculation records. Resolve origin keys only
   against declarations in the selected source snapshot; do not guess labels.
2. Normalize names in O(name length), tokenize acronym/case/separator boundaries,
   strip one terminal container suffix and preserve semantic qualifiers.
3. Identify occurrences by a length-safe tuple (repo, commit, unit path, key, role),
   not load-order artifact IDs. Sort evidence by exact source identity.
4. Group exact normalized labels, then sort all output collections ordinally.
5. Canonicalize supported explicit primitive aliases; missing type stays null.
   A known/unknown pair is not a mismatch. Group only same-label calculations.
6. Sort calculation occurrences; compute possible pair count using long arithmetic.
   Enumerate at most64 pairs per candidate, report incompleteness, and canonicalize
   comparison details/order/IDs. Hash inequality is reported separately.
7. Project repository-capability membership from existing definitions. Apply
   index.View(view, caps.Scope(cap)) before collection; never rewrite artifacts.

Collection/sorting costs O(N log N) plus source JSON size. Pair work is bounded by
64 per candidate, rather than enumerating all pairs and truncating afterward.
Missing/unsupported metadata contributes no guessed observation. HTTP errors and
invalid selections retain existing Explorer behavior. UI remounts on scope change
so stale capability data cannot masquerade as current evidence.
