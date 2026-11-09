# Rank glossary candidates into domain concepts with an explainable score

* Status: accepted
* Date: 2026-11-01
* Coordination: issue #4, layer 1 follow-up; extends [MADR-0004](0004-glossary-candidates.md)
* Related: [ubiquitous language](../ubiquitous-language.md), [MADR-0002](0002-ubiquitous-language.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for Rank glossary candidates into domain concepts with an explainable score; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:fafb62ac75f2fa70b012ba48f39ac022a3064ee84746be64036de97dd233bb99`.
Previous decision status (historical): `proposed; applied in this workstream`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem

MADR-0004 lists every explicit declaration as a term candidate. On a real corpus
that is a flat list of mostly implementation detail. Three C#/TypeScript/SQL
repositories (checkout API, web shop, support desk) produced 2,211 candidates, of
which an estimated 10% are domain concepts. Showing every property clutters the list;
hiding classes and properties leaves only calls and routes that do not connect to
the domain; and without clustering or definitions the list is not understandable.

## Decision drivers

Deterministic, no model or database at runtime; evidence and uncertainty must
survive; the ranking must say why; a human must be able to override it; the exact-label
candidate of MADR-0004 must keep its meaning.

## Considered options

1. Keep the flat candidate list and add search and filters only.
2. Add a concept layer above candidates: fold representations, nest attributes, score
   with explainable features whose weights are fitted offline from human labels.
3. Rank with an embedding or LLM at runtime.

## Decision

Choose option 2. The exact-label **term candidate** of MADR-0004 is unchanged and
remains the evidence unit (`GET /api/glossary`). A new read-only projection,
`GET /api/glossary/concepts`, adds a **glossary concept**: a group of declarations
whose stem is equal, shown with a **tier** and the reasons for it.

* Concept-bearing declarations are types, tables and message contracts. Properties,
  fields and columns are nested as **attributes** of the declaring concept. Test units,
  generated and external declarations are excluded and counted.
* **Representation folding** is a reviewable suffix list (Dto, Row, Response, Request,
  Entity, Entry, Model, Input, Body, Result, version tokens). It runs only in the stem,
  never in MADR-0004 normalization. A folded concept is "related names", not an
  assertion that values are interchangeable. A trailing plural is singularized in the
  stem only.
* **Variants**: implementations of an interface that has two or more implementations are
  listed under that interface.
* **Features** are boolean, each derived from a fact in the artifacts (persistence,
  message contract, calculation input, role suffix, operation name, integration model,
  feature-folder naming, test/infrastructure location and so on). The score is the
  integer sum of the feature weights in `glossary-weights.json`. The weights are fitted
  offline from labelled keep/hide decisions and checked in; the runtime performs no
  inference. Every concept returns its feature list and points.
* **Tiers** are presentation only: `core`, `supporting`, `plumbing`. Plumbing remains
  reachable with its reasons and is never deleted from the response.
* Calculations are reported as **metrics**, separate from concepts, using the existing
  candidate comparison with its pair budget unchanged.
* A concept never implies the same value, never expands abbreviations, and does not
  infer a concept from an `XId` attribute (rejected: it produced external identifiers).

## Consequences and verification

Good: the default view is a short list with reasons; wrong rankings are correctable by
changing a reviewable weight or suffix list, not code. Bad: the suffix lists and
weights encode conventions of the corpus they were fitted on (CQRS, MediatR,
Clean Architecture), and TypeScript type members are absent from ce.v1, so TypeScript
concepts lack attributes and receive no data-shape signal. A language-aware member
extraction is a separate contract change.

Verification: unit tests per feature with negative cases; a committed synthetic gold set
with a precision/recall regression gate; determinism under shuffled input and alternate
cultures; the fixture capabilities and a private corpus measured against hand-labelled
samples (see "Validation").

## Validation

Measured on three private repositories (C# checkout API, Next.js shop, TypeScript + Supabase support desk;
2,211 flat candidates before, 1,284 concepts after test and integration separation) and the five fixture repositories.
Artifacts came from the real extractors. One person labelled items by reading source, with tiers hidden and the order
shuffled. Estimates are weighted to the real pool sizes and are accurate to roughly 12 points at 60-80 items per sample.

| Check | Result |
|---|---|
| Fixture gold set (29 labels, committed as a test) | 28 of 29. The one disagreement, `weekly sale` (a response-only type), is listed in the test. |
| Cross-validation over four labelled samples (A-D) | precision 77%, recall 65% pooled. Refitting weights did not beat the seeded weights (73% / 60%). |
| **Fresh blind sample E (80 items, drawn from the final engine and weights, used once)** | **precision about 60%, recall about 58%; 78% recall when 11 ambiguous items are excluded** |
| Shown / hidden on the private corpus | about 190 of 1,284 concepts shown, against 2,211 candidates shown before |

What this does and does not show:

* It removes most clutter and gives a reason for every placement, but about four in ten shown concepts are still
  not domain concepts in my labelling. The ranking is an aid for review, not a finished glossary.
* Numbers measured earlier in the work were more optimistic (up to 76% / 79% on a sample that had already shaped
  the rules). A fresh sample drawn after the rules changed gave 45% / 62%, which led to separating integration
  models, restricting `featureFolder` to C# non-operation folders, and adding `representationOnly`. E was drawn after
  those changes. Treat E, not the earlier figures, as the estimate.
* Labels are not consistent about DTO-only types (a published result such as `WeeklySalesResponse` versus a nested
  part such as `UserOfferDiscountCodeDto`). That ambiguity caps any score, and the `representationOnly` weight is a
  stated prior between the two sets of labels.
* One labeller, three corpora from one author. A second reviewer and a different codebase are needed before the
  weights are called general.

Known errors on E, in order of cost: integration models are only softly penalized (a hard veto is the likely fix);
`featureFolder` is over-weighted and rewards plain DTOs in feature folders; one-word value types without attributes
(TypeScript members are not in ce.v1) are missed. These are recorded, not tuned away, so E stays a valid estimate.
