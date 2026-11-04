# Architecture decision ledger

This is the navigation index for the MADRs in this checkout. A record's declared
status and scope are authoritative; implementation, merge or operator-local
adoption does not make a proposal accepted repository-wide. See the proposed
[evidence-based council process](0026-evidence-based-decision-councils.md).
No existing status was changed to produce this index.

## Status ledger and legend

| Diagram color | Text label | Meaning |
|---|---|---|
| Green | A: accepted | Explicitly accepted within the record's stated scope, not implementation/release certification. |
| Yellow | P: proposed/pending | Review or evidence still needed, including locally applied/operator-authorized proposals. |
| Red | D: draft | Not yet ready for decision review; not a severity rating. |
| Gray | H: historical | Rejected, deprecated or superseded; retain the decision and replacement links. |

There are **36 local records: 1 scoped accepted, 35 proposed, 0 draft,
0 historical**. MADR-0016 is accepted for the **operator workflow only**.
Both `0005` paths are distinct records, named `0005-G` and `0005-PR` below solely
for navigation. No `0015` exists; this is a numbering gap, not a missing file
invented by the index. MADR-0025 landed in owner-merged #55 and remains proposed.

| ID | Title / record | Declared status and scope |
|---|---|---|
| 0001 | [GitHub work claims and isolated queen–worker delivery](0001-github-work-coordination.md) | P: proposed |
| 0002 | [Establish and govern a shared ubiquitous language](0002-ubiquitous-language.md) | P: repository-wide adoption proposed; applied in this workstream |
| 0003 | [Run project-wide build and vulnerability gates locally before push](0003-local-ci-before-push.md) | P: contributor-wide adoption proposed; operator-requested workstream application |
| 0004 | [Treat glossary groups as deterministic term candidates](0004-glossary-candidates.md) | P: contributor-wide adoption proposed; applied in this workstream |
| 0005-G | [Rank glossary candidates into domain concepts with an explainable score](0005-glossary-concepts.md) | P: proposed; applied in this workstream |
| 0005-PR | [Coordinate PR updates and preserve review context](0005-pr-lifecycle-and-conflict-prevention.md) | P: repository-wide adoption proposed; applied to this operator's agent-assisted work |
| 0006 | [Maintain a curated project changelog](0006-keep-a-changelog.md) | P: repository-wide adoption proposed; applied to this operator's work |
| 0007 | [Version the product against an explicit compatibility contract](0007-semantic-versioning.md) | P: repository-wide adoption proposed; applied to this operator's work |
| 0008 | [Separate evidence navigation from reviewed glossary assertions](0008-glossary-flow-and-reviewed-assertions.md) | P: owner review proposed; applied in this workstream |
| 0009 | [Share a live LikeC4 canvas without changing evidence semantics](0009-live-likec4-architecture.md) | P: owner review proposed; implementation/validation described as in progress |
| 0010 | [Preserve uncertainty while improving deterministic repository mappings](0010-deterministic-mapping-fidelity.md) | P: owner review proposed; applied in this workstream |
| 0011 | [Source-bound security validation with explicit unresolved findings](0011-source-bound-security-validation.md) | P: owner review proposed; applied in this workstream |
| 0012 | [Revision-bound binding preview and explicit export](0012-revision-bound-binding-preview.md) | P: owner review proposed; applied in this workstream |
| 0013 | [Versioned integration contracts as evidence, separate from inference](0013-integration-contract-standards.md) | P: owner review proposed; does not itself authorize extractor implementation |
| 0014 | [Remove recoverable worktrees after PR publication](0014-pr-publication-worktree-retention.md) | P: repository-wide adoption proposed; operator-authorized publication and merged-to-main cleanup checkpoints |
| 0016 | [Merge PRs only into main and reconcile the existing stage history](0016-main-only-pr-integration.md) | A: operator workflow accepted by explicit directive; implementation subject to owner review |
| 0017 | [PostgreSQL snapshots and bounded Explorer reads](0017-postgres-explorer-snapshots.md) | P: implementation authorized for review; owner retains final architecture decision |
| 0018 | [Evaluate RuVector separately from canonical fact storage](0018-ruvector-evaluation-boundary.md) | P: experiment authorized; adoption requires owner decision |
| 0019 | [Evaluate tools independently before introducing dependencies](0019-independent-tool-evaluation.md) | P: repository-wide adoption proposed; applied to #31 by operator instruction |
| 0020 | [Draft evidence-platform exchange contracts, separate from extractor facts](0020-platform-exchange-contracts.md) | P: owner review proposed; first shape-contract package authorized for review only |
| 0021 | [Explicit container engine and qualified execution profiles](0021-explicit-container-engine-core.md) | P: proposed |
| 0022 | [Single-host analysis ownership and publication](0022-local-analysis-lifecycle.md) | P: proposed |
| 0023 | [Host-owned source checks and acquisition credentials](0023-host-acquisition-boundary.md) | P: proposed; implementation contract authorized by the operator |
| 0024 | [Issue labels and explicit delivery dependencies](0024-issue-labels-and-dependencies.md) | P: operator-authorized label pilot; pending owner review |
| 0025 | [Language-neutral frontend contract and deterministic unit discovery](0025-language-neutral-frontends-and-discovery.md) | P: owner review proposed; not adopted or implemented |
| 0026 | [Evidence-based decision councils and a navigable MADR ledger](0026-evidence-based-decision-councils.md) | P: proposed; accepts no existing decision |
| 0027 | [Proposed source-bound assessment of business objects and events](0027-domain-object-event-assessment.md) | P: research contract only; no production grading |
| 0028 | [Portable, source-bound decision navigation](0028-portable-decision-ledger.md) | P: bounded portable contract; no ontology reasoning or adoption |
| 0029 | [Frugal CI with executable local and hosted parity](0029-frugal-ci-and-executable-parity.md) | P: proposed; full gates retained, measured optimization required |
| 0030 | [Govern dependency caches across framework and toolchain upgrades](0030-dependency-cache-lifecycle.md) | P: proposed; scoped cleanup and retention policy, not implemented automation |
| 0031 | [Convert maintained JavaScript to TypeScript 7 when modified](0031-typescript7-on-touch.md) | P: repository-wide adoption proposed; operator-directed workstream rule, no compiler upgrade yet |
| 0032 | [Drive implementation with behavior scenarios and red-green-refactor tests](0032-tdd-bdd-delivery.md) | P: repository-wide adoption proposed; operator-directed TDD/BDD workflow, no new framework mandated |
| 0033 | [Keep authored models, code candidates and collector authority separate](0033-bounded-model-and-collector-evidence.md) | P: bounded evidence and offline collection; no central deployment or business approval |
| 0034 | [View revision-bound original models without granting execution authority](0034-revision-bound-read-only-model-views.md) | P: explicit source attachments, immutable scope and read-only viewer boundary; no hard CPU deadline or approval claim |
| 0035 | [Require measurable objectives for performance and operational-quality issues](0035-measurable-service-level-objectives.md) | P: operator-directed SLO rule; quantitative targets and evidence required, not measured compliance |
| 0036 | [Configure evidence retention and age-based historical resolution](0036-configurable-evidence-retention.md) | P: configurable minimum/default and coarser history; durations/central custody need owner approval, no cleanup or UI implemented |

