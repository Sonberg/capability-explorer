# Preserve uncertainty while improving deterministic repository mappings

* Status: accepted
* Date: 2026-11-01
* Related: issue #6, MADR-0002/0005/0006/0007/0008

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for Preserve uncertainty while improving deterministic repository mappings; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:79da510d717342478bb58310d3b22a5d1243703adc5f0cef280da72d6f493211`.
Previous decision status (historical): `applied in this workstream; proposed for owner review`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem

Static repository analysis currently loses Next.js route handlers, puts some
absolute HTTP URLs inside route paths, and selects C# package assemblies using a
fixed framework preference rather than the analyzed project's target. Fixing
visibility must not manufacture service bindings or hide incompatible references.

## Decision drivers

Source evidence, reproducibility, explicit uncertainty, bounded offline analysis,
existing ce.v1 contracts, small dependencies and reviewable multi-developer work.

## Considered options

1. Execute analyzed builds/framework configuration and infer service equivalence.
2. Extend the existing static extractors with explicit supported cases and diagnostics.
3. Keep the omissions and repair mappings manually downstream.

## Decision outcome

Choose option 2. Keep the existing extract → bind → evaluate pipeline. No analyzed
build scripts, Next configuration, application code or model inference execute.

Use pinned NuGet.Frameworks for compatible framework reduction and NuGet.Versioning
for cached version constraints instead of hand-maintained compatibility/range parsers.
Cached versions must satisfy complete bounds, including exclusivity and exact pins;
prereleases require an explicit prerelease bound. Invalid/missing/floating unlocked
requests remain unresolved. Bounded deterministic rounds select cached versions
satisfying all reachable requests; empty intersections omit the package's compile
assets with a diagnostic. Non-convergence fails closed. This is not full NuGet
restore, nearest-wins emulation or MSBuild evaluation.
Read literal project targets, select compatible
package compile assets and exact-target installed reference packs. Prefer compatible
ref assets to lib assets. Unsupported project metadata, missing packs/assets and
conflicting assembly identities produce coverage diagnostics. Equal assembly bytes
can be deduplicated; conflicting package assemblies cannot be silently chosen by
filesystem order. Narrow signed shared-framework overlap rules are distinct from
arbitrary package conflict resolution. This remains bounded static analysis, not a
replacement for MSBuild or full NuGet restore. Package lock format remains v1.
Selected framework packs may supply dependencies pruned by .NET restore. Recognize
that only from the selected pack's versioned PackageOverrides data and supported
version constraints; hash that metadata with resolution inputs. Apply the SDK's
stable .NETCoreApp pruning ceiling of major.minor.32767 only to this metadata
pathway, never to cached package-version bounds. Missing metadata,
unknown constraints or requirements above the supplied ceiling remain unresolved.
Do not infer package coverage merely from an assembly or package name.

Discover static exported Next app-router handlers in app/ or src/app/. Route groups
are structural, dynamic segments retain parameter metadata, and catch-all versus
optional catch-all remain distinct. Unsupported handler construction, configuration
and parallel/interception routes disclose incomplete coverage. Do not synthesize
framework-generated HEAD/OPTIONS handlers or infer a proxy's destination.

Normalize HTTP URI authority separately from path. An absolute target overrides a
static base URL; relative C# typed-client targets follow URI resolution. Preserve
encoded reserved separators, normalize default ports and dot segments, and reject
unknown/malformed authorities. Host hints remain unbound configuration evidence;
they do not identify a deployed service. Existing host-only identity grammar is
preserved. Derived metadata contains query names, never credentials/query values/
fragments. Evidence still points to original source, which may itself contain
sensitive literals; this is not source-code redaction.

Bind analysis identity to source/configuration and selected reference bytes.
Replay uses a fixed synthetic corpus, immutable content manifests, known offline
extractor commands, two runs per build, schema/evidence checks and exact reviewed
semantic deltas. Missing inputs or unexpected changes fail closed. Unit tests
cover package/framework resolution independently of the HTTP/Next replay. Synthetic
proof does not establish coverage for an unavailable private repository corpus.

## Consequences and validation

Better mappings can intentionally change artifact facts and config hashes without
changing ce.v1. Treat the additive Next support as minor-release material and HTTP/
reference corrections as fixes under MADR-0007; do not publish or bump a product
version in this PR. Add user-facing notes under Unreleased per MADR-0006.

Keep fixture sources, revision IDs, golden artifacts and evaluation thresholds
unchanged. Target stage-1-contract-and-fixtures only, reconcile it before local CI,
run the ordinary act/Trivy gate, and request owner review. Separate worker worktrees
and exact path claims prevent concurrent edits. See the execution plan for measured
validation and explicit limitations, rather than assuming this decision proves tests.

## Primary references

- [.NET package pruning](https://devblogs.microsoft.com/dotnet/nuget-package-pruning-in-dotnet-10/)
- [SDK package-pruning implementation](https://source.dot.net/Microsoft.NET.Build.Tasks/GetPackagesToPrune.cs.html)
- [NuGet framework selection](https://learn.microsoft.com/en-us/nuget/reference/target-frameworks)
- [NuGet.Frameworks package](https://www.nuget.org/packages/NuGet.Frameworks/7.9.0)
- [Package/framework assembly compatibility](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/nuget-package-compatibility-rules)
- [Next route handlers](https://nextjs.org/docs/app/api-reference/file-conventions/route)
- [Next dynamic segments](https://nextjs.org/docs/app/api-reference/file-conventions/dynamic-routes)
- [Axios request configuration](https://axios-http.com/docs/req_config)
- [System.Uri](https://learn.microsoft.com/en-us/dotnet/api/system.uri)
- [HTTP URI semantics, RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html)

Brain workflow grounding: ruvnet/sparc `docs/tutorial.md` and `docs/API.md`, retrieved
2026-11-01, support independent, source-bound phase evidence. They are workflow
references, not implementation evidence for NuGet, Next.js or this repository.
