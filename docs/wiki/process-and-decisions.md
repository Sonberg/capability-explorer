---
type: Concept
title: Authored models and code-derived candidates are different evidence
description: BPMN, DMN, static-analysis and DDD boundaries for evidence collection.
status: draft
tags: [bpmn, dmn, static-analysis, ddd]
generated: {by: copilot-cli/1.0.105, at: "2026-11-04T01:10:00+01:00"}
sources:
  - id: existing
    resource: https://github.com/Sonberg/capability-explorer/issues/12
  - id: research
    resource: qualification.md
---
# Two independent high-priority tracks

Authored BPMN/DMN ingestion observes a model declaration. Static analysis derives
candidate process fragments and predicates from program semantics. Neither
proves the business process is complete, approved or observed in production.
The existing issue already distinguishes these capabilities.[^existing]

For authored models, preserve original bytes, digest, revision/path, model IDs,
task/event/gateway kinds, sequence versus message flows and references. For DMN,
retain dependencies, ordered rows/cells, hit policies, aggregation, types and
raw expression language/text. Parsing is not execution or schema/soundness
validation. Version/namespace and extension support must be explicit.[^research]

For static candidates, derive sequence and branches from control/data flow, not
method names, call-graph order or an LLM narrative. Preserve predicates, evaluation
order, short-circuiting, null/error paths and side effects. Emit unresolved
boundaries rather than guessing asynchronous behavior or human roles.

Code-to-DMN/FEEL interchange needs proof for the supported subset, including
rule order, overlap and errors. Otherwise emit a non-executable candidate and
diagnostic. Round-trip parsing alone does not establish semantic equivalence.

# DDD as an evidence lens, not automatic approval

Later domain candidates can link terms, entities, value objects, relationships,
state transitions and invariant sites to evidence. Repository/table/class names
cannot establish accepted bounded contexts, aggregates or ubiquitous language.
Transaction and invariant evidence can support a candidate, not team ownership
or business sign-off.

DDD candidate delivery must not block BPMN/DMN. Accepted terminology, target
models, reconciliation and stakeholder presentation remain downstream. Keep
these profiles separate under the [collector contract](collector-contract.md).

[^existing]: Public issue's authored-versus-reconstructed distinction and evidence-first semantic constraints, read before the bounded round.
[^research]: Bounded source qualification and implementation gates; no parser runtime or semantic equivalence test is implied.
