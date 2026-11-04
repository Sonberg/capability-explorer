# Source And Ingested Integration Comparison

Date: 2026-11-04. Status: in progress while the remaining serial refresh runs.

## Scope And Method

The requested scope is every available ingested capability, integration by
integration, including HTTP, messaging, timer/cron scheduling, browser/window
navigation and data-store boundaries. SalesPlanRepository and
ArticleReferenceRepository were added as standalone local capabilities using
their verified GitHub repositories. Their asserted connections to APT, ASQ and
Squid must be checked against source; names alone are not integration evidence.

Only completed fresh managed runs enter the comparison. Each row is bound to an
exact repository revision and an artifact whose bytes match the completed run's
resolved manifest. Every recorded boundary-edge citation is checked against the
exact Git blob, using LF normalization and 1-based UTF-16, end-exclusive spans.
Snippet hashes are verified rather than trusting file/line existence alone.
Recorded facts retain their original resolution and unresolved reason.

The local row-by-row inventory is
`.sandbox-cache/integration-audit-evidence/comparison.json`. It includes declared
integration nodes, every recorded `REQUESTS`, `EXPOSES`, `HANDLES`, `PUBLISHES`,
`CONSUMES`, `READS`, `WRITES` and `MAPS_TO` edge, source spans and verification
status, per-artifact partial coverage/diagnostics, and explicitly unproven
route-only HTTP candidates. A separate source-syntax census lists possible
HTTP, messaging, scheduling, navigation and data calls without pretending regex
matches establish runtime behavior or exhaustive semantic discovery.

The operational comparison script is local and read-only with respect to Git,
artifacts and journal evidence. It passed strict TypeScript 7 checking plus
deterministic CRLF, UTF-16, multiline and bounds tests before use. Source export
directories are cleaned after successful publication, so comparisons read the
exact immutable Git revisions, not a mutable checkout or remote HEAD.

## Completed Refreshes

| Capability | Fresh Run | Result |
| --- | --- | --- |
| Weekly sales | `1dbcc9a0e3b540ffa1f6417deae5e522` | Completed, local publication confirmed |
| Demand pricing | `e43c5d57639d4395adc2c53e307b496e` | Completed, local publication confirmed |
| Kraken | `0a2ca5b1b7d64e6a9a0a7d01b7a6e13f` | Completed, local publication confirmed |
| APT | `757b015df5734698a12062c68f0e56d5` | Completed, local publication confirmed |
| SalesPlanRepository | `ecc4b0d5cf1545ec8aa888852a113aa0` | Completed, local publication confirmed |
| ArticleReferenceRepository | `b620c3079ec9447ab66d8d4c62622dca` | Completed, local publication confirmed |
| ASQ | Pending serial refresh | Earlier attempts retained, not claimed published |
| Squid | Pending supported-repository retry | Earlier eight-artifact attempt retained, not claimed published |
| Glufs | No available ingested artifacts; prior source access failed | Not qualified as refreshed or compared |

The first six completed capabilities cover 30 artifacts, ten distinct
repositories and 252 recorded boundary facts. All checked source citations match;
there are zero evidence mismatches in that comparison pass. The separate syntax
census currently has 323 candidates, including the subsequently qualified
GraphQL/Cosmos/Dapr forms. This is not an integration count or coverage score.

## Verified Findings

### Kraken Google Pub/Sub Is Missing From The Integration Product

At analyzed revision `asq-kraken@46c57ac1e6904b811d65080066988e14219f2114`,
`Kraken.Infrastructure/Google/PubSub/PubSubPublisher.cs` constructs a
`Google.Cloud.PubSub.V1.PublisherClient` for a topic in the configured
`GCP_PROJECT_ID` and calls `publisher.PublishAsync(message)`.
`Kraken.Api/Extensions/PubSubPublisherExtensions.cs` supplies the concrete topic
IDs `quantify`, `quantified`, `estimate` and `estimated`.

