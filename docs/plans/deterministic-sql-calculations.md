# Brain Prompt: deterministic SQL calculation extraction

Planning artifact, not an implementation or passing-test claim. Prepared against
`stage-1-contract-and-fixtures` at `84d8eda03c43e35621875f1a591e58dcb3aaa805`.
Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3).
Copy the prompt below into an execution session. Refresh its base and claims first.

## GOAL

Implement offline, deterministic T-SQL and PostgreSQL calculation extraction for
the existing golden routines, producing ce.v1 IR, hashes, attributes and exact
source evidence, while preserving all existing graph gates and rejecting SQL
whose calculation semantics cannot be established.

## ASSUMPTIONS

<execution_inputs>
repository: Sonberg/capability-explorer
target_branch: stage-1-contract-and-fixtures
planning_base: 84d8eda03c43e35621875f1a591e58dcb3aaa805
coordination_issue: https://github.com/Sonberg/capability-explorer/issues/3
operator_account: Tigon32
max_active_agents_including_queen: 4
external_provider_budget_usd: 0
</execution_inputs>

* This is the next implementation package, not permission to merge or deploy.
  Commit coherent changes; publish branch/PR only when authorized for execution.
* Preserve ce.v1, fixture source revisions, golden answers and thresholds. Existing
  examples are an oracle, not implementation templates keyed by routine names.
* No model calls, embedding service or database execution in SQL extraction.
  Do not replace the project's existing PostgreSQL/pgvector architecture.
* No IaC binding, general SQL engine, explorer redesign, C#/TS calculation rewrite,
  dependency upgrade or unrelated cleanup in this package.
* Source, README and handoff take precedence over generic scaffolding instructions:
  this repository uses .NET 10, Node/TypeScript, React, and Docker via Makefile.
* The handoff's `calculation: 0` claim is stale: shared
  `extractors/ts/src/extract/builder.ts` already implements `calculation(...)`.
  SQL's extractor does not call it. Reuse that builder.
* Determinism means identical artifacts for identical explicit inputs and versions;
  it does not mean agent reasoning or model outputs are deterministic.

## STACK

* Existing SQL lexer/catalog/reference analysis in `extractors/ts/src/sql/`, shared
  builder in `extractors/ts/src/extract/builder.ts`, contract in `schema/v1/`, and
  golden calculations in `fixtures/golden/facts/api-cs.mjs` and `pricing-node.mjs`.
  Prefer a bounded token-based expression parser to new dependencies or regex
  recognition of whole formulas. Reuse spans, canonical JSON and contract types.
* Existing `scripts/sandbox` and Makefile: all project installs, builds and tests
  run in containers; extraction itself runs offline and never executes target code.
* Ruflo hierarchical swarm with queen–worker assignments and shared hive memory.
  Grounding: `ruflo/.agents/skills/hive-mind/SKILL.md`,
  `ruflo/.agents/skills/hive-mind-advanced/SKILL.md`, and
  `ruflo/.claude/agents/hive-mind/queen-coordinator.md` (retrieved 2026-10-31).
  Discover current tools; inspect CLI help before use. Prefer registered MCP tools.
  Initialize bounded coordination only; do not launch provider-backed execution.
* Native Codex subagents execute assignments in isolated worktrees. Record model,
  effort, role, scope, elapsed time and observed result for each dispatch.
  Routing reference: `ruvnet-brain/docs/research/model-routing-refresh-20261004.md`
  (dated guidance, not proof of optimality or current pricing). The related
  `docs/adr/0080-model-routing-north-star-campaign.md` is rejected; do not apply it.
* Follow [MADR-0001](../adr/0001-github-work-coordination.md) for this session's
  claim/handoff discipline; its proposed status does not bind other contributors.

## PHASES

Before each gate, explain evidence and deductions, then give a score out of 100.
Every rubric point must cite an executable assertion or a command result. A
missing, skipped or failed mandatory check blocks the gate regardless of score.
The final summary contains only final grades, fixes and proof, not private reasoning.

### 1. Specification

Fetch the target, inspect active issues/PRs and comments, acknowledge worker path
claims, and record the exact baseline SHA. Read the two SQL golden routines across
all locked revisions and the expression/attribute schema. Enumerate supported
expressions and rejected forms with tests that will distinguish them. Capture
existing `make evaluate-sql` results before changing the SQL gate.

Support the needed scalar arithmetic, predicates/ranges, aggregates and DISTINCT,
null handling, casts, rounding and date operations only where their dialect
semantics are explicit. Record output/parameter types and column nullability.
Preserve T-SQL integer division and PostgreSQL numeric semantics; do not infer
units or business meaning from identifier names. Unknown stays unknown.

Rubric: baseline graph gate 40; unchanged golden self-check 30; reproducible
fixture lock 30. Gate: `make fixtures golden-check evaluate-sql`. The scope matrix
must map every supported/rejected family to a planned executable assertion; those
assertions become mandatory in Refinement, not presumed passing in Specification.