## Relationship map

All local records appear below; labels are abbreviated for readability and the
table provides full titles and links. The graph deliberately shows **selected,
source-declared relationships**, not every reference or an inferred task DAG.
Cycles among related decisions are not evidence of circular hard prerequisites.
An isolated node means no selected edge, not proof of independence.

- `X --> Y`: X extends or explicitly follows Y's rules; label states which.
- `X -.-> Y`: X references/relates to Y; **not** a hard dependency.
- `X ==> Y`: X overrides a stated part of Y; **not** whole-record supersession.

```mermaid
flowchart LR
  subgraph Governance
    a01["0001 Work claims [P]"]:::proposed
    a03["0003 Local CI [P]"]:::proposed
    a05p["0005-PR PR lifecycle [P]"]:::proposed
    a06["0006 Changelog [P]"]:::proposed
    a07["0007 Compatibility [P]"]:::proposed
    a11["0011 Security evidence [P]"]:::proposed
    a14["0014 Worktree retention [P]"]:::proposed
    a16["0016 Main-only: operator scope [A]"]:::accepted
    a19["0019 Tool evaluation [P]"]:::proposed
    a24["0024 Issue labels [P]"]:::proposed
    a26["0026 Decision council [P]"]:::proposed
    a29["0029 Frugal CI [P]"]:::proposed
    a30["0030 Cache lifecycle [P]"]:::proposed
    a31["0031 TypeScript 7 on touch [P]"]:::proposed
    a32["0032 TDD and BDD [P]"]:::proposed
    a35["0035 Measurable SLOs [P]"]:::proposed
    a36["0036 Evidence retention [P]"]:::proposed
  end
  subgraph Evidence_and_language
    a02["0002 Ubiquitous language [P]"]:::proposed
    a04["0004 Term candidates [P]"]:::proposed
    a05g["0005-G Concept ranking [P]"]:::proposed
    a08["0008 Reviewed assertions [P]"]:::proposed
    a09["0009 Live LikeC4 [P]"]:::proposed
    a10["0010 Mapping fidelity [P]"]:::proposed
    a12["0012 Binding preview [P]"]:::proposed
    a13["0013 Integration contracts [P]"]:::proposed
    a25["0025 Frontend discovery [P]"]:::proposed
    a27["0027 Object and event assessment [P]"]:::proposed
    a28["0028 Portable decision ledger [P]"]:::proposed
  end
  subgraph Runtime_and_exchange
    a17["0017 PostgreSQL snapshots [P]"]:::proposed
    a18["0018 RuVector boundary [P]"]:::proposed
    a20["0020 Exchange contracts [P]"]:::proposed
    a21["0021 Container profiles [P]"]:::proposed
    a22["0022 Analysis ownership [P]"]:::proposed
    a23["0023 Host acquisition [P]"]:::proposed
    a33["0033 Models and collector [P]"]:::proposed
    a34["0034 Read-only model views [P]"]:::proposed
  end
  a05p -->|extends claims| a01
  a05p -->|extends validation| a03
  a14 ==>|cleanup condition only| a05p
  a16 ==>|stage target only| a05p
  a06 -.-> a07
  a11 -.-> a03
  a19 -.-> a11
  a24 -.-> a01
  a26 -.-> a19
  a26 -.-> a24
  a04 -.-> a02
  a05g -->|extends candidates| a04
  a08 -.-> a04
  a09 -.-> a08
  a10 -.-> a08
  a12 -.-> a10
  a13 -.-> a10
  a17 -.-> a07
  a18 -.-> a17
  a20 -.-> a13
  a20 -.-> a17
  a21 -.-> a11
  a22 -.-> a17
  a22 -.-> a20
  a22 -.-> a21
  a23 -.-> a22
  a25 -.-> a23
  a27 -.-> a02
  a27 -.-> a08
  a27 -.-> a13
  a27 -.-> a26
  a28 -.-> a26
  a28 -.-> a14
  a29 -.-> a03
  a29 -.-> a11
  a29 -.-> a28
  a30 -.-> a03
  a30 -.-> a28
  a30 -.-> a29
  a31 -.-> a19
  a31 -.-> a29
  a31 -.-> a30
  a32 -.-> a29
  a32 -.-> a31
  a35 -.-> a24
  a35 -.-> a29
  a35 -.-> a32
  a36 -.-> a33
  a36 -.-> a30
  a36 -.-> a35
  classDef accepted fill:#dcfce7,stroke:#166534,color:#14532d
  classDef proposed fill:#fef9c3,stroke:#854d0e,color:#713f12
  classDef draft fill:#fee2e2,stroke:#991b1b,color:#7f1d1d
  classDef historical fill:#e5e7eb,stroke:#4b5563,color:#111827
```

