# Establish and govern a shared ubiquitous language

* Status: proposed for repository-wide adoption; applied in this workstream
* Date: 2026-10-31
* Decision owner: repository contributors
* Requested by: project operator in the current work session
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [MADR-0001](0001-github-work-coordination.md)

## Context and problem statement

Capability Explorer already has a ce.v1 contract and distinct extraction,
evaluation, binding, storage, exploration and interpretation implementations.
Their vocabulary is spread across schema documentation, code and tests. There
was no dedicated project glossary. Contributors and agents can consequently use
“capability,” “analysis,” “artifact,” “binding,” “unknown” or “evidence” with
different meanings, producing incompatible implementations and misleading claims.

The SQL work exposed a concrete ambiguity: an identifier named `currency` and
arithmetic scaling were treated as proof of monetary semantics. The operator
chose conservative evidence and requested both a ubiquitous language and a MADR.
We need a shared vocabulary that preserves the executable contract and makes
uncertainty explicit, without redesigning the system or inventing business facts.

## Decision drivers

* Consistent names across code, tests, documentation, issues and reviews.
* Evidence-backed claims and explicit unknown, partial and candidate states.
* Clear ownership across existing implementation contexts.
* One discoverable vocabulary with reviewable changes.
* Compatibility with ce.v1 and existing deterministic evaluation gates.
* Lightweight maintenance suitable for human contributors and coding agents.

## Considered options

1. Leave terminology distributed across schema documentation and source code.
2. Maintain one shared glossary, backed by the executable contract and a change policy.
3. Create independent glossaries per context and translate at every boundary.

## Decision outcome

Choose option 2 for this workstream: [docs/ubiquitous-language.md](../ubiquitous-language.md)
is the shared vocabulary. Link it from the project README and contract guide.
Repository-wide acceptance remains subject to contributor review; a local commit
or agent consensus does not imply that review has happened.

### Authority and boundaries

The ce.v1 schema, relation catalogue and validators remain authoritative for wire
behavior. The glossary consolidates meanings and supplies a context map; it does
not override executable constraints. A disagreement is a defect to reconcile,
not permission to weaken validation or silently reinterpret existing artifacts.

Use the existing implementation boundaries: capability/source acquisition,
extraction, contract/evidence, evaluation, persistence, binding, exploration and
interpretation. This map records current ownership, not a completed DDD redesign.
Context-specific terms may be qualified where necessary. Do not conflate a
source snapshot with a binding snapshot or an interpretation proposal with an
extracted fact. Development terms such as queen and swarm are not domain entities.

### Naming and uncertainty

Use glossary terms in new public interfaces, diagnostics, tests and work claims.
Preserve distinctions between revision and branch, analysis and artifact, symbol
and occurrence, extracted facts and derived bindings, partial coverage and
partial attributes, and IR hash equality and full artifact determinism.

Identifier names alone cannot establish business meaning. Unknown units or
currency remain unknown until source-supported semantics establish them. Exact
source spans establish provenance but do not independently prove the correctness
of an interpretation. A passing gate establishes only its tested assertions.

### Change and review policy

For a new public term, add its definition, context and a concrete example before
introducing the interface. Clarifications retaining the same meaning can update
the glossary in an ordinary reviewed change. Semantic changes require a new ADR
or an explicitly superseding decision, together with affected schema/contract,
implementation, tests and glossary updates in the same reviewed work package.
Record compatibility and migration consequences where wire behavior changes.

Follow MADR-0001's visible claims and designated integration ownership for shared
language files. Reviewers check vocabulary against the actual schema and source;
no model vote or glossary edit authorizes changing golden expectations. Any
exception to a work package's golden-preservation rule needs explicit agreement.

## Consequences

Positive: contributors have a single reference, review discussions distinguish
facts from interpretations, and tests can state uncertainty precisely. The
existing architecture gains shared terminology without new runtime dependencies.

Negative: the glossary needs maintenance and can drift from source. Reviewers
must reconcile conflicts and avoid turning a shared vocabulary into a ban on
necessary context-specific terms. Adoption remains cooperative, not mechanically
enforced by the document.

## Validation and adoption

Check that glossary links resolve, context names correspond to implementation
directories, and definitions match schema/validator behavior. Review a concrete
SQL unknown-currency example and an extracted-versus-bound relationship example.
A contributor review in issue #3 or the reviewing PR should accept or amend this
ADR; then update its status with that decision reference. Until then, preserve
the stated workstream scope and proposed repository-wide status.
