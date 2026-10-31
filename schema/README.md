# ce.v1 extractor contract

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
| `sql` | `sql\|<store ref>\|<schema>.<object>[#column]` | `sql\|@unbound:conn=Sales\|dbo.Sales` |
| `chan` | `chan\|<provider>\|<namespace ref>\|<entity>` | `chan\|servicebus\|@unbound:config=ServiceBus:ConnectionString\|topic/article-stats-updated` |
| `store` | `store\|<provider>\|<instance>[\|<database>]` | `store\|mssql\|sql-sales\|SalesDb` |

Rules:

- **C#** uses Roslyn's documentation comment ID (`ISymbol.GetDocumentationCommentId()`). It already
  encodes kind, containing type, generic arity and parameter types. The assembly segment is the one
  the compiler sees: reference assemblies for framework types (`System.Runtime`, not
  `System.Private.CoreLib`).
- **TypeScript**: the module path is relative to the package root. The declaration path is the chain
  of containers joined with `.`. `#meaning` (`value`, `type`, `namespace`) separates merged
  declarations that share a name. Overloads add `(sig:<8 hex>)` before `#` only when more than one
  declaration exists.
- **External symbols** use `@ext` as the repository: `cs|@ext|System.Runtime|M:System.Math.Round(System.Decimal,System.Int32)`
  and `ts|@ext|typescript|lib.es5.d.ts|Math.round#value`.
- **SQL built-ins**: `sql|@builtin:<dialect>|<FUNCTION>`.
- **Routes** are normalised: parameters become `{}`, the query string is dropped and there is no
  trailing slash. The original template stays in `props.route`.
- A node whose key names a repository (`cs`, `ts`, `proj`, `svc`) must be declared in an artifact
  for that repository.

### Unbound resources

Extractors often know the *logical* name of a resource (a connection-string name, a base-URL variable)
but not the physical resource. They say so explicitly:

```
@unbound:<hint kind>=<value>      e.g. @unbound:conn=Sales, @unbound:base=salesApiBaseUrl, @unbound:dbproject=SalesDb
```

An edge to an unbound key has `resolution: "unresolved"` and `unresolvedReason: "configuration"`.
Binding happens at snapshot level (stage 3), using environment and IaC configuration or a
human-maintained service map. That binding produces new `rule`- or `human`-derived edges and never
rewrites extractor output. Two keys with the same table name but different hints
(`@unbound:conn=Sales` vs `@unbound:dbproject=SalesDb`) are **different resources** until a binding
says otherwise.

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
- `cmp` keeps JavaScript strictness (`===`/`!==` vs `==`/`!=`).

`attributes` is a fixed set: aggregation, timeBucket, period, denominatorSource, filters, rounding,
numericType, nullHandling, zeroDenominator and currency. Each attribute is `known` (with value and
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
