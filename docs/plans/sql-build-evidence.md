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
