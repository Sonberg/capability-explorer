# Language-neutral frontends and repository discovery — specification

Status: research proposal for owner review; no runtime behavior or language adoption.
Baseline: `3d0ff8813df60b07ac86c8220b811be3debe5401`.

## Goal and non-goals

Separate a shared, evidence-bearing analysis core from language-specific semantic
frontends, then define deterministic analysis-unit discovery independent of
extractor implementation. The deliverable is a bounded, source-grounded research
package. It does not choose a new language, add a parser, change `ce.v1`, implement
a production scanner, execute analyzed build scripts, or certify semantic quality
or arbitrary repository layouts.

## Acceptance trace

IDs map each issue checkbox to a concrete proposal and an independently named
conformance or review obligation. No criterion is satisfied by a self-attested
boolean. Exact-source citations and exclusions are collected in the final report.

| ID | Issue acceptance obligation | Required proposal / evidence |
|---|---|---|
| F01 | Adapter inputs and outputs | Typed draft binds immutable source/unit identity, toolchain, config and output facts. |
| F02 | Toolchain, feature and span model | Provenance, supported feature set and source-coordinate contract are explicit. |
| F03 | Resolution, coverage and failures | Resolved/candidate/unresolved distinctions and complete/partial/unsupported/failed outcomes are explicit. |
| F04 | Old/new producer-consumer compatibility | Cases distinguish old artifact acceptance from old-consumer rejection of newly emitted vocabulary. |
| F05 | Malformed input and rollback/versioning | Malformed records fail closed; rollback and versioned closed-profile / registry alternatives are compared. |
| F06 | Compare compiler APIs, Tree-sitter, SCIP and controlled LSP | Semantic fidelity, license, maintenance, reproducibility, offline dependency and execution risk are compared with primary references. |
| F07 | Demand-gated future pilot | No language is selected absent recorded investigation demand and a bounded acceptance plan. |
| D01 | Compare discovery ownership patterns | Existing per-extractor discovery, host-produced manifests and opt-in repository declarations are compared. |
| D02 | Stable analysis-unit identity | Identity is repository-relative and stable without host path, timestamps or traversal order. |
| D03 | Marker and override policy | Include/exclude precedence, nested ownership, references/workspaces and competing markers are specified. |
| D04 | Bounded discovery | File, depth, byte and candidate budgets; nested repositories; generated/vendor/test treatment are explicit. |
| D05 | Ambiguous / marker-free repositories | Ambiguity is surfaced, not silently reported as successful empty discovery. |
| D06 | Deterministic sealed inventories | Independent fixtures cover root/nested/mixed/multiple-marker/standalone/overlap/dedup/missing-dependency cases. |
| Q01 | Repeatability and canonical output | Ordered inventory and canonical facts/evidence repeat byte-for-byte; operational timestamps are separate. |
| Q02 | Precision/recall, links and spans | Future measurements define inventory precision/recall, false/missing links and source-span correctness. |
| Q03 | Negative discovery inputs | Unknown language/capability, ambiguity, conflict, limits, traversal and symlink escape fail explicitly. |
| Q04 | Safe execution boundary | No analyzed scripts, ambient credentials, undeclared network or snapshot escape; partial/failure cannot masquerade as success. |
| G01 | Existing contract and prior issue boundaries | Preserve `ce.v1`, distinguish #33 exchange envelope from #12 contract-document evidence, align #6 resolution, retain #37/#48 frozen-source/credential boundary, and state #23 corpus limitation. |
| G02 | Recommendation, dissent, unknowns and pilot | A bounded decision and explicit unknowns/dissent are recorded; no automatic implementation issue is spawned. |

## Requirements and failure semantics

The adapter contract is separate from the closed `ce.v1` record language
vocabulary. A language-neutral fact shell may transport a source-bound fact, but
language-specific frontends remain responsible for semantic claims. No generic
syntax tree is treated as compiler-resolved meaning. A consumer must preserve
resolution state, evidence provenance, unsupported features and coverage loss.

Discovery operates only over an immutable, repository-relative source snapshot.
Marker parsing and path normalization are deterministic and offline. Stable unit
identity is derived from normalized POSIX-relative owner path plus a declared
unit key; host absolute paths, timestamps and enumeration order are excluded.
Conflicting identities, overlapping ownership, unsupported capabilities,
ambiguity, budget exhaustion, invalid declarations, path traversal and symlink
escape are explicit errors or partial results, never a successful empty inventory.

Hard boundaries: do not run repository build/package scripts; do not inherit
credentials; do not access undeclared network; do not traverse outside the
snapshot; do not weaken failure into a successful partial result. Per-issue
scope excludes DDD business-object/domain/integration-event grading and all
publication, shared manifests, existing HANDOFF/CHANGELOG and runtime changes.

The machine-checkable constraints for this delivery are:

- No parser adoption or new-language choice without recorded demand.
- Do not modify schema/v1/record.schema.json or change the ce.v1 language vocabulary.
- Do not implement production repository discovery or add runtime dependencies.
- Do not execute analyzed repository build/package scripts or inherit credentials.
- Do not use undeclared network access or traverse outside an immutable source snapshot.
- Do not claim fixture conformance proves semantic resolution or all repository layouts.
- Keep #33 exchange-envelope, #12 contract-document evidence, #6 resolution, #37/#48 frozen-source boundary and #23 corpus limitation distinct.
- Issue 42 excludes DDD business-object, domain-event and integration-event grading.

## Acceptance evidence map

The architecture package must include the proposed ADR, strict adapter and
discovery schema drafts, old/new compatibility cases, producer comparison with
checked primary sources, policy pseudocode, sealed independent inventory
expectations and negative cases. The focused checker validates declared
requirements against these evidence artifacts; it is a research-package
integrity gate, not a discovery engine or semantic-accuracy test.
