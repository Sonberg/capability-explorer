# Handoff: remaining work

Status: reconciliation package prepared for `stage-1-contract-and-fixtures`.
The sole future work/alignment/PR target is that branch; main must remain intact.
#5/#7/#8 landed on main; #9 landed only on `feat/main-aligned-delivery` after #8.
This package includes both histories and corrected #10 lifecycle documentation.
Do not call stage reconciled until the owner actually merges this package.
The operator reports notifying the owner; subsequent validation is unconfirmed.
See [reconciliation and source SHAs](plans/stage-branch-reconciliation.md).
Environment/IaC binding remains open.
Shared terminology: [ubiquitous language](ubiquitous-language.md),
[MADR-0002](adr/0002-ubiquitous-language.md). Issue #6 tracks mapping defects;
those fixes are separate from synthetic test coverage.

PR updates and newcomer workflow: [MADR-0005](adr/0005-pr-lifecycle-and-conflict-prevention.md).

## What exists

| Area | Where | State |
|---|---|---|
| ce.v1 contract | `schema/v1/`, [schema/README.md](../schema/README.md) | Stable. JSON Schema, the relation catalogue and the key grammar. |
| Fixtures + golden set | `fixtures/` (5 repos, locked revisions), `fixtures/golden/facts/*.mjs` → `fixtures/expected/` | Hand-written expected facts for every node, edge and calculation, anchored to text in the source. `make golden` regenerates them. |
| Validators | `platform/src/CapabilityExplorer.Contracts`, `extractors/ts/src/contracts` | Both stacks validate the same contract. |
| Scorer | `platform/src/CapabilityExplorer.Evaluation`, `ce evaluate` | Measures recall, accuracy, evidence, precision on exhaustive categories, calculations, attributes, irHash and coverage. Can be limited with `--repository` and `--language`. |
| C# extractor | `platform/src/CapabilityExplorer.Extractors.CSharp` (`ce-cs`) | 100% on graph metrics and on calculations (`Calculations*.cs`, gated by `fixtures/evaluation.csharp.json`). |
| TS/JS extractor | `extractors/ts/src/extract` (`ce-ts extract`) | 100% on graph metrics and on calculations (`calculations.ts`; gated in `fixtures/evaluation.extractors.ts.json`). |
| SQL extractor | `extractors/ts/src/sql` (`ce-ts extract-sql`) | 100% on graph metrics for all 5 SQL golden artifacts (`make evaluate-sql`). SQL calculations/attributes/IR hashes pass 5/5, 52/52 and 5/5; see the build evidence. |
| Analyse pipeline | `scripts/analyze`, `make analyze CAPABILITY=<id>` | Three sandboxed steps: export → packages → offline extract (C#, TS/JS, SQL), written to `.sandbox-cache/artifacts`. |
| Explorer | `platform/src/CapabilityExplorer.Explorer` (+ React client) | Live reload. Capabilities, architecture canvas, optional LLM layer. |
| Store | `platform/src/CapabilityExplorer.Store` | Migrations, an idempotent importer and binding snapshots, tested with Testcontainers. Fed by `make analyze` when `CE_DATABASE` is set. |
| Binding | `platform/src/CapabilityExplorer.Binding`, `bindings.json` | Service-map binding of `@unbound` hints into `human` edges; shown in the explorer as bound/broken links. |

Ground rules that apply to all work:

- Every build, install and test goes through `scripts/sandbox` via `make`. Nothing runs on the host.
- Extraction is `--offline` and never builds, restores or executes the analysed code.
- Evaluation thresholds are only ever raised.

## Local checks before pushing

Install the repository hook with `make install-git-hooks`; run `make local-check`
for the full project gate. Fetch/integrate `origin/stage-1-contract-and-fixtures` first; local-check enforces
that alignment and rejects changes to stage-1-contract-and-fixtures during CI. Run `make pr-check` immediately
before PR creation, base changes or review handoff; if stage-1-contract-and-fixtures advanced, integrate and rerun. See [MADR-0003](adr/0003-local-ci-before-push.md).
The local hook is installed in this clone; other contributors must install it.
The tracked `out/evaluation.json` is a historical missing-actual report, not current
acceptance evidence. Current reports are `.sandbox-cache/extracted/{cs,ts,sql}/evaluation.json`.

## Remaining work (independent workstreams)

Each workstream owns its own directories so they can run in parallel. The shared files are
`fixtures/evaluation.extractors.json` (gates), `Makefile`, `scripts/analyze` and `README.md`. Keep edits
to these minimal and rebase before merging.

### 1. C# calculation extraction — DONE

Implemented in `Calculations.cs`, `CalculationAttributes.cs`, `CalculationIr.cs`; `make evaluate-cs` gates the three calculation metrics at 1.0 via `fixtures/evaluation.csharp.json`. Fold the thresholds into `evaluation.extractors.json` once workstream 2 lands. The original brief follows.

- **Goal:** emit `calculation` records (IR `expr`, `irHash`, `inputs`, `output`, `attributes` with
  evidence) for the methods the golden set marks as calculations. Start with `fixtures/golden/facts/{api-cs,worker-cs,forecast-cs}.mjs`
  (`a.calc(...)`). They cover 11 calculations and 112 attributes.
- **IR:** the grammar is in `schema/v1/record.schema.json` (`$defs/expr`). `irHash` is the sha256 of the canonical
  JSON (sorted keys, see `canonicalJson` in `fixtures/golden/lib.mjs`).
- **Scorer:** see `CompareAttribute` in `Evaluator.cs`. `unknown` in the golden set must stay `unknown`, because a
  guessed value is `AttributeFabricated`. Planted special cases are in `ServiceFixtureScenarioTests.cs`: zero
  denominators, overflow, rounding, and the constant change between v1 and v2 that must change `irHash` but not
  the attributes.
- **Where:** `platform/src/CapabilityExplorer.Extractors.CSharp` (new `Calculations*.cs`). `ArtifactBuilder.ToJsonl`
  currently hard-codes `calculation: 0`.
- **Done when:** `make evaluate-cs` passes with `calculationRecall`, `attributeAccuracy` and `irHashMatch`
  gated at 1.0 for csharp. Add these thresholds to a csharp-specific config, or to
  `evaluation.extractors.json` once TS is done too.

### 2. TypeScript/JavaScript calculation extraction — DONE

- **Goal:** the same as workstream 1 for `web-ts` (`averagePerActiveWeek`) and `pricing-node`
  (`priceForDemand`, `weeklyRevenueSek`). Watch for:
  - JSDoc-typed JS
  - `Math.round` as `halfTowardPositiveInfinity`
  - `toFixed`
  - `Math.trunc`
  - the moved function in `v2-move`, which keeps the same `irHash` under the new key.
- **Where:** `extractors/ts/src/extract/` (new `calculations.ts`). The shared `builder.ts` already emits calculation records and computes their hashes.
- **Done when:** `make evaluate-ts` passes with the calculation metrics gated at 1.0.

### 3. SQL extractor: calculation integration

- **Done:** `ce-ts extract-sql` (`extractors/ts/src/sql/`: `lexer`, `ddl`, `refs`, `project`, `extractor`) is a
  dependency-free hand parser for T-SQL and PostgreSQL. It runs offline, needs no database and executes nothing.
  `make evaluate-sql` passes the graph thresholds at 1.0 for all 5 golden artifacts, and `scripts/analyze` step 3
  runs it. Unit tests: `extractors/ts/test/sql.test.ts`.
- **Fixture gap:** the `api-cs` fixture has no `.sqlproj` (the golden unit `SalesDb` is just `database/`), and
  `pricing-db` has no project file either. With a `.sqlproj` the extractor takes the name from it; without
  one it infers the name (connection-string database for T-SQL, `<package>-db` for PostgreSQL) and says so in an
  info diagnostic. Adding a `.sqlproj` to the fixture would be cleaner but changes every locked revision.
- **Current package:** bounded expression parsing, typed IR and conservative attributes for routine
  calculations, emitted through the existing `ArtifactBuilder.calculation` API. SQL calculation
  recall, attribute accuracy and IR-hash thresholds are 1.0; all graph thresholds are preserved.
  The PostgreSQL golden currency/output unit was explicitly corrected to unknown because names
  and scaling do not establish monetary semantics. The subsequent security scan found
  CVE-2026-109461; the operator approved upgrading the pricing fixture to Azure Identity
  4.14.1 (locked MSAL 7.1.2), regenerating both pricing revisions and four goldens.
  Independent comparison verified unchanged semantic facts.
  Unsupported calculations retain graph facts and emit `SQL_CALCULATION_UNSUPPORTED`; dynamic
  SQL remains partial. See [the execution contract](plans/deterministic-sql-calculations.md).
- **Known gaps:** no `CREATE TRIGGER`, `SYNONYM` or cross-database references (they are `unresolved`); DML in
  `INSERT … ON CONFLICT` and `MERGE` is covered by simple rules only.

### 4. Deterministic package resolution + private feeds (C#) — DONE

- **Problem:**
  - `ce-cs fetch-packages` and `PackageResolver` choose versions from whatever the shared
    `.sandbox-cache/analyze-packages` folder holds. Re-analysing the same commit can give different edges
    into package code (api-cs gave 82 and then 67 edges).
  - The importer rejects different bytes under the same analysis key.
- **Do:**
  - Resolve one version set per project, written as a lock file next to the export.
  - Use only those exact versions when extracting.
  - Include the resolved set in `header.configHash`.
  - Optional: support private feeds from `nuget.config` with credentials passed only to step 2.
- **Where:** `PackageFetcher.cs`, `PackageResolver.cs`, `scripts/analyze`.
- **Done when:** running analyze twice on different cache states gives byte-identical artifacts. Add a test.

### 5. Persist analyses + bind unbound resources (stage 3): done for the service map

- `make analyze` imports into PostgreSQL when `CE_DATABASE` is set; `BINDINGS=` also saves a binding snapshot.
- Remaining: environment/IaC files as a second binding source (Kubernetes/compose/appsettings), a UI to edit the map,
  and reading analyses from the database in the explorer (it still reads artifact files).

## Glossary: issue #4 first slice

Read-only deterministic candidates and the Glossary tab are implemented in the
stacked glossary workstream. Names group only by explicit normalization; calendar
and active qualifiers remain distinct. Source occurrences retain revision/unit/record
identity, unknowns remain unknown, and comparison completeness is explicit.
See [MADR-0004](adr/0004-glossary-candidates.md) and
[build evidence](plans/glossary-build-evidence.md). Flow tracing, persistent reviewed
terms/same-value links, and LLM proposals remain separate unimplemented layers.

## Smaller follow-ups

- C# synthetic package and calculation tests now cover locked packages, explicit division IR, unsupported-loop graph retention and generated-source exclusion. Broader synthetic graph/route coverage remains open; issue #6 fixes are separate.
- `scripts/extract-fixtures.sh` re-parses the lock file with sed. A `ce` subcommand would be sturdier.
- The explorer polls every 2 s. Fine locally, but worth making configurable.
- The TypeScript 7 native compiler API: evaluate before upgrading beyond `typescript@5.9`.


## C# transitive project visibility (issue #6, defect 1)

The transitive-reference workstream propagates cached compilation references
through acyclic project graphs and deduplicates by compilation identity. Direct
DEPENDS_ON edges still describe only declared references. Synthetic chain,
diamond/repeat-byte, missing-project and unrelated-project controls pass; the
C# golden gate remains unchanged. See [execution evidence](plans/transitive-project-references.md).
This does not fix framework selection, package version conflicts, older framework
fallback, conditional MSBuild references, or other issue #6 mapping gaps.
