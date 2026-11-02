# Issue #31: research foundation execution

Scope: tool-free, dependency-free validation foundation. Base main
`1e1122b62587eda7f3e2817c1619ba29ce292923`; branch
`feat/code-insights-foundation`, isolated `/private/tmp/ce-research-foundation`.
Root is the sole writer. Read-only scc council members own separate role briefs.
No live repository collaboration/lease service is defined by tracked scripts;
GitHub issue #31 records scope and ownership instead of invented lease receipts.

## Specification

Acceptance criteria and intended instrument:

| ID | Observable requirement | Verification |
|---|---|---|
| F1 | Historical ratings recompute and weights total 100; unknown current ratings cannot borrow history | Node unit/negative tests and `specification` gate |
| F2 | Read a sealed miniature source corpus with exact hashes and independently declared oracle; malformed/path-escaping/symlink inputs fail | Corpus gate and temporary-directory tampering tests |
| F3 | Tool desk records require distinct SME/adversary/advisor/judge identities and claim references; unresolved blockers prevent introduction | Council negative tests and per-tool desk/decision gates |
| F4 | Runtime claims require artifact/config/corpus/baseline identity and existing matching receipt bytes; stale or fabricated declarations fail closed | Receipt, digest and state-separation negative tests |
| F5 | Per-tool introduction gates reject bundled tools/modes, absent rollback, wrong PR base and unmerged foundation dependencies | Per-tool PR negative tests |
| F6 | Future research phases remain incomplete without their evidence, while foundation checks pass independently | CLI tests for foundation versus global research completion |
| F7 | Ordinary sandbox CI runs the cheap foundation tests without candidate installs/new services | Make integration plus full local act/security/pr-check before publication |

Constraints: no candidate dependency, external paid inference, source execution,
schema/production UI changes or merge authority. Preserve the protected product
fixtures. New research fixtures use synthetic source only. Machine checks attest
structural integrity and observed bytes, not whether a human/model opinion is true.
No full research or tool adoption claim is in scope. Private Glufs remains #23.

Specification gate: acceptance table contains F1–F7, bounded scope and explicit
failure cases; independently executable acceptance starts in Refinement.
Final specification score: 97/100 (coverage30/30, baseline30/30, boundaries25/25,
ownership12/15: cross-process enforcement relies on existing GitHub/manual
coordination, not a distributed lease authority). No known architectural flaw.
Not tested in this phase: validator implementation, candidates, CI.

## Phase ledger

### Pseudocode

1. Parse only explicit CLI modes; unknown arguments/stages fail, never default to
   success. Resolve the checkout and constrain every evidence path beneath it.
2. Load bounded JSON, reject traversal/symlink components, read bytes and recompute
   SHA-256. Verify register arithmetic and sealed corpus/oracle independently.
3. For each tool stage, validate its proposal identity and role-specific criteria;
   bind all four independent role records to the canonical proposal digest.
4. Verify claim references and actual brief artifacts. A pending/defer/reject
   record may be complete, but cannot advance the trial/introduction state.
5. Trial checks require actual receipt files, matching identities, zero exit and
   complete non-skipped cases. Rehash sealed inputs and outputs; compare repeated
   canonical facts from independent process IDs. Never run receipt commands.
6. Introduction requires a second judge decision over the trial digest, no open
   blockers, one tool/mode and main-only PR scope, opt-in/rollback and merged
   foundation. Ordinary local CI remains separate and mandatory.
7. Global research phase checks fail if required dossiers/evidence are absent;
   foundation checks validate only F1–F6 and cannot claim full issue completion.

Complexity: O(total bounded evidence bytes + registered modes + cases), with
explicit limits on file bytes, record/array counts and path depth. No unbounded
tree walks, network access, analyzed scripts or command execution in validation.
Error paths return exit 1 with the violated invariant. Tests inject absent,
tampered, malformed, stale and role-conflicting data, including valid-looking
declarations with no artifacts. A consistent maliciously authored receipt cannot
be authenticated by a hash; independent review remains required and disclosed.

Final pseudocode score: 97/100 (oracle/comparability35, negative cases30,
reproducibility20, measurement limits12/15: no runtime candidate evidence exists).
Instrument: explicit F1–F7 trace and algorithm/failure-path inspection.
Not tested: production implementation or candidate execution.

### Architecture

- `scripts/research/evidence.mjs`: standard-library bounded path/JSON/hash API.
  Public inputs have JSDoc types; references are `{path, sha256}` values.
- `scripts/research/tool-gates.mjs`: pure council/stage checks plus injected
  evidence reader. No shell, network, package loading or promotion capability.
- `scripts/research/check-code-insights.mjs`: strict CLI dispatch and foundation
  arithmetic/corpus checks. Global research phases fail closed until a separately
  reviewed study executor provides actual phase evidence; this foundation does
  not turn the master prompt's future commands into fabricated green results.
