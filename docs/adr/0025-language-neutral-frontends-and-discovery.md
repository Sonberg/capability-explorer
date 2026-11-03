# MADR-0025: Language-neutral frontend contract and deterministic unit discovery

- Status: proposed for owner review; not adopted or implemented
- Date: 2026-11-03
- Baseline: `3d0ff8813df60b07ac86c8220b811be3debe5401`
- Related: issue #42; issues #6 and #23; MADR-0010, MADR-0012, MADR-0013, MADR-0020 and MADR-0023

## Context and problem

`ce.v1` is a closed extractor fact format with a closed language vocabulary.
Current extractors discover their own analysis units using different markers,
recursion and test/skip policies; corpus replay contains additional discovery
logic. `RepositorySource` chooses a repository/ref, not the set of project units
inside it. A common format alone cannot make syntax-only facts semantically
equivalent to compiler-resolved facts.

The existing host acquisition/frozen-source boundary and the proposal for
`ce.platform.v1` are separate from extractor facts. MADR-0013 concerns contracts
inside analyzed applications, not a frontend's fact envelope. Issue #23's
unavailable corpus remains a validation limitation. The #37/#48 frozen-source
and credential boundary is preserved, not reimplemented or relaxed here.

## Decision drivers

- Preserve backward compatibility with old `ce.v1` readers and artifacts.
- Keep language semantics and toolchain fidelity explicit.
- Bind facts, spans and discovered units to the same immutable source revision.
- Make unsupported, ambiguous, partial and failed outcomes distinguishable.
- Avoid running analyzed builds, package scripts or repository-controlled tools.
- Make repeatability, offline execution and resource ceilings reviewable.
- Avoid selecting a language without observed demand or authorizing runtime work.

## Considered options

1. Keep each extractor's discovery and facts entirely private: simplest now, but
   discovery duplication and contract drift remain.
2. Replace frontends with one universal syntax parser: common syntax surface, but
   does not establish compiler binding, type resolution or language semantics.
3. Emit compiler/indexer-specific artifacts directly as `ce.v1` and widen its
   enum: old consumers still reject unknown values; this changes an existing
   version's meaning.
4. Define separate, versioned adapter and discovery drafts while retaining
   language-specific semantic producers: requires explicit qualification and
   compatibility review, but makes evidence, capabilities, coverage and
   discovery policies comparable without pretending they are identical.
5. Centralize discovery in a new service/runtime: introduces authority,
   deployment and dependency decisions beyond this research scope.

## Proposed decision

For owner review, choose option 4 as the next design direction only:

1. Keep `schema/v1/record.schema.json` and the `ce.v1` language vocabulary
   unchanged. An internal proposed `ce.frontend.contractDraft.v1` is separate
   from both `ce.v1` and `ce.platform.v1`.
2. Treat the frontend profile/registry as versioned and closed. A profile id is
   accepted only against its exact pinned offline registry; schema text or a
   registry cannot make an old `ce.v1` consumer accept a new language value.
3. Preserve source, toolchain, config and capability provenance; model canonical
   byte spans and source coordinate encoding; retain resolution class, evidence,
   coverage omissions and explicit outcome. A claim of `complete` requires
   cross-field validation beyond JSON Schema.
4. Define deterministic repository-relative `unitKey`, marker/manifest
   precedence, nested ownership, explicit workspace/project references, hard
   budgets, nested-repository boundaries, test/generated/vendor policy and
   fail-closed ambiguity. Do not follow symlinks or resolve paths outside the
   immutable snapshot.
5. Keep discovery output as data. Do not evaluate build files, imports,
   generators, package scripts, arbitrary plugins or repository declarations as
   executable code. No credentials, undeclared network or automatic dependency
   installation are allowed.
6. Require independent fixture inventories and measured precision/recall,
   false/missing-link and source-span criteria before a future implementation or
   pilot can claim qualification.

The exact field shapes and algorithmic rules are research drafts in the adjacent
schemas and pseudocode, not adopted runtime contracts.

## Compatibility, rollout and rollback

Old valid `ce.v1` records remain the compatibility baseline. New producers using
the current closed language vocabulary must continue satisfying the old schema.
A new language value in `ce.v1` fails old schema validation and must not be
silently treated as an unknown string. New draft contracts get their own
versioned identifier and strict schema; unknown features fail as unsupported.
Malformed artifacts fail before typed interpretation.

If a later approved pilot changes profiles, it must first pass old artifact/new
consumer and old consumer/new producer cases, preserve source digests and have an
explicit rollback pair (producer profile plus consumer profile). Rollback must
restore the prior closed profile and last compatible artifact; no guessed
down-conversion or registry lookup can reinterpret bytes.

## Consequences

Positive: downstream consumers can distinguish source coverage and semantic
resolution from syntax support; discovery choices become inspectable and
reproducible; future work can be demand-gated without changing existing facts.

Costs/risks: every producer still needs language-specific project resolution,
license and maintenance review, offline dependency pinning, independent gold
tests and explicit handling for unsupported features. A host manifest can be
stale; a repository declaration is untrusted; a standards-based envelope alone
cannot establish correctness. Budget values, feature vocabulary, canonical
serialization and partial-result policies remain unresolved.

## Future pilot gate

No pilot begins until there is recorded language/unit demand, an authorized frozen
source, named ownership, a pinned offline producer profile and license review,
independently authored gold inventory/link/span labels, predeclared quantitative
thresholds, bounded resources, no-script/no-network/no-credentials evidence,
old-consumer compatibility tests and tested rollback/stop criteria. If any gate
is missing, report `not-measured` or `blocked`; do not select a language or imply
an implementation. Issue 42 does not automatically spawn implementation work.

## Rejected scope and open questions

- No parser/indexer/LSP implementation is selected or installed.
- No new language is chosen; no language demand is inferred from ecosystem
  popularity or available libraries.
- No production scanner, service, dependency, schema migration or build script
  is added.
- No claim is made about real Glufs sources, arbitrary repository layouts,
  semantic accuracy, performance, source-span accuracy or measured
  precision/recall.
- The parent's separate DDD business-object/domain/integration-event research is
  out of scope and is not a dependency of this decision.

## References

See the source-pinned findings and primary-source comparison in
[`language-neutral-frontends.md`](../research/language-neutral-frontends.md).
