# Maintain source-bound architecture and diagrams with relevant changes

* Status: proposed
* Date: 2026-11-04
* Related: [architecture](../architecture/index.md), [wiki decision](0033-okf-llm-knowledge-wiki.md)

## Context and decision drivers

Source graphs, target sketches and research notes alone do not describe the
implemented system's responsibilities and trust boundaries. Documentation needs
reviewable diagram source, exact source identities and same-change upkeep.

## Considered options

1. Screenshots only: difficult to review and maintain.
2. Generated code graphs only: insufficient explanation of scope and intent.
3. Source-bound prose and text diagrams: selected as the proposed convention.

## Decision outcome

Maintain `docs/architecture/` as the implementation view, initially using Mermaid
fenced blocks alongside prose. Identify inspected source/version and link
implementing paths. Separate static source, configured deployment and observed
runtime evidence. Never draw planned components as built.

Every change assesses components, responsibilities, contracts, integrations,
storage, data flow, deployment/runtime dependencies and trust boundaries.
Update affected prose, diagram sources and references in the same change.
Otherwise state "Architecture impact: none" with a reason.

Implementers own updates; reviewers compare arrows, labels and boundaries with
source and render modified diagrams. Syntax validity alone is insufficient.
When integrating onto another revision, reconcile the source, not only the SHA.
The initial view intentionally describes the inspected stage-1 implementation.

Product graphs of analyzed repositories are distinct from these diagrams of
capability-explorer. The [wiki](../wiki/index.md) stores related concepts and
proposals, not competing copies of the current architecture.

## Consequences

Architecture changes become reviewable with code, at a modest maintenance cost.
No automated drift detector or runtime validation is implemented by this
documentation decision. Proposed status does not establish repository-wide
acceptance or an automatic freshness guarantee.
