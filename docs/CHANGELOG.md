# Changelog

Notable changes are curated here using [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/).
The release policy follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
See [MADR-0006](adr/0006-keep-a-changelog.md) and [MADR-0007](adr/0007-semantic-versioning.md).

This file starts with policy adoption; it does not reconstruct prior release
history or imply that existing private package versions are product releases.

## Unreleased

### Added

- HTTP capability recommendations in architecture inspection and binding review,
  with explainable observed route coverage, request/endpoint source evidence,
  explicit cross-capability destination revisions and stale-review protection.
  Candidate selection remains unreviewed; export never adopts a binding.

- Proposed MADR-0036 describes interface-configurable evidence retention with
  policy minimums/defaults, verified central custody, age-based historical
  resolution and explicit archival/deletion safeguards. Numeric durations and
  enforcement remain unimplemented pending policy-owner decisions.

- Focused independent TypeScript collection-consumer acceptance for attempt
  metadata versus immutable content identity and explicit optional partial
  results. Existing producer failure/cleanup proof is mapped separately from
  unproven mid-export cancellation and crash recovery.

- Proposed MADR-0035 requires scoped quantitative SLOs for performance and
  measurable operational-quality issues, with reproducible workloads, resource
  envelopes, failure accounting and an optimization stop rule. Functional
  correctness gates remain mandatory; policy is not measured compliance.

- Pinned, non-root Node/.NET development container without a default engine
  socket, credential mounts or automatic service provisioning. Trusted native
  build/restore/frontend/backend goals work without an installed container
  engine; sandbox and service engine requirements remain explicit.

- Offline authored BPMN/DMN evidence and bounded, non-executable Roslyn
  process/decision candidates, separate from unchanged native `ce.v1`.
  Negotiated immutable collection, independent .NET/TypeScript consumers,
  observed-input freshness and real CLI dogfood accompany two design-only
  OpenAPI 3.2.1 stakeholder contracts. Proposed MADR-0033 keeps coverage,
  source content, local permissions and central publication authority separate;
  no workflow/FEEL engine, business approval or central deployment is included.

- PostgreSQL-backed global deprecation marks with optimistic version tokens and explicit,
  idempotent legacy import. Integrations now use one pinned database snapshot/revision selection
  with global marks and bounded projections; database outages never return stale JSON marks.
  This endpoint requires PostgreSQL even when other Explorer views use file mode.

- Portable, source-bound decision-ledger schema, configured canonical-source
  adapters, proposed topic facets and typed relationships with deterministic
  generated navigation and offline checks (MADR-0028). No full ontology
  reasoning, cross-project acceptance or inferred identity is introduced.
- Explicit guarded post-merge remote-topic branch lifecycle in MADR-0014,
  separate from local worktree cleanup. Permission/protection gaps defer
  deletion; no automatic-deletion setting change or actual cleanup is claimed.

- Proposed source-bound business-object/domain/integration-event assessment
  research, gap analysis, separate maturity/exposure profiles, draft schema and
  nine synthetic cases (MADR-0027). Offline contract tests cover hard ceilings,
  uncertainty and stale reviews; no semantic extractor, production grade,
  measured accuracy or security certification is introduced.

- Linked MADR title/status ledger, RAG relationship map and source-backed edge
  ledger. Proposed MADR-0026 defines bounded SME/adversary/advisory/judge reviews,
  explicit acceptance authority, evidence receipts and atomic CRUD lifecycle
  maintenance of records and navigation; no existing proposals are automatically
  accepted or existing decision scopes widened.

- Proposed language-neutral frontend and repository-discovery contracts,
  compatibility research and sealed synthetic conformance checks (MADR-0025).
  Existing ce.v1 stays unchanged; no parser, new language or production scanner
  is adopted. A future pilot requires demonstrated demand and qualification.

- Explicit, optional grounded glossary proposals with versioned input/model
  caching, recorded-model validation and PostgreSQL-backed human acceptance or
  rejection. Selected occurrence roles become reviewed assertions atomically;
  suggested relationships never become source facts. Deterministic candidates
  remain available without a model, and review is read-only without a database.

- Host-local analysis run and attempt journals, atomic admission, exclusive
  execution, guarded publication and conservative crash recovery. Analysis now
  requires a host .NET 10 runtime; incompatible host/container lock domains fail
  closed. File mode remains supported and Kafka is not required (MADR-0022).

- Draft platform enrollment, evidence-bundle and intake-receipt shape profiles,
  controlled conformance fixtures, a seven-boundary plan catalogue and cheap
  validation checks. Proposed MADR-0020 preserves ce.v1 and distinguishes platform
  contracts from analyzed application contracts; no central service is delivered.

- Tool-free research evidence foundation with sealed synthetic source anchors,
  independent council packet validation and failure-path tests. Proposed
  MADR-0019 separates per-tool evaluation from single-tool introduction PRs;
  no candidate dependency is adopted and issue #31 remains incomplete.

- Opt-in PostgreSQL Explorer foundation: streamed validated imports, immutable CAS-published analysis snapshots, explicit default revisions, bounded SQL projections and paged core browsing. File mode remains the default; remaining database views and full issue #28 cutover are not complete.
- Proposed MADR-0017/0018, deterministic opt-in workload tooling and an executable RuVector compatibility/recovery experiment. The tested RuVector image failed adoption prerequisites; no extension is adopted.

