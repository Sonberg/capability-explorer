# Handoff: remaining work

Status at handoff: branch `stage-1-contract-and-fixtures`. `make ci` is green.

## What exists

| Area | Where | State |
|---|---|---|
| ce.v1 contract | `schema/v1/`, [schema/README.md](../schema/README.md) | Stable. JSON Schema, the relation catalogue and the key grammar. |
| Fixtures + golden set | `fixtures/` (5 repos, locked revisions), `fixtures/golden/facts/*.mjs` → `fixtures/expected/` | Hand-written expected facts for every node, edge and calculation, anchored to text in the source. `make golden` regenerates them. |
| Validators | `platform/src/CapabilityExplorer.Contracts`, `extractors/ts/src/contracts` | Both stacks validate the same contract. |
| Scorer | `platform/src/CapabilityExplorer.Evaluation`, `ce evaluate` | Measures recall, accuracy, evidence, precision on exhaustive categories, calculations, attributes, irHash and coverage. Can be limited with `--repository` and `--language`. |
| C# extractor | `platform/src/CapabilityExplorer.Extractors.CSharp` (`ce-cs`) | 100% on graph metrics and on calculations (`Calculations*.cs`, gated by `fixtures/evaluation.csharp.json`). |
| TS/JS extractor | `extractors/ts/src/extract` (`ce-ts extract`) | 100% on graph metrics. **No calculations.** |
| Analyse pipeline | `scripts/analyze`, `make analyze CAPABILITY=<id>` | Three sandboxed steps: export → packages → offline extract, written to `.sandbox-cache/artifacts`. |
| Explorer | `platform/src/CapabilityExplorer.Explorer` (+ React client) | Live reload. Capabilities, architecture canvas, optional LLM layer. |
| Store | `platform/src/CapabilityExplorer.Store` | Migrations and an idempotent importer, tested with Testcontainers. Not yet fed by `make analyze`. |

Ground rules that apply to all work:

- Every build, install and test goes through `scripts/sandbox` via `make`. Nothing runs on the host.
- Extraction is `--offline` and never builds, restores or executes the analysed code.
- Evaluation thresholds are only ever raised.

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

### 2. TypeScript/JavaScript calculation extraction

- **Goal:** the same as workstream 1 for `web-ts` (`averagePerActiveWeek`) and `pricing-node`
  (`priceForDemand`, `weeklyRevenueSek`). Watch for:
  - JSDoc-typed JS
  - `Math.round` as `halfTowardPositiveInfinity`
  - `toFixed`
  - `Math.trunc`
  - the moved function in `v2-move`, which keeps the same `irHash` under the new key.
- **Where:** `extractors/ts/src/extract/` (new `calculations.ts`). `builder.ts` hard-codes `calculation: 0`.
- **Done when:** `make evaluate-ts` passes with the calculation metrics gated at 1.0.

### 3. SQL extractor

- **Goal:** extract the SQL units, which have no extractor yet:
  - `api-cs` SalesDb: a T-SQL `.sqlproj`, 3 golden artifacts, unit `SalesDb`.
  - `pricing-node` `db/`: PostgreSQL migrations, 2 golden artifacts, unit `pricing-db`.
- **Nodes and edges:** `Project`, `Table`, `Column`, `View`, `Routine`, `DECLARES`, `READS`, `WRITES`, plus the
  routine calculations (`pricing.weekly_revenue`).
- **Keys:** `sql|@unbound:<repo>/<unit>:dbproject=<name>|schema.object[#column]` and `sql|@builtin:<dialect>|FN`.
  See `fixtures/golden/facts/api-cs.mjs` (database section) and `pricing-node.mjs` (`database()`).
- **Approach:** a parser that runs no code and needs no database. For example a .NET project using
  `Microsoft.SqlServer.TransactSql.ScriptDom` for T-SQL, and a PostgreSQL grammar such as `pgsql-parser`/libpg_query
  WASM in Node, or a careful hand parser. It must work offline.
- **Wire up:** `scripts/analyze` step 3, plus a `make evaluate-sql` gate with `--language sql`.
- **Done when:** all 5 SQL golden artifacts pass the graph thresholds. Calculations come second.

### 4. Deterministic package resolution + private feeds (C#)

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

### 5. Persist analyses + bind unbound resources (stage 3)

- **Goal:**
  - `make analyze` also imports artifacts into PostgreSQL. The `Store` importer already exists.
  - Snapshot-level binding of `@unbound:` hints (env vars, config keys, connection names) to concrete
    services and stores. Sources: environment/IaC config, or a human-maintained service map (`bindings.json`).
  - Binding produces new `rule`/`human` edges and never rewrites extractor output (see schema/README "Unbound resources").
- **Explorer:** shows bound cross-repo links. Example: pricing-node `REQUESTS GET /api/forecasts/{}` →
  forecast-cs endpoint, and the v2 route change showing as a broken link.
- **Where:** `platform/src/CapabilityExplorer.Store`, a new `Binding` library, `Explorer/Architecture.cs`.

## Smaller follow-ups

- C# extractor unit tests on synthetic projects. Today only the golden gate covers it; TS has synthetic tests.
- `scripts/extract-fixtures.sh` re-parses the lock file with sed. A `ce` subcommand would be sturdier.
- The explorer polls every 2 s. Fine locally, but worth making configurable.
- The TypeScript 7 native compiler API: evaluate before upgrading beyond `typescript@5.9`.