The five fresh Kraken artifacts contain ten `EXPOSES`, ten `HANDLES` and one
`REQUESTS` record, but no `PUBLISHES` or `CONSUMES` records. Thus absence of a
messaging edge is an extraction/product gap, not evidence that Kraken has no
messaging dependency. Runtime topic deployment and subscriptions are not proved
by this source-only observation.

### API Requests And Browser Navigation Must Stay Distinct

APT's freshly analyzed client revision is
`7fe7388ce06fef417b84dca2c5e8a710d870c3ed`. Its navigation helpers use:

- `modules/core/utils/openAsqArticleInNewTab.ts:18-19`:
	`CONFIG.asqUrl` plus `/deepDiveArticle`, passed to `window.open()`.
- `modules/core/utils/openArticleInNewTab.ts:15-35`:
	`CONFIG.squidUrl` plus `/quantification/single/...`, passed to `window.open()`
	in three planning-area branches. The `useAbsoluteSquidUrl` argument can also
	select a relative navigation base, which must not be silently treated as an
	external Squid deployment in every execution.

The source-side ASQ/Squid navigation behavior is verified, but a configured base
name is not independent proof of a deployed repository/host identity. The fresh
client artifacts have three exposed/handled HTTP endpoints and one API request,
but no dedicated navigation relation for these helpers. The TypeScript HTTP-client
rules qualify fetch/axios/OpenAPI patterns, not browser-window navigation as a
dedicated integration relation. An ordinary compiler `CALLS` observation is not
equivalent to a resolved application-navigation destination.
All four named helper call sites are in fact present as compiler-resolved
`CALLS` to `ts|@ext|typescript|lib.dom.d.ts|Window.open#value`, with verified
source-span hashes. The gap is therefore the missing dedicated navigation
relationship and destination mapping, not total loss of those call sites.

The manual graph editor supports a distinct navigation type so a reviewer can
represent a verified intended application link without mislabelling it as an
extractor-proven API request. This does not add missing automatic extraction.

### APT Shared-Service HTTP Calls Are Extracted But Unbound

The fresh APT API revision is
`8b601e55a29adfb7cddb226882041653cd44368f`. These eight source-side requests
are present and their citations verify against that revision. Paths below are
relative to `src/Apt.Infrastructure/Common/`; line numbers identify the first
recorded source evidence, not an invented target deployment.

| Source Client / Method | HTTP Operation | Source Line |
| --- | --- | --- |
| `SalesPlanRepository/RestClient.cs`, `Batch` | `POST /salesplanrepository/v2/api/v2/sales-plans/query` | 41 |
| `SalesPlanRepository/RestClient.cs`, `BatchMarketSales` | `POST /salesplanrepository/v2/api/v2/market-sales-plans/query` | 74 |
| `SalesPlanRepository/RestClient.cs`, `BatchOrderPlacementDateWeeks` | `POST /salesplanrepository/v2/api/v2/sales-plans/order-placement-date` | 135 |
| `ArticleReferenceRepository/RestClient.cs`, `Get` | `GET /article-reference-repository/api/article-references/{}/{}/{}/{}` | 26 |
| `ArticleReferenceRepository/RestClient.cs`, `Update` | `PUT /article-reference-repository/api/article-references/{}/{}/{}/{}` | 46 |
| `ArticleReferenceRepository/RestClient.cs`, `WhatIf` | `PUT /article-reference-repository/api/article-references/what-if/{}/{}/{}/{}` | 67 |
| `ArticleReferenceRepository/RestClient.cs`, `GetReferenced` | `POST /article-reference-repository/api/referenced-articles` | 81 |
| `ArticleReferenceRepository/RestClient.cs`, `GetBulk` | `POST /article-reference-repository/api/article-references/fetch-references` | 100 |

