# Draft evidence-platform exchange contracts, separate from extractor facts

* Status: accepted
* Date: 2026-11-02
* Related: [issue #33](https://github.com/Sonberg/capability-explorer/issues/33), [ubiquitous language](0002-ubiquitous-language.md), [integration declarations](0013-integration-contract-standards.md), [snapshots](0017-postgres-explorer-snapshots.md), [main-only PRs](0016-main-only-pr-integration.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: evidence-platform exchange design and first shape-contract package only.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:781751e0f9055f1c2a8d7fcf6241c1563c5beb3d5f7824f5e52d456fc6a5ecb7`.
Previous decision status (historical): `proposed for owner review; first shape-contract package authorized for review only`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem statement

Capability-explorer discovers evidence in repository sets. The target platform
also needs centrally retained repository representations, cross-cluster alignment,
prepared data products and controlled stakeholder projections. Existing ce.v1
artifacts and local snapshot/read mechanisms do not establish a deployed central
service, enrollment authority, multi-tenant API or product platform.

The operator's six-diagram HTML supplies architectural intent. Its implemented
labels, automatic merging and suggested transports/latencies are not proof of
this project's capabilities or accepted decisions. Its local file is not copied
into the repository. Issue #33 records the reference digest and derived proposal.

MADR-0013 concerns contracts declared by analyzed applications. This decision
concerns the evidence platform's **own** contracts. Likewise, project terminology
must not be confused with the analyzed-repository Glossary tab. Current
`Capability` means a selected repository set, not a business function or cluster.

## Decision drivers

Conservative evidence, independently identified repository revisions, reusable
products, explicit authority/uncertainty, bounded validation, compatibility,
frugal deployment and small owner-reviewed slices.

## Considered options

1. Expose current collector JSON/database/UI structures directly to all consumers.
2. Introduce all target services, storage engines and event infrastructure first.
3. Define separate draft exchange profiles and boundaries, then qualify providers
   and consumers incrementally without changing ce.v1.

## Proposed decision

Choose option 3 for owner review. Seven logical boundaries are catalogued in
[`config/platform/exchange-contracts.json`](../../config/platform/exchange-contracts.json):

| Boundary | Responsibility | First-slice status |
|---|---|---|
| Enrollment | Registry-issued identity, membership and producer-role input | Draft shape only |
| Evidence bundle | Repository/revision manifest referencing ce.v1 artifacts | Draft shape only |
| Intake receipt | Distinct receipt, validation, rejection/quarantine and publication shapes | Draft shape only |
| Alignment | Traceable, uncertainty-preserving derived findings | Design only |
| Product API | Logical versioned prepared-product contract | Design only |
| Viewpoint projection | Authorized aggregation of selected products | Design only |
| Team feedback | Separately scoped derived findings, never implicit write authority | Design only |

The central layer should retain each enrolled repository/revision independently;
cluster aggregates reference exact member selections and coverage. One logical
history does not mandate one physical database or a microservice per diagram box.
Consumers use product contracts rather than database tables or renderer IDs.
Local collection/reporting remains useful without central availability.

### First draft profile

Use one self-contained JSON Schema Draft 2020-12 at
[`schema/platform/v1/envelope.schema.json`](../../schema/platform/v1/envelope.schema.json),
wire family `ce.platform.v1`, with closed enrollment, evidence-bundle and
intake-receipt profiles. Reuse existing JsonSchema.Net 9.6.0 with explicit format
assertions. No new package/project/lockfile, production endpoint or central store
is introduced. Existing ce.v1 payloads, validators and protected goldens remain
unchanged. Controlled examples use synthetic placeholder identities/hashes.

An enrollment maps existing `repositoryId` to provider/authority/immutable provider
ID and a locator alias. One record declares one explicit membership; additional
memberships require separately identified records and a future registry policy.
Neither those fields nor role IDs authenticate the sender or grant access.

A bundle separates `content` from `publicationAttempt`. Artifact descriptors name
ce.v1, unit/extractor/config/input identities, path, digest and declared byte size.
Their metadata must later be checked against actual framed artifact bytes and
registry scope. Coverage is producer-declared; partial analysis is not deletion.

A published receipt requires publication metadata and no errors. Earlier states
cannot carry publication metadata; rejected/quarantined shapes require errors.
Schema-valid status text is not proof that durable storage, authorization,
validation, a legal transition or publication actually occurred.

### Proposed content identity and compatibility

For the later semantic provider, propose SHA-256 of UTF-8
[RFC 8785/JCS](https://www.rfc-editor.org/rfc/rfc8785) canonical `content` only.
Exclude `publicationAttempt` and the envelope's `contentSha256` itself. This is
**not implemented** by the schema or plan checker. Ordinary serializer output is
not automatically JCS. Before external use, test duplicate-name rejection,
Unicode preservation, numeric bounds and cross-language canonical byte vectors.
This choice does not redefine existing ce.v1 `irHash` or analysis keys.

The profiles are closed drafts, not a stable external API release. Adding an
optional field can still break an older closed validator; do not call it backward
compatible without negotiated profile/version and consumer conformance evidence.
Follow MADR-0006/0007 for actual release notes and compatibility decisions; do not
invent a product release number for this review package.

## Security and evidence limits

Shape validation cannot verify referenced bytes, prove evidence semantics,
authorize enrollment/access, detect contradictory metadata across documents,
implement idempotence/order/CAS, or guarantee sanitization. These remain explicit
future obligations in the catalogue. No arbitrary submitted URI is fetched.
Do not accept model proposals, name similarity or authored declarations as proof
of deployed integrations. New bindings/derivations retain original input vectors,
rules and reviewer identities rather than rewriting observations.

## Delivery and validation

`make EXECUTION=sandbox test-platform-plan` checks catalogue integrity and negative
cases using the existing Node image. Actual JSON Schema shape tests run in the
existing Contracts.Tests suite; `make EXECUTION=sandbox platform-contract-check`
restores and builds only the selected contract-test project. This is not OpenAPI certification or a
provider/consumer service test. Global runtime/refinement/completion claims remain
incomplete; the plan checker rejects borrowing foundation success for them.

Next slices decide artifact transfer/authentication before selecting HTTP
endpoints/OpenAPI profiles, then implement intake/last-good storage, product
preparation and authorized projections. Podman portability is separate issue #34.
Every source PR targets fresh main and normal local act/security; the owner merges.
No automatic merge, deployment, private enrollment or cross-repository write
authority is granted by this decision.

## Consequences

Downstream ownership and wire assumptions become reviewable before infrastructure
commitments. Some useful target capabilities remain deliberately unimplemented.
The first profile can evolve during review; broader integration and scale claims
require later executable evidence against exact source, inputs and runtime.
