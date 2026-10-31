# capability-explorer

Extracts a versioned model of architecture, dependencies, business concepts and calculations from
several codebases (C#, TypeScript/JavaScript, SQL). Every claim is traceable to an exact revision and
source span.

**Status: stage 1 (contract and fixtures).** No extractor exists yet. What exists is the contract
every extractor must produce, a reference fixture set with known answers, validators on both stacks,
and an idempotent PostgreSQL importer.

## Layout

| Path | Stage 1 content |
|---|---|
| `schema/v1/` | `record.schema.json` (JSON Schema, the source of truth), `relations.json`, [rules](schema/README.md) |
| `fixtures/` | Five fixture repos (C#, TypeScript/JavaScript, T-SQL, PostgreSQL) built deterministically from overlays, plus golden artifacts. See [fixtures/README.md](fixtures/README.md). |
| `platform/` | .NET 10: `Contracts` (validator, evidence verifier), `Evaluation` (scoring against golden), `Store` (migrations, importer), `Cli` (`ce`), `Capabilities` (repository sets and source acquisition), `Interpretation` (optional LLM), `Explorer` (API + React/Tailwind UI), tests |
| `extractors/ts/` | Node/TypeScript package. Stage 1 contains only the contract validator (ajv against the same schema). |
| `scripts/sandbox` | Runs every build, install and test in a disposable container |

## Running

Everything goes through `make`, and `make` only calls `scripts/sandbox`. Nothing is restored,
installed or compiled on the host. The host needs Docker (and make).

```sh
make ci                 # fixture compile checks + all tests
make test               # golden check, TS tests, .NET contract tests, store tests
make fixtures-compile   # every fixture revision compiles (or fails) as intended
```

Behind a TLS-intercepting proxy, run `export SANDBOX_EXTRA_CA_FILE=/path/to/ca.pem` first.

### Exploring the model

```sh
make explore                                   # golden fixtures → http://127.0.0.1:5080
make explore ARTIFACTS=out/ EVALUATION=out/evaluation.json   # extractor output + its score
```

The explorer is an ASP.NET Core server over a directory of artifacts. Its UI is React + Tailwind (Vite),
in `platform/src/CapabilityExplorer.Explorer/client`. `make ui` builds it into `wwwroot`, which is a build
output and not committed. `npm run dev` in that folder serves it with hot reload and proxies the API to a
running explorer. Everything runs in the sandbox and publishes on `127.0.0.1` only.

- **Capability selector**: a capability is a named set of repositories, given as GitHub URLs or local
  paths, that are checked together. Selecting one scopes every view to its repositories at their locked
  revisions. Manage them on the **Capabilities** page or in `capabilities.json`. See below.
- **Revisions bar**: pick one revision per repository. Every view shows that combination, which is
  labelled as a selection of code revisions, not a deployment.
- **Explore**: search symbols; open one to see its exact source span at that revision, its calculation
  (formula and attributes, each with evidence), outgoing and incoming edges with resolution and
  derivation, and whether the key exists in each loaded revision (present, absent, or *not observed*
  when that analysis was partial).
- **Integrations**: services and endpoints; HTTP calls with their candidate endpoint (a route match is
  labelled a candidate while the base URL is unbound); channels and tables grouped by name with every
  distinct key kept apart.
- **Calculations**: all implementations side by side. Values that differ from the most common value in
  a column are highlighted. `unknown` is shown as unknown, never filled in. Tick two rows for a
  deterministic attribute-by-attribute comparison.
- **Changes**: diff two revisions of a repository. You get continuity candidates with reasons (rename,
  move), logic and attribute changes with possible static consumers, added and removed symbols, and
  *not observed* instead of removed for partial analyses.
- **Artifacts** and **Evaluation**: coverage, diagnostics and rejected files, plus the `ce evaluate`
  report when one is loaded.

#### Capabilities

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
commit are loaded; extractors arrive in stage 2.

**Local repositories**

- Absolute and `~/` paths listed in `capabilities.json` are mounted read-only automatically when
  `make explore` starts.
- `~` means your home directory on the host, even inside the container.
- A path added through the UI after start-up needs a restart, or a parent directory mounted up front:
  `make explore SOURCES=$HOME/Code`.
- Relative paths resolve inside this checkout.

**Private GitHub repositories**

`make explore` resolves a token on your machine and forwards it into the explorer container only. It
never prints the token. The order is:

1. `GITHUB_TOKEN` or `GH_TOKEN`.
2. `gh auth token`, if you are logged in with the GitHub CLI.
3. With `GITHUB_AUTH=git`, also your git credential helper (macOS keychain or Git Credential Manager).

`GITHUB_AUTH=none` turns this off.

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
SANDBOX_PASS_ENV=CE_FOUNDRY_API_KEY make explore INTERPRETATION=foundry \
  FOUNDRY_ENDPOINT=https://<resource>.services.ai.azure.com FOUNDRY_DEPLOYMENT=<deployment> [RECORD_TO=recordings/]
# Replay stored responses (no live model), as the tests do:
make explore INTERPRETATION=recorded RECORDINGS=recordings/
```

See [docs/intelligence-layer.md](docs/intelligence-layer.md).

Source is read with `git show` from `fixtures/.repos`, and only for files an artifact cites. All
repository content is rendered as text, never as markup.

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

### Sandbox model

`scripts/sandbox <dotnet|node> <cmd…>` runs `<cmd>` in a stock SDK image with these guarantees:

- It runs as the host uid, never as root.
- The root filesystem is read-only, all capabilities are dropped and `no-new-privileges` is set.
- Only the repository is mounted. Package caches live in `.sandbox-cache/`.
- The environment is an explicit allowlist, so no host variables or credentials leak in.
- npm lifecycle scripts are disabled.
- `--offline` adds `--network none`. Network is used only for `restore` and `npm ci`. Builds and
  tests that need nothing external run offline, so a restored package can execute build logic but
  cannot reach anywhere.
- `--docker` mounts the Docker socket. Only `make test-store` uses it, so Testcontainers can start a
  throwaway `postgres:17-alpine`. The socket gives control of the host Docker daemon, so analysis and
  extraction steps must never get it.

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
- `extractors/ts` tests: the same golden files validate against the same schema in TypeScript.

## Next (stage 2)

- Roslyn extractor (`MSBuildWorkspace`) and TypeScript extractor (`ts.createProgram` + `TypeChecker`),
  both emitting ce.v1.
- Open decision: TypeScript 7 (native compiler) has a different programmatic API. Stage 1 pins
  `typescript@5.9` for the extractor until that API is evaluated.
