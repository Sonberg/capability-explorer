# Version the product against an explicit compatibility contract

* Status: applied to this operator's work; proposed for repository-wide adoption
* Date: 2026-11-01
* Decision owner: repository owner and contributors
* Related: [MADR-0006](0006-keep-a-changelog.md), [ce.v1 contract](../../schema/README.md)

## Context and problem

The private TypeScript extractor and Explorer UI manifests currently say 0.1.0;
.NET build properties do not declare an authoritative product release version.
No GitHub releases or local tags were found during this inspection. These facts
do not establish that the product has been released as 0.1.0 or 1.0.0.
The artifact protocol identifier `ce.v1` is distinct from the product version.

## Decision drivers

Predictable upgrades, immutable evidence, explicit compatibility boundaries and
owner-controlled releases without speculative manifest/version churn.

## Considered options

1. Infer a product version from a private component manifest or schema identifier.
2. Adopt Semantic Versioning 2.0.0 for coordinated product releases and document
   the public compatibility surface.
3. Use dates or commit IDs as the only release versions.

## Decision outcome

Choose option 2. Use MAJOR.MINOR.PATCH: incompatible public changes require a
major increment, compatible features/deprecations a minor increment, and
compatible fixes a patch increment after 1.0. Reset lower components when
incrementing higher ones. Released contents are immutable. Prereleases use a
hyphen suffix and precede their normal release; build metadata does not affect
precedence. Declare the public API before 1.0.0.

For this project, the release review must inventory and test:

| Compatibility surface | Project obligation |
|---|---|
| Artifact format | JSONL framing/schema, relation catalogue, symbol identity, canonical IR/hash rules and accepted evidence semantics |
| CLI and configuration | Documented commands, flags, exit behavior, configuration files and machine-readable output |
| Explorer HTTP interfaces | Documented request/response shapes, scope/revision selection and error behavior |
| Stored data | Supported upgrade paths for imported artifacts, bindings and reviewed annotations; append migrations rather than rewriting applied ones |

This ADR makes those surfaces review obligations; it does not falsely assert
that they already have exhaustive compatibility tests or stable 1.0 guarantees.
Internal classes and explicitly experimental spike layouts are not public APIs.
Label experiments clearly; their outputs must still preserve declared evidence.

While development remains 0.y.z, SemVer permits instability. Our stricter project
convention is to increment the minor for breaking changes or new features and
patch for compatible fixes; document every break and migration. Existing ce.v1
validation/identity promises remain in force even before product 1.0.
A correctness fix may change newly extracted facts without redefining the artifact
format; retain old artifact bytes and ensure changed extractor/config inputs
cannot collide with an existing analysis identity. Review that distinction.

The owner selects the first actual release and its supported surfaces. At that
point, add one authoritative product version source and validate release metadata
against it. Private package placeholders need not move in lockstep unless shipped
as separately supported artifacts. A new protocol version requires its own
migration/compatibility decision, not an automatic rename on every product bump.

## Consequences and verification

No tag, release, manifest bump or publishing workflow is created by this ADR.
Every PR records compatibility impact and intended bump category (or no release
impact); release review takes the strongest applicable change. Reviews must cite
contract/golden, API/CLI and migration checks relevant to the changed surface,
plus the normal stage-aligned local gate. Establish missing compatibility tests
before claiming support. Human owner approval remains necessary for publication;
CI success and PR approval are not release authorization.

## Sources

* [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html)
