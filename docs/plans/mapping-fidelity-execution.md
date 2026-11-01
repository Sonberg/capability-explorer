# Deterministic mapping fidelity: execution evidence

Status: implementation and focused validation complete; exact-HEAD local CI and owner-review PR handoff are the remaining publication gates.
Approved Brain Prompt: [issue #6](https://github.com/Sonberg/capability-explorer/issues/6#issuecomment-6558155565).
User invoked Brain Build after this prompt. Sole target:
`stage-1-contract-and-fixtures`, starting at `9aa05475848d27328a2253cb66db87fb77975ffe`.
Integration branch: `feat/deterministic-mapping-fidelity`. PR #21 was independent
at the starting checkpoint; it merged into stage at `5ce7f21` during final local CI.
Its canvas changes are now inherited from stage, not authored by this package.

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

At the specification checkpoint, candidate integration, private Glufs replay, full
CI/security and running candidate artifacts were not yet tested. The receipts below
record subsequent verification; private corpus/performance/remote deployment claims
remain excluded.

## Pseudocode and failure paths

1. Parse literal project targets and metadata; reject unsupported conditions with
   coverage diagnostics. Traverse only reachable target-selected package dependencies.
2. Ask NuGet.Frameworks for the nearest compatible compile group, prefer ref over lib,
   and select the exact-target installed reference pack. Missing input stays partial.
3. Group assemblies by identity and content digest. Deduplicate identical bytes;
   diagnose conflicting bytes, except documented signed framework overlap policy.
4. Discover static Next handler exports under the selected app root. Derive route
   shape and parameter kinds; unsupported handlers/configuration remain incomplete.
5. Classify HTTP URL as absolute, relative or unknown. Parse the authority separately,
   apply static base resolution, remove sensitive derived metadata and retain unresolved
   deployment identity. Unknown authority/options never invent a target or method.
6. Seal baseline/candidate source and executable manifests, extract each twice offline,
   validate schema and exact source spans, then compare every semantic record against
   a reviewed delta inventory. Missing/changed inputs or unexplained deltas fail.

Complexity: framework selection is bounded by available candidate groups; dependency
traversal uses visited sets over the reachable graph. Assembly hashing is linear in
selected candidate bytes, grouping/sorting O(n log n). Route scanning is linear in
source AST size and URL normalization in URL length. Replay hashes all sealed bytes
and sorts semantic records O(n log n); it is a validation tool, not a request-path
service. No additional inference, persistent service or image is introduced.

Pseudocode gate: 95/100 (one design iteration). Contracts cover every acceptance row;
C# HTTP 11/11 and TS mapping/full suite 121/121 have executable positive/negative
cases. Five points retained for resolution/replay integration evidence, which is
required at the next gates. Brain closing pass: `sparc/docs/tutorial.md` reconfirmed
source-bound independent evidence; retrieval found no reason to add runtime machinery.

## Replay operation and trust boundary

Build/install through `make build`, then seal a new candidate with
`scripts/mapping-replay prepare .sandbox-cache/mapping-replay/candidate <HEAD-SHA>`.
Keep the baseline bundle from the separately built stage checkout. Copy its sealed
bundle into this checkout's `.sandbox-cache/mapping-replay/`, then run:

```sh
scripts/mapping-replay .sandbox-cache/mapping-replay/baseline-v3 \
  .sandbox-cache/mapping-replay/candidate tests/mapping-fixtures/manifest.json \
  .sandbox-cache/mapping-replay/result
```

Bundle paths and output directories are immutable per run; choose fresh names for
a new source snapshot. `sourceSnapshotHash` and `buildHash` bind actual bytes;
`commitHint` is an explicitly advisory caller label. The normal clean-commit local
CI gate independently binds publication to HEAD. These manifests are local integrity
checks, not signed attestations or distributed authority. Installed dependency bytes
are sealed and selected versions checked against the lock; replay does not recompute
registry tarball integrity. Full replay inputs are synthetic, with their own content
identity; they do not alter locked product fixture sources/revisions/goldens.

## Architecture gate

Architecture score: 95/100 (two review iterations). Integrated source `8589efa`
builds with zero warnings/errors; C# extractor 59/59, contract 105/105 and TS
122/122 tests pass, with zero skips. `make test-extractors test-contracts` evidence
is in `.sandbox-cache/mapping-integration.log`; the separate final TS run is in
`.sandbox-cache/ts-integration.log`. Typed `HttpRequestTarget`, `ProjectModel` and
`PackageResolution` stay within their existing extractor assemblies; no new service,
image, public artifact shape or dependency cycle is introduced.

Independent reviews fixed opaque/spread HTTP option guessing, C# typed-base evidence
and conflicting registrations, metadata/rejected-target fingerprint gaps, unknown
project target visibility, project/package collisions and empty-reference-group
fallback. C# resolution's 28 added cases and HTTP's 14 cases cover these boundaries.
The CLI now invokes `FetchAsync(models)` so target-aware behavior reaches production.

Five points remain for integrated replay/full CI and unavailable private-corpus
verification; these are explicit limits rather than a claim of MSBuild equivalence.
Brain closing pass: `sparc/docs/tutorial.md` again requires independent evidence;
no additional runtime component was justified. Existing static parsing, NuGet's
pinned compatibility reducer and native URI parsers implement the chosen contracts.

## Refinement gate and final local evidence

Refinement score: 95/100 (three verification iterations). The first integrated
C# golden run exposed SDK-pruned dependencies incorrectly reported as missing;
selected-pack PackageOverrides evidence fixed the cause without suppressing unknown
packages or changing thresholds. Replay then rejected the intentional TS input-hash
expansion; an exact, source-manifest-bound transition now permits only that reviewed
change. Output-path traversal is rejected after path normalization.

Final focused results, all zero failures/skips:

| Command/evidence | Result |
|---|---|
| `make test-extractors evaluate-cs` | 74 tests; 10/10 C# goldens; all metrics/coverage at 100% |
| TS typecheck and `npm test` through sandbox | 122 tests |
| `make test-contracts` | 105 tests |
| `make evaluate-ts evaluate-sql` | 3/3 TS and 5/5 SQL goldens, unchanged thresholds |
| Sandbox Explorer / Interpretation / Capabilities tests | 138 / 28 / 28 tests |
| `make test-mapping-replay` equivalent sandbox command | 16 tests, including failure paths |
| `scripts/mapping-replay` with fixed image digests | 21 assertions, 17 exact semantic changes, 38 verified source spans; two byte-identical runs per build |

The committed [replay receipt](../evidence/mapping-fidelity-replay.json) binds the
source snapshots, executable bytes, synthetic inputs, expected manifest and local
ARM64 runtime image digests. It records the deliberate input-hash transition and
limitations. The actual CLI commands ran offline; analyzed code was not executed.
Fixture sources, revision IDs, golden bytes and thresholds have no diff from stage.

Logs: `.sandbox-cache/mapping-csharp-recheck.log`, `mapping-ts-sql-evaluation.log`,
`mapping-platform-regression.log`, `ts-integration.log`, `replay-final-tests.log`
and `replay-final2.log`. Earlier failed logs remain available for diagnosis.
Independent native reviewers covered resolution/HTTP boundaries and replay integrity.
Brain closing pass: `sparc/docs/tutorial.md` reconfirmed source-bound independent
proof; no additional runtime tool was justified. No external inference calls were
made; native session spend is unavailable and savings are not estimated.

Five points withheld: private Glufs source-to-artifact improvement is unmeasured
(3), and this focused receipt does not prove every target framework/runtime platform
or arbitrary MSBuild/NuGet graph behavior (2). These boundaries remain explicit.

## Completion and publication handoff

The full project act/Trivy pre-push gate must pass on the final clean commit, followed
by fresh `make pr-check` and a PR targeting stage-1-contract-and-fixtures with owner
review requested and auto-merge disabled. Exact publication SHA/base/gate receipts
belong in the PR handoff comment and `.sandbox-cache/local-ci/last-success.txt`;
do not modify tested source merely to transcribe the subsequent publication result.
Completion is not implied by the focused score above. Main, production databases,
release tags and deployed artifacts are untouched.

What this work does not prove: real Glufs diagnostic-count reductions, full MSBuild
or NuGet graph emulation, execution of Next configuration/proxies, deployment service
identity, a performance target, remote deployment or owner acceptance. Issue #6
stays open for those remaining investigations. PR #21 merged during the first full
act run: CI passed, but the final stage check blocked the push. Integrating stage
preserved both changelog entries and requires a fresh full gate on the merge commit.
This is observed stale-base rejection, not an inferred protection claim.
