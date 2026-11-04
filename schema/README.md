# ce.v1 extractor contract

See the [ubiquitous language](../docs/ubiquitous-language.md) for shared domain terms.

Every extractor (Roslyn, TypeScript, SQL, config) writes the same format: one **JSONL artifact per
analysis unit** (one project) at one exact revision. The platform only accepts artifacts that pass
both validators:

- `record.schema.json`: JSON Schema 2020-12 for each line. It is the single source of truth. The .NET
  platform embeds it and the TypeScript side loads it from here; neither keeps a copy.
- `relations.json`: the node kinds each relation may connect.
- Everything below that JSON Schema cannot express. These rules are enforced by
  `platform/src/CapabilityExplorer.Contracts/ArtifactValidator.cs`, and the cheap ones are mirrored in
  `extractors/ts/src/contracts/validateArtifact.ts`.

## Framing

```
header        exactly once, first line
evidence*     \
node*          |  any order
edge*          |
calculation*   |
diagnostic*   /
complete      exactly once, last line
```

A file without a final `complete` record is **truncated** and is never imported. `complete.counts`
must match the records, so dropped lines are detected as well. Extractors write `complete` last,
after everything else has been flushed.

## Identity

| Identity | What it is | Where |
|---|---|---|
| `symbolKey` | One symbol within one revision. Stable across line moves. | `node.symbolKey`, `edge.from`/`to`, `calculation.symbolKey` |
| occurrence | One observation: (artifact, symbolKey). The artifact fixes revision, unit, extractor and config. | `node` table, unique `(artifact_id, symbol_key)` |
| `lineageId` | Possible continuity across revisions. Never produced by extractors; assigned by explicit matching (stage 5). | — |
| record `id` | Local to one artifact. Prefixed by kind: `n:` `e:` `ev:` `c:` `d:`. | every body record |

Line numbers are never part of identity.

### symbolKey grammar

Segments are separated by `|`. The first segment is the scheme.

| Scheme | Form | Example |
|---|---|---|
| `cs` | `cs\|<repo>\|<assembly>\|<documentation comment ID>` | `cs\|api-cs\|Sales.Api\|M:Sales.Api.Calculations.WeeklySalesCalculator.AveragePerCalendarWeek(System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow},System.DateOnly,System.DateOnly)` |
| `ts` | `ts\|<repo>\|<package>\|<module path>\|<declaration path>#<meaning>` | `ts\|web-ts\|@fixture/web\|src/sales/weeklyStats.ts\|averagePerActiveWeek#value` |
| `proj` | `proj\|<repo>\|<unit path>` | `proj\|api-cs\|src/Sales.Api/Sales.Api.csproj` |
| `svc` | `svc\|<repo>\|<service name>` | `svc\|api-cs\|Sales.Api` |
| `http` | `http\|<service ref>\|<METHOD> <normalised route>` | `http\|api-cs/Sales.Api\|GET /api/articles/{}/weekly-sales` |
| `sql` | `sql\|<store ref>\|<schema>.<object>[#column]` | `sql\|@unbound:api-cs/Sales.Api:conn=Sales\|dbo.Sales` |
| `chan` | `chan\|<provider>\|<namespace ref>\|<entity>` | `chan\|servicebus\|sb-pricing.servicebus.windows.net\|queue/article-stats-updated` |
| `store` | `store\|<provider>\|<instance>[\|<database>]` | `store\|mssql\|sql-sales\|SalesDb` |

Rules:

- **C#** uses Roslyn's documentation comment ID (`ISymbol.GetDocumentationCommentId()`). It already
  encodes kind, containing type, generic arity and parameter types. The assembly segment is the one
  the compiler sees: reference assemblies for framework types (`System.Runtime`, not
  `System.Private.CoreLib`).
  Keys up to 2,048 characters are unchanged. Longer keys use
  `cs|<repo-or-@ext>|<assembly>|sha256:<64 lowercase hex characters>`, hashing the
  complete original scoped key with SHA-256 over UTF-8. Declarations and references
  use the same mapping; observed collisions across extraction units fail closed.
  Consumers must follow `DECLARES` to locate a compacted member's containing type
  rather than parse its digest. Names, signatures and source evidence remain separate.
