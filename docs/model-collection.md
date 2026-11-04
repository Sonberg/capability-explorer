# Offline process and decision evidence

This delivery separates authored `ce.models.v1` evidence, non-executable
`ce.candidates.v1` interpretations and the negotiated `ce.collection.v1`
snapshot. Native `ce.v1` is unchanged. See [MADR-0033](adr/0033-bounded-model-and-collector-evidence.md).
None of these profiles establishes business approval or executes a process,
decision, expression or analyzed source.

## Extract locally

Use the existing pinned sandbox profiles and build preparation:

```sh
make EXECUTION=sandbox build
scripts/sandbox --offline node node extractors/ts/src/models/cli.ts \
  --source fixtures/models/order.bpmn --repository sales \
  --commit 0123456789abcdef0123456789abcdef01234567 \
  --out .sandbox-cache/order-model.json --content-profile model-content
scripts/sandbox --offline dotnet dotnet \
  platform/src/CapabilityExplorer.Extractors.CSharp/bin/Debug/net10.0/ce-cs.dll \
  extract-candidates --source fixtures/candidates/reviewed.cs --repository sales \
  --commit 0123456789abcdef0123456789abcdef01234567 \
  --out .sandbox-cache/code-candidates.json
```

The SHA above is a synthetic example; real collection must use the exact source
revision and matching enrollment/plan, not copy that value. These commands do
not acquire or verify a remote repository's HEAD. Model input bytes remain local.
The model CLI accepts BPMN/XML and DMN files, rejects unsafe XML and bounds
bytes, structure and worker time/memory. Default structural output omits model
text, but attributes may still contain source content; it is **not** an
automatically safe external-export format.

The qualified model profile is BPMN 2.0.2 plus fixture-qualified DMN 1.3, not all
elements or all standard semantics. BPMN's shared unversioned namespace does not
prove which revision authored a document. Unknown constructs and unresolved
external references remain visible partial coverage. External references are
never fetched. Foreign-namespace constructs cannot become core flow facts.
DMN rule/cell order is retained; no FEEL interpreter or engine is included.
The machine-readable construct census is `schema/models/support.json`; rich
fixtures qualify lanes, nested processes/local calls and DMN input/BKM/
knowledge dependencies, types, expression language and aggregation.

Code candidates use the bounded synchronous, single-file intra-method recipe
and reviewed scalar/orchestration fixtures in `fixtures/candidates`. Unsupported
methods and other executable declarations make coverage partial. The scalar
decision recipe is deliberately restricted to qualified integer/boolean
predicates and literal results; this is not arbitrary C# or business-process
equivalence. Candidate predicates/source locations are content-bearing.

## Seal and consume a snapshot

The versioned collection plan declares repository/provider/producer/tenant/rights
references, exact commit/tree, recipe/configuration/policy hashes and literal
relative input paths with expected hashes, byte lengths and required/optional
dispositions. Do not put credentials, personal Git author data or raw model XML
into it. Paths resolve against the plan directory, not an arbitrary remote URI.

```sh
scripts/sandbox --offline dotnet dotnet \
  platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll \
  collect --plan .sandbox-cache/plan.json --out .sandbox-cache/collection
scripts/sandbox --offline dotnet dotnet \
  platform/src/CapabilityExplorer.Cli/bin/Debug/net10.0/ce.dll \
  validate-collection .sandbox-cache/collection
scripts/sandbox --offline node node extractors/ts/src/collection/cli.ts \
  validate .sandbox-cache/collection
```

Every included artifact requires its matching explicitly listed
`native-content`, `model-content` or `candidate-content` permission for that repository;
default collection refuses them rather than guessing that generic JSON is safe.
Explicit local permission is not authenticated central authorization and does
not override credential restrictions. Use the actual plan schema and executable
dogfood generator, not the synthetic command examples as production enrollment.

Output contains `collection.json`, bounded `artifacts/` and separate attempt
metadata. Both consumers independently verify manifest identity and actual
artifact bytes. Export stages and validates before an atomic directory rename.
An identical, byte-verified target is reused; a conflicting or corrupted target
is not replaced. Required failures do not produce a completed collection.
Optional failures remain explicit partial dispositions. Attempt observation
time/identity is not the immutable content hash, and validation is not publication.
The manifest declares its profile-specific canonicalization recipe. Both
consumers use ordinal keys and fully escaped UTF-16 code units, without Unicode
normalization; artifact bytes remain unchanged. This is not an RFC 8785 claim.

## Freshness and executable dogfood

`ce collection-freshness --previous <vector.json> --current <vector.json>`
compares complete observed repository revisions/trees, source/dependency hashes,
recipe, configuration, enrollment, rights and profiles. Exit `0` means matching
observed inputs; `1` reports stale/unknown, and `2` rejects invalid usage.
Incomplete enumeration cannot authorize reuse. A matching vector is not proof
that a remote source or external authority has remained unchanged since
observation. Manual/CI observation is the initial trigger; no real-time SLA or
paid daemon is implied.

That two-vector mode is an input comparison, not a time-qualified freshness
receipt. Add both `--as-of <ISO-8601-offset-time>` and
`--policy <observation-policy.json>` for explicit temporal evaluation. The policy
file supplies `observedAt`, `collectedAt`, `attemptId`, optional
`sourceEffectiveAt`/`publishedAt`, `policyId`, `policySha256`,
`maxAgeSeconds`, and an optional `lastSuccessful` reference (`attemptId`,
`collectedAt`, `inputVectorSha256`, `bundleSha256`). Missing age policy is unknown;
the exact expiry boundary is stale. Reserved policy ID `rights` must match the
observed rights hash. No clock is read by this evaluation. Observation age is
not an invented policy-creation time; the clock and policy remain caller assertions.
Evaluating a failed attempt never replaces the supplied last-successful receipt.

```sh
make EXECUTION=sandbox test-collection-dogfood
```

This target uses two temporary synthetic Git repositories and the actual
shipped model, code-candidate and collection CLIs in separate offline pinned
Node/.NET containers. It checks both consumers, repeat identity, a committed
source mutation, denied content, tampered bytes, last-good preservation,
policy expiry/last-successful references and freshness/argument failures.
It does not need a database, Explorer, Docker
socket inside analysis containers or a central service. It runs in the existing
canonical CI graph, not an additional hosted job. Its source hashes are part of
the reviewed expanded frugal-CI plan, so changing the harness requires explicit
plan review. Development `.spec.ts` command probes are additional native
qualification; they do not replace the pinned sandbox gate.

## Stakeholder handoff and limitations

The two documents under `schema/openapi` describe proposed central intake/receipt
and immutable process/decision read boundaries using OpenAPI 3.2.1 and shared
local schemas. Follow their design-only metadata and tooling-qualification
notes. They are consumable design artifacts, not running providers.

Central contract ownership, authenticated enrollment, identity/authorization,
operational freshness guarantees and deployment remain downstream decisions.
No anonymous-provider access is authorized. `received`/`validated` are not
`published`; publication needs explicit metadata and its own authority.
Existing Explorer and PostgreSQL paths are retained.

The fixtures are synthetic, not a representative private business corpus.
Filesystem descriptor/no-follow checks and atomic rename reduce aliasing/races
but do not qualify an adversarial concurrently changing filesystem. No viewer,
editor, model execution, business approval, central deployment or release
certification is included.
