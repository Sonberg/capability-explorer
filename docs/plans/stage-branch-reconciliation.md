# Stage-branch reconciliation

Status: implemented on an isolated feature branch; owner review/merge pending.

## Explicit operator decision

On 2026-11-01 the operator clarified that future work, alignment and PRs target
`stage-1-contract-and-fixtures` exclusively. Main must not be damaged or changed
by this reconciliation. Earlier main-based instructions and checkpoints are
superseded by this decision and the corrected MADR-0001/0003/0005.

## Observed source state

| Ref | SHA | Observation |
|---|---|---|
| stage-1-contract-and-fixtures | `7fd2a2fe0905f20d6da7f2006bd76de5d2197d8c` | Ancestor of main; zero stage-only commits |
| main | `b1f0bdb36a012b9d5cf15180021e4840073cfb1c` | 63 commits beyond stage; preserved as-is |
| feat/main-aligned-delivery | `80cc6b9019eee6ac2b1178e2348f5f06102a9c7b` | Includes PR #9's eight commits absent from main |
| docs/pr-lifecycle-madr | `7c5f800ac291c36a641472869113e4d7f3cc1e67` | PR #10 lifecycle docs, corrected in this package |

PR #9 merged into its intermediate parent after that parent had already merged
into main. A PR marked merged therefore did not mean its work was delivered to
main. Earlier handoff statements implying otherwise are corrected here.

## Preservation and implementation

An isolated `fix/stage-branch-delivery` branch starts at stage. It fast-forwards
locally to the inspected main commit, then merges #9's parent history and #10's
documentation history. This one-time reconciliation preserves original commits;
it does not reset, revert, force-push, or update main. Future work aligns only to
stage, not to subsequent main changes.

The alignment helper fetches stage into a per-check ref, before and after CI.
Receipts bind exact HEAD, target name and target SHA. Old receipts are invalid.
The pre-push hook blocks main updates and deletions before any runner starts.
Hosted PR CI rejects other target branches. Existing main push CI is preserved
and stage push CI is added. No new images, matrix or paid services are introduced.

## Review and remaining work

Review the branch-policy changes separately from the historical SQL/glossary/C#
commits retained for reconciliation. Run sandboxed regression tests and the full
installed pre-push act gate, then `make pr-check` against fresh stage. The GitHub
handoff records exact source SHA, actual outcomes and remote target state.

Open one reconciliation PR with base `stage-1-contract-and-fixtures`. #10 is
superseded by its included and corrected documentation; do not merge #10 into main.
Only the owner decides whether to merge the reconciliation PR. No auto-merge.
Hooks are local guardrails and do not establish server-side branch protection.

The LikeC4/ultrawide spike remains parked, unpushed, in its isolated worktree. After
reconciliation, transplant only its intended feature changes onto fresh stage;
do not resume old main-based branches or carry over runtime coordination files.