No draft or historical nodes are fabricated to populate the legend.

### Edge evidence ledger

Each row covers exactly the listed graph edges. Direction is from the
referencing/extending/overriding record to its target.

| From | To | Type and source passage |
|---|---|---|
| 0005-PR | 0001, 0003 | Extends: [Decision outcome](0005-pr-lifecycle-and-conflict-prevention.md#decision-outcome) explicitly extends claims and validation. |
| 0014 | 0005-PR | Scoped override: [Decision outcome](0014-pr-publication-worktree-retention.md#decision-outcome), merged-only cleanup condition, not the entire lifecycle. |
| 0016 | 0005-PR | Scoped override: [Context](0016-main-only-pr-integration.md#context-and-problem-statement), stage-only target direction. |
| 0006 | 0007 | Related metadata in [record](0006-keep-a-changelog.md). |
| 0011 | 0003 | Related metadata in [record](0011-source-bound-security-validation.md). |
| 0019 | 0011 | Related metadata in [record](0019-independent-tool-evaluation.md). |
| 0024 | 0001 | Related metadata in [record](0024-issue-labels-and-dependencies.md). |
| 0026 | 0019, 0024 | Related metadata in [record](0026-evidence-based-decision-councils.md). |
| 0004 | 0002 | Related metadata in [record](0004-glossary-candidates.md). |
| 0005-G | 0004 | Extends: coordination metadata and [Context](0005-glossary-concepts.md#context-and-problem). |
| 0008 | 0004 | Related metadata in [record](0008-glossary-flow-and-reviewed-assertions.md). |
| 0009 | 0008 | Related metadata in [record](0009-live-likec4-architecture.md). |
| 0010 | 0008 | Related metadata in [record](0010-deterministic-mapping-fidelity.md). |
| 0012 | 0010 | Related metadata in [record](0012-revision-bound-binding-preview.md). |
| 0013 | 0010 | Related metadata in [record](0013-integration-contract-standards.md). |
| 0017 | 0007 | Related metadata in [record](0017-postgres-explorer-snapshots.md). |
| 0018 | 0017 | Related metadata in [record](0018-ruvector-evaluation-boundary.md). |
| 0020 | 0013, 0017 | Related metadata in [record](0020-platform-exchange-contracts.md). |
| 0021 | 0011 | Related metadata in [record](0021-explicit-container-engine-core.md). |
| 0022 | 0017, 0020, 0021 | Related metadata in [record](0022-local-analysis-lifecycle.md). |
| 0023 | 0022 | Related metadata in [record](0023-host-acquisition-boundary.md). |
| 0025 | 0023 | Related/context in the [record](0025-language-neutral-frontends-and-discovery.md#context-and-problem); no implementation prerequisite inferred. |
| 0027 | 0002, 0008, 0013, 0026 | Related metadata in the [record](0027-domain-object-event-assessment.md); not acceptance or an implementation prerequisite. |
| 0028 | 0026, 0014 | Related metadata in the [record](0028-portable-decision-ledger.md); no acceptance inferred. |
| 0029 | 0003, 0011, 0028 | Related metadata in the [record](0029-frugal-ci-and-executable-parity.md); retains full-gate and ledger rules, no acceptance inferred. |
| 0030 | 0003, 0028, 0029 | Related metadata in the [record](0030-dependency-cache-lifecycle.md); upgrade housekeeping proposal, not cleanup authorization. |
| 0031 | 0019, 0029, 0030 | Related metadata in the [record](0031-typescript7-on-touch.md); incremental migration rule, not completed compiler adoption. |
| 0032 | 0029, 0031 | Related metadata in the [record](0032-tdd-bdd-delivery.md); behavior-driven test-first delivery, not existing feature-test evidence. |
| 0035 | 0024, 0029, 0032 | Related metadata in the [record](0035-measurable-service-level-objectives.md); issue applicability, measured optimization and behavioral guardrails, not measured compliance. |
| 0036 | 0033, 0030, 0035 | Related metadata in the [record](0036-configurable-evidence-retention.md); collector custody, cache ownership and measurable lifecycle objectives, not deletion authority. |

## Updating the ledger

The [generated portable index](../research/decision-ledger-preview/index.md),
[typed graph](../research/decision-ledger-preview/graph.mmd) and
[machine-readable ledger](../research/decision-ledger-preview/ledger.json)
are checked projections of `config/decisions/ledger.json` and canonical records.
The selected diagram above stays a human navigation view, not an exhaustive
dependency graph or taxonomy. Run `node scripts/decisions/check.mjs generate`
after explicitly refreshing changed source receipts, then
`make test-decision-ledger`; read-only checks reject stale generated views.

Add a record, change a status or record a supersession together with its table
row, graph node/color and applicable edge evidence. Preserve scope qualifiers
and decision links. Check all local paths/anchors and directory coverage.
Check current main/open PRs for ID reservations; do not infer acceptance from
a merge or author a council receipt after the fact.
The [CRUD lifecycle](0026-evidence-based-decision-councils.md#madr-lifecycle-crud-includes-navigation)
also covers reads, renames and withdrawal/retirement without losing history.
