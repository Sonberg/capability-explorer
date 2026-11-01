# Brain Prompt: evidence-led repository insights research

This is a paste-ready research execution brief, not a tool-adoption decision.
Tracking and complete owner-review copy: [issue #31](https://github.com/Sonberg/capability-explorer/issues/31).
It extends [the existing desk assessment](../research/code-analysis-options.md)
and its 17 scored modes. Planning baseline: main `1e1122b62587eda7f3e2817c1619ba29ce292923`.
The [research register](../research/code-insights-research-register.json) preserves
historical scores and records refreshed upstream pins without claiming new tests.

## GOAL

<goal>Identify the smallest, safest combination of existing and complementary tools
that measurably improves the evidence-backed insights capability-explorer obtains
from other repositories, publish a reproducible comparison and actionable GitHub
recommendations, and leave adoption to an explicit implementation decision.</goal>

## ASSUMPTIONS

- <repository>Sonberg/capability-explorer</repository>; authenticated account Tigon32.
  Fetch current main before starting; work in an isolated feature branch, target
  main only, preserve user changes, and never merge or enable auto-merge.
- This is research plus bounded experiments, not a platform replacement, production
  integration, private-source upload or permission to enable a learning daemon.
- Read HANDOFF, MADR-0001/0002/0010/0011/0013/0016/0017 and issues #3/#4/#6/#12/#23/#24/#28.
  Reconcile actual source with issue status: closing an issue does not prove all
  planned capabilities shipped. The declared-contract catalog may be unmerged.
- Existing Roslyn, TypeScript checker, bounded SQL parser, ce.v1/evaluation,
  reviewed bindings, security tools, explanations and embeddings are comparators.
  A library already used internally is different from an implemented tool adapter.
- Preserve the November 1 desk scores as historical judgments. They are not
  measured accuracy, throughput, security or savings and must not be relabelled.
- No external paid model calls are authorized. Model-based experiments, including
  optional local generation, need a separately explicit experimental scope.
- Private Glufs input remains unavailable under #23. Use sealed public/synthetic
  corpora; report unavailable acceptance rather than substitute invented results.
- Default outcome may be “retain the current implementation”; no tool must win.

## STACK

- Existing `scripts/analyze`, `scripts/sandbox`, `schema/v1`, Contracts and
  Evaluation establish extraction → evidence → evaluation → persistence → views.
  Exact baseline paths are in the current desk report; inspect their source again.
- Existing Node/Python standard-library checks can maintain the reference ledger,
  weights and results. Do not add a framework or database for this research.
- Repomix and code2prompt are context-packaging candidates; scc/cloc are inventory
  candidates; Universal Ctags is declaration inventory, not compiler resolution.
  Use the register's pinned upstream source URLs and the primary reference log.
- Tree-sitter, ast-grep, SCIP prebuilt-index ingestion and Graphify code-only are
  complementary structural/indexing candidates. Index creation and index ingestion
  have different execution risks; score their modes separately.
- CodeQL, Joern and existing Semgrep/Trivy/Gitleaks cover different security/dataflow
  questions. Never equate a sink rule with whole-program taint or SCA with SAST.
- Understand-Anything and Graphify mixed-media modes need per-output classification.
  Their code extraction, graph resolution, model labelling and UI presentation are
  separate capabilities, not one undifferentiated “AI analysis” score.
- Brain grounding: `sparc/docs/adrs/ADR-001-sparc-metaharness.md` describes traceable
  phase gates. Apply that evidence discipline with existing tools; this citation
  does not establish an installed SPARC runtime or authorize installing one.
- Native read-only research workers may run in parallel; root integrates the
  reference/score registers and issue. Ruflo is a coordination ledger, not proof
  that research, experiments or a distributed release gate actually ran.

## PHASES

### S — Specification: insight needs and honest baseline

1. Inventory implemented tools, versions, entrypoints and known limitations from
   current main. Include internal versus external configuration and implicit model
   calls. Separate “implemented”, “documented”, “proposed” and “unavailable”.
2. Define answerable insight questions and expected evidence before selecting tools:
   language/framework inventory; declarations/resolved references; service/package
   topology; HTTP/event/data-store integration across repositories; calculations;
   dataflow/security; authored contracts/processes/decisions; change impact; and
   candidate domain concepts. Specify unsupported/dynamic cases for each.
3. Preserve two independent axes: execution method (parser/compiler, deterministic
   heuristic, packer, model, human review) and epistemic status (observed, derived,
   candidate, unresolved, reviewed). Deterministic inference is still derivation;
   an upstream `INFERRED` label does not alone establish an LLM call.
4. Required candidates include all user-supplied tools and Knolli's discovery list;
   verify technical claims through primary documentation/source. Add alternatives
   only when they address a named gap, including Tree-sitter/SCIP/ast-grep,
   CodeQL/Joern and cloc, and relevant protocol-specific parsers under MADR-0013.
5. Create the small phase validator described below before claiming phase success.

Deliver: updated inventory, requirements-to-insight matrix, claim-level references,
candidate/mode register, and explicit safety/budget assumptions.
Rubric /100: insight coverage30; source-bound baseline30; fact/inference boundaries25;
constraints and ownership15. Gate: `node scripts/research/check-code-insights.mjs specification`.

### P — Pseudocode: comparison and experiment protocol

1. Specify one common sealed input manifest and ground-truth oracles. Include the
   current C#/TS/SQL fixtures and small missing-language fixtures when justified.
2. Include ambiguous overloads/missing dependencies, duplicate names in different
   repositories, same topic/path on different brokers/hosts, dynamic SQL/URLs,
   generated files, partial classes, unsupported syntax, malformed inputs,
   hostile config/plugins/processors, symlinks and secret sentinels.
3. State explicitly that a call graph is not ordered BPMN control flow; numeric IR
   is not full DMN/FEEL; a currency-named column does not establish business
   semantics; a UI database grouping is not proof of physical table deployment.
   Keep the project's own ubiquitous language separate from analyzed-repository
   glossary concepts. Similarity and matching names never prove identity.
4. Normalize output deterministically and retain raw bytes. Record the exact tool,
   dependency/config/rule/source hashes, exit status, omissions, source spans and
   raw-to-canonical mapping. Do not discard semantic ordering to force repeatability.
5. Measure incremental correct facts, duplicates, false links, unresolved and
   unsupported cases against the same oracle. Deduplicate before counting gains.
   Evaluate packers for retained evidence, token count with named tokenizer/version,
   exclusions, loss from compression and limits—not semantic call-graph accuracy.
6. Start at a small tier; use a medium tier only after correctness/safety pass.
   Run analyzers sequentially under identical resource limits. Report median/range
   for a few trials; do not publish p95 from three observations. Separate setup,
   cold/warm execution, cache effects and any model costs.

Rubric /100: oracle and comparability35; negative/uncertain cases30; reproducibility20;
measurement limits15. Gate: `node scripts/research/check-code-insights.mjs pseudocode`.

### A — Architecture: role-based shortlist and trust boundaries

1. Preserve weights for continuity: determinism/provenance25, semantic/language
   fit20, safe offline execution20, integration fit15, operating cost10,
   maintenance/license10. Score 0–5, with cited rationale BEFORE the score;
   total = sum(weight × rating / 5). Compare within roles, not one global winner.
2. Keep evidence confidence separate: measured, source-inspected, primary-doc claim,
   secondary discovery, or unknown. Unknown gets no fabricated rating; report a
   score interval/coverage and exclude it from an evidence-complete adoption rank.
   Recompute historical totals and run a ±5-point weight sensitivity analysis.
3. Hard gates override totals: no unauthorized execution/egress or source writes;
   compatible license/distribution assessment; source/index identity; bounded
   resources; usable evidence; and honest handling of unsupported cases.
4. Prefer at most THREE first-round trials: scc inventory; ONE packer selected by
   desk evidence (Repomix versus code2prompt); and ONE structural candidate selected
   for an observed coverage gap (Ctags/Tree-sitter/SCIP/Graphify/ast-grep).
   Keep security/dataflow and model-inference trials separate, not mandatory.
5. Require operator-owned inert config, scrubbed environment, read-only sealed
   source mounts, network disabled during extraction, explicit output mounts and
   CPU/memory/time/size limits. Installation is a separate trusted preparation step;
   analyzed repositories' packages/scripts/plugins must not execute.
6. Retain compiler-backed facts as canonical unless an independently verified
   replacement meets their role's evidence contract. Design a thin producer or
   research sidecar first; do not mutate ce.v1/schema to accommodate vague edges.
7. Give each writer a separate worktree and exact paths. Use up to root + two
   researchers + independent reviewer if actual session capacity permits. Do not
   promise eight executors merely because a config says eight. Only root changes
   shared registers/manifests; source research can overlap, timed trials cannot.

Rubric /100: role fit and score integrity30; safety/provenance30; integration25;
frugal execution15. Gate: `node scripts/research/check-code-insights.mjs architecture`.

### R — Refinement: bounded trials and corrected insights

Execute only the approved shortlist after installation/source review. Two runs
must produce equal canonical facts for deterministic modes. Inspect disputed
claims against source and oracles; preserve contradictions rather than voting
them into truth. Report adapter effort, runtime/dependency footprint, peak memory,
disk, elapsed time and marginal value beyond the current baseline. Run failure
paths and prove no writes/egress. No installation or runtime behavior can be marked
verified from README prose alone. Record blocked trials without inventing scores.

For any separately authorized model trial, use fixed inputs and record exact
model/artifact, prompt hash, parameters, repetition history, citation validity and
measurable costs. Deterministic sampling settings do not prove deterministic
results. Human-validated truth and uncertainty must remain visible in outputs.

Rubric /100: correctness/evidence40; isolation/failures25; measured marginal value25;
reproducibility10. Gate: `node scripts/research/check-code-insights.mjs refinement`.

### C — Completion: owner recommendations, not automatic adoption

Produce an updated readable report plus machine-readable references, per-mode
scores/uncertainty and raw trial receipts. Every recommendation names a current
insight gap, observed value, hard-gate status, rejected alternatives, dependency
cost and smallest implementation slice. Recommend retain, trial, thin adapter,
defer or reject; no dependency or implementation MADR is adopted automatically.

Create/update ONE dedicated research issue, link #6/#12/#23/#24 and the existing
report, and include a traceable checklist and owner decisions. Avoid duplicate
issues; create separate implementation issues only for supported, non-overlapping
gaps. Update HANDOFF/changelog only for actual published work. Commit changes.
For a source PR, integrate fresh main before normal local act/security and PR
checks, target main explicitly, and leave merge to the owner. For a planning-only
handoff, publish the complete prompt/results on the issue without pretending a
full CI or experimental run occurred. Apply MADR-0014 to recoverable inactive
worktrees after publication; preserve private evidence and active previews.

Rubric /100: source/claim traceability35; actionable role-based decisions30;
honest limits20; owner handoff/cost15.
Gate: `node scripts/research/check-code-insights.mjs completion`.

## STANDING RULES

- Self-verify every phase, justify deductions with source/proof BEFORE giving a
  score /100; below95 fix and regrade, maximum five attempts. Architectural flaws
  cap the affected score at70. Report final scores/fixes/proof and what was not tested.
- Treat README/marketing/third-party claims as claims. Knolli is discovery material,
  not authority on competitors. Refresh current source/release pins at execution;
  a source SHA is not proof of an installed artifact or complete license inventory.
- Keep observed facts, deterministic derivations, model proposals and human review
  separate in storage, scoring and presentation. Existing hybrid tools may span all.
- Maintain every consulted reference, including failed/inaccessible URLs, date,
  immutable revision where available, exact supporting path/claim and confidence.
  A reference list without claim mapping is insufficient. Keep sensitive source
  and raw security output private; publish redacted evidence and hashes.
- Read the canonical AgentDB checkpoint first and verify final writes by exact-key
  readback. Do not create a competing store or rely on failed automatic hooks.
- No main writes, force-push, merges, paid calls, private-source uploads, source
  dependency execution, broad plugin setup or silent eligibility exceptions.
- Batch essential questions with recommended defaults. “Status” gets a table.
  READY means the stated research proofs ran; a plan or a high score is not proof.

## COST GUARDRAILS

- No external paid inference. Mechanical inventory/scoring uses scripts; bounded
  reading/synthesis uses existing subscription workers and independent gate review.
  Use a real installed router only after interface verification; never invent a
  cheap-routing script, routing receipt, model saving or dollar total.
- More than $1 projected external spend or 20 paid calls requires an explicit
  estimate and approval. Report actual paid calls and unknown subscription cost.
- Reuse cached pinned sandbox images. No browser, graph database, large model or
  additional service in ordinary CI merely to compare tools. No `[all]` bundles.
- Initial budget: desk research plus at most three candidate trials, 20 minutes
  each for a small corpus. A packaging failure permits one targeted retry; otherwise
  record unavailable evidence. No 1M/10M experiment is authorized by this brief.
- Keep at least10GiB actual free disk for experiments, plus their estimated output.
  Tiny CI fixtures must use deterministic capacity inputs and separately test the
  real guard; do not require a hosted runner to have the experimental reserve.
- Repeated passing trials need no rerun after unchanged relevant inputs. Parallel
  desk research is useful; concurrent timing runs confound performance evidence.

## DONE CRITERIA

These commands are **proposed research deliverables**, not existing or passed
checks. In Specification implement the small standard-library validator and its
negative cases: missing refs, incorrect weights/totals, cross-role ranking,
unverified adoption, mixed fact/inference outputs, absent runtime receipts and
inaccessible sources asserted as tested must fail. It must distinguish a complete
research decision to defer/reject from a passed candidate adoption gate.

```sh
node scripts/research/check-code-insights.mjs specification
node scripts/research/check-code-insights.mjs pseudocode
node scripts/research/check-code-insights.mjs architecture
node scripts/research/check-code-insights.mjs refinement
node scripts/research/check-code-insights.mjs completion
node scripts/research/check-code-insights.mjs all
```

Completion additionally requires the real GitHub issue URL/number and explicit
unverified results. If a PR is published, ordinary `make EXECUTION=sandbox local-check`
and `make EXECUTION=sandbox pr-check` remain mandatory for its exact source/base;
research validators cannot waive them. No experiment or candidate adoption is
claimed by this prompt. The executing model must keep a source-bound checkpoint
with acceptance contract, evidence, unresolved decisions and one exact next action.