Every row is `unresolved` with reason `configuration`, despite having a known
method and normalized path. Both distinct client classes share the same emitted
base hint, `@unbound:apd-apt-aggregate-webapi/Apt.Infrastructure:client=RestClient`.
That hint is therefore insufficient to distinguish the two service bases in a
human binding map. Per-node/per-repository manual anchors avoid conflating them;
they do not retroactively resolve the extracted HTTP fact or prove gateway
rewrites. The fresh APT API artifacts contain 58 exposed/handled endpoints and
27 outbound requests overall, all with verified emitted source citations.

### SalesPlanRepository Version Templates Prevent Literal Route Joins

Fresh `apd-salesplanrepository-webapi@371b8e89aa45a1b33e63257e0c2a7f4f7d793835`
produced six native artifacts and ten exposed endpoints. Four units remain
partial. In `src/SalesPlanRepository.Api/Controllers/V2/`, both
`SalesPlansController` and `MarketSalesPlansController` declare
`[ApiVersion("2.0")]` and routes containing `v{version:apiVersion}`.
The recorded endpoint keys therefore contain `/api/v{}/...`, whereas APT's
source calls `/salesplanrepository/v2/api/v2/...`.

The query and order-placement-date operations exist in the target source.
Recognizing that compatibility requires accounting for both the gateway prefix
and the version segment; the current exact normalized-route equality join does
neither. The source route/version metadata is not proof of the configured
gateway deployment, but it explains why a literal route join misses these
otherwise corresponding operations.

### ArticleReferenceRepository HEAD Differs From APT's Legacy Contract

Fresh `apd-articlereferencerepository-webapi@05e95a86a16823f419f3377c55679c8ebf8b5220`
produced five partial native artifacts and three exposed endpoints. The exact
source `src/ArticleReferences.Api/Controllers/ArticleReferencesController.cs`
agrees with those endpoint records:

- `GET /article-references`, with season/product/article/channel supplied as
	query parameters.
- `PATCH /article-references`, with query identifiers and a body collection.
- `DELETE /article-references`, with query identifiers.

APT's five recorded calls instead use legacy `/article-reference-repository/api/...`
paths, path-encoded identifiers, `PUT` updates and additional bulk/what-if routes.
Those are not the current controller's contract merely with a missing base URL.
This is a verified source-contract difference between the analyzed revisions,
not an omitted controller in the native artifact. It is not, without deployment
and gateway/version evidence, proof that production calls fail. The repository
or version behind the legacy gateway contract must be confirmed before treating
these as proven links to the new standalone capability. A manual mapping remains
an explicitly reviewed association, not protocol compatibility proof.

### GraphQL And Request-Message HTTP Calls Are Not Boundary Records

At the fresh ArticleReferenceRepository revision, the infrastructure unit emits
two `REQUESTS` records for health checks. Its business code also contains:

- `PlanningProductComposite/RestClient.cs`, `Get`: GraphQL
	`SendQueryAsync<SeasonArticlesBySeasonDepartmentIdsResponseModel>` for
	`seasonArticlesBySeasonDepartmentIds`.
- The same file, `GetDepartmentId`: GraphQL
	`SendQueryAsync<SeasonProductV1ResponseModel>` for `seasonProduct`.
- `BoPoTimeV1/RestClient.cs`, `GetGroundSeason`: a GET `HttpRequestMessage` for
	`Season/{seasonNumber}`, sent through `HttpClient.SendAsync`.

These are real outbound operations in the selected source, not declarations of
the two health checks. They do not appear as corresponding native HTTP boundary
records. The client bases remain configuration-dependent. Ordinary compiler
calls, where present, must not be mistaken for resolved HTTP/GraphQL integration
facts.

### Cosmos And Dapr Persistence Are Missing From The Data Boundary Product

The fresh SalesPlanRepository infrastructure code references
`Microsoft.Azure.Cosmos`. `Repositories/V2/SalesPlans/CurrentSalesPlansRepository.cs`
queries a container through `GetItemLinqQueryable`, feed iterators and
`ReadNextAsync`; it saves/deletes with `CreateItemAsync`, `DeleteItemAsync` and
`UpsertItemAsync` through current/deleted container helpers. Its emitted units
have no corresponding native `READS`, `WRITES` or `MAPS_TO` boundaries.

