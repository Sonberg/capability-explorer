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
| `platform/` | .NET 10: `Contracts` (validator, evidence verifier), `Store` (migrations, importer), `Cli` (`ce`), tests |
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
- `extractors/ts` tests: the same golden files validate against the same schema in TypeScript.

## Next (stage 2)

- Roslyn extractor (`MSBuildWorkspace`) and TypeScript extractor (`ts.createProgram` + `TypeChecker`),
  both emitting ce.v1.
- An evaluation command that scores extractor output against `fixtures/expected`.
- Open decision: TypeScript 7 (native compiler) has a different programmatic API. Stage 1 pins
  `typescript@5.9` for the extractor until that API is evaluated.
