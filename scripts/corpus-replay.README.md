# Locked corpus replay

`./scripts/corpus-replay validate-plan .sandbox-cache/local-plan.json` verifies
inputs without extraction. `./scripts/corpus-replay run .sandbox-cache/local-plan.json`
performs two offline runs of each explicitly selected extractor for each build.
Run paths must be new: retain prior reports and choose a new `output` each time.
The script does not fetch repositories, install target dependencies, execute target
code, import databases, infer binding equivalence or accept arbitrary commands.

A local plan uses this structure (placeholder hashes must be replaced with actual
SHA-256 digests; repository commits must be full Git SHA-1 identifiers):

```json
{
  "format": "corpus-replay-plan.v1",
  "output": ".sandbox-cache/corpus-replay/review-1",
  "images": { "node": "sha256:<64 hex>", "dotnet": "sha256:<64 hex>" },
  "builds": {
    "baseline": { "path": ".sandbox-cache/mapping-replay/baseline", "buildHash": "<64 hex>" },
    "candidate": { "path": ".sandbox-cache/mapping-replay/candidate", "buildHash": "<64 hex>" }
  },
  "repositories": [{
    "id": "authorized-repository",
    "commit": "<40 hex>",
    "checkout": ".sandbox-cache/private/checkout",
    "source": ".sandbox-cache/private/export",
    "files": { "src/example.ts": "<SHA-256 of exact bytes>" },
    "inputHash": "<SHA-256 of canonical files map>",
    "commands": ["ts"]
  }]
}
```

Use existing `scripts/mapping-replay prepare` to seal trusted extractor builds.
Build hashes seal all binaries, source and installed dependencies; they are not
signatures or independent proof that a build came from a trusted compiler. The
candidate's recorded source files must match the current checkout. Runtime images
are exact locally available Docker image IDs, including their framework packs.
The wrapper must be run with the repository's configured sandbox images; extraction
uses the plan's pinned IDs. Never seal or execute a build supplied by an untrusted
analyzed repository.

`checkout` must be an authorized self-contained Git checkout accessible in the
sandbox. Its HEAD must equal `commit`; every exported file must match the blob at
that commit. Source paths/bytes are checked independently of the caller's manifest.
Submodules and symlinks fail closed. Git object reads do not execute checkout hooks,
smudge filters or repository programs. Export all tracked blobs without running
project tooling. `files` uses `inventory` and `inputHash` uses
`hash(canonical(files))` from `mapping-replay-lib.mjs`.

For `csharp`, add `dependencies` containing `packages`, `packagesHash`, `lock` and
`lockHash`. The lock uses the existing extractor package-lock version 1 format,
covering every `.csproj` in the export with exact package IDs/versions. All locked
package folders must exist. `packagesHash` seals the complete package directory
inventory; `lockHash` seals the lock's raw bytes. Fetching metadata is a separate,
authorized data-only step. No resolve/fetch fallback occurs during replay. Repos
with unsupported/missing target packs may still emit partial diagnostics: a
successful deterministic replay is not proof of complete or correct mapping.

The local report binds the plan, runner scripts, image IDs, source inventories and
build hashes. It checks ce.v1 schema/structure, source evidence snippets and raw
repeat-byte equality. Per-unit semantic deltas include added/removed units and
preserve diagnostics and uncertainty. A zero delta is valid repeatability evidence;
nonzero changes require review, not automatic approval. Private output includes
artifact content and extraction logs and stays under ignored `.sandbox-cache`.
Publish only manually reviewed/redacted aggregate findings.

Tests: `scripts/sandbox --offline node node --test tests/corpus-replay.test.mjs`.
The tests are synthetic and make no claim about access to private repositories.
Current configured Glufs HEAD API requests returned HTTP 404 under Tigon32 during
this work; source SHAs, dependency locks, target-pack requirements and actual
mapping improvements therefore remain unverified pending readable authorized inputs.
