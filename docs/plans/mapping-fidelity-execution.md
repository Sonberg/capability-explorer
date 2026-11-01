# Deterministic mapping fidelity: execution evidence

Status: specification accepted; implementation and validation in progress.
Approved Brain Prompt: [issue #6](https://github.com/Sonberg/capability-explorer/issues/6#issuecomment-6558155565).
User invoked Brain Build after this prompt. Sole target:
`stage-1-contract-and-fixtures`, starting at `9aa05475848d27328a2253cb66db87fb77975ffe`.
Integration branch: `feat/deterministic-mapping-fidelity`. PR #21 remains independent,
with successful hosted CI and owner review pending. Its canvas paths are excluded.

## Contract and ownership

Deliver target-aware C# framework/package visibility, static Next.js endpoints,
correct HTTP target identities and source-bound deterministic replay. Preserve
ce.v1, source evidence, conservative uncertainty, direct dependencies, locked
fixture sources/revisions and all evaluation thresholds. The existing transitive
project-reference repair is a regression requirement, not new scope.

The coordinator acknowledges isolated scopes in
[issue #6](https://github.com/Sonberg/capability-explorer/issues/6#issuecomment-6558366987)
and [issue #3](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6558367349).
C# resolution owns ProjectModel/PackageResolver/PackageFetcher/PackageLock/
CSharpExtractor and new helpers/tests. HTTP/Next.js owns TS extraction rules and
dedicated tests. Replay owns new scripts/mapping-replay*, tests/mapping-replay*
and tests/mapping-fixtures/. The coordinator owns C# Rules HTTP changes/tests,
shared manifests, locks, Makefile and documentation. No two writers share a worktree.
MADR-0010 is reserved here; MADR-0009 belongs to PR #21.

No production data mutation, inferred deployment binding, canvas change, BPMN/DMN
implementation, analyzed-code execution, main change, merge, auto-merge or release.
Static support has explicit limits: this is not full MSBuild/NuGet emulation.
Historical Glufs counts require reproduction; unavailable private inputs must be
reported instead of replacing them with invented measurements.

## Acceptance mapping

| Requirement | Executable evidence required |
|---|---|
| Target-compatible framework/package assets | Focused C# resolution tests for exact/compatible/unsupported TFMs, missing packs and packages without compatible assets |
| No ambiguous assembly identity | Same-byte deduplication and conflicting-identity diagnostics; chain/diamond/missing/cycle regression tests |
| Static Next.js endpoints | GET/POST exports, app/src-app, route groups, dynamic/catch-all/optional catch-all, unsupported export negatives |
| Conservative HTTP target identity | C#/TS absolute host/path separation, explicit absolute override, relative/config hints, dynamic/malformed authority negatives |
| Safe URL metadata | Credentials/query values/fragments excluded from target identities and derived props; query names only |
| Stable facts and evidence | Exact repository/commit/path/span and schema validation; no automatic proxy/resource equivalence |
| Determinism and analysis identity | Two byte-identical extractions per locked input/build; distinct build/config identity for changed extractor inputs |
| Reviewable delivery | Existing golden thresholds, full local act/Trivy push hook, fresh stage alignment/pr-check, owner-review PR |

Catch-all endpoint patterns retain distinct normalized tokens (`{*}` and `{**}`)
plus parameter-kind metadata. This does not introduce wildcard binding or prove a
proxy's remote target. Existing host-only HTTP identities remain; sanitized scheme
metadata may distinguish source evidence without changing the key grammar.

## Phase gates and receipts

Specification rubric: reproduced failures 40, conservative boundaries 30,
acceptance/ownership 30. Baseline `make test-extractors test-ts test-contracts`
passed in the sandbox: 17 extractor tests, 108 TS tests, 105 contract tests;
no failures/skips. Receipt: `.sandbox-cache/mapping-baseline.log`.
TS regression tests then reproduced missing Next.js endpoints and malformed
absolute URL targets before fixes. C# inspection confirmed fixed framework-list
selection and path-only reference deduplication; dedicated red tests are in progress.

Specification score: 95/100. Five points remain for real private-corpus access and
the precise supported resolution subset, to be resolved or explicitly bounded by
implementation evidence. No implementation or later gate is passed by this score.

Pseudocode gate: executable selection/normalization cases and
`make test-extractors test-ts`. Architecture gate:
`make build test-contracts test-extractors test-ts` plus independent boundary review.
Refinement gate: `make test-extractors test-ts test-contracts test-explorer
evaluate-cs evaluate-ts evaluate-sql` and a new sandbox replay command that fails
on missing inputs, unexpected semantic deltas, invalid evidence or nondeterminism.
Completion: clean commit, ordinary push/full act-security hook, fresh `make pr-check`,
owner-review PR targeting stage. No gate bypass or duplicate unchanged-head local CI.

## Cost, grounding and limitations

Native coordinator plus three `gpt-6.1-sol`/high workers; scripts handle mechanical
inventory. No additional inference-provider calls or services. Native session
spend is unavailable; no dollar savings claim. Existing npm/NuGet caches are copied
with copy-on-write into isolated worktrees, not shared as concurrent mutable stores.
All build/install/test commands use the existing sandbox and images.

Brain grounding: `sparc/docs/tutorial.md`, retrieved 2026-11-01, supports independent
phase evidence. It is not proof of extractor/runtime functionality. Library choices
also require primary package/documentation sources. Checkpoint persistence uses
explicit canonical-path store and exact-key readback; no automatic learning-health
claim follows from search results or successful CLI exit alone.

Not yet tested: candidate integrated behavior, private Glufs replay, full candidate
CI/security, served candidate artifacts, performance comparison or remote deployment.
