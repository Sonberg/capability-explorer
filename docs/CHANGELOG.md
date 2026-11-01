# Changelog

Notable changes are curated here using [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/).
The release policy follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).
See [MADR-0006](adr/0006-keep-a-changelog.md) and [MADR-0007](adr/0007-semantic-versioning.md).

This file starts with policy adoption; it does not reconstruct prior release
history or imply that existing private package versions are product releases.

## Unreleased

### Added

- Contributor policies for curated release notes and explicit product compatibility/versioning.
- Revision-bound glossary connection graphs with bounded traversal, explicit source/binding evidence, calculation-input dependencies and visible coverage gaps.
- PostgreSQL-backed reviewed terms, occurrence assertions and mismatch issues, with optimistic concurrency, audit attribution and stale-source detection.
- Integrated glossary flow/review UI, including a read-only mode without a database. Human assertions remain distinct from source facts and do not establish field-level value propagation.
- MADR-0008 defining evidence navigation, review provenance and the trusted-local mutation boundary.
