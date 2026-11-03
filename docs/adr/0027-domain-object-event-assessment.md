# Proposed source-bound assessment of business objects and events

* Status: proposed for independent owner review; not accepted
* Date: 2026-11-03
* Scope: research model and synthetic contract only; no extractor, grader, schema/v1 change, or production classification
* Related: issues #4, #6, #12, #23, #24, #33, #43, #53, #54; MADRs 0002, 0008, 0013, 0026

## Context

The current model can record source-bound code facts and separate reviewed
glossary assertions, but does not define business objects, DDD event profiles,
field-level exposure decisions, or a maturity grade. A research package must not
relabel generic code symbols or message contracts as domain truth. It must keep
source observations, declarations, deterministic findings, model suggestions,
human reviews, policy verdicts, and grades distinguishable and revision-bound.

The existing ce.v1 record contract lists `MessageContract` and `Channel`, generic
`PUBLISHES`/`CONSUMES` relations, and evidence, derivation, and resolution
metadata; it contains no dedicated Business Object/Domain Event taxonomy or
proof that a field reaches a recipient. See the [base schema](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/schema/v1/record.schema.json#L104-L169)
and [relation constraints](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/schema/v1/relations.json#L128-L151).
The proposal therefore stays outside ce.v1 and is not an implementation
authorization.

## Evidence and distinctions

* **Business object:** a context-local business concept; do not identify it
  from a DTO, class, table, generated type, or matching spelling alone. Entity
  identity, value-object semantics, and aggregate consistency boundaries are
  related but distinct interpretations. Existing CRUD/transaction-script design
  is not defective merely because it lacks a rich domain model.
* **Domain event:** an occurrence within a context. **Integration event:** a
  selected, externally consumable representation; it may be projected from one
  or many internal facts or not published at all. Preserve author/source
  terminology when definitions disagree and record the operational
  classification separately.
* **Command versus event:** Fowler's 2017 event-driven article distinguishes
  notification, event-carried state transfer, event sourcing, and CQRS.
  Intentional state transfer is legitimate, not automatic leakage. A
  passive-aggressive command is an anti-pattern lead, not a verdict from a
  name/suffix; the request/response flow can require live observation.
* **Context:** Fowler's bounded-context guidance recognizes polysemes and
  different models shaped by teams/culture. A repository, namespace, topic, or
  deployment does not by itself establish a context. Do not force a global
  canonical model.
* **Event conventions:** Microsoft's domain-event guidance distinguishes
  local side effects from propagation of committed changes. Its
  in-process/asynchronous framing is that source's convention, not a universal
  definition. Keep producer declaration, domain occurrence, published
  projection, envelope, protocol binding, recipient, and deployed route apart.
* **Contract/topology:** AsyncAPI 3.1.0 describes application-relative
  operations/messages/channels and can describe events, commands, requests,
  and responses. Its introduction warns that a receiver document cannot be
  inferred by reversing sender actions and that routing infrastructure may be
  absent. Channel/name matches do not prove an actual consumer or counterpart.
* **Envelope/privacy:** CloudEvents 1.0.2 treats envelope context separately
  from payload schema. Its Privacy and Security section says sensitive
  information SHOULD NOT be context attributes because producers, consumers,
  and intermediaries may inspect or log them; protocol security/encryption
  recommendations are not automatic compliance certification.
* **Delivery/compatibility:** transactional-outbox relays can publish more
  than once; idempotency is needed for a claimed duplicate-tolerant guarantee,
  not a basis for exactly-once claims. Pact tests particular minimal consumer
  interactions, not all safety or behavior.

These are source-specific qualifications and synthesis, not a claim that the
authors prescribe this package's rubric. Relevant pinned references and their
scope are recorded in
[`domain-object-architecture.md`](../research/domain-object-architecture.md).
The original `docs/adr/0013-integration-contract-standards.md` is also proposed,
not evidence of current AsyncAPI/CloudEvents adapter implementation.

## Decision options

1. Infer business meaning, context, recipient, field flow, and exposure from
   names, call graphs, topic strings, schema/envelope conformance, or model
   output. **Reject:** those are useful leads/declarations, not semantic or
   runtime proof.
2. Keep deterministic source facts, reviewed mappings, optional model
   suggestions, exposure decisions, and profile grades separate; require
   evidence and version identity at each adoption boundary. **Recommend for
   owner consideration:** supports reproducible bounded research while
   preserving uncertainty.
3. Delay all research until live traffic or an implementation exists. **Reject
   as the only option:** source-bound inventory gaps and reviewer prerequisites
   can be specified now without pretending to resolve them.

## Proposed contract and assessment profiles

Use the versioned draft
[`domain-object-assessment.schema.json`](../../schema/research/domain-object-assessment.schema.json)
and
[`domain-object-rubric.json`](../../config/research/domain-object-rubric.json).
Every record carries exact repository/commit/unit/symbol or document
pointer/span/hash; extractor and binding versions/configuration hash; rule,
rubric, glossary, context-map, and exposure-policy versions. A hash binds bytes,
not source authenticity, authorization, semantic truth, or live routing.

Raw facts, optional suggestions, reviews, policy verdicts, and assessment results
are separate. Context, projection, field-propagation, and consumer relations
need explicit source mappings or current reviewed assertions. Names, calls,
topics, brokers, envelope fields, or matching schemas never manufacture those
links. Unsupported dynamic, generated, external, and recursive shapes stay
unknown/partial; a serialized field name alone is not semantic proof.

Exposure is an independent verdict: `approved`, `needs-review`,
`policy-violation`, or `unknown`, with rule identity, policy owner, audience,
and evidence. A suspicious-name heuristic is a review lead, not a violation;
absence of a suspicious spelling is not proof of no leakage. Assess purpose,
audience and each nested field. Correlation IDs and timestamps are neither
automatically safe nor automatically prohibited. Intentional event-carried
business state is not a leak solely because state is carried.

The three **proposed** profiles—business object, domain event, integration
event—use cumulative hard gates with pass/fail/unknown/not-applicable statuses.
Not-applicable requires a reason and cannot waive policy. A policy violation
blocks every grade; weighted averages cannot offset it. Unknown/unassessed or
insufficient evidence is not bronze. The tier names bronze/silver/gold/platinum
are project proposals, not DDD standards, security certifications, or
Databricks/medallion data tiers.

Bronze asks for supported source-bound shape/identity/context evidence and
visible limitations. Silver adds reviewed contextual language, field purpose,
projection distinctions and field exposure as applicable. Gold requires current
context-scoped ubiquitous-language review and the profile's applicable tests,
contract, compatibility, or guarantee evidence. Platinum adds current owner
stewardship, independent review, change/deprecation policy, and representative
consumer/handler evidence. Gold/platinum cannot rely on spelling or an LLM
definition in place of current domain review. An internal domain event needs no
external consumer, broker, outbox, CloudEvents, or AsyncAPI to earn an
applicable high grade.

Any source, context-map, glossary, policy, rubric, tool, or binding change
changes assessment identity and invalidates affected reviews until re-evaluated.
Stale state is explicit, never silently carried forward.

## Consequences and limits

The draft is deliberately not a domain-event extractor, ORM exposure detector,
security policy, live topology probe, accuracy claim, or certification.
Synthetic fixture outcomes validate proposed contract guards only; they prove
no runtime grade, real field propagation, no-leakage property, or extractor
capability. Public/protocol standards are not evidence of deployment or
consumer compatibility. A selected implementation slice requires an owner
decision after pilot prerequisites exist. The accepted status of this MADR and
the shared index/diagram remain for the integration owner.

## References

* Evans, [DDD Reference](https://www.domainlanguage.com/ddd/reference/);
  Microsoft, [Tactical DDD](https://learn.microsoft.com/en-us/azure/architecture/microservices/model/tactical-domain-driven-design).
* Fowler, [Bounded Context](https://martinfowler.com/bliki/BoundedContext.html),
  [Ubiquitous Language](https://martinfowler.com/bliki/UbiquitousLanguage.html),
  and [What do you mean by “Event-Driven”?](https://martinfowler.com/articles/201701-event-driven.html)
  (updated 2017-02-08).
* Microsoft, [Domain events: design and implementation](https://github.com/dotnet/docs/blob/7716b982e6de4da9f050176d2a8b1daa6c78ba3e/docs/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation.md).
* [AsyncAPI 3.1.0](https://www.asyncapi.com/docs/reference/specification/v3.1.0);
  [CloudEvents 1.0.2](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md).
* OWASP, [API3:2023 Broken Object Property Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa3-broken-object-property-level-authorization/)
  (event-audience application is an architectural inference); [transactional outbox](https://microservices.io/patterns/data/transactional-outbox.html);
  [How Pact works](https://docs.pact.io/getting_started/how_pact_works).

Public source versions and interpretation are explicit. BA knowledge search
returned candidate `knowledge-refresh20261028-ba`, but reading its supporting
passage failed with HTTP 502. No retained BA position, organization-wide
standard, or adoption is claimed; cross-project accepted material is not
enterprise adoption.
