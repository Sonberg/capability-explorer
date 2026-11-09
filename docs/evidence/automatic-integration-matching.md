# Automatic Integration Matching

Date: 2026-11-05. Qualification uses existing published artifacts and cached
source objects; it does not re-extract private repositories, probe production
services, change credentials, adopt bindings or reconcile unknown runs.

## Implemented Contract

- Reconcile confirmed analyses after core publication and at worker startup.
  Revisit existing sources when new targets arrive. Preserve successful analysis
  state and the last complete catalogue if matching fails.
- Match HTTP exact identities/existing explicit bindings and bound message/data
  resources. Group HTTP observations by capability/run, repository/unit, qualified
  caller and host, then compare their complete operation set with target services.
  A unique best service with at least two distinct related operations produces an
  inferred connection, not a proven binding. Tied targets and isolated overlaps
  remain suggestions. Dynamic targets remain unresolved. Generic-root and health
  route-only candidates do not support a cluster.
- Compare gateway-prefix suffixes, method/path layouts, concrete versions and
  version templates. Keep HTTP-method, identifier-layout, concrete-version and
  available payload-schema differences explicit. Missing schemas and templated
  version support remain unknown. Group inferred observations into one candidate
  graph edge per service cluster, with all observations available through the
  paged catalogue. System views retain only their incoming/outgoing associations.
- Persist derived results separately with artifact/run identities, evidence,
  algorithm version and input fingerprint. The architecture projection filters
  both revisions and capability membership; manual mappings are untouched.
- Offer a separate on-demand LLM inspector using the configured model. Structured
  output must cite supplied source and target pages and use declared node keys.
  Unproven target identity prevents a model-compatible verdict from being
  presented as validated compatibility. No model result adopts a connection.

## Real Data

The following measurements are historical 2026-11-05 evidence, not a rerun of
the 2026-11-10 source-selection qualification below.

The current deterministic pass covers 8 capabilities, 114 artifacts and 6,138
integration observations. It produces 0 proven cross-capability links, 14
observations in 4 inferred service clusters, and 11 remaining uncertain
candidates. All 25 records have 109 verified evidence references in 17 source
files, with no anchor or snippet failures. These counts do not mean
that the capabilities have no real dependencies: unresolved configuration,
dynamic URLs, gateway/version differences and extraction gaps prevent proof.

Catalogue fingerprint:
`f638c5144cdf54bb283e3113277eb0fcc9421c9685935d2ab5b22f5db606bf8d`.

Two caller capabilities each form one inferred association to a sales-plan
service and one to an article-reference service. The sales-plan clusters align
5 operations after gateway/version-template comparison, with compatibility still
unverified. The article-reference clusters retain 9 legacy observations and
explicit incompatible-contract warnings. These are service associations, not
claims that a mismatched endpoint exists or that deployed calls fail.

The seven user-reviewed examples are not hard-coded into matching rules and no
manual mappings were added. Three examples still lack enough extracted HTTP
route or message-contract observations for this cluster matcher. Names alone do
not fill those gaps. Messaging/data retain the existing exact-resource rules;
this cluster comparison increment is HTTP-only.

Seven explicitly requested relationships were evaluated serially through the
configured live model, using exact published source revisions. All 75 supplied
source pages were independently checked against 29 cached Git files. The final
model output contains 2 suggestions, 2 incompatibility findings, 1 bounded
not-observed result and 2 withheld responses. There is no precision/recall claim:
this is a user-selected diagnostic set, not a blind benchmark, and some bounded
retrieval results lack the receiver needed for an end-to-end conclusion.

The model initially overclaimed compatibility from DTO similarities and later
treated differing HTTP methods as equivalent. The first evaluation is retained,
not relabelled successful. Retrieval was improved to favor concrete boundary
callers and receiver contracts; a regression now blocks unsupported model-only
compatibility. Source review remains separate from the model's verdict.

Detailed pair verdicts, exact revisions, model responses and source pages remain
in the owner-only workspace report
`.sandbox-cache/integration-matching-evidence/validation.md` and evaluation
receipts. Private source snippets are not copied into this tracked document.

## Executable Scenarios

### Source-selection qualification (2026-11-10)

`integration-cluster-v2` applies the same source-selection validation as binding
preview and recommendations. Repository, commit and unit metadata must match
the validated artifact. One selection cannot mix revisions of a repository or
conflicting artifact content for one repository/unit. Rejection preserves the
last catalogue; it does not repair inputs or rerun extraction.

An exact HTTP key is not enough when multiple provider units declare it. Such
matches remain suggestions even within one capability, retaining both sides'
revision and evidence. Input reordering does not change the result.
This is source-level identity qualification, not business approval or runtime
proof. Revision-bound human qualification and private-corpus acceptance in #6
remain separate.

