# Offline collection exchange

`ce.collection.v1` is an immutable offline handoff profile, separate from the
existing `ce.platform.v1` contract. `plan.schema.json` describes an explicitly
bounded local export plan; `collection.schema.json` describes the manifest and
attempt metadata. The OpenAPI files under `schema/openapi/` are design-only
central-intake and product-read contracts, not deployed services.

## Export and validation

The .NET library exposes:

```csharp
CollectionExporter.ExportAsync(planFile, outputDirectory, cancellationToken)
CollectionExporter.ValidateAsync(bundleDirectory, cancellationToken)
```

The static methods return `CollectionOperationResult` (`IsValid`,
`ContentSha256`, `Problems`, `Reused`). A bundle contains `collection.json`,
`attempt.json`, and profile-addressed artifact files. The content digest is
canonical and excludes attempt timestamps, so identical content can be reused
without conflating distinct audited attempts. Output is staged and atomically
renamed; conflicts and failed required inputs do not replace a last-good
bundle. Optional failures are represented as partial results.

The explicit canonicalization recipe is
`ce.collection.v1/utf16-escaped-json`, not RFC 8785. Hash the recipe ID, NUL,
then ASCII JSON with ordinal UTF-16 key ordering, every string code unit
escaped as lowercase `\uXXXX`, ordered arrays, safe integers in shortest
decimal form, booleans and null. Do not normalize Unicode. Duplicate members,
unpaired surrogates and excessive depth are refused. Only closed collection
metadata is canonicalized; producer artifacts retain their exact byte hashes.
Canonical hash input is bounded after escaping.

Plans use literal relative input paths, exact repository/commit/tree identity,
declared lengths and SHA-256 digests, explicit required/optional disposition,
and producer/profile identity. Reads, counts and bytes are bounded. URI
retrieval, traversal and symlinks are not accepted. The independent TypeScript
validator recomputes bundle and artifact digests and can be run with
`node src/collection/cli.ts validate <directory>` from `extractors/ts`.

Native `ce.v1` payloads use the existing artifact validator. Every included
artifact is conservatively content-bearing, including structural model output
and native opaque properties. Its exact matching `native-content`,
`model-content` or `candidate-content` permission must be declared for that
repository. Both consumers scan every native JSONL line. The collector does not export
raw BPMN/XML, source expressions, credentials, or personal Git author data by
default. A local plan's rights/tenant/provider references are declarations,
not proof of authorization by a central authority.

Both consumers validate complete nested authored/candidate payloads against the
single-source producer schemas, in addition to collection identity and byte
checks. The API aliases reference those schemas rather than maintaining a
weaker common shape. Unknown profile roots are rejected. Do not treat successful
collection validation as central receipt, publication, or authorization.

## API design and validation status

Both OpenAPI documents declare `openapi: 3.2.1`, omit production servers and
security schemes, and are explicitly marked design-only. Central intake
submission and receipt lookup are proposals; a received or validated receipt
does not mean published. Publication requires separately authorized metadata.
Product reads describe immutable authored/candidate evidence, coverage,
freshness and pagination. Authentication, ownership, retention and deployment
remain unresolved blockers; absent OpenAPI security is not permission for
anonymous access.

The repository pins local official OpenAPI 3.2 schema assets to avoid remote
resolution. The focused AJV 8.20 test validates each complete document against
the official OpenAPI 3.2 structural schema after a guarded in-memory
equivalence rewrite: for the exact SHA-256-pinned schema asset, it verifies
that the only `meta` dynamic anchor is `#/$defs/schema`, all five dynamic
references are `#meta`, and every string `$ref` is local and resolves. It then
rewrites those references to `#/$defs/schema` on a cloned schema only. The
vendored asset and OpenAPI documents are never modified. The test also rejects
an OAS 3.1 version, malformed required parameter fields, and broken local
document references; it separately rejects `servers` and `security` as
design-boundary violations (those fields are not claimed to be invalid OAS).

This qualifies full-document structural validation of these documents with
that precisely guarded workaround. It does **not** qualify AJV as an engine
for the OpenAPI 3.2 `schema-base` / Schema Object dialect. Component schemas
are separately compiled as Draft 2020-12 with local references and validated
against representative payloads. Do not treat those checks as complete
Schema Object-dialect qualification. No 3.1 fallback or custom subset is used;
deployment still requires resolved ownership, security, and authorization
decisions.

## Decision record

- **Evidence:** Existing `ce.platform.v1` is a closed contract, so collection is
  an additive `ce.collection.v1` profile rather than a mutation.
- **Privacy and failure analysis:** Content-bearing producer artifacts can
  include expressions or source details. Export therefore requires explicit
  content-profile permission, validates bytes and identity, rejects unsafe
  paths, and protects the last-good output on required-input failure.
- **Interoperability:** Canonical content identity is independent of attempt
  time, with cross-runtime tests covering Unicode, U+2028, non-BMP characters,
  `<`, `+`, and equivalent integral number spellings.
- **Validator qualification:** Complete-document OAS 3.2 structural
  validation uses the hash- and structure-guarded AJV rewrite described above.
  The `schema-base` Schema Object dialect remains unqualified. The APIs remain
  design-only pending deployment authority and security decisions.

These are implementation decisions supported by the tests and validator
probes, not independent council votes.
