# Communication style pilot

Date: 2026-11-05. All examples below are synthetic. They do not describe private
repositories, actual test results or permission to operate a service.

This pilot checks the [communication guide](../communication-style.md).
Its eight pairs cover two issues, two chat replies, two technical explanations
and two procedures. The rewrite direction alternates between A and B to reduce
simple ordering bias. Reviewers receive both texts; this is not a blinded study.

## Criteria declared before review

For each pair, recover the same actor, action, condition, negation, quantity,
obligation, uncertainty and evidence boundary. The rewrite must disclose no
private information. Rate fidelity separately from clarity and actionability.

The screening threshold is zero material meaning, obligation or privacy changes,
at least six of eight rewrites equally clear or clearer, and none materially
less clear. Any material defect requires correction and a targeted recheck.
Model review cannot satisfy independent human acceptance or prove effectiveness
across the repository. No sentence-length or readability score is a pass gate.

## Samples

### 1. Bug issue: incomplete observation

**A:** The graph's partial-coverage rendering currently classifies an unobserved
declaration as removed; remediate this mismatch without relaxing the
complete-coverage removal path.

**B:** With partial coverage, the graph labels a declaration that was not
observed as removed. Show it as not observed instead. Keep removal behavior
unchanged when coverage is complete.

Rewrite: A -> B. Acceptance: partial coverage must not establish removal;
complete-coverage behavior remains unchanged.

### 2. Change issue: candidates and review

**A:** Add a separate list of candidate targets. Do not show a candidate as a
reviewed connection. Acceptance: candidates remain visible, and the reviewed
connection count excludes them.

**B:** Introduce a candidate-target listing whose presentation remains disjoint
from reviewed connections; qualification requires candidate discoverability
and exclusion from reviewed connection counts.

Rewrite: B -> A. Acceptance: visibility and separation both remain mandatory.

### 3. Chat: bounded successful verification

**A:** Parser qualification is green across the 12 exercised input scenarios,
with authorization outside the exercised verification envelope.

**B:** All 12 exercised parser input scenarios passed. Authorization was not tested.

Rewrite: A -> B. Acceptance: retain the count and passing result without
claiming authorization verification.

### 4. Chat: blocked operation

**A:** Analysis remains blocked because the previous run's final state is
unknown. I did not clear the block or start another run.

**B:** Analysis admission remains inhibited by the prior run's unreconciled
terminal state; neither block clearance nor a subsequent run was performed by me.

Rewrite: B -> A. Acceptance: unknown is not failed; neither clearance nor a new
run occurred. No retry permission is implied.

### 5. Explanation: target certainty

**A:** A candidate target represents a possible destination rather than an
established runtime connection; review is separately recorded and cannot be
inferred from candidate presence.

**B:** A candidate target is a possible destination, not a proven runtime
connection. Review is recorded separately. A candidate does not imply review.

Rewrite: A -> B. Acceptance: possibility, runtime uncertainty and review remain
separate claims.

### 6. Explanation: reference and revision

**A:** A branch is a reference that can move. A revision is an exact commit.
Record the resolved commit, not only the branch name.

**B:** Branch references are mutable selectors, unlike exact-commit revisions;
the record must retain the resolved commit rather than branch nomenclature alone.

Rewrite: B -> A. Acceptance: preserve the distinction and recording obligation.

### 7. Procedure: recovery prerequisites

**A:** Before any retry, the operator must preserve the journal and obtain an
authorized reconciliation decision; a missing process ID does not constitute
termination evidence or permission to clear the unknown state.

**B:** Before a retry, the operator must preserve the journal and obtain an
authorized reconciliation decision. A missing process ID does not prove
termination or permit clearing the unknown state.

Rewrite: A -> B. Acceptance: both prerequisites remain requirements. Do not
create a recovery command or imply that these conditions alone authorize retry.

### 8. Procedure: safe evidence

**A:** Before publishing an issue, the author must remove secrets and private
URLs from any excerpt. Label redacted excerpts as redacted. Do not attach the
raw log. A neutral project name does not make the log safe.

**B:** Prior to issue publication, mandatory author-side excerpt sanitization
must remove secrets and private URLs, with redaction labeling retained;
raw-log attachment is prohibited, regardless of neutral project naming.

Rewrite: B -> A. Acceptance: retain the actor, prerequisite, labeling obligation
and unconditional raw-log prohibition in this procedure.

## Review receipt

Read-only evaluator: `ste-judge`, agent
`78eb76ca-f56f-4f5b-a06e-dd9f7980dc5c`; author/integration writer:
session `94729006`. The evaluator also judged the earlier proposal, so this is
a same-task review with shared context, sources and model lineage.

The initial review rated all eight rewrites clearer, but rejected sample 3:
the rewrite changed "scenarios" to "tests". That substituted an unsupported
evidence claim. The author restored "scenarios" and requested a targeted recheck.
The failed first result is retained; no average clarity score overrides it.

The targeted recheck also required the qualifier "exercised" to retain the
original scope exactly. The evaluator's final recommendation was conditional
on this wording: "All 12 exercised parser input scenarios passed. Authorization
was not tested." That exact correction is now in sample 3. No criteria changed
and no separate third review is claimed.