Older algorithm catalogues are not reused under the new rules. The read-only
API reports unavailable and the graph keeps its existing analyses/manual
connections with a warning. The old file is not changed. The existing
`ce match-integrations` command can rebuild it from authorized published inputs;
this qualification does not invoke that command on private data.

Synthetic regressions cover mismatched repository/revision/unit metadata,
mixed revisions, conflicting same-revision content, provider-unit ambiguity,
input reordering and old-catalogue refusal. They do not establish new real-data
counts, performance compliance or deployed integration behavior.

Existing xUnit and browser runners were reused; no BDD framework was added.

| Acceptance | Executable selector | Result |
| --- | --- | --- |
| Explicit binding, both revisions and evidence | `Given_published_capabilities_when_binding_proves_http_identity_then_automatic_match_retains_both_revisions` | Observed assertion red, then green |
| Channel/store identity, not shared names alone | `Given_resource_participants_when_reconciling_then_identity_controls_connection_strength` | Meaningful reds, then green; invalid fixture attempts are not red evidence |
| Reject generic-root false candidate | `Given_only_a_generic_root_route_when_reconciling_then_no_dependency_is_suggested` | Observed red, then green |
| Gateway/version-template call clusters infer one association | `Given_related_gateway_calls_when_contract_paths_align_then_one_inferred_cluster_connects_the_service` | Passed |
| Method and identifier differences remain visible | `Given_different_methods_and_identifier_locations_when_clustering_then_contract_differences_remain_visible` | Passed, including closest PUT/PATCH diagnostic pairing without treating methods as equivalent |
| Ties and a single overlapping operation do not infer a service | `IntegrationClusterTests` negative scenarios | Passed; single-overlap assertion red then green |
| Payload/concrete-version differences are explicit | `IntegrationClusterTests` schema/version scenarios | Passed |
| One candidate graph edge retains cluster facts and warnings | `Given_an_inferred_cluster_when_projecting_then_one_candidate_edge_keeps_all_facts_and_contract_warnings` | Passed |
| Shared targets do not leak unrelated clusters into scoped systems | `Given_shared_target_clusters_when_viewing_one_capability_then_only_its_incoming_and_outgoing_connections_are_shown` | Source-bound red then green; incoming association retained |
| Selecting an inferred edge opens its contract assessment | Browser name containing `Inferred cluster at` | Both desktop/mobile red then green; ordinary route-review flow retained |
| Confirmed publication writes separate catalogue | `Given_confirmed_publication_when_reconciling_then_a_separate_revision_bound_catalogue_is_written` | Observed red, then green |
| Revisit an older caller when target arrives | `Given_an_existing_caller_when_a_target_is_published_then_incoming_references_are_revisited_without_reanalysis` | Passed existing implementation; no fabricated red |
| Failed reanalysis and tamper preserve catalogue | `Given_failed_reanalysis_or_tampered_artifacts_when_reconciling_then_the_last_complete_catalogue_survives` | Exposed uncaught invalid-data failure, repaired and green |
| Frozen accepted/configuration bytes remain immutable | `Given_changed_frozen_inputs_when_matching_then_the_published_identity_is_rejected` | Both mutation cases red, then green; frozen binding hashes are verified by the same collector |
| Revision-filtered graph; suggestions and manual links separate | `Given_automatic_results_when_inspecting_then_only_selected_revisions_connect_and_suggestions_remain_separate` | JSON-contract red, then green; fixture alias/HTML parse failures excluded |
| Model suggestions require citations and declared keys | `IntegrationSuggestionTests` | Includes invalid citations/keys, unsupported verdicts, missing evidence and incompatibility |
| Model cannot establish unproven target identity | `Given_unproven_target_identity_when_model_claims_compatibility_then_the_result_remains_unverified` | Observed red, then green |
| Cancel before inference | `Given_cancelled_assessment_when_suggesting_then_no_model_request_is_sent` | Source-bound red and green below |
| Local request guard and no configured model | `IntegrationSuggestionTests` API scenarios | Passed |
| Desktop/mobile LLM inspector without adoption | Browser name containing `LLM integration suggestions` | Both red, then green; old copied assets were separately diagnosed |

## Commands And Results

Run from the repository root unless a directory is specified:

```bash
dotnet test platform/tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj --filter 'FullyQualifiedName~IntegrationSuggestionTests|FullyQualifiedName~ArchitectureConnectionsTests|FullyQualifiedName~HttpBindingRecommendationTests' -v minimal
dotnet test platform/tests/CapabilityExplorer.Store.Tests/CapabilityExplorer.Store.Tests.csproj --filter FullyQualifiedName~LocalPublicationTests -v minimal
TMPDIR=/private/tmp dotnet test platform/tests/CapabilityExplorer.Explorer.Tests/CapabilityExplorer.Explorer.Tests.csproj -v minimal
dotnet platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll match-integrations
```

