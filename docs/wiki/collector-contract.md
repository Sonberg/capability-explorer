---
type: Contract Proposal
title: Repository evidence collection export boundary
description: A language-neutral offline boundary separating collection from middleware and presentation.
status: draft
tags: [collection, contracts, provenance]
generated: {by: copilot-cli/1.0.105, at: "2026-11-04T01:10:00+01:00"}
sources:
  - id: native
    resource: https://github.com/Sonberg/capability-explorer/blob/ae9bac966037a5348027c2b2d3e0201a430bf09c/schema/README.md
  - id: platform
    resource: https://github.com/Sonberg/capability-explorer/issues/33
---
# Ownership first

The collector owns authorized repository acquisition, deterministic structural
evidence, source-bound declarations, diagnostics and honest coverage. Middleware
owns central intake, normalization, cross-repository alignment and data products.
Domain stewards own meaning and approval. Presentation consumes those outputs;
it must not manufacture authority. These are proposed responsibility boundaries,
not deployed services.[^platform]

Keep the native ce.v1 record contract unchanged for this proposal. Its records
already describe per-unit/revision evidence; collection export must reference
them rather than silently redefine their identity or semantics.[^native]

## Minimum offline export

Start with a full immutable snapshot, no required central broker, service,
database or delta protocol. Align the selected collection-owned profile with
existing platform contract work instead of introducing a competing envelope.

The implementation must freeze exact fields, bounds and errors for:

| Section | Required meaning |
|---|---|
| Profile and producer | Versioned profile, extractor/recipe/configuration identity |
| Authorized selection | Stable repository/provider identity and exact revisions/trees |
| Artifact manifest | Relative safe paths, lengths, media/profile identity and byte digests |
| Native evidence | Unchanged native artifacts and resolvable local record references |
| Optional models/candidates | Separate authored and code-derived classes, never implicit native facts |
| Coverage | Enumerated, excluded, eligible, inspected, capped, inaccessible and unsupported inputs |
| Freshness | Source revision/observation time separate from export/attempt time |
| Authority context | Nonsecret scope/rights references; not credentials or inferred grants |
| Validation | Explicit required/optional profile outcomes and limitations |

Content identity excludes attempt metadata only after canonicalization rules
and examples are agreed and tested. IDs local to one artifact are not
cross-revision lineage. Bind references to artifact digest plus record/symbol
identity and preserve exact hash conventions. Do not relabel a whole-line hash
as a UTF-16 column-span hash.

## Evidence classes and disclosure

Keep authored declarations, compiler-observed facts, code-derived process/
decision candidates and future steward responses distinguishable.
[Process and decision evidence](process-and-decisions.md) details the boundary.
Local resolution does not establish business approval, deployment, runtime
traffic or recipient authorization.

Default exports exclude unrestricted source, personal Git author data and
credentials. XML or excerpts require an explicitly authorized content profile.
Element IDs are not line spans; use artifact-plus-element locators unless a
verified source mapping exists.

## Coverage and failure behavior

Choose a unit of counting and explicit disjointness rules. Unknown tree coverage
is not zero. Missing items in partial observations are not proof of deletion.
Declared coverage is not measured completeness of the business landscape.

Stage output and publish a completion marker only after required validation.
Interrupted/cancelled exports never look complete. Optional failure can yield
an explicitly partial export; required failure rejects completion. Reject
unsafe paths, conflicting duplicates, byte tamper and unsupported required
profiles. Never dereference arbitrary artifact URIs.

## Acceptance boundary

Use two synthetic repositories and independent .NET/TypeScript consumer
validators. Test repeat determinism, duplicate retries, conflicting bytes,
tamper, invalid paths, interrupted output, partial input and unknown profiles.
No live workspace integration or central-platform implementation is needed.
The [delivery plan](delivery-review.md) assigns bounded implementation slices.

[^native]: Native artifact framing, evidence, identities, relation and completion rules at the inspected stage-1 source.
[^platform]: Existing platform issue supplies the public integration context; collector-only scope here narrows the next implementation slice, not all future platform work.