Fresh ArticleReferenceRepository
`src/ArticleReferences.Infrastructure/Articles/ArticlesRepository.cs` uses
`DaprClient.QueryStateAsync`, `SaveStateAsync` and `DeleteStateAsync` with the
logical state-store name `articles`. Its five partial artifacts likewise have
no native data boundaries for these operations. The source establishes Dapr
state operations, not the physical database/account behind a deployed Dapr
component. No missing data edge is interpreted as absence of persistence.

### Route Matching Does Not Prove Gateway Ownership

The existing landscape lifts HTTP edges to capabilities when a declared target
or route candidate is available. Configured gateway prefixes and dynamic URL
wrappers can prevent a source request from matching a target service's native
route. A route-only match is explicitly a candidate, not proof that the configured
base points at that repository. Shared service client names alone do not repair
that uncertainty. The target contracts and APT source-side relationships are
checked above; the remaining ASQ/Squid source-side relationships await their
completed refreshes.

### Landscape Projection Also Filters Relationships

The landscape implementation in
[Architecture](../../platform/src/CapabilityExplorer.Explorer/Architecture.cs)
lifts recognized HTTP/resource boundary facts to capability ownership. It does
not promote every compiler call into an inter-capability integration. Resources
are retained only when at least two repositories belonging to at least two
capabilities touch them. Unknown or single-capability resources can therefore
disappear from the landscape even though a lower-level view has a source fact.
Shared-repository links prove catalogue membership, not a deployed network call.

The verified failure classes are distinct: a missing dedicated boundary rule;
an emitted but unresolved configured target; version/gateway route-normalization
gaps; a selected-source API contract difference; and deliberate scope/projection
filters. Correct snippet hashes establish evidence integrity, not completeness
or runtime compatibility across those layers.

## Graph Review Workflow

The delivered graph action persists manual connections separately from
extracted facts and labels them manual. Both ends can be anchored to a selected
capability, repository and observed analyzed node. Repository-only selection is
supported when no suitable node is available. Selected ownership is validated,
and source/target anchors survive editing, reload and background refresh.
Landscape aggregates to capabilities; system graphs use selected repository
targets. Existing user-authored mappings are preserved.

See [manual graph connection qualification](manual-graph-connections.md) for
the API, concurrency, privacy, TypeScript 7 and desktop/mobile verification.

## Recovery And Explicit Limits

The original refresh was blocked first by overlength C# symbol identities and
then by a cross-filesystem publication rename. The bounded-key fix passed 215
extractor regressions and isolated real-source extraction with and without
packages. Publication now preserves staging and uses artifact-side preparation
and rollback storage; the same regression passed on physically distinct APFS
and workspace filesystems.

Every retired unknown run was separately owner-approved, verified under both
journal locks and retained unchanged with inventory/hash/mode/link receipts.
No blanket reset, inferred termination or unconfirmed database publication was
used. Details are in [issue 54 evidence](issue54-symbol-keys.md) and
[publication storage evidence](analysis-publication-storage.md).

The owner explicitly excluded `apd-squid-quantify-cicd` from native-code analysis
after it produced no supported C#/TypeScript/SQL units. Its exact source revision
`d32f9ca1ecae0562a3bc34c4a880e509c85827c4` and full failed attempt are retained;
it is not silently described as analyzed. Infrastructure and migration-tool
semantics from that repository are outside the refreshed native product.

All current refresh commands disable PostgreSQL publication. Saved artifact
publication, in-memory Explorer views and a database's published snapshot are
separate states. Partial extraction coverage remains partial even when every
emitted citation is correct. Full Docker-only CI is not executed in this native
Podman qualification; the static frugal-CI policy check passes with the explicit
Docker profile and preserves 57 canonical commands.