- Revision-bound binding preview and reviewed JSON export, with stale-input/conflict checks and explicit separation from adoption.
- Local source/history/SAST/image audit collection and fail-closed evidence verification, with source, rules, database and platform identities.
- Locked offline corpus replay harness; unavailable private-corpus acceptance remains explicitly unverified.
- MADR-0011/0012 documenting source-bound security validation and binding review.

- Proposed integration-contract standards MADR covering versioned OpenAPI/AsyncAPI and evidence-preserving adjacent standards; parser implementation remains future work.
- Referenced, weighted comparison of deterministic analysis, context packaging and model inference tools, including the existing implementation.

- Contributor policies for curated release notes and explicit product compatibility/versioning.
- Authorized worktree cleanup guidance covers target ancestry, active-use checks, private backup verification, retained branches and measured disk recovery.
- MADR-0014 makes verified PR publication the default worktree cleanup checkpoint, preserving remote code and private evidence without waiting for merge.
- Revision-bound glossary connection graphs with bounded traversal, explicit source/binding evidence, calculation-input dependencies and visible coverage gaps.
- PostgreSQL-backed reviewed terms, occurrence assertions and mismatch issues, with optimistic concurrency, audit attribution and stale-source detection.
- Integrated glossary flow/review UI, including a read-only mode without a database. Human assertions remain distinct from source facts and do not establish field-level value propagation.
- MADR-0008 defining evidence navigation, review provenance and the trusted-local mutation boundary.

- Static Next.js app-router handler extraction with explicit dynamic/catch-all route shapes and unsupported-case coverage diagnostics.
- Source-bound baseline/candidate replay with repeatability, source evidence and reviewed semantic-delta checks.
- MADR-0010 documenting conservative framework resolution and cross-repository mapping evidence.

### Changed

- Source Check/Create-and-check becomes explicitly asynchronous under the single
  trusted host worker. Package-feed Settings is a non-secret host catalogue;
  credential configuration/migration belongs to the host, not Explorer.
  Supported Explorer launchers exclude acquisition credentials while retaining
  separate database/model authentication (MADR-0023).

- Add explicit Docker/Podman selection for direct sandbox and lockfile scans, with rootless-machine diagnosis and fail-closed guards on unqualified Podman paths. Full Podman act/Testcontainers/dev/audit support remains future work (MADR-0021).
- PRs now merge only into main (MADR-0016); main-only local/hosted gates replace the historical stage-only policy. Existing PR #27 reconciles both histories without merging the separate catalog feature.
- Preserve trusted native development alongside the sandbox CI profile; `make dev` now re-analyzes configured repositories in sandboxes before importing artifacts, with a private revision/hash receipt.

- Separate owner-review PR publication from complete security assurance; unavailable external repositories are explicitly not assessed, while strict audit failures and open findings remain visible.
- Pin CI actions and runtime images; the compatible .NET Azure Linux SDK reduces reported OS findings compared with the previous Ubuntu image. Remaining container findings are tracked in issue #24; no security-complete claim is made.
- Dependency scanning now includes all severities. NuGet destinations require an operator-controlled exact HTTPS origin allowlist, forwarded only to package fetching.
- Architecture and glossary connection diagrams use a shared live LikeC4 canvas instead of a separate fixture iframe, with original source evidence and reviewed association semantics retained.
- Diagram inputs follow selected capability/revisions and analysis generation; obsolete layouts are discarded. Viewpoints, grouping, routing controls and diagram-source download share the selected snapshot.
- MADR-0009 records the live renderer boundary and validation requirements. The standalone spike remains a regression harness; the 200 ms p95 settling target remains open.

### Security

- Validate source records, package identifiers and Git revisions; bound process/archive output and reject unsafe export/cache paths.
- Disable source-view Git transport helpers and lazy fetching. Restrict package redirects and credential forwarding.
- Keep evidence-verification Git reads offline and bounded; omit source text from mismatch diagnostics and ignore Git replacement refs for original-commit evidence and source viewing.
- Exclude credentials from C#/TypeScript Service Bus identities and TypeScript PostgreSQL hints and dynamic-expression metadata.
- Restrict TypeScript source/config/JSON reads to the selected repository, with a narrow compiler builtin-library allowance and explicit unresolved-source diagnostics.
- Protect API routes with trusted-local browser checks; these do not provide authentication for remote deployments.
- Prevent model credentials or ambient Azure identity from silently following saved endpoint changes; restrict default model transport and create settings files exclusively with owner-only permissions.

### Fixed

- Standard BPMN/DMN layout is distinguished from opaque extensions with an
  explicit structural-retention disposition. Qualified incoming/outgoing
  QNames validate local sequence-flow endpoints. DMN allowed values are
  retained under content opt-in and displayed beside their columns.
  Accessible grouped diagnostics retain counts and exact occurrence locators.
- Overlong C# symbol keys use deterministic full SHA-256 identities within the
  existing ce.v1 limit, consistently for declarations and references. Short
  keys remain readable; oversized assembly segments are also bounded. Existing
  build fingerprints prevent reuse of the previous extraction configuration.
  Affected private-corpus acceptance remains unverified (#54/#23).

- Normalize request header names so reviewed binding previews send one valid JSON Content-Type header.
- HTTP request identities now separate absolute hosts from routes and exclude credentials, query values and fragments from derived URL metadata.
- C# package/reference visibility follows the analyzed target framework and reports incompatible assets or conflicting assembly identities.
- Cached C# packages must satisfy complete version bounds; incompatible repeated requests omit package assets instead of silently selecting an invalid version.
- TypeScript analysis configuration hashes no longer depend on the checkout's absolute path.
