# Treat glossary groups as deterministic term candidates

* Status: applied in this workstream; proposed for contributor-wide adoption
* Date: 2026-10-31
* Coordination: issue #4, layer 1; depends on PR #5
* Related: [ubiquitous language](../ubiquitous-language.md), [MADR-0002](0002-ubiquitous-language.md)

## Context and problem

The Explorer observes names, types and calculations across repositories. Similar
names are useful navigation aids but do not establish a shared business concept.
The project's engineering glossary and the product's domain candidates serve
different purposes. Issue #4's original all-weekly-average grouping would require
semantic inference beyond deterministic normalization.

## Decision drivers

Source identity and uncertainty must survive projection; the feature must work
without a model or database, with bounded comparisons and deterministic output.

## Considered options

1. Infer shared business concepts from names or embeddings.
2. Group exact normalized labels as unreviewed candidates, preserving occurrences.
3. Require human links before exposing any vocabulary.

## Decision

Choose option 2. Split camel/snake/kebab names, handle acronym boundaries, lowercase
invariantly, and remove one terminal Dto/Entity/Record token only when a base name
remains. Preserve Id, calendar and active qualifiers. Do not expand abbreviations.
Calendar-week and active-week labels stay separate. Candidate membership never
means "same value" or reviewed equivalence.

Occurrence identity includes repository, commit, unit path, symbol and role;
calculation observations additionally distinguish their source records so valid
same-symbol calculations cannot overwrite one another;
evidence retains exact source identity. Calculations use their declared symbol
names because ce.v1 output contains no business name. Only explicit named metadata
or resolvable declarations may contribute input/parameter occurrences. Missing
information stays unknown. Generated/external declarations are excluded.

Compare at most 64 distinct calculation pairs per candidate in stable ordinal
order. Report possible pair count and completeness. Known type/attribute differences,
missing evidence and unequal expression hashes are separate signals; unequal
hashes are not proof of mathematical inequivalence. A count of two calculations
alone is not a divergence. A candidate with no observed differences and incomplete
comparisons cannot be described as having no differences.

## Consequences and verification

The first slice is read-only: no glossary persistence, LLM definitions, flow graph
or same-value links. Capabilities/revisions scope the projection using existing
Explorer behavior. Explicit human links or reviewed proposals can be later layers.

Tests cover meaningful normalization negatives, unknowns, source identity,
selection boundaries, shuffled-input/culture determinism, pair limits and API
serialization. UI verification includes search, selection and exact source
navigation with no database/model configured. No schema, extractor, fixture,
dependency or evaluation-threshold changes are required.
