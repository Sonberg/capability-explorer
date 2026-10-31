# Reference fixtures

Five small repositories covering two related business flows, **weekly sales per article** and
**demand-based pricing**, with known symbols, calls, integrations and calculation variants. Precision
and coverage are measured against them before anything is scaled up.

```
web-ts        TS view + fetch ─────────HTTP──────────▶ api-cs        ASP.NET controllers + EF Core + T-SQL
                                                          ▲  ▲         dbo.Sales, dbo.ArticleStats
forecast-cs   minimal APIs, typed HttpClient ──HTTP──────┘  │
   ▲  ▲       EF Core/Npgsql, keyed DI, reflection          │
   │  └──consumes topic article-stats-updated ◀── worker-cs nightly job (Dapper) ──reads/writes─┘
   │                                                (publishes)
pricing-node  Express, axios ──HTTP──┘   generated OpenAPI client ──HTTP──▶ api-cs
              pg + PostgreSQL migrations, checkJs JavaScript, handler-map dispatch
              consumes *queue* article-stats-updated in namespace sb-pricing (not the topic above)
```

| Repo | Stack | Patterns it exercises |
|---|---|---|
| `api-cs` | .NET 10, ASP.NET Core controllers, EF Core SqlServer, T-SQL | attribute routes, interface dispatch, scoped DI, explicit `ToTable`, inline TVF |
| `worker-cs` | .NET 10 worker, Dapper, Service Bus | SQL in string constants, topic publish, hosted service, broken project reference |
| `web-ts` | TypeScript, `fetch` | template-literal URL with configured base, client-side calculation |
| `forecast-cs` | .NET 10 minimal APIs, EF Core Npgsql, Service Bus | `MapGroup` + extension-method routes, typed `HttpClient` with base address from config, keyed DI, `Activator.CreateInstance`, open-generic repository, convention vs attribute table mapping, partial classes, generated file, topic subscription, `fold`-style calculation, NaN/throws/overflow |
| `pricing-node` | Node, Express 5, axios, `pg`, PostgreSQL | routers mounted under prefixes, inline route lambdas, axios `baseURL` from env, generated OpenAPI client, dynamic `ORDER BY`, migrations + SQL function, `checkJs` JavaScript, overloads, handler-map dispatch, JSON-config-bound queue, re-export move |

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
| Same name ≠ same resource | `dbo.Sales` via EF (`@unbound:api-cs/Sales.Api:conn=Sales`) vs the DB project (`@unbound:api-cs/SalesDb:dbproject=SalesDb`) | `Cross_repo_links_…` |
| Unchanged units reuse | `Sales.Domain` and `SalesDb` have the same `inputHash` at all three api-cs tags | `Unchanged_units_keep_their_input_hash_across_revisions` |
| Configuration-driven targets | Base URL from `globalThis.SALES_API_URL`; Service Bus namespace from config; connection strings by name | unresolved edges with `reason: configuration` |

| Same config key, two repos | `worker-cs` and `forecast-cs` both read `ServiceBus:ConnectionString`, so their unbound channel keys must differ; `pricing-node` names `sb-pricing` literally and uses a *queue* | `Same_config_key_name_in_two_repositories_yields_different_channels` |
| Contract change breaks a consumer | `forecast-cs` `v2-contract` moves routes to `/api/v2/forecasts`; `pricing-node` still calls `/api/forecasts/{}` | `Route_version_change_breaks_the_match_with_an_unchanged_consumer` |
| Constant change | `v2-contract` raises smoothing alpha 0.3 → 0.5: `irHash` changes, shape attributes do not | `Constant_change_changes_the_calculation_but_not_its_shape` |
| Move behind a re-export | `pricing-node` `v2-move`: `priceForDemand` moves to `demand.ts`, old module re-exports it. Callers' code is unchanged, the resolved key changes, `irHash` does not. | `Moving_a_function_behind_a_re_export_…` |
| Generated code | OpenAPI client and `*.g.cs` contract are `generated: true` | `Generated_code_is_marked` |
| Runtime choice of implementation | keyed DI → two `candidate` edges; `Activator.CreateInstance(Type.GetType(…))` → `unresolved: reflection`; `handlers[subject]` → `unresolved: dynamic-dispatch` + rule candidate | `Keyed_di_…`, `Dynamic_dispatch_…` |
| Generic data access | `Repository<T>` reads `weekly_history` or writes `Forecasts` depending on the type argument | `Data_access_through_a_generic_repository_…` |
| Dynamic SQL | `ORDER BY ${sortColumn}` → `sqlAnalysis: partial` | `Dynamic_sql_is_marked_partial` |
| Numeric special cases | empty input → `NaN` (smoothing) vs throws (`Average`); unchecked int loop wraps; PostgreSQL bigint overflow throws; `Math.trunc` vs `toFixed` vs `ROUND` | `Special_cases_are_planted` |
| Currency | `weekly_revenue` sums across a `currency` column; legacy JS converts to SEK and silently drops unknown currencies | `Special_cases_are_planted`, `FixtureSqlTests` |

`FixtureSqlTests` runs the `pricing-node` migrations in the Testcontainers PostgreSQL and checks the
engine actually produces what the golden attributes claim: active-week denominator, mixed currencies,
half-away-from-zero rounding, NULL on an empty period, a half-open period and bigint overflow.

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
