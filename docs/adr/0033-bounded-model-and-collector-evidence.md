# Keep authored models, code candidates and collector authority separate

* Status: proposed
* Date: 2026-11-04
* Decision owner: repository contributors; central contract ownership remains unresolved
* Coordination: issues #12, #33 and #75
* Related: MADR-0013, MADR-0020, MADR-0029 and MADR-0032

## Context and problem statement

Stakeholders need consumable process/decision evidence without requiring an
Explorer instance, database or central service during collection. Authored
BPMN/DMN, static-code interpretations, transport validation and business approval
have different authorities. A successful XML parse or accepted upload must not
silently promote one into another.

## Decision drivers

Observable coverage, offline reproducibility, source-bound identities,
content minimization, explicit uncertainty and independently readable contracts.
"What you cannot see, you cannot govern" requires visibility of unknown and
stale inputs, not a claim that all business behavior is visible in source.

## Considered options

1. Reuse native `ce.v1` facts for every model and interpretation: rejected
   because it conflates authorities and would break its compatibility contract.
2. Execute models or infer unrestricted business processes from code: rejected
   because execution, business approval and semantic equivalence are not proven.
3. Use separate bounded evidence profiles and an offline immutable collection:
   selected for this operator-authorized implementation and owner review.

## Decision outcome

Keep native `ce.v1` unchanged. Use separate `ce.models.v1` authored evidence and
`ce.candidates.v1` non-executable, unreviewed code candidates. Negotiate the new
`ce.collection.v1` exchange profile explicitly rather than adding incompatible
alternatives to the closed `ce.platform.v1` schema.

Declare support for BPMN 2.0.2 and fixture-qualified DMN 1.3, not all versions
or all language constructs. Preserve source identities, order, references and
content-bearing expressions; report unsupported, opaque and omitted content.
Coverage describes the declared recipe, not whole-standard validity or runtime
equivalence. Reject unsafe XML and bound resources before model interpretation.
Parser packages and metadata alone are not security or fidelity evidence.

Static candidates require explicit source/semantic oracles for the supported
synchronous orchestration and scalar-decision subsets. Keep predicates, effect
order, exceptional outcomes and exact source revisions. Do not infer business
roles, approvals, executed behavior or authored-model equivalence.

Collection operates on explicit repository/enrollment/profile inputs without
executing target source or retrieving arbitrary remote content. Retain original
model bytes locally. Exporting XML, native snippets, expressions or predicate
text requires an explicitly permitted content profile; default transport must
omit them safely or refuse. Local flags record operator intent and do not
authenticate central enrollment or grant rights.

Seal bounded artifact bytes and a canonical manifest atomically. Distinguish
content identity, source observation and submission attempt identity/time.
Identical content can be reused without making every attempt the same receipt.
Required failures never create a completed collection; optional failures remain
visible partial coverage. Neither `received` nor `validated` means `published`.
Existing Explorer/database behavior remains; offline collection does not
authorize its removal.

Freshness is relative to an explicitly observed, complete input vector:
repository revisions/trees, enumerated source and dependencies, recipe,
configuration, enrollment, rights and negotiated profiles. A changed vector
invalidates reuse; an incomplete vector is unknown. A matching vector is not
a real-time SLA, proof of an unobserved remote HEAD, business approval or proof
that rights remain valid beyond the observed authority state. Manual and CI
triggers are the initial mechanism; no paid learning daemon is required.

Supply two design-only OpenAPI 3.2.1 documents for intake/receipts and immutable
process/decision product reads with shared local schemas. Publish tooling
qualification limits honestly. Central ownership, identity/authentication,
authorization, deployment and operational guarantees remain downstream gates;
the documents do not authorize an anonymously accessible provider.

## Validation and consequences

Use the existing test runners with source-bound red/green receipts and
Given/When/Then scenarios. Require independent .NET and TypeScript collection
consumers, negative XML/transport fixtures and real CLI dogfood on sealed local
Git repositories. Repeat, mutate, deny and tamper flows must be observable.
Run the existing local/hosted gates without redundant new CI jobs.

This decision adds bounded recipes and maintenance costs, not a workflow engine,
FEEL evaluator, process-mining product, model editor or central integration
deployment. Implementation and test evidence are recorded separately; this
proposed record itself certifies none of them and accepts no previous proposal.
