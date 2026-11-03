# capability-explorer

Extracts a versioned model of architecture, dependencies, business concepts and calculations from
several codebases (C#, TypeScript/JavaScript, SQL). Every claim is traceable to an exact revision and
source span.

**Status: stage 3 started (persistence and binding).** A C# extractor (Roslyn) and a TypeScript/JavaScript extractor
(TypeScript compiler API) produce the ce.v1 contract, and a SQL extractor reads T-SQL projects and PostgreSQL
migrations as text. Both are gated in CI against a reference fixture
set with known answers, and `make analyze` runs them over the repositories of a capability. Also
present: validators on both stacks, an idempotent PostgreSQL importer and the explorer UI.
C#, TypeScript/JavaScript and the supported SQL routine calculations are extracted and gated.

Shared terminology: [ubiquitous language](docs/ubiquitous-language.md).
Decision overview: [MADR status ledger and relationship map](docs/adr/README.md).

## Contributing and PR updates

Start with [HANDOFF](docs/HANDOFF.md) and the
[PR lifecycle and conflict-prevention decision](docs/adr/0005-pr-lifecycle-and-conflict-prevention.md).
**All PRs merge only into `main`. Preserve main through owner-reviewed PRs.**
Claim scope before editing and use an isolated feature branch/worktree based on
freshly fetched `origin/main`. Integrate that target
before CI; create PRs explicitly with `gh pr create --base main`.
Do not directly push, merge, revert or rewrite main without explicit authority.
The current transition uses existing PR #27; see [MADR-0016](docs/adr/0016-main-only-pr-integration.md).
One integration owner coordinates shared files and dependent PRs. The default is
owner review and merge; agents do not merge PRs or enable auto-merge. Run
`make pr-check` after the required pre-push gate and before creating a PR or
handing updated code back for review. See the MADR for conflicts and retargeting.

## Before pushing

Install Docker and GitHub CLI, then run `gh extension install nektos/gh-act --pin v0.2.94`.
Download the pinned local runner once:

```sh
docker pull catthehacker/ubuntu:act-24.04@sha256:c58e2b364da03b0c804c7d660f2ecbedf2f221a382b9baa0b344b0144780ff43
```

Run `make install-git-hooks` once in each clone. `make local-check` executes the full
GitHub CI job locally: action advisory audit, Trivy all-severity dependency scan
(including dev dependencies), fixture compilation, all tests and golden gates.
The pre-push hook requires a clean checkout of the outgoing commit and blocks on
failure. Package, image and vulnerability database downloads are cached locally.

Wayne Ashley Berry's `gh-act` is a dependency auditor, so it is invoked separately
by `scripts/actions-audit` using the host GitHub login before act starts, to avoid replacing the Nektos runner's `gh act` command.
`make actions-audit` and `make security-check` run those focused checks. This setup
applies project-wide; see [MADR-0003](docs/adr/0003-local-ci-before-push.md) for the
policy, limits, pinned tools and hook installation behavior. Hosted CI remains required.

## Layout

Trusted native development remains the default outside CI. Use `EXECUTION=sandbox`
for fully containerized development/validation; CI selects that profile and uses
only Docker on the runner. Repository extraction is sandboxed in either profile.
Starting analysis also requires a trusted host .NET 10 runtime: the host owns
the run journal, exclusive execution and publication while containers produce
staged artifacts. See the [local lifecycle guide](docs/local-analysis-lifecycle.md)
for supported host/API combinations and recovery limits.
Run `make dev` (or `make EXECUTION=sandbox dev`) to import saved analysis artifacts
and start the explorer without re-analyzing repositories. Run `make analyze-all`
separately to refresh the data, or opt in at startup with `DEV_REANALYZE=1`.
Analysis runs up to two offline extraction jobs concurrently across repositories and languages.
Use `make analyze CAPABILITY=<id> ANALYZE_JOBS=4` to raise the limit, or `ANALYZE_JOBS=1`
for serial extraction. The same setting applies to `analyze-all`, `analyze-worker`, and
the worker started by `make dev`; restart an existing worker to change its limit.
Package downloads and publication remain sequential. Each extractor writes isolated staging
output, merged only after every job succeeds. Logs include phase and extraction-task durations;
increase concurrency conservatively because concurrent Roslyn processes can use substantial memory.
Compare the private `.sandbox-cache/analysis-receipt.json`
application/repository SHAs and hashes with the owner to verify shared inputs;
unavailable repositories remain failures or not assessed, never successful analysis.

| Path | Content |
|---|---|
| `schema/v1/` | `record.schema.json` (JSON Schema, the source of truth), `relations.json`, [rules](schema/README.md) |
| `fixtures/` | Five fixture repos (C#, TypeScript/JavaScript, T-SQL, PostgreSQL) built deterministically from overlays, plus golden artifacts. See [fixtures/README.md](fixtures/README.md). |
| `platform/` | .NET 10: `Contracts` (validator, evidence verifier), `Binding` (service-map binding of `@unbound` hints), `Evaluation` (scoring against golden), `Store` (migrations, importer), `Cli` (`ce`), `Extractors.CSharp` (`ce-cs`), `Capabilities` (repository sets and source acquisition), `Interpretation` (optional LLM), `Explorer` (API + React/Tailwind UI), tests |
| `extractors/ts/` | Node/TypeScript package: the TypeScript/JavaScript extractor (`ce-ts extract`) and the contract validator (ajv against the same schema). |
| `scripts/sandbox` | Isolates capability source acquisition and extraction; normal development runs on the host |

## Running

Everything goes through `make`. Trusted project restore, build, tests, the Explorer API and Vite run directly on
the host for normal debugger and hot-reload support. Capability analysis alone runs in hardened containers because
it reads repositories that must be treated as untrusted input.

Install .NET 10, Node.js 22, Docker and make, then run:

```sh
make ci                 # fixture compile checks + all tests
make test               # golden check, TS tests, .NET contract tests, store tests
make fixtures-compile   # every fixture revision compiles (or fails) as intended
make backend            # Explorer API on http://127.0.0.1:5080
make frontend           # Vite dev server with hot reload
```

### Run the complete development environment

With Docker running, one command starts the complete local stack:

```sh
make dev
```

This builds the solution and production frontend, starts PostgreSQL with pgvector and Ollama, imports and
embeds the available artifacts, starts the capability-analysis worker, and serves the application at
<http://127.0.0.1:5080>. The first run can take longer while Docker images and Ollama models are downloaded.
The Explorer API, solution, frontend build, import, and embedding run directly on the host; only capability
source acquisition and extraction run in the analysis sandbox.

The built frontend is already served by `make dev`. For frontend hot reload, leave it running and start Vite
in a second terminal:

```sh
make frontend
```

To analyse a configured capability while the stack is running, use the **Analyse** action in the UI or run:

```sh
make analyze CAPABILITY=weekly-sales
```

The Explorer reloads completed artifacts automatically. Stop the local PostgreSQL and Ollama containers with:

```sh
make dev-down
```

To run everything required by CI rather than start the development environment:

```sh
make ci
```

### Exploring the model

```sh
make explore                                   # golden fixtures → http://127.0.0.1:5080
make explore ARTIFACTS=out/ EVALUATION=out/evaluation.json   # extractor output + its score
```

The explorer is an ASP.NET Core server over a directory of artifacts. Its UI is React + Tailwind (Vite),
in `platform/src/CapabilityExplorer.Explorer/client`. `make ui` builds it into `wwwroot`, which is a build
output and not committed. `make frontend` serves it with hot reload and proxies the API to a host-native
`make backend`. Both bind to `127.0.0.1` only.

- **Capability selector**: a capability is a named set of repositories, given as GitHub URLs or local
  paths, that are checked together. Selecting one scopes every view to its repositories at their locked
  revisions. Manage them on the **Capabilities** page or in `capabilities.json`. See below.
- **Revisions bar**: pick one revision per repository. Every view shows that combination, which is
  labelled as a selection of code revisions, not a deployment.
- **Explore**: search symbols; open one to see its exact source span at that revision, its calculation
  (formula and attributes, each with evidence), outgoing and incoming edges with resolution and
  derivation, and whether the key exists in each loaded revision (present, absent, or *not observed*
  when that analysis was partial).
- **Architecture**: a pan/zoom canvas in three levels of abstraction.
  - *System*: repositories, data stores and channels.
  - *Components*: types, modules and SQL objects of one repository, grouped by unit.
  - *Code*: the members of one component and everything they touch, including external APIs and
    unresolved targets.

  Double-click to drill down; breadcrumbs go back up. Line style shows certainty: proven, static
  target, unbound, candidate or unresolved. Clicking a connection lists the underlying facts with
  their evidence. Edge kinds can be toggled.
- **Integrations**: services and endpoints; HTTP calls with their candidate endpoint (a route match is
  labelled a candidate while the base URL is unbound); channels and tables grouped by name with every
  distinct key kept apart. This view now requires a migrated PostgreSQL database and a published
  snapshot; it never falls back to legacy files. Global deprecated/obsolete marks use versioned writes.
- **Calculations**: all implementations side by side. Values that differ from the most common value in
  a column are highlighted. `unknown` is shown as unknown, never filled in. Tick two rows for a
  deterministic attribute-by-attribute comparison.
- **Changes**: diff two revisions of a repository. You get continuity candidates with reasons (rename,
  move), logic and attribute changes with possible static consumers, added and removed symbols, and
  *not observed* instead of removed for partial analyses.
- **Artifacts** and **Evaluation**: coverage, diagnostics and rejected files, plus the `ce evaluate`
  report when one is loaded.

#### Capabilities

Shared definitions live in `capabilities.json`. Optional, git-ignored `capabilities.local.json`
uses the same `{ "capabilities": [...] }` structure and is combined with shared definitions
by the Explorer, CLI export, and analysis scripts. A local entry with the same `id` replaces
the entire shared capability, including its repository list; other entries are appended.
Start a new local file with `{ "capabilities": [] }`. Invalid local configuration fails loading
rather than being silently skipped.

When the local file exists, newly added UI capabilities are saved there. Existing shared
definitions stay in the shared file, and local definitions are never copied into it.
Deleting a capability removes that ID from both files, including any overridden shared entry.
Restart the Explorer after editing configuration files manually. For a custom `CAPABILITIES`
path, the matching sibling is `<name>.local.json`; add it to your own ignore rules.
Analysis receipts hash shared and local configuration separately without including their contents.

Each line is `[id=]location [ref]`:

```
https://github.com/acme/orders-api
https://github.com/acme/web/tree/release/2026.10
billing=https://github.com/acme/billing-service v4.2.0
/home/me/code/warehouse-jobs
```

*Check now* acquires each source read-only and locks it to one commit. GitHub repositories become bare,
blob-less partial clones under `.sandbox-cache/sources`, with no checkout, hooks, submodules or build.
Local repositories are read in place. The definitions live in `capabilities.json`, which is fit to
commit; the locked revisions live in `.sandbox-cache/sources/snapshots.json`. Repository ids are global,
because artifacts are keyed by them. A repository shows as *analysed* once artifacts for its locked
commit are loaded (see *Analysing a capability* below).

**Local repositories**

- Absolute and `~/` paths listed in `capabilities.json` are read directly by the host-native Explorer.
- `~` means your home directory.
- Relative paths resolve inside this checkout.

**Private GitHub repositories**

`make explore` resolves a token on your machine and passes it to the host-native Explorer process only. It
never prints the token. The order is:

1. `GITHUB_TOKEN` or `GH_TOKEN`.
2. `gh auth token`, if you are logged in with the GitHub CLI.
3. Your git credential helper (macOS keychain or Git Credential Manager), with the default `GITHUB_AUTH=git`.

`GITHUB_AUTH=auto` skips the git credential helper. `GITHUB_AUTH=none` turns authentication off.

The token is given to git through environment config, as an HTTP header for `https://github.com/` only.
It never appears in a clone URL, a repository config, a command line or on disk. If GitHub refuses the
token, the clone is retried anonymously, so public repositories still work. A fine-grained token with
*Contents: read* on the repositories you need is enough; the Capabilities page shows whether credentials
are available.

#### Optional LLM explanations

The intelligence layer is **off by default** (`interpretation:provider = none`). Everything above,
including comparisons, works without it, and nothing is sent anywhere. When it is enabled, the
comparison panel gets an *Explain differences* button. The model receives only the deterministic
attribute table, never source or file paths. Its answer must cite those differences or it is
rejected. The result is shown as an unreviewed AI proposal.

```sh
# Microsoft Foundry (OpenAI-compatible v1 endpoint). The key is read from CE_FOUNDRY_API_KEY;
# without a key, Entra ID (DefaultAzureCredential) is used.
CE_FOUNDRY_API_KEY=<key> make explore INTERPRETATION=foundry \
  FOUNDRY_ENDPOINT=https://<resource>.services.ai.azure.com FOUNDRY_DEPLOYMENT=<deployment> [RECORD_TO=recordings/]
# Replay stored responses (no live model), as the tests do:
make explore INTERPRETATION=recorded RECORDINGS=recordings/
```

See [docs/intelligence-layer.md](docs/intelligence-layer.md).

Source is read with `git show` from `fixtures/.repos`, and only for files an artifact cites. All
repository content is rendered as text, never as markup.

### Analysing a capability

```sh
make analyze CAPABILITY=weekly-sales     # while `make explore` runs, the UI picks up the results by itself
```

Test projects and conventional test files (`tests/`, `__tests__/`, `*.test.*`, `*.spec.*`) are excluded from the
default production graph and therefore from imports and embeddings. Set `"includeTests": true` on a capability in
`capabilities.json` when it should retain tests. `INCLUDE_TESTS=0|1 make analyze ...` overrides that setting for one run.

Each analysis stage runs in its own sandbox and gets only what it needs. Optional import and embedding happen on
the host after artifact validation:

| Step | Sandbox | What happens |
|---|---|---|
| 1 export | network, GitHub token, local repositories mounted read-only | Checks the capability again, so each ref is locked to its current commit (`LOCKED=1` keeps the last check). Then it writes each locked tree to `.sandbox-cache/analyze/<id>/src/`. Only regular files are written: symlinks and files over 8 MB are skipped. A blob-less clone fetches the blobs it needs in one batch. |
| 2 packages | network, no GitHub token, no local mounts | Resolves one exact version set per project (declared versions, ranges at their lower bound, floating ones to the newest stable, then nuspec dependencies) and writes it as `.sandbox-cache/analyze/<id>/locks/<repo>.json`. It downloads those packages into `.sandbox-cache/analyze-packages`, unpacked as data only: nuspec plus `ref/`/`lib/` assemblies. There is no restore and no MSBuild evaluation. Feeds come from the repository's `nuget.config` (https only; nuget.org unless `<clear />`). Credentials for private feeds are `%VAR%` references in that file, resolved from the variables named in `NUGET_PASS_ENV=VAR1,VAR2`; only this step receives them, they go only to their own feed's host, and they are never printed or stored. `PACKAGES=0` skips this step (no packages are used). |
| 3 extract | `--offline` | Runs `ce-cs extract` (with only the locked package versions), `ce-ts extract` and `ce-ts extract-sql` over each tree. The output for each revision is swapped into `.sandbox-cache/artifacts/<repo>/<commit>/` only once every extractor has finished. |

For npm dependencies, configure the registry and scope in Explorer Settings. Analysis also reads
your host npm user configuration (`~/.npmrc`, or `npm_config_userconfig`) for `_authToken` entries
matching those configured registries. Host tokens override saved credentials for that run only;
`${NPM_TOKEN}` references require the variable to be set in the terminal running analysis.
Only the package-download sandbox receives the resolved credential, and npm lifecycle scripts
remain disabled. Credentials are not copied into exported repositories or saved settings.

The analysed code is parsed and bound, never restored, built or executed. `make explore` loads
`fixtures/expected` and `.sandbox-cache/artifacts`. For the same repository, commit and unit, the
analysed artifact replaces the golden one. The explorer polls the artifact directories and the
capability files and reloads when they change, and the UI refetches what it shows.

**C# (`ce-cs`)**

- It builds Roslyn compilations from `.csproj` read as XML. Project references come from the project
  files, framework references from the SDK reference packs, and packages from step 2.
- Source generators do not run. Their absence is an info diagnostic.
- Constructors are `Method` nodes named after their type (`props.constructor`, parameter list as `signature`).
  `new T(...)` and `: base(...)`/`: this(...)` are `CALLS` to the declared constructor; a type with no declared one
  (default, or a record's primary constructor) gets a `REFERENCES` instead. A constructor `REFERENCES` the repository
  types of its parameters, so "this input is built from a Cart and a Quote" is a fact in the graph. Code inside a
  constructor body (lambdas included) belongs to it. Only repository types are recorded: `new List<int>()` and an
  `ILogger` parameter say nothing about the system. Field and property initializers are not attributed to a
  constructor, and a constructor's own parameter types are not yet recorded for ordinary methods.
- Compiler errors make the affected files `partial`.
- Rules: ASP.NET Core controllers and minimal APIs, DI (including keyed services and reflection),
  EF Core, Dapper, Azure Service Bus, typed `HttpClient`.
- Calculations: a method that returns a number and contains an aggregate, an accumulation loop, rounding or a
  division becomes a `calculation` record. The body is lowered with the semantic model: locals are inlined,
  `Where/Select/Distinct` + `Sum/Average/Min/Max/Count` become one `agg`, a `foreach` that accumulates into a
  local becomes a `fold`, `Math.Round` keeps its mode, and implicit numeric conversions become `conv`. The
  attributes come from the IR and the syntax (never from names); what the code does not settle stays `unknown`.
  A construct the IR cannot express (other statements, unresolved calls, non-constant statics) means no
  calculation for that method, never an approximation.

**TypeScript/JavaScript (`ce-ts`)**

- A unit is one `tsconfig.json`, or a `package.json` with plain JavaScript and no tsconfig. Each file
  belongs to the deepest unit that includes it.
- Imported packages are not installed. Calls into them are left out, and a `DEPENDENCIES_NOT_INSTALLED`
  info diagnostic names them. Calls into the TypeScript libs resolve to `ts|@ext|typescript|…`.
- Rules: Express routes (apps, nested routers, `route()` chains), `fetch`, axios clients, generated
  OpenAPI clients, `pg` SQL, Azure Service Bus (literal values in imported JSON config bind the
  channel), and handler maps behind dynamic dispatch (as `candidate` edges).

**SQL (`ce-ts extract-sql`)**

- A unit is a `.sqlproj` (T-SQL; with `Sdk=` every script below it, otherwise the `<Build Include>` list,
  minus pre/post-deploy scripts), or else the folder above `migrations/`, `tables/`, `functions/`… that holds
  loose `.sql` files. The dialect (T-SQL or PostgreSQL) is detected from the scripts.
- Scripts are tokenised and read statically, in path order, so migrations apply in sequence (`ALTER TABLE …
  ADD/DROP/RENAME COLUMN`, `DROP`, `CREATE OR REPLACE`). No database is needed and no script is ever run.
- Nodes: `Project`, `Table`, `Column` (with type and nullability), `View`, `Routine`. Edges: `DECLARES`,
  `READS` and `WRITES` (tables and columns, resolved through aliases, CTEs and subqueries) and `CALLS`
  between routines of the unit. A reference to an undeclared object is an `unresolved` edge with
  `missing-reference`.
- Dynamic SQL (`EXEC (…)`, `sp_executesql`, `EXECUTE`) is reported as `SQL_DYNAMIC` and makes the unit
  `partial`. Reads and writes are never guessed.
- Without a `.sqlproj` nothing names the unit, so the name is inferred and an `SQL_UNIT_NAME_INFERRED`
  info diagnostic says from what: the database in the repository's connection strings (T-SQL), or the
  enclosing package name plus `-db` (PostgreSQL). The reference fixtures rely on this.

- Routine calculations use a bounded typed expression parser and ce.v1 lowering. Unsupported
  calculations produce `SQL_CALCULATION_UNSUPPORTED`; ordinary graph facts remain available.
  Dynamic SQL retains its existing partial coverage behavior. Units and currency stay unknown
  when names or arithmetic scaling are the only clues. See the
  [SQL work package and evidence](docs/plans/sql-build-evidence.md).
- The subset includes int32/bigint and dialect decimal/numeric arithmetic, SUM, COUNT DISTINCT,
  AND/comparisons/BETWEEN, null replacement, NULLIF, two-argument ROUND, T-SQL DATEDIFF(day),
  PostgreSQL date_trunc(week), and date plus/minus integer. Widening int32-to-bigint and PostgreSQL
  integer-to-unbounded-numeric casts preserve conversions. Numeric parameter types normalize
  `int`/`integer` to `int32`; string/date declaration spellings remain explicit.

**Known limits**

- Calculations are extracted for C# and TypeScript/JavaScript, and gated at 100% (`fixtures/evaluation.csharp.json`, `fixtures/evaluation.extractors.ts.json`).
- TS/JS calculations cover numeric functions that round (`Math.round/trunc/floor/ceil`, `toFixed`) or aggregate (`filter`/`map`/`reduce`, `new Set(..).size`, `for…of` sums). Other shapes yield a `CALCULATION_UNSUPPORTED` info diagnostic instead of an approximate IR.
- SQL calculations cover bounded static single-expression routines with at most one table. Joins,
  procedural statements, smallint/tinyint, implicit return conversions, constrained casts/returns,
  exponent/leading-dot numeric literals and other unsupported semantics are rejected. Parser
  limits apply after tokenization: 4096 body tokens and expression depth 64. Numeric literals are
  limited to 1024 characters before numeric conversion; these are not whole-file limits.
- Package versions are locked per analysis: the same commit and lock give byte-identical artifacts whatever the shared download folder holds. `header.configHash` covers the resolved set. Floating versions (`1.*`) are resolved when the lock is written, so re-running the packages step later can pick a newer one and then yields a different `configHash`. Without `--lock` (as in `make evaluate-cs`) `ce-cs` falls back to the folder's contents; the hash records what it used.
- Only `@unbound` hints in edge targets are bound; environment/IaC files as a binding source are not read yet (service map only).

`make evaluate-cs`, `make evaluate-ts` and `make evaluate-sql` run each extractor over every fixture revision. They
score it against the golden units of its own languages using `fixtures/evaluation.csharp.json` (C#),
`fixtures/evaluation.extractors.ts.json` (TS/JS) and `fixtures/evaluation.extractors.json` (SQL). Graph recall, edge
accuracy, evidence and precision are held at 100%; for all three extractors so are calculation recall, attribute accuracy and
`irHash` match. All three targets run in `make test`.

### Storing analyses and binding unbound resources

Storing is optional and off unless `CE_DATABASE` (an Npgsql connection string) is set:

```sh
CE_DATABASE='Host=127.0.0.1;Port=5433;Database=ce;Username=ce;Password=...' make analyze CAPABILITY=weekly-sales
```

Step 4 of `make analyze` then runs migrations and imports every artifact it just wrote. The importer is
idempotent, and only the trusted host import step gets the connection string during analysis.
The Explorer also uses the configured database for curated marks and pinned integrations.
Tests start their own PostgreSQL with Testcontainers.

`bindings.json` is a human-maintained service map from an `@unbound:` hint to a concrete service or store
(format in [schema/README.md](schema/README.md#unbound-resources)). It is read by both consumers:

- `make explore` reads it (`BINDINGS=other.json` to change it). A bound `REQUESTS` edge becomes a link from the
  caller's repository to the endpoint, drawn as *bound*. If the endpoint is missing in the selected revisions
  (the forecast-cs `v2-contract` route change) the link is *broken*. The extractor's own edge stays unresolved
  and is listed as the underlying fact.
- `make analyze … BINDINGS=bindings.json` with `CE_DATABASE` also saves a binding snapshot: new `bound_edge`
  rows, keyed by a hash of the artifacts and the bindings file, pointing at the extractor edge they came from.
  `ce import --bindings bindings.json <artifacts…>` does the same by hand.

Binding adds `human`-derived edges beside the facts. `node`, `edge` and `artifact` rows are never changed.

Deprecation marks are global curated PostgreSQL rows, not snapshot members. After `ce migrate`,
explicitly import the legacy JSON array once (repeat imports are safe):

```sh
dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll import-deprecations --file /absolute/path/deprecations.json
```

Set `CE_DATABASE` in the trusted host environment. Import preserves timestamps, inserts missing
marks and reports invalid/conflicting entries without overwriting existing values; any diagnostic
returns exit 1 for operator review. It does not modify `capabilities.json` or delete the legacy file.
Runtime never reads that legacy file, including during database outages.

`GET /api/deprecations` lists marks with a `version`. `PUT /api/deprecations` takes
`{key,status,note,expectedVersion?}`: omit the token only for creation. Delete with
`DELETE /api/deprecations?key=...&expectedVersion=...`. Stale tokens return 409; unavailable
PostgreSQL returns 503. Reload before retrying a conflict. The Integrations editor retains the
token captured when editing starts and displays write failures.

`GET /api/integrations?cap=...&snapshot=...&view=repo:full-sha,...` pins one published analysis
snapshot and revision selection plus the current global mark set. Omitting `snapshot` resolves the
current publication once. Removal facts use only revisions inside that snapshot, ending at the
selected revision; they do not follow a newer publication. Database projections are capped at
1,000 integration edges and 1,000 relevant nodes with an eight-KiB per-row guard, a one-MiB
response limit and a ten-second request deadline. Exceeding a budget is explicit, never truncation.
Other file-mode views remain available, but integrations/marks without `CE_DATABASE` return 503.

### Validating extractor output

There are three levels, each stricter than the one before:

| Question | Command | Checks |
|---|---|---|
| Is it a valid ce.v1 artifact? | `ce validate out/*.jsonl` | schema, framing (truncation), ids, references, relation catalogue, unbound scoping, counts, coverage consistency |
| Does the evidence match the code? | `ce verify-evidence --repo <git dir> out/*.jsonl` | every span exists at that commit and its snippet hash matches (`git show`, no checkout) |
| Is it **correct**? | `make evaluate ACTUAL=out/ [REPO=forecast-cs]` | scores against `fixtures/expected` using `fixtures/evaluation.json`; also runs both checks above |

`ce evaluate` pairs artifacts by header (repository, commit, unit path), not by file name, and reports:

- **Recall**: golden nodes (`symbolKey` + kind), edges (`from`, relation, `to`, or the reason when the
  target is unknown), calculations and every calculation attribute.
- **Accuracy**: matched edges must agree on `resolution`, `unresolvedReason` and `derivation`.
  Claiming `resolved` for an interface call is an error. For attributes, an `unknown` in the golden set
  must stay `unknown`; a guessed value is reported as `AttributeFabricated`.
- **Evidence located**: a matched fact must point at the same file and overlapping lines.
- **Precision**, only where the golden set is exhaustive (integration edges, endpoints, channels).
  Golden sets are subsets, so an extra `CALLS` edge costs nothing, but an extra `READS` does.
- **Coverage**: `full`/`partial`, incomplete paths and expected diagnostics.

The output is a text report with one line per finding, saying where the fact was expected, plus
`out/evaluation.json`. The exit code is 1 if any threshold in `fixtures/evaluation.json` fails. The
thresholds start at 100%; lower one only as a reviewed decision. `make evaluate-golden` scores the
golden set against itself and runs in CI as a self-check of the scorer.

### Analysis sandbox

`scripts/analyze` uses `scripts/sandbox <dotnet|node> <cmd…>` only for capability export, package acquisition and
extraction. The trusted solution, frontend, tests and Explorer process run on the host. Analysis containers have
these guarantees:

- It runs as the host uid, never as root.
- The root filesystem is read-only, all capabilities are dropped and `no-new-privileges` is set.
- Only the repository is mounted. Package caches live in `.sandbox-cache/`.
- The environment is an explicit allowlist, so no host variables or credentials leak in.
- npm lifecycle scripts are disabled.
- Extraction uses `--offline` (`--network none`). The analysed code is parsed, never restored, built or executed.
- Analysis containers never receive the Docker socket. `make test-store` runs on the host and Testcontainers talks
  to Docker directly.

## Stage 1 done criteria

"Identity and evidence are consistent", checked by:

- `GoldenArtifactTests`: every golden artifact passes the contract, points at its locked commit, and
  every evidence span hashes to the text at that commit (`git show`, no checkout).
- `ValidatorRejectionTests`: one test per rule (truncation, framing, ids, foreign keys, relation
  catalogue, coverage, placeholders, tampered evidence, wrong revision).
- `FixtureScenarioTests`: the planted scenarios (variants, rename, filter change, broken project,
  unbound resources) are present in the golden facts.
- `ImporterTests` (Testcontainers): imports are atomic, idempotent, safe under concurrent duplicates,
  and reject a changed artifact under the same analysis key.
- `FixtureSqlTests` (Testcontainers): the PostgreSQL fixture SQL runs and behaves as its golden
  attributes claim.
- `EvaluatorTests`: starting from a perfect "extractor" (golden = actual), each test breaks one thing
  and checks that the scorer reports exactly that finding and moves exactly that metric.
- `ExplorerApiTests`: the explorer API surfaces the planted scenarios (renames, contract breaks, partial
  analyses, same-named channels) and refuses to serve uncited files.
- `InterpretationTests` / `InterpretationApiTests`: the deterministic comparer on the planted variants,
  the layer off by default (409, everything else works), and on with a recorded provider. Invalid model
  output is rejected, responses are cached by input hash, and the Foundry client is tested against a fake
  HTTP endpoint. No test calls a live model.
- `CapabilityTests` / `CapabilityApiTests`: parsing of repository lines (GitHub URL forms, local paths,
  refs, rejected input), atomic definition and snapshot storage, global repository ids, locking local
  repositories, and views scoped to a capability's locked revisions.
- `extractors/ts` tests: the same golden files validate against the same schema in TypeScript. The
  extractor is also tested on synthetic projects: routers, clients, SQL, messaging and plain JavaScript.
- `BinderTests` / `BindingApiTests` / `BindingStoreTests` / `ImportCommandTests`: binding the FORECAST_API_URL hint to forecast-cs (bound in `v1-base`, broken in `v2-contract`), extractor edges untouched, malformed maps rejected, snapshots idempotent in PostgreSQL, and `ce import --bindings` end to end.
- `LiveReloadTests`: new artifacts on disk raise the explorer's generation and replace the same unit.

## Next

- Broader SQL calculation syntax beyond the documented static subset.
- Open decision: TypeScript 7 (native compiler) has a different programmatic API. The extractor pins
  `typescript@5.9` until that API is evaluated.
