# Versioned integration contracts as evidence, separate from inference

* Status: proposed for owner review; extractor implementation not authorized by this decision alone
* Date: 2026-11-01
* Related: MADR-0002/0005/0007/0008/0010; issues #6 and #12

## Context and problem

The project extracts code-level HTTP and messaging facts, but has no explicit
integration-specification policy. The TypeScript `openapi.client` rule recognizes
generated-client code; it is not an OpenAPI document parser. The ce.v1 record
schema uses JSON Schema 2020-12. Neither fact establishes general OpenAPI,
AsyncAPI or payload-schema conformance.

Contracts can improve cross-repository visibility, but declared interfaces do not
prove deployment, actual traffic, compatible implementation or business meaning.
Similar topic names must not collapse separate resources automatically.

## Decision drivers

Deterministic offline analysis, revision-bound evidence, explicit uncertainty,
safe reference resolution, interoperability, incremental delivery and compatibility.

## Considered options

1. Infer interfaces from code and names alone.
2. Add versioned contract adapters alongside code extraction, preserving provenance.
3. Import an external inferred graph as the authoritative architecture.

## Proposed decision

Choose option 2. Retain extract → bind → evaluate and ce.v1 compatibility.
Contract declarations, code observations, human bindings and model proposals remain
distinct evidence classes. Any new record/relation requires schema review and
MADR-0007 compatibility assessment; do not invent unsupported ce.v1 fields.

### Standards register

| Standard | Reference baseline | Intended role and scope |
|---|---|---|
| OpenAPI | 3.2.1; retain explicit 3.2.0 recognition | HTTP interface declarations; first adapter candidate. Version-specific support matrix also covers encountered 3.0/3.1 documents without silently upgrading them. |
| AsyncAPI | 3.1.0 | Channels, messages, application-relative send/receive operations, replies and declared servers; second adapter candidate. Older versions require explicit adapters. |
| JSON Schema | Draft 2020-12 for ce.v1 | OpenAPI 3.2 defaults to its 2020-12-based dialect; respect `jsonSchemaDialect` and resource `$schema`. AsyncAPI 3.1.0 defaults to its Draft-07-based Schema Object, with alternatives selected by `schemaFormat`. Never reinterpret all payloads as 2020-12. |
| AsyncAPI protocol bindings | Individually pinned `bindingVersion` | Preserve Kafka/AMQP/MQTT/etc. protocol-specific metadata when actually supported. AsyncAPI core version does not pin binding versions. |
| CloudEvents | 1.0.2 specification baseline | Event envelope metadata, distinct from broker/channel identity and payload schema. Specification patch version is not the event's `specversion` value. |
| HTTP | RFC 9110 | Method and authority semantics; retain unknown server variables and unresolved deployment configuration. |
| gRPC / Protocol Buffers | Declared proto2/proto3 syntax or edition; parser version pinned per adapter | Candidate future service/message declarations. No target generators/plugins or analyzed builds execute. No claim of current support. |
| GraphQL | September 2025 edition as evaluated baseline | Candidate future SDL/operation adapter; no live introspection or resolver execution. No claim of current support. |
| BPMN / DMN | Separate scope decision in issue #12 | Process/decision models, not substitutes for transport contracts. Pin precise OMG versions in that workstream before implementation. |

OpenAPI's official latest reference was 3.2.1 on the research date; the user-linked
Swagger page still described 3.2.0. AsyncAPI's latest URL resolved to 3.1.0.
These are research-date baselines, not instructions to download moving versions.

### Interpretation and identity

Preserve repository, full revision, document path/hash, document version, JSON
Pointer and source range where available, adapter version and configuration hash.
Retain operation direction from the described application's perspective. Servers,
addresses, protocol, environment and binding evidence constrain resource identity;
channel/message display names alone never establish cross-repository equivalence.
Security schemes describe declared requirements, not proof of enforcement.
An operationId or schema title is not a globally unique resource identity.

Report declared-contract versus observed-code mismatches without choosing one as
truth. Unsupported features produce coverage diagnostics. Unresolved references,
schema alternatives, variable servers and dynamic addresses remain unresolved.
Model explanations may cite these records but cannot overwrite their facts or
silently adopt bindings. Human adoption retains reviewer and observed revisions.

### Security and repeatability

Parsing is offline and read-only. Do not fetch `$ref`, schema URLs, example URLs,
servers, callbacks or links from analyzed input. Resolve local references only
within the sealed export; external references require separately approved,
content-addressed inputs. Reject traversal and symlink escapes. Bound document
bytes, nesting, aliases, reference expansion and graph size; detect cycles without
infinite expansion. Reject duplicate mapping keys. No executable YAML tags,
repository plugins, target config execution or live service probes.
Resolve references relative to the appropriate document/resource base URI.
For future XML adapters disable DTD and external entity resolution.

Treat descriptions/examples as untrusted text; sanitize display, never execute
embedded HTML or instructions. Keep credentials and sensitive examples out of
derived graph metadata and logs; original source access remains separately guarded.
Canonical output must bind parser, dialect, input and resolver configuration.

## Consequences and acceptance

This records a direction, not delivered standards compliance. Each adapter needs
a separately claimed implementation scope, pinned parser/license review and a
supported/unsupported feature matrix. Validate fixtures for exact versions,
cross-file refs, cycles, hostile paths/YAML, schema dialects, operation direction,
server variables, duplicate names across repositories and declaration/code drift.
Include OpenAPI callbacks/webhooks and server overrides, AsyncAPI channel keys
versus addresses and per-binding versions. CloudEvents wire `specversion` is
`1.0`; its source/id event-instance identity is not a reusable contract identity.
Run twice against sealed inputs and compare canonical bytes; preserve existing
goldens and thresholds. Prove zero network and no analyzed-code execution.

Initial implementation order: OpenAPI read-only declarations, then AsyncAPI
read-only declarations; evaluate binding previews separately. Broader standards
remain backlog candidates. An owner-reviewed PR targets only
`stage-1-contract-and-fixtures`; acceptance of this MADR does not auto-merge code.

## Primary reference log

All accessible references checked 2026-11-01; specification URLs are versioned
where available. This is a desk review, not parser certification.

- [OpenAPI 3.2.1 authoritative specification](https://spec.openapis.org/oas/v3.2.1.html)
- [User-linked Swagger 3.2.0 page](https://swagger.io/specification/v3.2/)
- [AsyncAPI 3.1.0, including schema formats and bindings](https://www.asyncapi.com/docs/reference/specification/v3.1.0)
- [JSON Schema 2020-12](https://json-schema.org/draft/2020-12)
- [CloudEvents 1.0.2](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md)
- [AsyncAPI protocol bindings](https://github.com/asyncapi/bindings)
- [HTTP semantics](https://www.rfc-editor.org/rfc/rfc9110.html)
- [Protobuf editions](https://protobuf.dev/editions/overview/)
- [GraphQL September 2025](https://spec.graphql.org/September2025/)
- [BPMN/DMN owner scope issue](https://github.com/Sonberg/capability-explorer/issues/12)
