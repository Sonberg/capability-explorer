# HTTP capability recommendations (#81)

## Scope and acceptance scenarios

HTTP first, research followed by delivery in architecture inspection and binding
preview. Recommendations never adopt a binding, check a review box, create a
manual graph connection, or change extracted facts.

| Scenario | Given / When / Then | Executable selector |
| --- | --- | --- |
| R81-01 | Given an unbound client and declared matching endpoint, when ranked, then return the service with request and endpoint provenance without modifying facts. | `HttpBindingRecommendationTests.Exact_match_returns_service_and_bidirectional_evidence_without_mutating_facts` |
| R81-06/07/08 | Given separate consumer/provider capabilities, when explicitly selecting destination revisions and reviewing, then allow reviewed export only, reject destination hints and stale destination revisions, and write no bindings. | `HttpBindingRecommendationTests.Cross_capability_recommendation_requires_explicit_review_and_rejects_stale_destination` |
| R81-02/03/09 | Given 46 repeated calls (beyond the canvas sample) and two equally matching services, when reordered, then all evidence survives, scores remain deterministic, and ambiguity survives. | `HttpBindingRecommendationTests.Reordered_targets_and_repeated_call_sites_preserve_scores_and_ambiguity` |
| R81-04 | Given conflicting methods, prefixes, versions or encoded paths, when compared, then do not invent compatibility. | `HttpBindingRecommendationTests.Conflicting_method_prefix_version_or_encoded_path_does_not_match` |
| R81-04/08 | Given dynamic targets or existing bindings, when recommending, then abstain or report broken bindings without replacing them. | `HttpBindingRecommendationTests.Dynamic_targets_abstain_and_existing_bindings_are_not_overridden` |
| R81-05 | Given one hint with matching and unmatched routes, when ranked, then report partial coverage and block exact binding eligibility. | `HttpBindingRecommendationTests.Whole_hint_coverage_reports_unmatched_calls_and_blocks_export_eligibility` |
| R81-09/10 | Given architecture and binding preview at desktop/mobile sizes, when a candidate is handed off, then start unreviewed, require explicit review for export, preserve destination revisions, invalidate changed snapshots, and do not create graph connections. | `HTTP recommendation at *px requires explicit review after architecture handoff` in the existing TS7 architecture browser harness |
| R81-11 | Given the same source identity with different artifact bytes on the destination side, when matching, then reject the inconsistent snapshot. | `HttpBindingRecommendationTests.Source_and_destination_with_same_identity_but_different_bytes_are_rejected` |

The cross-capability API scenario also changes capability membership and verifies
that the prior review becomes stale. The unmocked browser scenario
`HTTP recommendation real fixture API and browser export retain both source identities`
connects the actual fixture API, UI handoff and reviewed export without request
interception. The browser test is enabled with `CE_GRAPH_TEST_REAL_URL` pointing
to the fixture-only server; the other scenarios use controlled API responses.

## Bounded option evaluation and release decision

Labels are explicit service identities for four calls in the eight existing
`v1-base` golden artifacts, reviewed against their declared routes. A fifth
call has no static target and is labelled abstain. The existing `v2-contract`
forecast endpoint removal is an additional negative revision check, not an
independent blind sample. No parameters were fitted on these examples.

| Option | Precision / recall on four labelled static calls | Determinism / explanation | Missing evidence / cost |
| --- | --- | --- | --- |
| A: exact method and normalized route | Baseline: 4/4 correct candidates, 4/4 retrieved; 1/5 observations abstains | Exact key-tail equality, no host identity claim | No dynamic route inference; bounded in-memory comparison |
| B: contract compatibility | Not measured | Request/response schema evidence is not consistently available | Deferred; unavailable does not mean incompatible |
| C: structural similarity | Not measured | DTO and contract compatibility are not established by current artifacts | Deferred rather than inventing semantic weights |
| D: infrastructure/config ownership | Not measured | Existing hints identify configuration names, not deployments | Explicit aliases can corroborate a future version; no configuration execution |
| E: embeddings/model hints | Not measured | Non-deterministic inference is outside the first release | No model calls, embeddings, or inference cost |

The reproducible baseline is
`HttpBindingRecommendationTests.Locked_fixture_exact_route_baseline_matches_four_reviewed_calls_and_abstains_on_dynamic_call`.
These are fixture candidate-retrieval measurements, not calibrated confidence,
deployment correctness, or population precision/recall. The small curated corpus
has no blind holdout and cannot establish general ranking quality.

Decision for the bounded first release: use A only. Rank by integer percentage
of distinct request method/route signatures covered by the candidate service.
Each occurrence retains its own evidence but repeated calls cannot inflate the
score. Equal scores remain ambiguous. Report unmatched routes, partial analysis,
and unknown host ownership, request payload/query, response schema, headers and content
type. A score of 100 means complete observed route coverage, not 100% confidence.
There is no automatic winner or adoption threshold. Even a generic health route
only produces a reviewable candidate, never a high-confidence claim.

Exact route semantics are deliberately the existing artifact-key semantics.
Do not strip gateway prefixes, rewrite versions, decode reserved separators, or
relax the binder. Source and target revisions must be explicit in review.

The decision is recorded in [MADR-0037](../adr/0037-http-capability-recommendations.md).
Five warm real fixture-server HTTP requests measured 29.48, 33.22, 31.19,
26.26 and 28.28 ms on this macOS host. All returned the same fingerprint. These
include HTTP/serialization and are not an isolated matcher benchmark or SLO.

