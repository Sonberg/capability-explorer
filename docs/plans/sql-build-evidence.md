# SQL calculation build evidence

Execution of [the work package](deterministic-sql-calculations.md), coordinated in
[issue #3](https://github.com/Sonberg/capability-explorer/issues/3).
Base: `84d8eda03c43e35621875f1a591e58dcb3aaa805`.
Integration worktree: `/private/tmp/capability-explorer-work-package`.

## Specification

Baseline `make fixtures golden-check evaluate-sql` exited 0. Fixture revisions
matched the lock; 18 golden artifacts were current. SQL graph metrics: nodes
44/44, edges 61/61, accuracy 61/61, evidence 105/105, precision 22/22, coverage
5/5, evidence errors 0. Calculation recall 0/5, attributes 0/52, IR hashes 0/5
were measured but not yet gated. Raw session log: `/private/tmp/ce-sql-specification.log`.

The user explicitly approved correcting the name-derived SQL golden currency
claim to unknown. Accordingly, the PostgreSQL output unit is also unknown:
division by 100 establishes scaling, not a monetary unit. This is a narrow,
authorized exception to the prompt's preservation rule. Neither expression IR
nor locked source revisions nor thresholds change. `make golden golden-check`
regenerated the two pricing-db artifacts and confirmed all 18 are current.

### Scope and planned executable assertions

These assertions are required in Refinement, not claimed passing here.

| Family | Supported behavior / assertion | Rejection assertion |
|---|---|---|
| Routine shape | T-SQL table-return SELECT; PostgreSQL SQL scalar SELECT | joins, multiple statements, procedural and dynamic SQL emit no calculation |
| Arithmetic | precedence, parentheses, unary signs, exact numeric literals, integer division | unknown operators/functions never approximate |
| References | typed parameters and one table; aliases and quoted identifiers | ambiguous/unresolved columns reject |
| Predicates | comparisons, AND, BETWEEN with explicit inclusive/exclusive bounds | unsupported Boolean forms reject |
| Aggregates | SUM and COUNT DISTINCT, source-bound filter and bind order | unsupported aggregate form rejects |
| Nulls | ISNULL/COALESCE, NULLIF and declared nullability | unknown null behavior stays unknown |
| Dates | DATEDIFF day, date_trunc week, date plus integer | unsupported date signatures reject |
| Rounding | dialect ROUND with exact digits and numeric type | unsupported rounding signature rejects |
| Casts/types | explicit precision and scale; overflow and promotion boundaries | unknown or lossy semantics reject rather than invent |
| Evidence | exact valid spans for calculation and known attributes; unknown unit/currency | names alone never establish units |
| Resources | bounded token count and recursive depth | oversized/deep input rejects without crashing |
| Determinism | byte equality across roots/file order, stable IR after rename/cosmetic edits | changed literal changes IR/hash; no host paths/time/randomness |

Specification grade: 100/100 against the work-package rubric (baseline graph
40/40, golden self-check 30/30, fixture lock 30/30). The golden correction is
explicitly authorized, not an extractor accommodation. No new behavior has
been tested or graded. Brain closeout retrieved `sparc/docs/tutorial.md` and
`docs/adrs/ADR-001-sparc-metaharness.md`: keep evidence separate from execution;
no additional dependency is needed for this bounded extractor.

## Owner-requested running explorer check

`make explore` built the web app and .NET application (0 warnings/errors), then
listened on port 5080. HTTP GET `/`, `/api/meta` and `/api/calculations` succeeded.
Metadata reported 18 fixture artifacts, an empty problems array, source available,
and GitHub authentication configured. Interpretation and database similarity
were disabled. This is a startup/API smoke check using fixture artifacts, not
browser interaction, semantic-search verification or proof of new SQL extraction.
Raw log: `/private/tmp/ce-explore.log`.

## Permissions and language

Live GitHub checks confirmed login `Tigon32`, OAuth `repo`/`workflow` scopes,
and repository push/pull/triage permissions; repository is not archived/disabled.
No disposable PR was created; this verifies reported permissions rather than an
end-to-end PR creation. The glossary is [ubiquitous language](../ubiquitous-language.md).

## Routing and limits

Parser: native `gpt-6.1-sol`, high reasoning, isolated `/private/tmp/ce-sql-parser`.
Queen owns integration, shared documentation, golden correction and shared memory.
No separately billed provider calls were authorized or made. Native token usage,
subscription allocation, infrastructure costs and dollar savings are unknown.
Ruflo ledger registration does not itself prove native execution or learning.

## What has not been tested yet

New SQL behavior, full integration suite, browser interactions, production
publication, and end-to-end PR creation. Later phase evidence must replace these
open implementation checks before completion can be claimed.

## Pseudocode and architecture contract

1. Lex existing source, preserving absolute spans; restrict the routine to a
   single static SELECT with one optional catalog-resolved table and WHERE.
2. Parse declared parameters/types and bounded expressions into the shared
   discriminated AST. Pratt precedence parsing visits tokens linearly; token
   count is bounded at 4096 and recursion at 64. Invalid grammar or limits return
   an explicit unsupported result with a span, never a partial expression.
3. Resolve parameter/column references against declarations. Lower recursively
   into ce.v1 with deterministic input and aggregate binder order, preserving
   numeric lexemes, operand order and dialect integer/numeric semantics.
   Resolution is bounded by catalog size; do not execute source or query a DB.
4. Derive only attributes justified by AST/types/nullability; all others unknown.
   Associate known attributes and the calculation with exact source evidence.
5. Emit through the existing builder only after the entire expression passes.
   On rejection, emit a stable diagnostic and preserve ordinary graph facts.
   Builder canonicalization computes IR hashes and final artifact ordering.

The parser owns calculation-types.ts and exports supported/unsupported/not-candidate
outcomes. Lowering depends on these types and existing catalog/builder APIs;
the parser never imports lowering. Extractor calls lowering. No new dependencies
or circular imports are introduced. Shared manifests remain queen-owned.

Pseudocode gate: `make test-ts golden-check` ran the required `test-ts` target:
compilation succeeded, 37 tests passed, 0 failed, 0 skipped. Grade 100/100
(compilation 50, unchanged existing tests 50). The integrated skeleton is
`468008a`; no new parser behavior is claimed. Log: `/private/tmp/ce-sql-interface-gates.log`.
Brain closeout found thin adjacent scope-enforcement documentation
`ruflo/v3/docs/releases/v3.32.33.md`; no applicable dependency change.

Architecture gate: the same combined invocation also completed `golden-check`
with all 18 artifacts current and locked revisions unchanged after the explicitly
approved unknown-currency correction. Grade 100/100 (typed cross-module skeleton
50, schema/golden compatibility 50). Reused this single run for adjacent gates
because the source was unchanged; only evidence and language documentation were
added afterward. Architecture brain closeout rechecked queen/worker coordination;
Ruflo remains a ledger and native isolated workers perform implementation.

## Concurrent upstream work

Remote refresh found `7fd2a2fe0905f20d6da7f2006bd76de5d2197d8c` (local Ollama
integration) after the original baseline. Merged cleanly in `15f6e66`; no SQL or
language-document files overlapped. Final gates must use this combined lineage.
The running explorer smoke above predates this merge and will be repeated.

Refinement workers use three native Sol/high sessions, isolated by owned paths.
The test worker's effort was raised from the planned medium to high for dialect
semantics. No paid provider routing was invoked; numerical savings are unknown.
Shared checkpoint `project-state-current-sql-415e13d` was stored and retrieved
by exact key through Ruflo against the canonical original-worktree memory path.
The CLI warned that a separate AgentDB store exists; this receipt proves only the
selected store's exact write/read, not semantic search or cross-store recall.

## Refinement verification in progress

The first integrated gate stopped at one test defect: the PostgreSQL metamorphic
helper omitted the existing default `public` schema in its key lookup. It passed
98/99 tests, no skips; correction changed the lookup, not production behavior.
The second `make test-ts evaluate-sql evaluate-golden` passed 99/99 tests, no skips,
SQL calculations 5/5, attributes 52/52, IR hashes 5/5, all prior graph metrics and
zero evidence errors. Golden self-evaluation passed all 18 artifacts, 21/21
calculations, 212/212 attributes and 21/21 IR hashes. End-to-end gate time was
56.61 seconds (includes restore/build overhead; not an extraction benchmark).
Log: `/private/tmp/ce-sql-refinement-2.log`.

Independent native Astra/high review then found additional coercion and attribute
edge cases beyond the goldens. Refinement remains open until those regressions
pass: T-SQL default decimal casts/returns, PostgreSQL NULLIF promotion, nullable
parameters in aggregates, outer null replacement, string ISNULL truncation and
session-dependent T-SQL overflow. Golden success alone does not clear this gate.

### Final Refinement gate

Fourth integrated invocation passed: `make test-ts evaluate-sql evaluate-golden`,
108 tests passed, 0 failed, 0 skipped; SQL 5/5 calculations, 52/52 attributes,
5/5 hashes and every graph/evidence threshold passed. Golden self-check again
passed all 18 artifacts. End-to-end time 40.12 seconds, including build overhead.
Log: `/private/tmp/ce-sql-refinement-4.log`. Source worker fixes integrated through
`04d17dc`; extractor wiring, version/config and documentation are included in the
Refinement commit that records this result.

Independent Astra/high review resolved eight findings (the decimal cast and
return cases count separately), then independently passed 12/12 source-only
regression assertions in offline containers. No SQL was executed. Exact reviewed
worker commits: `b4e2373` and `99ffb32`, integrated as `70184e0` and `f5030fe`.
No routine-name/path special cases found. Existing graph thresholds and scope
were compared programmatically against the refreshed remote and are unchanged.

Refinement grade: 100/100 against the declared rubric: SQL golden semantic gate
40/40, graph/evidence 20/20, negative paths 20/20, determinism/metamorphic 20/20.
Four gate invocations were used; the first fixed a test helper, subsequent review
added conservative coercion/null/session/literal boundaries. Brain closeout
retrieved `sparc/docs/User_Guide.md`; it reinforced evidence-gated advancement,
with no additional dependency warranted.

| Acceptance | Executable evidence |
|---|---|
| Existing SQL calculations and attributes | evaluate-sql, 5/5 calculations, 52/52 attributes, 5/5 hashes |
| Graph facts/evidence preserved | evaluate-sql, nodes44/44 edges61/61 precision22/22 coverage5/5 errors0 |
| Types, nulls, casts, filters and rejection | sql-calculations.test.ts, including independent-review regressions |
| Valid exact source evidence | test helper checks spans/snippet SHA and validateArtifact |
| Deterministic complete bytes | sql-determinism.test.ts, independent roots and three creation orders |
| Stable/meaningful IR identity | cosmetic edits and rename stable; literal mutation changes hash |
| Bounded expression/literal work | depth64/token4096/single numeric literal1024 rejection tests |
| No schema/dependency/source-lock drift | golden-check and diff against refreshed remote |

SHA-256 inputs:

* revisions.lock.json: `c779ee204a406a187afd1f24dbbe036a5f997060d1be4a19417087267b059dc0`
* evaluation.extractors.json: `c0a35ca1f319d19700f61c9a2121f2b38f0d6f917bde7b328d4d96d8766dc86b`
* TS package-lock.json: `6451b54d934eedfddb7a744ae8e3c89e60e8d8f21dfdef58bc7e50f4582ba7e8`

Completion/full CI and refreshed explorer smoke remain pending at this phase.
