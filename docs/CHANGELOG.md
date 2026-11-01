# Changelog

Notable changes are curated here using [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/).
The release policy follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
See [MADR-0006](adr/0006-keep-a-changelog.md) and [MADR-0007](adr/0007-semantic-versioning.md).

This file starts with policy adoption; it does not reconstruct prior release
history or imply that existing private package versions are product releases.

## Unreleased

### Added

- Proposed integration-contract standards MADR covering versioned OpenAPI/AsyncAPI and evidence-preserving adjacent standards; parser implementation remains future work.
- Referenced, weighted comparison of deterministic analysis, context packaging and model inference tools, including the existing implementation.

- Contributor policies for curated release notes and explicit product compatibility/versioning.
- Revision-bound glossary connection graphs with bounded traversal, explicit source/binding evidence, calculation-input dependencies and visible coverage gaps.
- PostgreSQL-backed reviewed terms, occurrence assertions and mismatch issues, with optimistic concurrency, audit attribution and stale-source detection.
- Integrated glossary flow/review UI, including a read-only mode without a database. Human assertions remain distinct from source facts and do not establish field-level value propagation.
- MADR-0008 defining evidence navigation, review provenance and the trusted-local mutation boundary.

- Static Next.js app-router handler extraction with explicit dynamic/catch-all route shapes and unsupported-case coverage diagnostics.
- Source-bound baseline/candidate replay with repeatability, source evidence and reviewed semantic-delta checks.
- MADR-0010 documenting conservative framework resolution and cross-repository mapping evidence.

### Changed

- Architecture and glossary connection diagrams use a shared live LikeC4 canvas instead of a separate fixture iframe, with original source evidence and reviewed association semantics retained.
- Diagram inputs follow selected capability/revisions and analysis generation; obsolete layouts are discarded. Viewpoints, grouping, routing controls and diagram-source download share the selected snapshot.
- MADR-0009 records the live renderer boundary and validation requirements. The standalone spike remains a regression harness; the 200 ms p95 settling target remains open.

### Fixed

- HTTP request identities now separate absolute hosts from routes and exclude credentials, query values and fragments from derived URL metadata.
- C# package/reference visibility follows the analyzed target framework and reports incompatible assets or conflicting assembly identities.
- Cached C# packages must satisfy complete version bounds; incompatible repeated requests omit package assets instead of silently selecting an invalid version.
- TypeScript analysis configuration hashes no longer depend on the checkout's absolute path.