## Output contract

`GET /api/binding-preview/recommendations` accepts the existing `cap`/`view`
source selection and optional explicit `targetView=repo:revision,...`.
Discovery resolves default destination revisions once and returns exact
`sourceView` and `targetView` values for subsequent review. The response has:

- `fingerprint`, `algorithmVersion`, `sourceView`, `targetView`;
- `owners`: repository-to-capability memberships, retaining multiple owners;
- `groups`: stable hint-group identity, original hint where bindable, state,
	every observed request and ranked candidates;
- each candidate: concrete service target, integer score/features, matched and
	total distinct routes, exact-preview eligibility, matched request/endpoint
	pairs with repository/commit/unit/artifact hashes and source evidence,
	unmatched calls and missing evidence dimensions.

Groups with dynamic targets, no candidate or broken existing bindings remain
visible. There is no canvas-fact limit on recommendation evidence. This response
does not mutate the graph or convert a candidate to a resolved integration.
Preview/export accepts `targetView` and returns destination artifact provenance
as `targetSources`; only source-side hints are eligible for submission.

## Verification receipts

Commands below run from the workspace root unless noted. Exact touched-source
archives are retained under `.sandbox-cache/issue81-evidence/`. These archives
capture the named implementation/test slices, not the entire repository or
hosted build environment. No claim is made that every subsequent coverage
addition independently failed before implementation.

| Increment | Observed red | Green | Source archive SHA-256 |
| --- | --- | --- | --- |
| First matcher scenario | `Exact_match_returns_service_and_bidirectional_evidence_without_mutating_facts`: expected hint group, collection empty; 1 failed | Same scenario passed, plus independent baseline; 2 passed | Red `4c6204bb5569bfd353405c1a5b7d01f25b20dfa400ed64139d4903e23fee2f3c`; green `9d6986fd0354bd4d15e4eeaa9cc9be873214ff7139167289380921d68bef640e` |
| Cross-capability API | Expected HTTP 200, actual 404; 1 failed | Same scenario and existing preview tests; 24 passed | Red `4d38b7310804906e81f5a5d422a9b62c9cda00a344c54212a49d697ba5fa44c0`; expanded green slice `fde950d86d7c2de48afed7cae0fc68578caefa255fc5c5ca6028b4ffb7c8561f` |
| UI handoff | Both viewport scenarios failed awaiting the absent recommendation control | Same scenarios passed after implementation and accessible stale warning | Red `2d02929ebde9746d0839bf61e77c69e27f82810bab75950af0ddf1db81b4b839`; final UI sources retained with the final source archive |
| Immutable identity | Expected rejection of same identity/different bytes; no exception thrown | Same scenario and directly affected regressions; 34 passed | Red `2fca2d72a83859407ca83f7a240a816fd052634f99b992a9e551ef84efb46ee8`; green `5847840087399ac0f066416df9381df9fc6e2ffc8b666bc8e266bd04200081fb` |
| Query uncertainty | Whole-hint scenario expected `Query compatibility` in missing evidence; item absent | Same strengthened scenario and matcher/preview regressions; 34 passed | Red `2f1a8c9e48c62dbefd2e096a844e3a573b411cf63d962b96157654e036337e72`; green `762813e5b2b4813638e4217777726ec2e21dfeb389d04b6a07a377de895a345e` |

.NET selectors were executed from `platform` using:

```sh
dotnet test tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --filter FullyQualifiedName~HttpBindingRecommendationTests --logger 'console;verbosity=minimal'
dotnet test tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --filter FullyQualifiedName~Cross_capability_recommendation_requires --logger 'console;verbosity=minimal'
dotnet test tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --filter FullyQualifiedName~Source_and_destination_with_same_identity --logger 'console;verbosity=minimal'
```

The full Explorer suite passed 318 tests. After strengthening duplicate-call and
membership assertions, the recommendation class passed all 11 cases again;
the final query-disclosure increment passed all 34 matcher/preview checks.
The synthetic duplicate-call fixture initially had an incorrect coverage count;
its validator failure was fixed and is not counted as behavioral red evidence.
The editor runner discovered no .NET tests; the existing VSTest CLI was used.

From `platform/src/CapabilityExplorer.Explorer/client`:

```sh
npm run build
CE_GRAPH_TEST_URL=http://127.0.0.1:5083 CE_GRAPH_TEST_REAL_URL=http://127.0.0.1:5083 npm run test:graph-connections
```

The locked TypeScript 7 compiler/build passed. All five browser cases passed:
desktop/mobile existing manual connections, desktop/mobile recommendation
handoff and stale invalidation, and real fixture API/export. Desktop/mobile
screenshots and the real reviewed export screenshot are retained in the evidence
directory. The four existing `node --test tests/binding-preview.test.mjs`
state tests passed. `node scripts/decisions/check.mjs all` passed all validators
and 29 tests; `git diff --check` passed.

The unchanged full command `CE_CONTAINER_ENGINE=docker make EXECUTION=sandbox ci`
was attempted and exited 2 at `test-frugal-ci`: the selected Docker executable
is missing. No full sandbox/hosted CI success or clean exact-HEAD publication
receipt is claimed. No commit, push, private reanalysis, database mutation,
automatic binding adoption, or GitHub issue edit was performed.