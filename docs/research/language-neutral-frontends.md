# Language-neutral frontends and layout-independent discovery

Research date: 2026-11-03. Status: bounded proposal for owner review; no runtime
adoption, new-language selection, parser installation, private-source inspection or
semantic-performance claim. Exact local source snapshot: `3d0ff8813df60b07ac86c8220b811be3debe5401`.

## Recommendation

Keep the canonical semantic facts language-specific; define a versioned, typed,
source-bound adapter boundary and a separate deterministic analysis-unit
discovery result. Preserve the existing `ce.v1` schema and vocabulary. Treat
compiler APIs, Tree-sitter, SCIP indexers and controlled LSP sessions as
alternative fact sources whose output quality and operating risk must be
qualified per pinned implementation—not as interchangeable parsers.

Do not select a new language or implement a production scanner from this research.
Use a pinned offline registry and snapshot-bound policy in any later pilot. No
language, service, runtime dependency, schema migration or analyzed-build
execution is authorized. The proposed schema and MADR remain review drafts.

## Exact-source observations

All local citations below resolve at the exact source commit above.

| Observation | Evidence | Consequence |
|---|---|---|
| `ce.v1` header language is the closed set `csharp`, `typescript`, `javascript`, `sql`, `config`; the header schema is strict. | [`record.schema.json`](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/schema/v1/record.schema.json#L61-L79) | Adding a language value to `ce.v1` would make old validators reject it. |
| The .NET `HeaderRecord.Language` property is a `string`, but records are deserialized only after schema validation. | [`Records.cs`](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/platform/src/CapabilityExplorer.Contracts/Records.cs#L6-L21) | Registry indirection cannot make an old validator accept a value its embedded schema rejects. |
| Current TypeScript discovery walks to depth 12, skips a local set of directories, discovers `tsconfig.json` and unconfigured JavaScript `package.json`, sorts config paths, applies compiler JSON parsing, filters source files and optionally excludes tests. | [`project.ts`](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/extractors/ts/src/extract/project.ts#L19-L43) and [unit discovery](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/extractors/ts/src/extract/project.ts#L62-L94) | This is useful per-extractor policy, not a shared inventory contract; unreadable directories and depth cutoff are not the proposed fail-closed budget semantics. |
| C# discovery recursively enumerates `.csproj`, filters a separate ignored-directory list, sorts paths, and reads project XML as data; MSBuild is explicitly not evaluated. | [`ProjectModel.cs`](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/platform/src/CapabilityExplorer.Extractors.CSharp/ProjectModel.cs#L12-L15) and [discovery](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/platform/src/CapabilityExplorer.Extractors.CSharp/ProjectModel.cs#L56-L68) | Existing safety practice is to avoid executing analyzed project logic; it does not supply cross-language budgets or unit identity. |
| Corpus replay separately enumerates C# project paths. | [`corpus-replay-lib.mjs`](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/scripts/corpus-replay-lib.mjs#L62-L66) | Discovery policy is already duplicated outside extractor code. |
| `RepositorySource` identifies a repository and ref; `CapabilityDefinition.IncludeTests` is a coarse selection. | [`Definitions.cs`](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/platform/src/CapabilityExplorer.Capabilities/Definitions.cs#L11-L18) | This is not a manifest of owned units, file sets, workspace links or discovery budgets. |
| The artifact is one JSONL file per analysis unit; schema plus relational validators govern it. | [`schema/README.md`](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/schema/README.md#L5-L14) | A new internal adapter exchange must not be confused with a second implementation of `ce.v1`. |
| #33 proposes separate platform exchange profiles and expressly distinguishes existing `ce.v1` artifacts; current `Capability` terminology means a repository set, not a business capability. | [MADR-0020](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/docs/adr/0020-platform-exchange-contracts.md#L1-L23) | Keep the analysis adapter namespace separate from `ce.platform.v1` and from domain language. |
| MADR-0013 treats analyzed-application contract documents as a distinct integration boundary and calls for canonical bytes and no analyzed-code execution. | [MADR-0013](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/docs/adr/0013-integration-contract-standards.md#L85-L102) | This frontend research is not an API/event contract extractor or contract-document evidence package. |
| Host acquisition is a separate authority; offline extraction does not receive acquisition credentials or engine sockets. | [MADR-0023](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/docs/adr/0023-host-acquisition-boundary.md#L10-L14) and [credential boundary](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/docs/adr/0023-host-acquisition-boundary.md#L61-L65) | Any future adapter must bind an immutable source snapshot and remain offline/credential-free; this proposal does not claim OS isolation. |
Issue #23 records unavailable private Glufs sources and explicitly forbids claiming real-corpus fidelity from synthetic tests. | [Issue #23](https://github.com/Sonberg/capability-explorer/issues/23) | Real-corpus qualification remains unknown; this package uses only authored synthetic fixtures. It copies no private source. |

`ce.v1` (current extractor facts), #33 `ce.platform.v1` (proposed exchange
profiles), and #12/MADR-0013 (contract-document/integration evidence) are
different boundaries. MADR-0010's determinism/fidelity work and issue #6's
resolution gaps inform future qualification but do not measure a new frontend.
Preserve issue #37/#48's host-owned frozen-source/credential boundary; it remains
separate from unit discovery. DDD business-object, domain-event and
integration-event grading is explicitly outside issue 42.

## Discovery alternatives

| Pattern | Strengths | Risks and limits | Disposition |
|---|---|---|---|
| Existing per-extractor discovery | Uses language-native markers and existing compiler/config readers; small incremental changes. | Different recursion, skips, depth/error behavior and identities; corpus scripts duplicate rules. | Keep current behavior until a pilot; inventory gaps explicitly. |
| Host-produced unit manifest | Host can bind source revision, unit selection, limits and authorization before untrusted source data is interpreted. | Adds a host/extractor interface; a stale or incomplete manifest can omit units; host cannot infer language semantics. | Strong authority boundary; consider a pinned request/result envelope later, not a new service now. |
| Opt-in repository declaration | Explicit owners can name unusual workspaces, includes/excludes and references. | Untrusted, possibly malformed; cannot broaden snapshot authority or trigger scripts; adoption is voluntary and marker-free repos stay ambiguous. | Parse as bounded data only, with strict precedence and diagnostics. |
| Versioned shared policy contract, adapter-specific readers | Makes unit identity, ordering, budgets and status comparable without centralizing all parsing. | Each adapter still needs maintenance and conformance; contract consistency is not universal layout support. | Preferred research direction; no runtime library required or selected. |

The proposed discovery result schema records repository-relative stable unit
keys, owner and marker, versioned profile/capabilities, selected files and
references, exact hard budgets, exclusions and explicit outcomes. Detailed
precedence, containment and failure behavior is in the
[pseudocode specification](language-neutral-frontends-pseudocode.md). Marker
names alone do not prove semantic project membership. Nested repositories are
hard boundaries; explicit shared input is the only proposed cross-owner
deduplication exception.

## Producer comparison

The table compares source-backed properties of producer families, not tested
candidate products. “Semantic” denotes what a correctly configured implementation
can represent—not achieved accuracy in this repository.

| Producer | Semantic fidelity and spans | Identity/context unit | License and maintenance | Reproducibility/offline | Execution risk |
|---|---|---|---|---|---|
| Compiler APIs (e.g. Roslyn, TypeScript) | Upstream APIs expose parse/syntax and symbol/binding/semantic layers; compiler-resolved identities require supplied/resolved references, options, source and supported versions. Source positions need adapter normalization. | Invocation-scoped `Compilation`/`Program` and source trees; neither API supplies this project's stable unit key. | Roslyn repo `MIT`; TypeScript `Apache-2.0`; implementations are maintained separately. Pin compiler/API version, SDK/runtime, references and config. The TypeScript API guide warns it covers 6.0 and earlier and says 7.1 will have a different API; this does not establish feature or API equivalence. | Potentially repeatable when compiler, reference, config and source inputs are pinned; dependencies must be pre-provisioned offline. | No generic risk rating: a supplied host/plugin/config/source reader may add risk. Avoid builds and user scripts; constrain reads to the snapshot, deny ambient network/credentials and do not evaluate MSBuild. |
| Tree-sitter | Concrete syntax trees and byte/row-column ranges; incremental parsing is supported. Syntax/grammar nodes do not establish cross-file binding, types or compiler resolution. | One parse `Tree`, not a project semantic context. | Core runtime `MIT`; each grammar/binding has its own license and maintenance history. Pin runtime and exact grammar revisions. | Parser inputs and compiled grammar can be pinned for offline use; grammar/runtime versions and source encoding must be fixed. | The core is an in-process parser library, not a build runner; still review parser/grammar code and bound untrusted input. Downstream wrappers can add other risks. |
| SCIP protocol and indexers | `scip.proto` permits a precision spectrum from compiler-backed indexers to syntax-directed heuristics. It supplies symbol occurrences and positions; SCIP is an exchange format, not a semantic oracle. Position encoding differs by indexer. Independent indexer counts/activity are time-sensitive, not qualification evidence. | A static index has project-rooted `Document` entries; it has no live analysis session. | SCIP repo `Apache-2.0`; each indexer/dependency/license and maintenance cadence must be checked separately. Pin protocol and exact indexer. | A saved index can be consumed offline; producing it requires the pinned indexer and its project/reference inputs. Exact no-build behavior is indexer- and mode-specific, not established here. | Risk is version/mode/indexer-specific. An indexer may invoke compiler/build machinery, but this pass did not establish that for every indexer; verify before a pilot and never run analyzed repository scripts or install dependencies during discovery. |
| Controlled LSP | The protocol offers document/position requests such as definition and feature capability negotiation, but does not standardize a language-independent compiler symbol/domain model. Semantic fidelity depends on the selected server and supplied project inputs. | Stateful server session with documents addressed by URI/position, not a stable persisted index identity. | Inspected VS Code Node SDK is `MIT`; each server has its own license and release cadence. Protocol-document terms were not audited. Pin server, client/protocol version and settings. | Offline operation requires exact server/runtime/workspace inputs pre-provisioned; initialization, readiness and workspace/config state need deterministic capture. | Starting the server is process execution; any additional project tooling/config behavior is server-specific and unqualified. A future adapter needs a sealed local batch/session wrapper, bounded I/O, no credentials and no network. |

For primary-source details and exact checked revisions see
[References](#primary-references-checked-2026-11-03). No candidate was installed,
executed, benchmarked or checked against a representative language corpus.
License summaries are repository license observations, not a transitive
dependency audit or legal opinion.

## Compatibility matrix

The machine-readable cases are in
[`frontend-compatibility.json`](../../config/research/frontend-compatibility.json).
Core rules:

1. Old valid `ce.v1` artifacts remain readable. A new producer using only existing
   `ce.v1` languages is subject to the same old schema and invariants.
2. A new language value in an old consumer's `ce.v1` header fails the old closed
   enum. Neither a runtime registry nor a type declaration can change that.
3. Keep the proposed adapter (`ce.frontend.contractDraft.v1`) separately
   versioned and closed. A profile id resolves only through an exact pinned
   offline registry; an unrecognized language/capability yields `unsupported`.
4. Unknown fields, malformed spans, source digest mismatch, invalid resolution or
   malformed records fail before ingestion; they cannot be silently stripped or
   downgraded.
5. Versioned closed profiles are the primary proposal. A registry reference is an
   alternative for metadata/feature declarations only; it cannot widen an old
   consumer's schema. Rollback restores a previously accepted producer, profile
   and consumer together; it never reinterprets new bytes as old.

## Executable research evidence

`tests/frontend-contracts.test.mjs` validates representative instances against a
strict supported subset of the proposed JSON Schema drafts and uses the
fixture-only policy oracle in
[`conformance.mjs`](../../tests/fixtures/frontend-contracts/conformance.mjs)
against independently authored expected inventories. It exercises recursive
glob depth, zero-or-many `**` path segments, UTF-8 ordinal ordering under
permuted input, explicit exclude-over-include precedence, nested ownership,
deduplication, missing links, ambiguity, conflicts, depth/per-unit/total-file/
byte limits, unknown profiles/capabilities and path/symlink rejection.
Preclassified test/generated entries remain excluded; category overrides are
future-only and untested. Canonical inventory bytes use UTF-8 ordinal key/path
ordering and exclude operational timestamps.

Adapter evidence `startByte`/`endByte` always addresses bytes in the original
frozen source. Optional line/character positions carry a separate UTF-16 or
UTF-32 coordinate encoding. The draft's status checks reject profile mismatch
and complete results containing diagnostics, unresolved/candidate facts,
omissions or unsupported features. The focused byte test uses a UTF-8 non-ASCII
fixture; it is not a general source-coordinate or compiler-span validator.

This is an executable specification for the sealed synthetic cases only. It
does not walk a repository filesystem, discover projects, load a compiler,
resolve semantics, or establish that a real layout was scanned. `all` invokes
the focused tests as a package gate; the separately required `node --test`
command remains the direct test evidence.

## Safety, reproducibility and future pilot

Discovery and adapter requests bind a full repository revision and an immutable
snapshot digest. Paths are normalized repository-relative POSIX paths; empty,
`.`/`..`, absolute, drive, URI, backslash and NUL aliases are rejected. Symlinks,
nested-repository escape, unknown profile/capability, conflicting ownership and
hard-budget exhaustion fail explicitly. Missing
references are visible partial results; marker-free ambiguity is not successful
empty discovery. A profile must specify per-unit file, total candidate-file, depth, candidate
marker and byte budgets;
values must be benchmarked and agreed before any implementation, not invented by
this research.

No analyzed builds/package scripts, ambient credentials, undeclared network,
outside-snapshot traversal, automatic installs or host authority are allowed.
Canonical unit/fact/evidence ordering excludes operational timestamps; telemetry
is a separate channel. Repeat the same frozen inputs and compare exact canonical
bytes.

Future pilot entry criteria: recorded actual language/unit demand; authorized
immutable source; named frontend owner; pinned toolchain/grammar/server/license
and offline dependency closure; independent gold unit inventory, links and spans;
pre-registered precision/recall, false/missing-link and source-span thresholds;
resource ceilings; no-script/no-network/no-credentials checks; old-consumer
compatibility and a tested rollback/stop condition. If any item is unavailable,
record `not-measured`/`blocked` and stop without selecting a language. One
synthetic fixture suite cannot satisfy real-corpus demand or semantic accuracy.

## Unknowns, dissent and non-claims

- No new language was selected: issue 42 has no recorded language-specific demand
  in this bounded source review. No future language is inferred.
- Compiler APIs may yield higher semantic fidelity but require language-specific
  project/reference fidelity; Tree-sitter alone cannot promise compiler binding.
- SCIP output quality and risk depend on the indexer, not only its protocol. LSP
  is a controlled query transport, not a common semantic database.
- Budget values, registry ownership, canonical serialization profile, feature
  taxonomy, partial-result acceptance and quantitative pilot thresholds require
  owner review.
- Layout conformance tests do not establish semantic resolution, production
  discovery, precision/recall, source-span accuracy or support for arbitrary
  repositories.
- #23's private corpus is unavailable; no private source, credentials or exports
  were retrieved, copied or uploaded.
- No extra issue, parser dependency, service, schema migration or production
  discovery runtime is implied. Parent-owned full CI/publication remains separate.

## Primary references checked 2026-11-03

- [Roslyn compiler API model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model): compiler pipeline exposes separate syntax, symbol and semantic-analysis layers; API model can bind one compilation snapshot. Repository [`License.txt`](https://github.com/dotnet/roslyn/blob/82542416eba9dfc0f9017a986b4ca0f96da66b84/License.txt) reports MIT; checked main commit `82542416eba9dfc0f9017a986b4ca0f96da66b84` (2026-11-03).
- [TypeScript Compiler API guide](https://github.com/microsoft/TypeScript/wiki/Using-the-Compiler-API): `Program`, `CompilerHost`, `SourceFile`, diagnostics and file reading; page warns it describes TypeScript 6.0 and earlier and that 7.1 changes the API. [`tsconfig`](https://www.typescriptlang.org/tsconfig/) is the official configuration reference. Repository [`LICENSE.txt`](https://github.com/microsoft/TypeScript/blob/042e0fb9574980bd67964165df295e629f834fe3/LICENSE.txt) reports Apache-2.0; checked main commit `042e0fb9574980bd67964165df295e629f834fe3` (2026-11-03).
- [Tree-sitter basic parsing](https://tree-sitter.github.io/tree-sitter/using-parsers/2-basic-parsing.html) documents concrete syntax nodes and source byte/point ranges; [advanced parsing](https://tree-sitter.github.io/tree-sitter/using-parsers/3-advanced-parsing.html) documents incremental tree edits. Core [`LICENSE`](https://github.com/tree-sitter/tree-sitter/blob/9632b561db2f647e9c0be9e61cf91f10671a6491/LICENSE) reports MIT; checked master commit `9632b561db2f647e9c0be9e61cf91f10671a6491` (2026-11-02). Grammar licenses are separate.
- [SCIP protocol source](https://github.com/scip-code/scip/blob/bbec5345bfae602b31286a9ec74e2c2042713f50/scip.proto) documents indexer precision spectrum, workspace-relative non-symlink document paths, tool info and source position encodings. Repository [`LICENSE`](https://github.com/scip-code/scip/blob/bbec5345bfae602b31286a9ec74e2c2042713f50/LICENSE) reports Apache-2.0; checked main commit `bbec5345bfae602b31286a9ec74e2c2042713f50` (2026-10-28). Indexer licenses are independent.
- [SCIP README](https://github.com/scip-code/scip/blob/bbec5345bfae602b31286a9ec74e2c2042713f50/README.md) lists the protocol, bindings/CLI and separately maintained indexers. The repository moved from `sourcegraph/scip` to `scip-code/scip`; names/counts are not a current qualification.
- [LSP overview](https://microsoft.github.io/language-server-protocol/overviews/lsp/overview/) explains process/JSON-RPC sessions, document synchronization, positions, capabilities and that LSP does not define a cross-language compiler symbol model; [current 3.18 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.18/specification/) documents versioned capability negotiation. The inspected [VS Code Node LSP implementation](https://github.com/microsoft/vscode-languageserver-node/blob/main/License.txt) is MIT; individual servers remain separately licensed.
- [JSON Schema Draft 2020-12](https://json-schema.org/draft/2020-12/schema) is used for research-only schema drafts. No third-party validator or runtime library is adopted.
- Existing repository precedent: [MADR-0013](https://github.com/Sonberg/capability-explorer/blob/3d0ff8813df60b07ac86c8220b811be3debe5401/docs/adr/0013-integration-contract-standards.md#L91-L102) requires sealed inputs, canonical byte comparison, zero network and no analyzed-code execution. This is guidance, not evidence that these proposals are implemented.
- RuvNet process guidance: [`agent-sparc-coordinator/SKILL.md`](https://github.com/ruvnet/ruflo/blob/main/.agents/skills/agent-sparc-coordinator/SKILL.md) and [`sparc-refine/SKILL.md`](https://github.com/ruvnet/ruflo/blob/main/plugins/ruflo-sparc/skills/sparc-refine/SKILL.md) are documentation guidance for phase gates, not runtime capability claims. Brain retrieval was weak/ambiguous for a concrete schema-validation primitive; this package therefore adds no RuvNet library or asserted live capability.

## Focused package completion

| Gate | Score | Evidence |
|---|---:|---|
| Specification | 97/100 | 19 issue obligations and machine-checkable constraints; missing/duplicate/self-attested evidence mutations rejected. |
| Pseudocode | 97/100 | Ordered discovery/adapter procedures, hard boundaries, budgets, failure states and complexity checked. |
| Architecture | 97/100 | Source-pinned research, proposed MADR, strict contract drafts, compatibility cases and independently authored inventories. |
| Refinement | 97/100 | 43 focused tests passed; all 20 sealed fixture outputs matched; malformed schema instances and unsafe/unknown cases rejected. |
| Completion | 97/100 | The focused package gates below passed; research limitations and parent-owned integration are explicit. |

Focused commands:

```text
node scripts/research/check-frontend-contracts.mjs specification
node scripts/research/check-frontend-contracts.mjs pseudocode
node scripts/research/check-frontend-contracts.mjs architecture
node scripts/research/check-frontend-contracts.mjs all
node --test tests/frontend-contracts.test.mjs
git diff --check
```

These scores certify only the bounded research package and its focused tests.
The oracle is fixture-only, the schema validator covers the package's declared
subset, and neither is production discovery or a full Draft 2020-12 validator.
No semantic-accuracy, real-corpus, arbitrary-layout, precision/recall, full
repository CI or publication gate is claimed. Parent-owned integration,
full local/pre-push/hosted CI and publication remain pending.
