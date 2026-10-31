# Reference fixtures

Three small repositories covering one business flow, **average weekly sales per article**, with known
symbols, calls, integrations and calculation variants. Precision and coverage are measured against
them before anything is scaled up.

```
web-ts     TypeScript view + fetch client  ──HTTP──▶  api-cs  ASP.NET Core API + EF Core + T-SQL
                                                        ▲  dbo.Sales
worker-cs  nightly job (Dapper)  ──reads/writes──────────┘  dbo.ArticleStats
           └──publishes──▶ Service Bus topic article-stats-updated
```

## Layout

| Path | What |
|---|---|
| `src/<repo>/<step>/` | Overlay for one commit: files to add or replace, plus an optional `.delete` list |
| `manifest.json` | Commit order, tags, messages and fixed dates |
| `revisions.lock.json` | The commit SHA each tag must produce. Builds are deterministic. |
| `.repos/` (ignored) | Real git repositories built by `make fixtures` |
| `golden/facts/*.mjs` | Hand-written expected facts. Evidence is a text needle, located at the locked commit. |
| `expected/<repo>/<tag>/<unit>.jsonl` | Generated golden artifacts in ce.v1 format (`make golden`) |

The golden artifacts are a **reference subset**. They contain the facts every extractor must find,
not every reference a real extractor emits. Later stages measure recall against them, and precision
within the categories they cover.

## Planted scenarios

| Scenario | Where | Pinned by |
|---|---|---|
| Calendar weeks vs active weeks | `api-cs` `WeeklySalesCalculator` (ISO weeks in period) vs `worker-cs` `AveragePerActiveWeek` and `web-ts` `averagePerActiveWeek` (weeks with sales) | `FixtureScenarioTests.Weekly_average_variants_differ_as_planted` |
| A third bucketing | `api-cs` `dbo.fnWeeklySalesAverage`: `DATEDIFF(day)/7 + 1`, integer division, not ISO weeks | same |
| Different rounding | `halfEven` (C# `Math.Round` default), `halfAwayFromZero` (worker, T-SQL `ROUND`), `halfTowardPositiveInfinity` on float64 (JS) | same |
| Null and zero handling | null as zero (`?? 0m`, `ISNULL`) vs nulls excluded (`HasValue`, `!== null`); zero denominator returns 0 vs NULL (`NULLIF`) | same |
| Unknown stays unknown | No implementation states a currency → `currency: unknown` everywhere | same |
| Rename + move | `api-cs` `v2-rename`: `AveragePerCalendarWeek` → `CalendarWeekAverage`, file moved. Key changes; `irHash` is identical. | `Rename_changes_the_symbol_key_but_not_the_calculation` |
| Filter change | `api-cs` `v3-filter`: adds `&& !r.IsReturn`. `irHash` and `filters` change; nothing else does. | `Filter_change_changes_the_calculation_and_its_filter_attribute` |
| Consumers of a change | Controller → interface (`static-target`) → `HANDLES` endpoint → `REQUESTS` from web-ts | `Possible_consumers_…`, `Cross_repo_links_…` |
| Incomplete project | `worker-cs` `v2-broken`: references a missing `Sales.Shared` project. Artifact is `partial`, diagnostics explain why, no base symbol disappears. | `Broken_project_is_partial_and_loses_no_symbols` |
| Same name ≠ same resource | `dbo.Sales` via EF (`@unbound:conn=Sales`) vs the DB project (`@unbound:dbproject=SalesDb`) | `Cross_repo_links_…` |
| Unchanged units reuse | `Sales.Domain` and `SalesDb` have the same `inputHash` at all three api-cs tags | `Unchanged_units_keep_their_input_hash_across_revisions` |
| Configuration-driven targets | Base URL from `globalThis.SALES_API_URL`; Service Bus namespace from config; connection strings by name | unresolved edges with `reason: configuration` |

`make fixtures-compile` checks that every revision compiles, apart from `worker-cs@v2-broken`, which
must fail with `CS0234 … 'Shared'`. The fixtures are real code, so the Roslyn and TypeScript extractors
in stage 2 can load them.

## Changing fixtures

1. Edit or add an overlay under `src/<repo>/<step>/` (and `manifest.json` for a new step).
2. Run `make fixtures-lock`. SHAs change, and so does the lock file; review the diff.
3. Update `golden/facts/<repo>.mjs`, then run `make golden`. A needle that is not found fails loudly.
4. Run `make test`.

The fixture repos have nothing in common with the platform's own git history. Commit SHAs are
reproducible because author, committer, dates, file modes and line endings are fixed.
