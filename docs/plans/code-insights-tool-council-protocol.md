# Issue #31: independent tool councils and delivery protocol

Status: execution plan, not completed tool evaluations. Applies with the
[master prompt](code-insights-research-brain-prompt.md). This protocol defines
project workflow; it does not assert a new installed council runtime.

## Work packages and ordering

| Package | Scope | Exit condition |
|---|---|---|
| 0: foundation | Refresh main/HANDOFF/MADRs; baseline inventory, sealed corpora, oracles, reference/dossier schema, fail-closed validator and negative tests | Independently reviewed tool-free foundation PR; wait for its merge before dependent introduction PRs |
| 1: inventory | Separate scc and cloc desk councils; baseline inventory comparator | Select at most scc for first trial; no forced winner; any introduction is scc-only |
| 2: context packaging | Separate Repomix and code2prompt councils against the same packaging requirements | At most one initial trial and one single-packer introduction proposal |
| 3: structural gap | Separate Ctags, Tree-sitter, SCIP import, ast-grep and Graphify code-only councils | Choose at most one initial trial against a demonstrated gap; one adapter per introduction PR |
| 4: additional tracks | Separate CodeQL and Joern assessments, existing security comparators, Understand-Anything and Graphify hybrid assessments | Desk findings first; runtime work deferred beyond the initial three-trial budget or absent explicit model scope |
| 5: reconciliation | Per-tool verdicts, rejected/deferred alternatives, measured incremental value and next slices | Update #31, source-bound handoff and exact-key checkpoint; no blanket completion claim |

The initial order is a hypothesis based on cheap inventory first. Record changes
before trials, with the observed gap and budget. Required candidates all receive
a desk verdict; selecting a shortlist does not silently discard the rest.
Knolli's article supplies discovery leads, not an executable tool candidate or
technical authority. Screen every discovered alternative; a screened-out lead
gets a cited reason, while an eligible candidate gets its own council dossier.
Existing Roslyn/TS/SQL, explanations, embeddings and security modes are assessed
as baseline comparators, not disguised as new adapters. Protocol-specific tools
remain linked to #6/#12/MADR-0013; do not claim a process/contract parser exists.

## Council roles and independence

Each tool and materially different execution mode receives its own council.
Do not issue a shared verdict for deterministic Graphify and model-backed modes,
or for SCIP ingestion and execution of an indexer.

| Role | Required contribution | Forbidden shortcut |
|---|---|---|
| Domain/tool SME | Trace claimed insight to source and existing baseline; define falsifiable role-specific oracle | Vendor prose as execution proof |
| Safety/evidence adversary | Attack identity, false links, hostile inputs/configuration, licensing, egress, supply chain and measurement assumptions | Uncited speculative veto, or weakening a gate to obtain a winner |
| Integration/cost advisor | Thin integration, alternatives, maintenance footprint, CI image/cache needs, rollback and marginal value | Extra service/model/database without demonstrated necessity |
| Independent judge | Check evidence directly, adjudicate contradictions, identify unresolved blockers and issue a scoped verdict | Majority voting, averaging safety failures, writing the candidate then approving it |

Use additional language/security SMEs as sequential passes within the SME role
only when a question needs them. There must be distinct agent identities for
SME, adversary, advisor and judge within each tool's decision. The judge must not
author that tool's adapter, oracle changes, or advocacy briefs. Root coordinates
and integrates; it cannot substitute for an independent judge.

Discover actual capacity each session and stay within the project limit of eight
agents including root. The planning session exposed four slots; the resumed host
exposes nine, so the project cap still applies. With four slots, run root plus
SME/adversary/advisor, then the independent judge after workers finish. With more
slots, overlap independent tool councils and reviews without overlapping writes.
Reuse worker identities across different
tools to control overhead, keeping a per-tool conflict-of-role register. No
recursive delegation or unverified concurrency claims. If independent roles
are unavailable, leave the decision pending rather than simulate a council in
one response. Independent agents using the same underlying model are independent
execution contexts, not guaranteed epistemic diversity; disclose that limitation.

Workers return read-only briefs; root alone updates shared dossiers/registers.
An implementation writer uses its own worktree and cannot judge that tool.
Independent briefs are submitted before workers read one another's conclusions.
Then allow one evidence-focused rebuttal pass; the judge receives all briefs,
raw references, receipts, dissent and unresolved questions, not just a summary.
Keep briefs to approximately 800 words each; bound the judgment similarly.

## Per-tool state and evidence

State sequence: registered → desk-reviewed → shortlisted → trial-validated →
decision-recorded → PR-ready → PR-open → owner-merged. Terminal branches:
retain-baseline, reject, defer. No state is inferred merely from another tool.
PR-ready is a local review state; it does not authorize merge or release.

Each dossier lives under `docs/research/code-insights/tools/<tool-id>/` when the
foundation is implemented. Its small structured record must include:

- Stable tool/mode ID, role, exact source/release/config hashes, corpus/oracle
  identities, current baseline SHA and links to raw redacted receipts.
- Installed artifact digest, platform, dependency closure and source-to-artifact
  relationship; unknown relationships remain unverified. Historical and current
  assessment IDs are separate; current ratings never inherit historical evidence.
- Named insight gap, existing capability overlap, required outputs and explicit
  success/failure criteria frozen before running the candidate.