The earlier focused matching slice passed 35 cases before its final two
cancellation/guard cases; those two then passed unchanged. Current focused
cluster/projection/HTTP regressions: 33/33. Complete current Explorer suite:
346/346, using isolated `--artifacts-path .sandbox-cache/integration-cluster-validation`
outputs so the existing worker binaries were not replaced.
Local publication suite: 6/6. Worker/extraction regressions selected through
`AnalysisCapabilityConcurrencyTests|AnalysisParallelTests`: 27/27. The UI
TypeScript 7/Vite build passes. From
`platform/src/CapabilityExplorer.Explorer/client`, the existing
`npm run test:graph-connections` harness passes 7/7 with its isolated fixture
server configured through `CE_GRAPH_TEST_URL`/`CE_GRAPH_TEST_REAL_URL`, including
the unmocked review/export case. Desktop/mobile screenshots were inspected.

The subsequent inferred-inspector/ordinary-route-review browser selectors passed
4/4 after the inspector routing fix. Real saved-data API and rendering checks
passed at 1440px and 390px: exactly 4 candidate cluster edges representing 14
observations, 2 scoped edges per caller system, correct warnings, and unchanged
manual catalogue. Shadow-DOM labels and screenshots were inspected. The native
renderer hides labels at low zoom and can place labels outside a panned viewport;
full real-data edge hit-testing was not qualified. The failed strict hit-test is
not relabelled green by the separately recorded rendering/API-only check.

Cancellation sources were captured before the red assertion and after green:

- Red archive SHA-256:
  `bcc1757224896ad41e982410537b8ce778f0d7136841f9d4cd84a48c83cdd918`.
- Green archive SHA-256:
  `564d2e9d2b045790c83e99609df4b7886f21d454c95b57d7e74e662b36c98d2f`.
- Unchanged test file SHA-256:
  `c542fc8fbf8c3fce18c74f5c5c697534e5e10e69ec31bb1e36015fbe55dbd0b3`.

The same selector containing `Given_cancelled_assessment` and
`Given_an_untrusted_request_when_assessing` ran against both archives: red exit
1 (`Assert.Null` found a sent model request), green exit 0 (2/2). Earlier
increments have observed red/green outputs and some exact file hashes, but not
complete immutable pre-edit source archives. They are not represented as fully
source-bound historical evidence under MADR-0032.

The capability-scope regression was independently captured with identical test
source SHA-256 `f556e8e6945d55f8a5611c7763c2e5451275351289f2c7257079d5dd4fd8fbaf`:
red archive `87850ddcc73747a33aeb9af0b0b6cac2409d7b2e9f0019df96ed1093042fd379`,
green archive `1ab850cf11352cf5c92b05029e4d3f716b93b8806e0aac72b28c9ad289c0a4e4`.
Red rejected an unexpected unrelated cluster; green retained only the selected
capability's incoming/outgoing edges. The initial invalid fixture was excluded.

The browser inspector regression archives are
`c382e53c0b166d9c9ee029b4d26aad3dd81e28a9e045758638d5d81d71b7c457` (red) and
`528146ef50d9cbca9223a16029fd67662bb7f57c8b84a17eb4309f86dac3b905` (green),
with unchanged test source
`f49f1c8b080249e62305ae5f29a1afd1617f6b53488c34901111baaf60f36bd3`.

## Limits

No live deployment, gateway ownership, complete DTO compatibility or physical
store identity is inferred. LLM citation validity does not prove semantic
correctness. SQL column/table grouping describes shared resources, not causal
execution order. Missing extraction coverage remains missing.

HTTP cluster normalization is an inference rule, not canonical endpoint
rewriting or a change to reviewed binding semantics. Contract comparison uses
only available extracted metadata; it is not complete query/header/auth/schema
validation. A cluster with incompatible contracts can still identify an intended
service association, but its graph certainty remains `candidate`.

The UI/API qualification is file-backed Explorer mode. PostgreSQL publication
receipts can be selected by the collector, but PostgreSQL-source endpoint parity
and real database qualification were not established here. The full gate was
attempted with `CE_CONTAINER_ENGINE=docker make EXECUTION=sandbox ci`: exit 2
before test execution because the selected Docker executable is missing.
Hosted gates and Docker-only CI remain distinct from the native tests above. No success is
claimed for unavailable infrastructure or unexecuted boundary scenarios.