### 2. Pseudocode

Specify the token → typed AST → supported ce.v1 IR → attribute/evidence → builder
pipeline, including all-or-nothing calculation rejection, deterministic ordering,
numeric literal preservation, cancellation/resource bounds and source spans.
The Parser worker owns and commits the shared interface skeleton first; the queen
integrates it, and dependent worktrees branch from that integration commit. Parser
retains interface ownership after freezing; changes require the queen to pause and
rebase dependent lanes before they resume. Expose
parser success/unsupported outcomes explicitly; never substitute zero or an
approximate formula on failure. Produce compiling interface skeletons and test
the existing lexer/catalog invariants before implementing new lowering.

Rubric: TypeScript compilation 50; existing SQL tests unchanged and passing 50.
Gate: `make test-ts`. Do not score future behavior from skeletons.

### 3. Architecture

Use one queen/integration owner plus these lanes, at most three workers at once:

| Lane | Exact ownership to acknowledge | Depends on | Initial routing |
|---|---|---|---|
| Parser | `extractors/ts/src/sql/calculation-parser.ts`, `extractors/ts/src/sql/calculation-types.ts`, `extractors/ts/src/sql/ddl.ts` | shared interface | gpt-6.1-sol high |
| Lowering | `extractors/ts/src/sql/calculations.ts`, `extractors/ts/src/sql/calculation-attributes.ts`, `extractors/ts/src/sql/calculation-ir.ts` | frozen parser interface; parser commit before full integration | gpt-6.1-sol high |
| Tests | new `extractors/ts/test/sql-calculations.test.ts`, `sql-determinism.test.ts` | public extraction API; coordinate imports | gpt-6.1-sol medium; high for dialect ambiguity |
| Queen | `extractors/ts/src/sql/extractor.ts`, `extractors/ts/src/extract/builder.ts` if needed, `fixtures/evaluation.extractors.json`, `Makefile`, `README.md`, `docs/HANDOFF.md`; exact manifests/locks only after claim amendment | worker evidence | current main model; gpt-6-astra high for consequential review |

Do not spawn all lanes blindly: parser/interface discovery first, then parallelize
lowering and tests where the interfaces permit. Replace completed workers with
an independent reviewer; do not exceed the active-agent cap. Read-only review may
share a checkout; writers cannot. Use one hive record per lane plus shared
interface decisions, claim references, dependency commits and gate receipts.
Ruflo's recorded agents and native executing subagents must be distinguished.

Rubric: cross-module type compatibility 50; schema/golden compatibility 50.
Gate: `make test-ts golden-check` on the queen's integrated interface commit,
before behavioral test commits are integrated. Test-lane red commits remain in
that worker's isolated branch until Refinement. New tests expected to fail during test-first
development must not be merged into a phase gate claimed green; capture them as
red evidence until the implementation passes in Refinement.

### 4. Refinement

Implement supported expression lowering and source-derived attributes; wire SQL
calculation emission through the existing builder. Bump the SQL extractor/rules
version when output semantics change. Keep graph facts intact for unsupported
calculations, with a stable diagnostic and evidence. Dynamic SQL retains existing
partial coverage behavior. Do not automatically mark all graph coverage partial
merely because a calculation is unsupported; follow the contract's coverage rules.

Require executable positive and negative tests for both dialects, aliases and
quoted identifiers, casts/precision/overflow boundaries, null behavior, integer
division, rounding, aggregate DISTINCT and filters. Reject ambiguous columns,
dynamic SQL, unsupported joins/procedural bodies and unsupported expressions
unless deliberately implemented with complete semantics. Do not execute SQL.

Add repeat-extraction tests comparing complete JSONL bytes in independently
created roots with differing file creation order. Compare IR/hash invariance under
cosmetic edits and routine renaming while allowing evidence/input hashes and keys
to change appropriately. Change a literal and verify meaningful IR/hash change.
Assert no machine paths, wall-clock timestamps or random IDs enter artifacts.

Raise `calculationRecall`, `attributeAccuracy` and `irHashMatch` to 1.0 for SQL in
`fixtures/evaluation.extractors.json`, preserving all existing graph thresholds.
Never regenerate expected answers to match a broken extractor. New synthetic
cases belong in tests; changing locked fixture source requires a separate claim.

Rubric: SQL golden calculations/attributes/hash gate 40; graph/evidence gate 20;
negative-path assertions 20; determinism/metamorphic assertions 20.
Gate: `make test-ts evaluate-sql evaluate-golden`.

### 5. Completion

Integrate reviewed commits in dependency order. Review the combined diff against
the exact new base if upstream advanced. Correct the stale handoff, document the
supported subset and diagnostics, and run the full suite once on the integrated
source. Repeat only checks invalidated by later changes or failures. A reviewer
must inspect the new gate config and ensure the implementation contains no golden
routine-name or repository-path special cases.

