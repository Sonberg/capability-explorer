# Architecture and source-bound gap analysis

Baseline for all repository citations: [`ceba8797eab7b465f214ac02847f4ee90b43d34d`](https://github.com/Sonberg/capability-explorer/tree/ceba8797eab7b465f214ac02847f4ee90b43d34d).
The table reports repository contract/source facts, not an analyzed customer's
domain. No private target or runtime consumer was examined.
The offline checker checks citation syntax and exact baseline links, not Git
history availability or the truth of source passages. Integration separately
reads the pinned source ranges; worktree Git metadata is not exposed to the
Node sandbox.

## Current evidence inventory

| Current source-bound fact or declaration | Proposed inventory mapping | What it does not prove |
|---|---|---|
| ce.v1 node vocabulary includes `MessageContract`, `Channel`, `Service`, `Endpoint`, generic `Type` and `Property`; edge types include `PUBLISHES`, `CONSUMES`, `HANDLES`, `MAPS_TO`, plus compiler/rule/human derivation and resolved/candidate/unresolved status. [record.schema.json L104-L169](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/schema/v1/record.schema.json#L104-L169) [relations.json L128-L151](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/schema/v1/relations.json#L128-L151) | Preserve generic facts, derivation, resolution, source/evidence, and candidate ambiguity. Proposed assessment uses a separate versioned schema. | No Business Object, aggregate, domain-event/integration-event classification, nested payload propagation, approved audience, runtime route, or policy verdict. |
| C# extractor constructs Roslyn compilations from project sources/reference packs/package assemblies; no restore/build/analyzed-code execution; missing references produce partial diagnostics. [CSharpExtractor.cs L13-L20](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/platform/src/CapabilityExplorer.Extractors.CSharp/CSharpExtractor.cs#L13-L20) | Keep unit, compiler/tool identity, configuration, source evidence and diagnostics/coverage in extraction provenance. | Not runtime behavior, generator output completeness, deployed consumer, or business meaning. Unsupported/dynamic serializers remain uncertain. |
| C# rules map recognized EF entities to tables with rule/configuration provenance, and recognized Service Bus sender/receiver calls to PUBLISHES/CONSUMES with bound/unresolved status. [Rules.cs L686-L696](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/platform/src/CapabilityExplorer.Extractors.CSharp/Rules.cs#L686-L696) [Rules.cs L858-L870](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/platform/src/CapabilityExplorer.Extractors.CSharp/Rules.cs#L858-L870) | Preserve EF and messaging evidence as technical facts; map to business concepts only by current reviewed evidence. | EF table mapping does not make an entity a business object; sender/receiver call does not prove committed publication, actual remote recipient, traffic or context crossing. |
| The TypeScript/JavaScript extractor emits ce.v1 through the TypeScript compiler API; hand-written TypeScript record views cover ce.v1's generic identity, evidence, derivation and resolution. [README](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/README.md#L1-L13) [records.ts L1-L30](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/extractors/ts/src/contracts/records.ts#L1-L30) | Reuse exact revision/unit/symbol/spans and extractor/binding/config identity; retain unresolved/partial facts. | No business taxonomy or approved field propagation. Runtime reflection, proxies and application serialization may differ from compiler-visible declarations. |
| SQL catalog types represent tables/views/columns/routines and source ranges; routine body can carry a dynamic marker. [ddl.ts L1-L31](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/extractors/ts/src/sql/ddl.ts#L1-L31) SQL extraction statically tokenizes and emits `SQL_DYNAMIC` incomplete-coverage diagnostics. [extractor.ts L21-L23,L59-L68](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/extractors/ts/src/sql/extractor.ts#L21-L23,L59-L68) | Preserve declarations, spans, dialect, static reads/writes and incomplete diagnostics as technical facts. | Tables/columns do not identify business objects; dynamic SQL reads/writes are incomplete; no event serialization or audience evidence. |
| Glossary research and reviewed assertions explicitly say name groups are not business meaning; exact occurrence/revision/artifact identities and current human assertion are required, and source changes make assertions stale/orphaned/ambiguous. [MADR-0008 L9-L45](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/docs/adr/0008-glossary-flow-and-reviewed-assertions.md#L9-L45) | Add future context-scoped glossary/context-map versions and independent reviewer decisions to assessment identity. | Existing global engineering glossary, analyzed-repository names and candidate groups are not per-context domain-approved Ubiquitous Language. |
| MADR-0013 proposes pinned OpenAPI/AsyncAPI/CloudEvents handling and says declarations/standards do not prove deployment, traffic, compatibility or business meaning. [MADR-0013 L8-L33,L43-L61](https://github.com/Sonberg/capability-explorer/blob/ceba8797eab7b465f214ac02847f4ee90b43d34d/docs/adr/0013-integration-contract-standards.md#L8-L33,L43-L61) | If later adopted, keep declaration dialect, envelope, payload schema, protocol binding, deployment and receiver evidence separate. | The MADR is proposed and does not prove those adapters or deployed message flows exist. |

This maps supported facts/declarations from C#, TypeScript/JavaScript, SQL,
ce.v1, and glossary review into a **future** inventory. No extractor was run on
an external application as part of this research. Source scope excludes private
corpus qualification (#23), security disposition (#24), workload/p95 evidence
(#43), and the unrelated extraction blocker (#54).

## Assessment architecture and tradeoffs

1. **Deterministic rules:** best for stable syntax/shape predicates,
   serialization callsites, explicit field references, policy matching and
   schema checks. They create candidates and explicit diagnostics; name
   heuristics are not policy verdicts. Risks: false certainty, compiler/runtime
   gaps, dynamic code and language-specific bias.
2. **Reviewed mappings:** best for business meaning, context ownership,
   synonyms/polysemes, field-purpose decisions and intentionally translated
   projections. Store reviewer, rationale, evidence, scope, source revision,
   glossary/context/policy version and expiry/invalidation. Costs: review effort
   and stale decisions; never silently propagate links on spelling changes.
3. **Optional model suggestions:** useful only as an explicitly untrusted
   queue of candidate terms, classification or likely mappings, with evidence
   references and model/config identity. They cannot write facts, pass gates,
   approve exposure or grade without the same human/evidence controls. They may
   be omitted entirely and are not required for deterministic behavior.

Proposed flow:

`source facts/declarations -> evidence-preserving candidates -> scoped human
reviews -> independent exposure decision -> profile-specific cumulative gates
-> version-bound result`.

No phase upgrades ce.v1 or runs an analyzed program. Cross-boundary claim
strength is limited by the evidence actually available. AsyncAPI sender
operations are not inverted into receiver behavior; channel names do not prove
consumer matching. A broker observation or live traffic source, if ever
authorized, would be separate evidence with its own scope and timestamp.

## External-source applicability and limitations

* Fowler's event-driven article (updated 2017-02-08) distinguishes notification,
  state transfer, event sourcing and CQRS; intentional state transfer can be
  legitimate. Its passive-aggressive command example is a review lead, and
  sender/recipient direction may require live observation.
* Microsoft event guidance at commit
  [`7716b982e6de4da9f050176d2a8b1daa6c78ba3e`](https://github.com/dotnet/docs/blob/7716b982e6de4da9f050176d2a8b1daa6c78ba3e/docs/architecture/microservices/microservice-ddd-cqrs-patterns/domain-events-design-implementation.md)
  discusses local side effects and integration propagation of committed
  changes; its in-process/asynchronous convention is not a universal definition.
* AsyncAPI 3.1.0 permits event, command, request and response messages and
  warns that sender-document reversal cannot reconstruct receiver documents or
  missing infrastructure routing.
* CloudEvents 1.0.2 privacy guidance discourages sensitive context attributes
  because intermediaries may inspect/log them; this is not compliance proof.
* Outbox relays may duplicate publication; idempotency supports a claimed
  duplicate-tolerant behavior. No exactly-once guarantee is inferred.
* Pact proves particular consumer/provider interactions, not all safety,
  compatibility, deployment, or every production consumer.
* OWASP API3 grounds property-level authorization risk; applying that rule to
  event recipients is this proposal's architectural inference, not an
  OWASP event certification.

Public references and limitations are listed in the proposed MADR. BA retained
knowledge search surfaced candidate `knowledge-refresh20261028-ba`; reading its
supporting passage failed HTTP 502. That is a partial retrieval, not proof BA
search is globally unavailable. No organization-wide or adopted BA rule is
claimed.
