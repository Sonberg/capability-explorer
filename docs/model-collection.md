# Offline process and decision evidence

This delivery separates authored `ce.models.v1` evidence, non-executable
`ce.candidates.v1` interpretations and the negotiated `ce.collection.v1`
snapshot. Native `ce.v1` is unchanged. See [MADR-0033](adr/0033-bounded-model-and-collector-evidence.md).
None of these profiles establishes business approval or executes a process,
decision, expression or analyzed source.

## Local fixture models

`make explore` and `make dev` prepare the four known synthetic files under
`fixtures/models` as a local, validated collection at a content-keyed path under
`.sandbox-cache/model-fixtures/`. Extraction uses the bounded model
CLI and preserves the original source only under an explicit `model-content`
permission recorded in the collection plan. A deterministic synthetic Git
revision binds the fixture bytes; both collection validators run before the
Explorer starts. The content-keyed directory avoids replacing a prior immutable
collection when a fixture or extraction recipe changes. Set `MODEL_COLLECTIONS`
to another path under `.sandbox-cache/model-fixtures` to select the output
location.

The fixture path is limited to the named BPMN and DMN files in `fixtures/models`;
it does not crawl local repositories, execute models, publish model products to
PostgreSQL, or embed them. The normal artifact import/embed steps in `make dev`
are unchanged. Run `make test-model-fixture-autoload` to verify both model kinds
are listed and their captured evidence/source are viewable in the ordinary
files-mode Explorer.

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
To retain an original for read-only viewing, explicitly add
`--source-out .sandbox-cache/order-source.json` together with
`--content-profile model-content`. This produces the separate closed
`ce.model-source.v1` attachment; it does not change `ce.models.v1`.
List both artifacts in the collection plan under their own profiles and the
same repository/revision/unit path, with exact byte lengths/hashes and
`contentProfile: "model-content"`. The repository must explicitly permit that
exact content profile. Older collection consumers refuse the unknown included
source profile; evidence-only collections remain readable.

The attachment retains canonical base64 of the original UTF-8 bytes, including
a BOM, with a 1 MiB raw-source cap. It binds the exact companion artifact bytes
and source digest; both consumers independently verify those hashes and the
repository/revision/path crosslink. DTDs/entities, malformed XML/UTF-8,
noncanonical base64 and mismatched or missing companions are rejected.
No generated layout replaces the original. Older evidence does not gain
original-source availability merely because it contains a source hash.
The model CLI accepts BPMN/XML and DMN files, rejects unsafe XML and bounds
bytes, structure and worker time/memory. Default structural output omits
free-form model/expression text but retains supported identifier references.
Attributes may still contain source content; it is **not** an
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

Standard BPMNDI/DMNDI/DC/DI namespaces are distinguished from vendor extensions.
The explicitly censused diagram, shape, bounds, edge and waypoint constructs
are structurally retained, not diagram-conformance or layout-semantics
validation. Other elements in those namespaces remain unsupported rather
than mislabeled vendor extensions; unknown namespaces remain opaque/partial.
Optional BPMN incoming/outgoing declarations are checked against local
sequence-flow endpoints and never create flows. Dangling, contradictory and
misplaced declarations reject the input rather than inventing relationships.
DMN input/output allowed-value declarations retain raw text only with
`model-content`; no FEEL expression is executed.

The table view associates those declarations with their input/output columns.
Missing declarations differ from present declarations with empty/uncaptured
text; absent text does not establish a reason or an unrestricted business
domain. Diagnostics group code, disposition and identified construct, show
total occurrences and expose each original message/locator through native
keyboard-accessible disclosures. A smaller diagnostic count is not evidence
of business completeness. See the [current artifact flow](architecture/current.md)
and [wiki synopsis](wiki/model-quality.md).

Selected installed parser review confirmed `saxen@11.2.0`,
`bpmn-moddle@10.3.1` and `dmn-moddle@12.2.1` against locked versions/integrity
and their shipped MIT license files. Shipped descriptor comparison identifies
the explicitly supported subset in the census, not full standard coverage;
the subsequently qualified layout and declaration constructs above do not
establish support for other types. This is a selected-package review, not a full transitive
legal audit. The locked install verifies archive integrity; installed-file and
descriptor hashes are retained in the qualification evidence.

Focused XML cases cover malformed/truncated input and exact accepted/one-over
byte, depth, element, attribute-count, UTF-8 attribute-byte and text-byte limits.
The real `.bpmn20.xml` CLI path preserves BOM-inclusive source bytes. The worker
deadline defaults to five seconds; a caller may only shorten it, not raise the
cap. Timeout rejection awaits actual worker termination; termination failures
remain explicit. This is worker/resource bounding, not OS isolation.