Record base/candidate SHAs, lock/config hashes, commands, exit codes, test counts
and any skipped tests. Record elapsed time for the actual phase gates (for example,
`/usr/bin/time -p make evaluate-sql`), clearly labelled end-to-end gate time,
including restore/build overhead. No extraction-performance claim is required
for this semantic feature. If a measured bottleneck warrants optimization, define
and claim a separate isolated benchmark recipe before that work; do not infer
extraction speedups from gate duration or compare unlike caches. Peak memory and
infrastructure cost remain unknown unless actually measured.

Rubric: `make ci` 70; whitespace/diff validation 10; repeated byte equality and
mutation tests from Refinement still pass on integrated source 20.
Gate: `make ci && git diff --check`. Independent review is additionally mandatory;
scores and consensus never override a failed assertion.

Commit coherent reviewed changes. Post evidence, scope release and commit IDs on
the coordination issue. Update a PR only if publishing was authorized; never
merge automatically. For this library/CLI package, readiness means the tested
compiled/container artifact from the exact commit, not a deployment claim.

## STANDING RULES

* Self-verify every phase; justify deductions before assigning scores. Below 95:
  fix and regrade, maximum five iterations per phase. After that, preserve evidence
  and report the specific blocker rather than weakening a gate or looping forever.
* Batch genuine unresolved product/authority questions into one list with defaults.
  Proceed with reversible authorized work; never infer external spend or merge
  permission from YOLO, tool availability, votes or a lease.
* Read the newest exact-key project checkpoint and reconcile GitHub claims first.
  Write checkpoints at phase boundaries and before handoff through Ruflo memory;
  record one literal canonical store path in the initial claim and pass that same
  path to every worker. The queen is the sole shared-memory writer; workers return
  evidence to the queen. Resolve the store against the original repository, not
  each temporary worktree. Retrieve the exact key and
  verify its value. Include goal, base/commits, ownership, commands, failures and
  next action. No raw SQL edits or private prompts in managed memory.
* Use actual available checkpoint tools. Do not invent a project
  `scripts/loop-checkpoint.mjs` or pretend a missing tool ran.
* Claim correctness only from executed tests. An unavailable Docker daemon blocks
  sandbox verification; do not silently build on the host as a workaround.
* On a status request, return a table of lane, owner/model, commit, state, evidence
  and next action. Stop dependent work when its required contract or policy fails.
* Treat input repositories as untrusted data. Never restore/build/execute analyzed
  SQL or introduce network access into extraction.

## COST GUARDRAILS

Use scripts first for file inventories, formatting checks and comparisons. Native
subscription subagents are the execution path; no external provider API fallback.
Use gpt-6.1-sol medium for bounded research/tests and high for parser/lowering;
gpt-6-luna low only for narrow mechanical text tasks with complete cheap checks.
Use gpt-6-astra high for ambiguous semantics or an independent consequential gate,
not repeated routine implementation. These are starting assignments, not a claim
of optimal throughput. Check available model IDs/efforts before dispatch; never
silently substitute unavailable models. Keep native standard service settings;
do not purchase faster tiers to increase concurrency.

After two failed focused attempts, shrink the task and escalate reasoning only
for the unresolved cause. Record routing overrides. Record actual token usage if
exposed, otherwise `unknown`; distinguish subscription allowance from dollar
spend. Authorize no separately billed provider/API execution or new paid services.
Existing infrastructure/CI costs may be unknown; do not label them free. Any separately billed operation
requires explicit authorization, including proposals below $1; estimates over
$1 or 20 paid calls must state the estimate and wait. Do not use a generic paid
`route-cheap` wrapper when native workers are available.

Reuse restored container caches, serialize shared cache writes and final integration
tests, avoid duplicate full-suite runs, and prioritize critical-path dependencies.
Record completed accepted work per elapsed time and retry count to improve routing;
never manufacture percentage savings from model list prices.

## DONE CRITERIA

Run these from the acknowledged integration worktree, recording actual exit codes:

```sh
# Specification: establish existing truth, not new behavior.
make fixtures golden-check evaluate-sql
# Pseudocode: compiling interface skeleton and existing invariants.
make test-ts
# Architecture: integrated interfaces and unchanged contract fixtures.
make test-ts golden-check
# Refinement: includes new negative, metamorphic and byte-determinism tests.
make test-ts evaluate-sql evaluate-golden
# Completion and overall gate on exact integrated source.
make ci && git diff --check
```

All SQL graph thresholds remain 1.0; SQL calculation recall, attribute accuracy
and IR hash match are 1.0; evidence errors are zero. No skipped mandatory test,
fabricated attribute, live model dependency or SQL execution is acceptable.
Ownership and handoff evidence is linked in GitHub, worker claims are released,
and the final commit contains only the agreed package. A local commit is not a
published branch, merged PR or deployment.