- `tests/research-foundation.test.mjs`: real temporary files and mutation tests;
  synthetic passing tool receipts are labelled test-only, never public trial data.
- `config/research/foundation.json`: frozen source corpus and oracle references.
  `tests/fixtures/research/` is independent of protected product goldens.
- Existing sandbox Make profile adds only `node --test` in its cached Node image.
  No manifest/lockfile change, model/service/image or mandatory candidate install.

Dependency direction: CLI → gates → evidence; tests import the same public APIs.
No new ce.v1 schema or production path. Evidence references cannot escape their
root or follow symlink components. A JSON declaration is not an authenticated
attestation; bytes and identities are checked, truth is independently reviewed.
The root integrator alone writes shared files. Deferred global phase gates are
intentional scope boundaries, not completed research claims.

Final architecture score: 97/100 (fit30, provenance30, integration25, cost12/15:
runtime deployment costs of future candidates remain unknown). Instrument:
acyclic module/scope inspection and F1–F7 to module/test mapping above.
Grounding: `sparc/docs/tutorial.md` and
`sparc/plugins/sparc/skills/sparc-review/SKILL.md`, retrieved this run: immutable
evidence identity is distinct from evidence truth. No SPARC package installed.
Not tested: implementation, candidates, containment or deployment.

### Refinement

Final scoped score: 97/100, four repair/verification iterations. Correctness and
failure-path requirements F1–F6 have executable positive/negative tests. The
independent `/root/foundation_final_review` reran 58 tests, all passed, none
skipped, and found no remaining scoped blocker after the final fix.

Fixed across iterations: diagnostic expectation; proposal-only council hashes
replaced by complete packet binding; strict numeric budgets; recursively rehash
the actual source entries behind corpus manifests, not only manifest bytes.
Regression tests reject changed claim URLs/verdicts/deleted blockers and source
mutation behind an unchanged manifest. Data integrity is not authentication.

Proof commands: `node --test tests/research-foundation.test.mjs`,
`make EXECUTION=sandbox test-research-foundation`, and
`node scripts/research/check-code-insights.mjs tool --id scc --stage desk`.
The scc desk record yields `defer`; its trial gate deliberately fails. Its judge
`/root/scc_final_judge` confirmed the transcribed record and recomputed proposal
and full packet hashes independently. This is a desk verdict, not a tool trial.

Rubric: correctness/evidence40/40, isolation/failures25/25, scoped utility22/25,
repeatability10/10. Deduction: the utility is limited to bounded evidence
integrity; hashes cannot authenticate process claims or independent authors.
Those boundaries are documented in MADR-0019 and the foundation guide; no claim
is made that the validator proves underlying evidence truth.
Not tested: candidate runtime/containment/performance, private repositories,
larger-than-limit artifacts, research phase executor, release/deployment.
Full local CI and publication remain the Completion gate.

### Completion: foundation only

Final scoped score: 97/100, one completion iteration. Full local act CI passed
for implementation commit `1dad7d660b46fe6abe1f3370088c78dea619e0bb` against main
`1e1122b62587eda7f3e2817c1619ba29ce292923`. The CI job runs normal security,
fixtures/goldens, .NET/Node/extractor/UI regressions and 58 new research tests.
The independent implementation review passed after the source-manifest fix.

F1–F6 map to `tests/research-foundation.test.mjs`; F7 maps to the ordinary
`make EXECUTION=sandbox local-check` job with the new dependency-free target.
`make EXECUTION=sandbox pr-check` verifies the receipt's exact head/main base.
This documentation-only completion commit must receive its own ordinary pre-push
gate before publication; the implementation receipt is not reused for that push.
The final published HEAD and final CI receipt belong in the PR handoff.

Rubric: traceability35/35, actionable scoped decisions30/30, honest limits20/20,
handoff/cost12/15. Deduction: full study completion and owner adoption remain
outside this bounded package. No known unaddressed foundation architecture flaw.
Scope is reviewable foundation delivery, not a release/deployment or issue close.

External paid inference calls: zero. Standard-library scripts did mechanical
validation; native subscription agents supplied independent reviews. No cheap
router/checkpoint scripts were invented or installed. Subscription cost/savings
remain unknown. Canonical AgentDB checkpoints use exact-key readback.

Parked: expanded scc oracle/executable qualification, other tool councils and
trial executor, private-corpus/image-audit acceptance (#23/#24). No merge or
auto-merge is authorized. After owner foundation merge, refresh main and begin
one tool's next evidence slice; separate PR per introduced tool/mode.

What was not tested: candidate runtime, large-artifact streaming qualification,
candidate containment/performance, private repositories or production deployment.
Full research phases deliberately remain incomplete and fail closed.

Each later phase records its instrument, final grade, limits and source-bound
commit. Phase commits are milestones; final PR checks bind the final clean HEAD.
No new router is installed: standard-library scripts do mechanical work; native
subscription agents perform independent review. External paid calls: zero;
subscription spend and savings are unknown, not estimated as zero.