| Sample | Initial fidelity | Clarity | Final disposition |
|---|---|---|---|
| 1 | Pass | Clearer | Complete-coverage condition preserved. |
| 2 | Pass | Clearer | Visibility and reviewed-count exclusion preserved. |
| 3 | Fail | Clearer | Exact evidence term and scope restored as required by the targeted review. |
| 4 | Pass | Clearer | Unknown state and both actions not taken preserved. |
| 5 | Pass | Clearer | Candidate certainty and separate review preserved. |
| 6 | Pass | Clearer | Exact commit and recording obligation preserved. |
| 7 | Pass | Clearer | Both prerequisites and missing-PID limits preserved. |
| 8 | Pass | Clearer | Redaction and raw-log prohibition preserved. |

The corrected version meets the screening criteria under the evaluator's
conditional recommendation: the seven unchanged samples passed fidelity, sample
3 uses the required correction, and all eight were rated clearer. This small,
paired, unblinded synthetic review is not independent human acceptance, measured
operational improvement or formal standard conformity. Human contributor review
remains necessary before repository-wide adoption.

## Scoped follow-up: context and prose clarity

This additive follow-up extends the existing pilot criteria with eight neutral,
synthetic scenarios. It does not replace or re-rate the earlier sample review.
The existing fidelity requirement still applies: no material change to meaning,
obligation or privacy. Clarity is judged separately. The screening target remains
at least six of eight equally clear or clearer, with none materially less clear.

| # | Before | After | Fidelity check |
|---|---|---|---|
| 1. Technical and business meaning | "In this tool, the technical Capability groups 2 repositories for analysis; it does not mean a business ability." | "In this tool, a technical Capability groups 2 repositories for analysis. It is not a business ability." | Preserves the quantity, repository-selection context and distinction from a business ability. |
| 2. Context-specific ownership | "In the repository context, 'owner' means the person who selects source repositories. In the conceptual-model context, 'owner' means the person who approves model changes. The example does not identify who holds either role." | "In the repository context, the repository owner selects source repositories. In the conceptual-model context, the model owner approves model changes. The example does not identify who holds either role." | Names the same two contexts and meanings; does not assert whether one or two people hold the roles. |
| 3. Unknown run state | "The final state of run R-17 is unknown." | "Run R-17 has no known final state. Do not call it failed or terminated. The unknown state does not permit a retry." | Keeps the state unknown; neither failure, termination nor retry permission is inferred. |
| 4. Attributed concept correspondence | "The review note attributes a possible concept correspondence between records A and B." | "The review note says records A and B may correspond as concepts. It does not establish owner approval, record identity or a runtime connection." | Retains the attributed possibility and excludes approval, identity and runtime claims. |
| 5. Condition, negation, quantity and must | "If coverage is partial, the operator must not report an unobserved declaration as removed. The operator must retain all 3 records." | "With partial coverage, the operator must not report any unobserved declaration as removed. The operator must retain all 3 records." | Preserves the condition, negation, quantity and both must obligations. |
| 6. Literal command and identifier | "For run `run-17`, use `tool inspect --run run-17`." | "Inspect run `run-17` with `tool inspect --run run-17`." | Keeps the command and identifier literal and unchanged. |
| 7. Historical status | "In fictional revision R1, a revision note says the export was available at that time; current availability is unverified." | "The revision note in fictional revision R1 marks the export as available at that time. It does not verify current availability." | Preserves the fictional revision, note, historical status and uncertainty about current availability. |
| 8. Informal STE adaptation | "For ordinary prose, an informal preference is 'up to 80% STE style'; it is not a measured quota or claim of ASD-STE100 conformance." | "Use STE-inspired clarity principles as an informal preference for ordinary prose. 'Up to 80%' is not a measured quota or claim of ASD-STE100 conformance." | Keeps the informal preference and its non-measured, non-conformance scope. |

### Author self-check and review status

The parent inspection found lost quantity in case 1, altered actor/identity in
case 2, and invented historical context in case 7. The author corrected those
source/rewrite premises and made the informal preference explicit in case 8.
The parent/integration AI review rechecked the corrected eight pairs at commit
`96ce1e1b40a6ee95c5f7aad9a99c9d391ce47c0a` on 2026-11-10.

| Cases | Fidelity recheck | Separate clarity judgment |
|---|---|---|
| 1, 3, 5, 7, 8 | No material change to actor, meaning, quantity, obligation, uncertainty or privacy found. | Equally clear. |
| 2, 4, 6 | No material change found; context, evidence attribution and literal command remain intact. | Clearer role labels, evidence boundaries or action wording. |

This passes the bounded screening threshold by AI inspection: eight equally
clear or clearer, none materially less clear, and no remaining material fidelity
defect found. It is not independent human acceptance, measured operational
readability, exhaustive source review or formal standard conformity. The earlier
pilot ratings are unchanged; designated human acceptance remains separate.

## Implementation checks

These checks were recorded in the original local pilot before publication.
The delivery PR records subsequent integrated validation separately.

- Both issue forms parse as YAML and pass local field/ID/required-value checks.
  Their rendering and availability in GitHub have not been tested or published.
- All 14 local Markdown links and form guide destinations resolve in this
  worktree. The three agent entrypoints reach the shared guide through AGENTS.
  This is static discovery evidence, not proof that every client loads it.
- `git diff --check` passes. All seven changed files stay below 500 lines.
- `node scripts/decisions/check.mjs check` and `make test-decision-ledger` pass,
  including all 29 existing ledger tests. ADR records and ledger projections
  are unchanged.
- No runtime code changed. No runtime build, full pre-push CI, commit, push or
  PR publication is claimed. Private local configuration remains ignored.
