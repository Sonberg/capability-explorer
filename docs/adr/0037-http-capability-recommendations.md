# Recommend HTTP capabilities from observed route coverage without adopting bindings

* Status: proposed
* Date: 2026-11-05
* Decision owner: repository contributors
* Coordination: [issue #81](https://github.com/Sonberg/capability-explorer/issues/81)

## Context and problem statement

An outbound host can remain unresolved even when an analyzed capability exposes
compatible HTTP endpoints. Existing architecture route candidates do not provide
a whole-host comparison or a revision-bound cross-capability review handoff.
Matching routes do not establish deployment identity or payload compatibility.

## Considered options and evidence

The [bounded evaluation](../research/issue81-http-capability-recommendations.md)
compares issue #81 options A-E and states unavailable measurements explicitly.
The existing locked base fixtures have four statically addressed calls with
reviewed service labels and one dynamic call. Exact method/normalized-route
matching retrieves all four labelled services with no extra candidates in that
small corpus; the dynamic call abstains. The changed forecast contract is a
negative revision case. This is not a blind benchmark, measured deployment
correctness, or calibrated confidence. No general accuracy claim follows.

Contract/schema compatibility and structural DTO matching lack sufficient
consistent extraction evidence. Config names are not infrastructure ownership.
Model hints add a different authority and determinism boundary. Those options
remain deferred; no new model, execution engine, or live discovery is introduced.

## Decision outcome

Implement the bounded HTTP-only option A for review. Rank candidate services by
integer coverage of distinct observed method/route signatures. Retain every
request occurrence, both source identities, endpoint/handler evidence where
available, unmatched calls and missing evidence dimensions. Repeated calls do
not increase the score; equal scores remain ambiguous. Shared-repository
capability ownership is many-to-many and does not multiply the score.

A score of 100 means complete observed route coverage, not confidence. Unknown
host ownership, request payload, response schema, headers, content type and
partial analysis remain explicit. Do not remove gateway prefixes, rewrite
versions, decode reserved separators, or infer dynamic paths. Preserve existing
artifact-key and exact binding semantics under
[MADR-0010](0010-deterministic-mapping-fidelity.md).

Architecture inspection and binding preview share a read-only recommendation
API. Discovery uses complete original request records, not collapsed canvas
nodes or sampled edge facts. It remains available for unresolved hosts that
already have route candidates. Existing valid bindings are not replaced;
broken ones are reported without a replacement recommendation.

Preview receives explicit source and destination revisions. Its fingerprint
includes artifact bytes on both sides, existing binding bytes, algorithm
version and relevant capability membership. Destination artifacts provide
declarations, never additional source hints. Unknown/ambiguous revisions and
inconsistent source identities fail closed. Selecting a candidate only
pre-fills an unreviewed draft. Explicit review and exact-key validation are
required to export; export is not adoption and writes no binding, graph overlay
or extracted fact. This extends, rather than bypasses,
[MADR-0012](0012-revision-bound-binding-preview.md).

## Consequences and validation

The first release can recommend only what existing static HTTP evidence can
support. It intentionally abstains where the route is unknown and may have low
recall across gateway/version transformations. Fixture precision/recall is not
a semantic or operational SLO. Messaging and other resource types are out of
scope. No automatic adoption threshold exists.

Acceptance scenarios, executable selectors, red/green source receipts,
regression results and unexecuted publication gates are recorded in the bounded
evaluation. Broader scoring or evidence adapters require new labelled evidence
and an explicit decision; implementation does not accept this proposal
repository-wide.