Code candidates use the bounded synchronous, single-file intra-method recipe
and reviewed scalar/orchestration fixtures in `fixtures/candidates`. Unsupported
methods and other executable declarations make coverage partial. The scalar
decision recipe is deliberately restricted to qualified integer/boolean
predicates and literal results; this is not arbitrary C# or business-process
equivalence. Candidate predicates/source locations are content-bearing.

Current independent inspection found no semantic mismatch in the synthetic
recipe. Follow-on tests cover exact length-10/11 boundaries, validation before
eligibility, and retained exception constructor arguments. This is present-day
qualification: the historical claim that expected outcomes were reviewed before
extraction is unproven. Issue #75 remains open without evidence or an explicit
acceptance-owner disposition; neither a commit message nor the oracle's status
string independently proves that chronology.

## Seal and consume a snapshot

The versioned collection plan declares repository/provider/producer/tenant/rights
references, exact commit/tree, recipe/configuration/policy hashes and literal
relative input paths with expected hashes, byte lengths and required/optional
dispositions. Do not put credentials, personal Git author data or raw model XML
into it. Paths resolve against the plan directory, not an arbitrary remote URI.
Extraction changes require a fresh recipe/configuration digest; do not reuse an
old immutable identity for different bytes. The executable dogfood recipe binds
model/collection source, the support census, lockfile, image inputs and built
producer/consumer assemblies; production callers own their exact input binding.

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
The aggregate artifact read budget is 128 MiB, including unsuccessful
size/hash checks. Later inputs that cannot fit are not opened; optional ones
remain partial and required ones prevent completion. Per-file reads are also
capped by the remaining budget, with at most one overflow sentinel byte before
an error aborts the operation. Staged verification has its own bounded pass.
An identical, byte-verified target is reused; a conflicting or corrupted target
is not replaced. Required failures do not produce a completed collection.
Optional failures remain explicit partial dispositions. Attempt observation
time/identity is not the immutable content hash, and validation is not publication.
Focused TypeScript consumer cases validate changed attempt metadata against an
unchanged content hash and explicit optional-failure dispositions. Existing .NET
producer tests cover required-input failure, staging cleanup and pre-cancelled
export. Pre-cancellation does not establish mid-export cancellation or arbitrary
crash recovery; see the [acceptance mapping](plans/collector-handoff-acceptance.md).
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
Candidate checks compare exact committed `git show` bytes, BOM-inclusive digest,
revision/path and nested UTF-16 spans/predicates, before sealing and after
collection. The denial case removes only `candidate-content`, keeping
`model-content` permitted. Additional native transport probes test resealed
permission/profile/revision/path/byte failures through both consumers.
These are synthetic source-binding proofs, not a guarantee that arbitrary
caller-declared commits belong to a trusted remote repository.
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

## Local Explorer model products

Start file mode with explicit `--collections <sealed-directory>[,<directory>]`
alongside the normal native `--artifacts` inputs. Collections are validated
before the combined native/model/capability snapshot is published in memory.
Model requests read only those immutable validated bytes, never repository
paths or arbitrary files supplied through HTTP.

For PostgreSQL, run the normal trusted migration command and use
`ce publish --collections <sealed-directory> --scope <capability-id|*>`
with optional native JSONL arguments. Model-only publication is supported.
Exact model/source products and collection-bound source dispositions become
immutable membership in the same compare-and-swap analysis snapshot as native
artifacts. Repository defaults/revision choices cover both kinds of membership.
Importing more products cannot add them to an already published snapshot.
Local collection permission declarations remain operator assertions, not
authenticated rights or authority to expose the trusted-local server remotely.

`GET /api/models` returns summaries only, default 20 / maximum 50 items, with
a 1 MiB serialized ceiling. Continue with the returned opaque cursor plus
snapshot/generation and unchanged capability/revision scope.
`GET /api/models/evidence?id=...` and `/api/models/source?id=...` require that
same snapshot/generation/scope. Selected evidence is capped at 16 MiB including
its wrapper; source at 1 MiB original bytes / 8 MiB serialized response.
All successful responses echo snapshot, numeric generation and selection.
Stale scopes fail with 409, invalid cursors with 400, denied source with 403,
missing source/IDs with 404, invalid stored products explicitly, and oversized
responses with 413. No successful truncation or unverified-source fallback is
performed. Model IDs are content-bound and identical between modes for the
same exact artifact inputs; opaque IDs and cursors do not grant authorization.

The fixtures are synthetic, not a representative private business corpus.
Filesystem descriptor/no-follow checks and atomic rename reduce aliasing/races
but do not qualify an adversarial concurrently changing filesystem. No viewer,
editor, model execution, business approval, central deployment or release
certification is included.