- **TypeScript**: the module path is relative to the package root. The declaration path is the chain
  of containers joined with `.` (`SalesApi.getWeeklySales`). `#meaning` (`value`, `type`, `namespace`)
  separates merged declarations that share a name. Overloads are **one** symbol with several
  declarations: list the signatures in `props.overloads` and give one evidence span per declaration.
  The module itself is `<module path>|(module)#namespace` (node kind `File`).
- **Anonymous code** (lambdas, arrow functions, handlers passed inline) has no key of its own. Its
  facts are attributed to the nearest named container. Top-level TypeScript code is attributed to the
  module node, and C# top-level statements to `M:Program.{Main}$(System.String[])` (Roslyn's documentation-comment ID for the synthesized entry point; doc IDs write `<>` as `{}`). Set
  `props.handler: "lambda"` where it matters.
- **Generics**: a call on a constructed generic (`Repository<WeeklyHistory>.ListAsync`) targets the
  original definition (``M:…Repository`1.ListAsync(…)``). The type arguments go in
  `props.typeArguments`. Rules that depend on them (EF table per entity) name them in
  `props.typeArgument`.
- **Partial types** are one symbol, with evidence in every file that declares part of it.
- **External symbols** use `@ext` as the repository: `cs|@ext|System.Runtime|M:System.Math.Round(System.Decimal,System.Int32)`
  and `ts|@ext|typescript|lib.es5.d.ts|Math.round#value`.
- **SQL built-ins**: `sql|@builtin:<dialect>|<FUNCTION>`.
- **Routes** are normalised: parameters become `{}`, the query string is dropped and there is no
  trailing slash. The original template stays in `props.route`.
- A node whose key names a repository (`cs`, `ts`, `proj`, `svc`) must be declared in an artifact
  for that repository.

### Unbound resources

Extractors often know only a *logical* name for a resource (a connection-string name, a config key, an
environment variable, a base-URL variable), not the physical resource. They say so explicitly, scoped
to the unit that reads the name:

```
@unbound:<repo>/<unit>:<hint kind>=<value>
  @unbound:api-cs/Sales.Api:conn=Sales
  @unbound:forecast-cs/Forecast.Api:config=ServiceBus:ConnectionString
  @unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL
  @unbound:api-cs/SalesDb:dbproject=SalesDb
```

The scope is required. `worker-cs` and `forecast-cs` both read a config key called
`ServiceBus:ConnectionString`, and nothing in the code proves it holds the same namespace in both. An
unscoped hint would give both repositories the identical channel key. The validator rejects unscoped
hints, and hints scoped to a repository other than the one producing the artifact.

An edge to an unbound key has `resolution: "unresolved"` and `unresolvedReason: "configuration"`.
Binding happens at snapshot level (stage 3), using environment and IaC configuration or a
human-maintained service map. That binding produces new `rule`- or `human`-derived edges and never
rewrites extractor output.

The service map is `bindings.json`. Each entry replaces one whole `@unbound:` segment of a key with a
concrete name:

```json
{ "version": 1, "bindings": [
  { "hint": "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL",
    "target": "forecast-cs/Forecast.Api", "note": "why a human believes this" } ] }
```

`http|@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL|GET /api/forecasts/{}` then becomes
`http|forecast-cs/Forecast.Api|GET /api/forecasts/{}`. The new edge has `derivation: "human"`,
`rule: service-map 1`, the original edge's evidence, and a deterministic id. It is `resolved` when
the bound endpoint is declared in the selected revisions. A `REQUESTS` edge whose bound endpoint does not
exist (a renamed or moved route) is `unresolved` with reason `missing-reference` and is shown as **broken**.
A binding is evaluated per snapshot, so the same map is bound in one revision selection and broken in another.
Bindings are exact: the hint, including its repository/unit scope, must match, and a hint that no edge uses is
reported as unused.

When the value is literal in the repository (for example `config/default.json` names the Service Bus
namespace), the extractor uses it directly and the edge is `resolved`. Same-named resources stay
distinct unless their full keys match: `topic/article-stats-updated` in an unbound namespace and
`queue/article-stats-updated` in `sb-pricing` are three different channels across the fixtures.

## Evidence

- Every `node`, `edge` and `calculation` references at least one evidence record. Every known
  attribute does too.
- Evidence repeats `repositoryId` and `commitSha`, and they must equal the header's. Evidence can never
  point into another revision.
- Spans: lines and columns are 1-based, columns count UTF-16 code units, `endColumn` is exclusive.
- `snippetSha256` is the sha256 of the UTF-8 span text after CRLF→LF normalisation.
  `EvidenceVerifier` recomputes it from `git show <sha>:<path>`, so tampered or misaligned evidence
  fails without any checkout.

## Derivation, resolution, review

These three are independent:

- `derivation`: who produced the claim, one of `compiler`, `rule` or `human`. `llm` is deliberately
  **not** allowed in extractor output. AI interpretations live in separate, reviewable tables (stage 6).
- `resolution`:
  - `resolved`: the target is proven.
  - `static-target`: the compiler proves the static target only (interface or virtual dispatch,
    DI-provided instances).
  - `candidate`: one of several possible targets.
  - `unresolved`: the target is unknown and `unresolvedReason` says why.
- `reviewStatus` is not part of extractor output. Reviews attach to stored facts later and never
  modify them.

Rule-derived records name their rule (`rule: {name, version}`). Every rule pack applied is listed in
`header.rules`.

## Coverage

`complete.status` is `full` or `partial`:

- `full`: every source file in the unit was analysed, and no diagnostic affects coverage.
- `partial`: `coverage.incompletePaths` and/or a diagnostic with `affectsCoverage: true` say what is
  missing.

Consumers must treat a symbol that is missing from a partial artifact as **not observed**, not as
**removed**.

## Calculations

`calculation.expr` is a small, language-neutral IR:

- Locals are replaced by `$n` placeholders. Inputs are declared in `inputs`, and lambda/row variables
  are introduced by `agg.bind`.
- Constants, numeric types, operators and operand order are kept. There is no reordering by
  commutativity or associativity.
- Rounding is explicit, with unambiguous mode names: `halfEven` (.NET default), `halfAwayFromZero`
  (T-SQL `ROUND`, `MidpointRounding.AwayFromZero`), `halfTowardPositiveInfinity` (JS `Math.round`).
- Calls keep their target symbol (`call.target`, `round.target`).
- Loops that are not a plain filtered sum/count become `fold` (`over`, `bind`, `acc`, `init`, `step`,
  optional `skip`), as in exponential smoothing. Dictionary or array lookups are `index`. Anything the
  IR cannot express faithfully is `opaque` with a reason; it is never approximated.
- `cmp` keeps JavaScript strictness (`===`/`!==` vs `==`/`!=`).

`attributes` is a fixed set: aggregation, timeBucket, period, denominatorSource, filters, rounding,
numericType, nullHandling, zeroDenominator and currency, plus `overflow` where it is relevant (`wraps`
for an unchecked C# loop, `throws` for PostgreSQL bigint arithmetic). Each attribute is `known` (with value and
evidence), `partial` or `unknown`. **Unknown means no evidence. It is never filled in from naming.**

`irHash` is the sha256 of the canonical JSON (sorted keys) of `expr`. It is identical across a pure
rename and changes when logic changes.

## Hashes

- `header.inputHash` covers the unit's own inputs and the public API surface hashes of its
  dependencies (`dependencySurfaces`). Together with the extractor and `configHash`, it decides
  whether an artifact can be reused for another revision.
- The importer's analysis key is `(repository, commit, unit path, extractor name, extractor version,
  configHash)`. Re-importing the same key with identical bytes is a no-op. With different bytes it is
  an error, because the extractor is not deterministic.

## Versioning

`schema: "ce.v1"` appears on every line. Additive, optional fields can be added within v1. Anything
that changes meaning, identity or required fields becomes `v2/` next to `v1/`, and the importer
accepts both during a migration window.
