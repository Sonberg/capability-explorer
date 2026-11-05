---
type: Concept
title: Model recognition, semantic coverage and rendering are separate
description: Source-grounded BPMN and DMN diagnostic and presentation boundaries.
status: draft
tags: [bpmn, dmn, evidence, coverage]
sources:
  - id: implementation
    resource: ../model-collection.md
  - id: architecture
    resource: ../architecture/current.md
  - id: contract
    resource: https://github.com/Sonberg/capability-explorer/issues/33
  - id: handoff
    resource: ../plans/collector-handoff-acceptance.md
---
# Three different claims

Recognizing a standard namespace is not the same as validating every construct
or business rule. The qualified census distinguishes structurally retained
standard layout, supported source semantics, unsupported standard constructs
and opaque vendor content. Genuine limitations remain visible.[^implementation]

Original-XML rendering is independent: the attachment must have explicit content
permission and independently verified bytes/crosslinks. A diagram that renders
correctly does not establish complete extracted semantics.[^architecture]

# Readable evidence without inventing approval

DMN allowed-value text is literal, associated with its column and not evaluated.
No declaration in evidence differs from a present declaration with unavailable
text. Grouped diagnostics preserve occurrence counts and exact locator details;
fewer groups do not mean fewer source occurrences.[^implementation]

The collector supplies source-bound evidence suitable for downstream aligned
architecture/business views. Accepted rules, ownership, abstraction levels and
organizational aggregation are downstream decisions, not facts inferred here.[^contract]

# Consumer handoff is not publication authority

Collection content identity excludes attempt metadata. An independent consumer
can validate the same evidence under a different valid attempt receipt without
changing the content hash. Optional omissions stay explicit and partial; required
failures do not become completed bundles. These are bounded contract claims,
not proof of arbitrary crash recovery or downstream business acceptance.[^handoff]

[^implementation]: Current bounded extraction and UI behavior, limitations and validation commands.
[^architecture]: Current source-observed component and artifact flow.
[^contract]: Collector handoff boundary and future aligned-viewpoint note.
[^handoff]: Existing producer proof and focused independent-consumer acceptance mapping.