- Tuning versus independently checked held-out fixtures, deduplication keys,
  unsupported-case denominator, normalization version and minimum useful gain.
  A protocol change after seeing results creates a new experiment identity.
- All four agent IDs/roles, independent briefs, rebuttal, direct claim references,
  disputed assertions, judge disposition and rationale for every blocker.
- Historical versus current ratings, score coverage/confidence and sensitivity;
  never replace unknown runtime evidence with a desk score.
- Safety/license assessment, output provenance, supported/unsupported scope,
  fixture accuracy and repeatability, operational footprint and CI implications.
- Verdict: retain, reject, defer, approve-trial, or approve-introduction-proposal;
  exact applicable evidence digest, limits, invalidation triggers and next action.
- Related issue, single-tool PR URL/base/head, focused and integration receipts,
  rollback, and eventual owner merge identity if actually observed.

Store sensitive source and full security findings privately; public receipts
contain safe excerpts and hashes. Authorship IDs are not cryptographic signatures.
No fabricated witness, model-routing, cost, or runtime receipts.

## Separate gates

1. Desk: verified need, baseline comparison, primary-source references, execution
   mode, plausible license/isolation route and feasible bounded oracle. The judge
   may approve a trial; this never approves introduction.
2. Trial: exact pinned artifact/config, sealed input, negative cases, no target
   script execution or unauthorized egress/writes, deterministic facts equal on
   repeated runs, and correct evidence mapping. Failures stay visible.
   Repeatability is limited to tested conditions; record fresh-process IDs,
   environment/locale/seed, cache state and relevant dependency identities.
3. Value: meet predeclared role-specific criteria beyond the existing baseline,
   including operational and review cost. A score alone cannot pass this gate.
4. Decision: independent judge directly checks receipts and each blocker. Missing
   license/safety/provenance evidence blocks introduction regardless of score.
5. PR: one tool/mode, opt-in adapter, regression/negative proofs, compatibility,
   minimal image/dependency footprint, rollback and applicable MADR/changelog.
   Refresh main, reconcile source, run required local act/security and PR checks
   against exact inputs, then create the owner-review PR targeting main.

Role-specific examples, to make precise before trials:

| Role | Useful value test | Excluded interpretation |
|---|---|---|
| Inventory | Correct language/file coverage missing from present inventory, with bounded cost and stable exclusions | COCOMO estimate as measured staffing cost |
| Packaging | Correct cited content retained under a fixed token/output budget, with secret/exclusion sentinels and no hidden execution | Token reduction alone as better semantic analysis |
| Structure/index | Additional oracle-correct declarations/references in a specified unsupported slice, no false cross-repository identity | More edges as more true integrations |
| Security/dataflow | Correct seeded findings and false-positive review cost beyond current tools | No findings as proof the project is secure |
| Model interpretation | Authorized scope, citation validity and separate proposal status | Fluent domain description as observed fact |

Do not invent universal accuracy or speed thresholds. Freeze numeric tolerances
and relevant fixture coverage per role before trials, including zero tolerance
for safety violations and false identity asserted as fact. Tiny fixtures are
acceptance evidence for those cases, not population-wide accuracy estimates.
At most two rounds per tool (desk and post-trial), one rebuttal per round; unresolved
blockers yield defer. Phase repair budget remains five attempts. Validator checks
record integrity and required evidence, not whether a model's opinion is true.

## PR boundaries and unattended operation

Before preparation, freeze the authorization envelope: three candidate attempts
maximum (failed candidates consume a slot), 20 minutes cumulative preparation
plus execution per candidate including one targeted packaging retry, one trial
at a time, zero paid calls, 2 GiB maximum new downloads and 1 GiB output per trial,
and 10 GiB actual disk reserve plus estimated output. These are conservative
planning limits, not measurements. A candidate exceeding them is deferred.
Preparation has no analyzed source, host credentials, Docker socket or broad
workspace mount; it is a separate reviewed environment with bounded network
access for pinned downloads. Extraction is offline against a sealed export.
Containment claims need negative evidence, not merely successful process exit.
The future validator must reject fabricated/missing receipts, changed artifact
hashes, wrong corpus, skipped cases and stale binaries. Mechanical validation
does not replace the judge inspecting underlying evidence.

Foundation changes introduce no candidate dependency. Subsequent PRs each contain
one tool/mode and its necessary adapter/tests/docs only. A second mode with a new
trust boundary gets a separate PR. A documentation-only rejection may be recorded
on #31 without creating a meaningless introduction PR.

Keep a PR/dependency ledger on #31 and acquire exact collaboration claims before
writing. An introduction depends on merged foundation code; wait for owner merge
instead of stacking unpublished dependencies. Independent desk research continues
while a PR waits. Conflicting shared-manifest edits are serialized by root, and
later writers start from refreshed main after the earlier dependency merges.
Do not rely on a prior PR's checks after its source or base changes.

Unattended defaults: no paid calls, private-code uploads, new permissions, large
models, broad installs, main writes, force-pushes, merges or auto-merge. Preserve
existing previews and user changes. Access or authority blockers park only their
dependent work; independent tools continue. If all eligible work is parked,
checkpoint the precise unblock conditions and stop without repeated retries or
questions. Waiting is not consent. Report missing evidence, never bypass gates.

The current request produces this plan. Future execution starts with package 0;
it does not start by installing all candidates. No council verdict, trial pass,
introduction PR or completed adoption is asserted by this document.
