# Draft platform exchange envelopes

These are the evidence platform's **own** draft contracts for issue #33, separate
from OpenAPI/AsyncAPI documents discovered inside analyzed repositories. They do
not change [`ce.v1`](../README.md), implement a central provider or define public
HTTP endpoints. See [proposed MADR-0020](../../docs/adr/0020-platform-exchange-contracts.md)
and the [machine-readable catalogue](../../config/platform/exchange-contracts.json).

## Profiles and scope

[`v1/envelope.schema.json`](v1/envelope.schema.json) is one self-contained JSON
Schema Draft 2020-12 document. `schema: "ce.platform.v1"` and `kind` select a
closed profile. Validators must assert formats, not merely collect annotations.
Existing JsonSchema.Net 9.6.0 is exercised with `RequireFormatValidation = true`.

| Kind | Required boundary data | Does not establish |
|---|---|---|
| `enrollment` | Versioned enrollment/registry identity, repository/provider mapping, declared membership and producer-role IDs | Sender authentication, authority or registry acceptance |
| `evidence-bundle` | Exact repository/commit and bounded ce.v1 artifact metadata, producer-declared coverage, content digest and separate attempt metadata | Artifact availability, framing/digest correctness, completeness or publication |
| `intake-receipt` | Attempt/enrollment/content identities, timestamp and status-specific publication/error shape | Durable receipt, legal transition, actual validation or current generation |

The central representation should preserve every repository/revision and its
ownership/provenance. Aggregates reference their member selections; they do not
invent a single global Git revision. Product APIs and viewpoint projections are
catalogued design boundaries, not implemented by these schemas.

## Identity, coverage and publication

`repositoryId` retains the current model's meaning. Provider, authority and
`providerRepositoryId` are registry mapping inputs; `locator` is an alias, not a
replacement for immutable provider identity. One enrollment declares one explicit
membership. Multiple memberships, identity normalization and scope authorization
require a future registry policy and cross-document validation.

A bundle's `content` owns repository, commit, registry/enrollment version,
artifact descriptors and coverage. Descriptors retain `artifactSchema: "ce.v1"`,
unit path, extractor/version, configuration/input hashes, artifact hash and size.
Applied rule versions remain in the original artifact header and must be retained
when the future receiver verifies and stores those original bytes.

`coverage.status: "full"` forbids declared omitted units. A partial bundle may
have no entirely omitted unit when a referenced artifact itself is partial.
None of these declarations proves complete observation. Missing input is not a
deletion; explicit removal, un-enrollment and replay/delta semantics are future
provider requirements.

`received` and `validated` receipts are not published snapshots. Only `published`
permits and requires `publication`; rejected/quarantined receipts require errors.
These conditional shapes do not implement the state machine or check freshness.

## Draft digest choice

The proposed later algorithm is SHA-256 of UTF-8 RFC 8785/JCS canonical
`evidence-bundle.content`. Attempt IDs/timestamps and the outer `contentSha256`
are outside that subject. The schema checks only digest syntax. No JCS encoder,
byte hasher, signature or authenticity mechanism is delivered here.

Do not assume ordinary `JSON.stringify` or a .NET serializer alone provides JCS
property ordering/serialization. Cross-language vectors, duplicate-property
rejection, Unicode and numeric handling need qualification before external use.
Existing ce.v1 expression canonicalization/`irHash` is a separate contract.

## Bounds and unsafe input

The draft bounds metadata: 256 artifact descriptors, declared byte length from
1 to 16 MiB per artifact, 256 omitted-unit paths, 32 producer roles/errors,
512-character diagnostic messages and safe-integer version/generation values.
These are provisional profile bounds, not measured throughput or enforced
aggregate stream/decompression limits. Future intake needs its own actual-byte,
total-size, timeout, reference and source-verification limits.

Path fields are literal relative paths, not URLs. Traversal, absolute/drive paths,
backslashes, empty segments and control characters are rejected. Never URL-decode
them. Symlinks, physical containment, concurrent mutation and actual file content
cannot be checked by a JSON shape. HTTPS locators exclude credential/query/fragment
forms, but no locator is fetched and cross-field authority consistency remains a
future registry check. Diagnostic shape does not prove secret sanitization.

## Validation and evolution

```sh
make EXECUTION=sandbox test-platform-plan
make EXECUTION=sandbox platform-plan-check PHASE=architecture
make EXECUTION=sandbox platform-contract-check
```

The cheap Node checker checks catalogue/profile identity and rejects missing
boundaries, unsupported runtime claims, vocabulary overloads and changed draft
digest boundaries. The existing .NET suite evaluates actual schema behavior with
controlled examples and negative cases. `tests/fixtures/platform/` uses synthetic
placeholder hashes/identities, not actual enrolled repository findings.

`refinement`, `completion` and `all` are not successful global-runtime modes of the
plan checker. No full provider/consumer, product API, OpenAPI, multi-tenant, time
travel or authorization conformance claim follows from passing these checks.

Closed draft objects reject unknown fields. Even adding an optional field can
break old validators. Settle profile negotiation, compatibility and deprecation
before external publication; do not treat this draft family name as a released
product version. Original source facts stay separate from rule/human derivations
and model proposals throughout future product preparation and projections.
