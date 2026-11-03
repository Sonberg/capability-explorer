# Specification: source-bound business-object and event assessment

Status: research proposal; not an accepted DDD standard, security decision, or
runtime capability. Baseline: repository commit
[`ceba8797eab7b465f214ac02847f4ee90b43d34d`](https://github.com/Sonberg/capability-explorer/tree/ceba8797eab7b465f214ac02847f4ee90b43d34d).

## Scope and vocabulary

A **business object** is a reviewed, context-local business concept. It may be
modeled as an entity (identity over time), value object (value semantics), or
aggregate (consistency boundary); none is inferred from a class, DTO, table,
generated type, or matching name. CRUD/transaction-script designs are not
defective merely because they do not adopt a rich DDD model.

A **domain event** records a business occurrence within an owning context. An
**integration event** is a selected representation published across a declared
boundary. Its payload may be projected from one or more internal facts, but this
relationship needs explicit mapping evidence or a current review. A source
event may have no integration projection or multiple projections. A **command**
expresses requested work, not an occurrence; passive-aggressive commands are a
review concern, never a suffix-based diagnosis. Keep source terminology and
operational classification when authors disagree.

An **envelope** holds transport-level context; payload schema, protocol binding,
deployed route, audience, and business meaning are separate evidence. A valid
envelope/schema does not establish a deployed consumer or semantic correctness.
AsyncAPI 3.1.0 supports messages for events, commands, requests and responses;
its introduction cautions that receiver documents cannot be inferred by
reversing sender actions and that routing topology may be absent. Channel/name
matches cannot establish an actual counterpart.

**Context ownership** is not the repository, namespace, deployment, capability,
topic, or class name. Ubiquitous language is local and can contain polysemes.
Context maps and human decisions retain uncertainty; there is no forced global
canonical model. Same spelling in two contexts is not identity.

## Evidence classes and assessment profiles

The versioned contract keeps source facts, deterministic-rule findings,
optional-model suggestions, human review decisions, exposure policy decisions,
and maturity assessments in distinct fields and identities. A suggestion never
becomes a fact or a grade without the same evidence and review gates.

Source provenance binds repository and full commit, unit, symbol or document
pointer/span, and content hash; extractor/tool version, binding/configuration
hash, rules, rubric, context map, glossary, exposure policy, and review versions
are separate. Hashes provide byte identity only, not source authenticity,
authorization, or proof that a declared deployment is live.

The proposed profiles are **business-object**, **domain-event**, and
**integration-event**. All criteria use pass/fail/unknown/not-applicable.
Applicability exclusions need a reason and cannot bypass a prohibition.
Exposure is independent: approved, needs-review, policy-violation, or unknown,
each with rule, policy owner, and evidence. Unknown/unassessed exposure is not
approval and is not bronze. A confirmed policy violation blocks every tier;
no weighted average can offset it.

Bronze, silver, gold, and platinum are proposed project labels, not DDD or
security certifications and not Databricks/medallion data tiers. The profile
gates and cumulative rules are in
[`domain-object-rubric.json`](../../config/research/domain-object-rubric.json).
Bronze requires source-bound shape, identity and context evidence with
limitations visible. Silver adds reviewed contextual semantics and field
purpose/exposure appropriate to the profile. Gold requires current
context-scoped language review and applicable tests/contract/guarantee evidence.
Platinum adds independent review, stewardship/change policy, and representative
consumer or handler evidence. A small/simple object need not adopt every DDD
pattern. A higher tier cannot be reached by averaging away any mandatory fail
or unknown. Gold/platinum cannot rely on spelling or an LLM definition in place
of current domain review.

Internal domain events do not require external consumers, brokers, outbox,
CloudEvents, AsyncAPI, or delivery guarantees to earn an applicable high grade.
Intentional event-carried state is not automatically overexposure. Assess nested
fields, audience, purpose, and evidence. OWASP API3 supports the general
property-authorization risk; treating an event audience as a boundary is a
project inference, not OWASP event certification. CloudEvents 1.0.2 says
sensitive information SHOULD NOT appear in context attributes because producers,
consumers, and intermediaries may inspect or log them; protocol security and
encryption recommendations are not automatic compliance certification.

Any source, context, glossary, policy, rubric, binding, or tool version change
changes assessment identity and invalidates affected semantic reviews pending
reassessment. Preserve stale records as stale; do not silently reuse a badge.

## Explicit safety envelope

* This package proposes a research model and synthetic contract checks; it does not implement a semantic extractor, production grader, or source scanner.
* The proposed bronze/silver/gold/platinum labels are project proposals, not DDD standards, security certifications, or medallion data-processing tiers.
* Facts, model suggestions, human reviews, exposure decisions, and maturity results are separate evidence classes.
* Context, field, projection, and consumer relationships require source evidence or a current reviewed assertion; names, calls, topics, and schemas do not establish them.
* Unknown, unassessed, and insufficient evidence never silently become bronze or a safe exposure verdict.
* A confirmed policy violation is a non-averagable hard stop; applicability requires rationale and cannot waive prohibited exposure.
* Gold and platinum require current reviewed context-scoped ubiquitous language; spelling and model suggestions are not substitutes.
* Internal domain events do not need a broker, outbox, external consumer, CloudEvents, or AsyncAPI to qualify for an applicable high profile.
* Intentional event-carried state is not automatically leakage; field names and envelopes alone are not semantic proof.
* Unsupported dynamic, generated, external, and recursive shapes remain explicit uncertainty, not silent no-leakage evidence.
* Cryptographic hashes bind bytes but do not establish source authenticity or reviewer authorization.
* Do not modify ce.v1, runtime extractors, dependencies, shared build wiring, MADR indexes, or the primary checkout.

## Traceable acceptance obligations

| ID | Falsifiable obligation | Positive evidence | Negative evidence |
|---|---|---|---|
| DO01 | Define object/event/command/envelope/context and three profile differences. | [Proposed MADR](../adr/0027-domain-object-event-assessment.md) decision table and source comparisons. | Name, topic, handler or schema does not prove business classification or flow. |
| DO02 | Map C#, TS, SQL, ce.v1 and glossary facts/declarations to inventory, with gaps. | [Architecture gap analysis](domain-object-architecture.md), exact base paths/lines. | Dynamic/recursive/unsupported shapes, runtime routing and unreviewed boundaries remain unknown. |
| DO03 | Require typed identity, nesting, ambiguity, exposure, applicability, ceiling and stale review. | [Draft schema](../../schema/research/domain-object-assessment.schema.json), versioned config, conformance tests. | Unknown property, stale review, missing evidence or profile mismatch rejected. |
| DO04 | Carry nine independently authored synthetic expected cases. | Fixture `cases.json` and separate `expected.json`. | No runtime/extractor/accuracy claim from synthetic oracle. |
| DO05 | Exercise semantic mutations and byte determinism. | Focused `node:test` cases and phase checker. | Invented links, false propagation/no-leakage, average-away, unknown-bronze, stale glossary and schema-only promotion fail. |
| DO06 | Define expert gold-set protocol and metrics without fabricating results. | [Completion protocol](domain-object-completion.md). | No measured precision, recall, agreement, or actual gold-set assertion. |
| DO07 | Recommend exactly one conditional, bounded pilot. | [Pilot gate](domain-object-completion.md). | No implementation, extra issue, or unsatisfied prerequisites assumed. |

This specification constrains a research package only. It does not update
`ce.v1`, qualify any extractor, assign real grades, certify security, claim
production routing/consumer topology, or close issues #23, #24, #43, or #54.
