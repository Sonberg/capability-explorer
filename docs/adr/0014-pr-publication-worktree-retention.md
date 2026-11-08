# Remove recoverable worktrees after PR publication

* Status: applied to this operator's agent-assisted work; proposed for repository-wide adoption
* Date: 2026-11-01
* Updated: 2026-11-09
* Decision owner: operator; repository owner and contributors for wider adoption
* Related: [MADR-0005](0005-pr-lifecycle-and-conflict-prevention.md), [MADR-0003](0003-local-ci-before-push.md)

## Context and problem statement

Linked worktrees duplicate dependency, build and scanner caches. Waiting for every
PR to merge retains these allocations throughout human review and can prevent
local validation through disk exhaustion. The operator requests cleanup after PR
creation, while preserving recoverability and the owner's review/merge authority.

## Decision drivers

* Reclaim disk promptly without losing code, evidence or another contributor's work.
* Keep human review independent of local checkout retention.
* Make review fixes reproducible from published commits.
* Avoid duplicate caches and unnecessary backups of reproducible dependencies.

## Considered options

1. Keep every worktree until its PR merges.
2. Remove recoverable, inactive worktrees after PR publication, with explicit checks.
3. Delete every worktree immediately when a PR number exists.

## Decision outcome

Choose option 2. For this operator, the request establishes standing authorization
to remove qualifying linked worktrees belonging to this project's work after their
PR is published, including draft PRs. It does not authorize cleanup in other
projects, deletion of branches, changes to main, force removal, or PR merges.
Repository-wide adoption requires owner/contributor review; publication of this
MADR does not itself demonstrate acceptance by other contributors.

PR publication becomes the default cleanup checkpoint. Merge is not required:
for an unmerged PR, recoverability is demonstrated by the published feature head,
not ancestry in stage. This supersedes MADR-0005's merged-only cleanup condition
for this case. Merged worktrees may still use its target-ancestry check.

### Required checks and handoff

Before removal, the integration owner must:

1. Verify the PR URL, explicit base `main`, published head
   SHA and remote feature ref. The exact local HEAD must be recoverable from that
   remote ref and match the PR head. A successful push alone is insufficient.
   Integrated worker histories without a matching published head may instead use
   the verified recovery-artifact path below; integration and exact-history
   recovery remain separate requirements.
2. Confirm the worktree is clean, including untracked files; inspect ignored files
   separately. Preserve non-reproducible notes, local validation receipts, necessary
   configuration and agent memory outside the worktree in a private verified
   archive. Keep credentials and raw security reports out of GitHub and public logs.
   Record which reproducible caches are intentionally discarded; do not archive
   node_modules, build output or scanner databases merely to retain their disk use.
3. Stop or hand off owned preview processes and containers, release collaboration
   claims, and verify no writer, active session, process directory, open file or
   container mount still depends on the checkout. Another contributor's ownership
   requires an acknowledged handoff. Uncertain activity means defer cleanup.
4. Record PR/head/base, original path and branch, backup path and integrity check,
   validation receipt identities, discarded caches and the recovery command in a
   durable handoff outside the checkout. Retain the primary checkout and branch refs.
5. From outside the checkout, immediately recheck source, remote or artifact recoverability,
   activity and archive integrity, then use `git worktree remove <path>` without
   force. Skip primary or locked worktrees and any failed prerequisite. Do not use
   blanket pruning or deletion to overcome a refusal.
6. Record actual free space before and after; allocated sizes are estimates and
   must not be presented as measured reclaimed space.

An ineligible checkout is marked **cleanup pending**, with its owner, exact blocker,
next review date or event, and required evidence/action until these checks pass.
Review pending entries at merge, handoff and renewed disk pressure; an expired
checkpoint triggers review, never automatic deletion. Publishing a PR must not terminate an in-progress user preview
or erase the session executing the handoff. Do not ask again for routine qualifying
cleanup under this authorization; ask only if a necessary action exceeds it.

### Merged-to-main cleanup checkpoint

The operator reaffirmed standing authorization on 2026-11-03 to remove local
linked worktrees whose work has been published as a PR and merged into `main`.
Review this inventory after an owner merge, at task handoff and when disk pressure
is reported. This is a required housekeeping checkpoint, not a reason to interrupt
active work or retain every completed checkout indefinitely.

All required checks above still apply. Verify the PR is actually merged with base
`main`, its recorded head equals the exact local HEAD, and its merge result is
reachable from freshly fetched `origin/main`. For squash/rebase merges, use the
PR's head and merge provenance; requiring the topic HEAD itself to be an ancestor
of main would incorrectly retain completed work. A branch name, PR number,
closed-but-unmerged state or old publication receipt is not sufficient.

Retain an advanced or dirty head until the reconciliation checks below pass.
Uncertain ownership, active claims/processes, a locked checkout or unverified
recovery evidence requires cleanup pending with its owner, blocker and next review
checkpoint. Preserve private non-reproducible evidence outside the checkout;
discard only inspected reproducible dependencies/build/scanner caches. Keep the
primary checkout, retained local branch refs and active implementation worktrees.
Remote topic-branch deletion remains the separate authorization below.

Record each candidate's path, PR/head/merge identities, eligibility checks,
removal result and measured free space in the durable handoff. Recheck immediately
before ordinary `git worktree remove`; never use force, wildcard deletion, global
cache purges or blanket worktree pruning to overcome an uncertain or refused
candidate. A future automated implementation must default to a read-only plan
and test these exclusion/race boundaries; no cleanup daemon is required.

### Integrated worker histories and dirty-work reconciliation

The operator authorizes a broader recovery method for inactive project worker
checkouts, not weaker deletion checks. A missing remote worker ref need not mean
permanent retention when integration and independent exact recovery are verified.
The primary checkout, active runtimes, ownership/claim checks, retained branches,
ordinary removal and separate publication permissions remain unchanged.

* **Patch-equivalent histories:** compare against freshly fetched `origin/main`
  and record its SHA, the original worker HEAD and the corresponding integrated
  commits/PRs. Patch IDs or `git cherry` support integration evidence, but do not
  establish exact-history recovery or account for merge resolutions. Inspect
  merge commits and any unmatched changes explicitly; unresolved differences
  remain cleanup pending. Preserve the original HEAD and required history in a
  Git bundle or equivalent self-contained recovery artifact outside the checkout.
  Record its checksum and included refs. Verify it and restore into a fresh
  repository that does not borrow objects from the original repository; confirm
  the exact HEAD and history before retirement. A local branch alone is not this
  independent proof. Keep the artifact until an owner-approved recovery-retention
  decision; this policy does not authorize its deletion or an extra push.
* **Duplicate dirty work:** compare base identities, staged and unstaged binary
  diffs separately, untracked files and relevant ignored content. Include file
  bytes, paths, modes and symlink targets in the recovery manifest. Equal
  `git diff HEAD` output alone cannot prove equality or preserve staging state.
  Preserve and independently restore the complete source state, including the
  original index/staging boundaries, private configuration and non-reproducible
  evidence outside both checkouts. Verify the restored state against the manifest.
  A surviving preview checkout is not by itself an independent recovery copy.
  Record any differences and the owner's choice of authoritative copy.
* **Unique dirty work:** obtain an explicit owner-authorized reconciliation or
  preservation handoff. Archive-only retirement must name the destination,
  recovery instructions and responsible owner without implying integration or
  acceptance. Apply the same independent restoration checks as duplicate work.
  Do not commit, publish, reset, overwrite or discard changes merely to make a
  checkout removable.

Classify ignored content explicitly: discard only inspected reproducible caches
and build outputs; preserve unknown content until classified. Recovery artifacts
must exclude reproducible dependencies, protect private data and remain outside
the retiring checkout. A checksum alone does not replace restoration testing.

A dirty checkout cannot be passed to ordinary removal unchanged. After the owner
acknowledges the verified handoff, separately authorize the exact source paths to
be restored/moved out of that checkout, with a final source-state race check.
Then verify it is clean and use `git worktree remove` without force. Without that
path-specific authorization, retain the dirty checkout even if its archive is
valid. A failed restore, changed source/HEAD, uncertain ownership or new runtime
dependency leaves the checkout intact and updates its pending review checkpoint.

The durable receipt records integration evidence separately from recovery proof,
authorization/handoff, archive location/checksum, independent restoration results,
source manifest, discarded generated paths, before/after state and measured free
space. This reconciliation path is manual; it does not implement a cleanup hook.

### Remote topic branches after owner merge

Remote branch retention is separate from the worktree cleanup authorization
above. The operator explicitly authorized a bounded audit/cleanup on 2026-11-03
and requested automatic deletion of merged PR heads. This expands only the
previous no-branch-deletion boundary for qualifying topic refs; it grants no
merge, history rewrite, main/stage/shared/release deletion or wider permissions.

For each authorized topic ref, record its exact current head, main-based merged
PR/head/merge receipt, reachable merge result, recovery identity, protections
and acknowledged absence of active ownership/open PRs. Squash/rebase requires
explicit PR provenance; `git branch --merged` alone is insufficient. Retain
advanced heads, unknown protections or ownership and any uncertain history.
Recheck immediately before deletion and use an exact expected-head guard;
a raced ref must remain intact, never retry deletion with a newer SHA.
Keep local branches and worktrees unless separately authorized.

Automatic head deletion is a separate GitHub repository setting requiring
sufficient administrative permission. It does not clean historical refs or
override protection rules. Read back the setting after changing it; refusal
means deferred, not enabled. In this pass Tigon32 had push/triage but no admin/
maintain permission, setting update returned HTTP404 and remained false;
ruleset inspection returned HTTP403. All 17 audit candidates were retained.
An authorized repository administrator must resolve the setting/protection
gap. Do not switch identity or bypass protections to complete cleanup.

This manual lifecycle is not an implemented cleanup scheduler.

### Recreating a review checkout

Recreate a temporary worktree from the retained local branch after verifying its
published identity, or fetch the remote branch and create a new local tracking
branch if needed. For an existing retained branch, from the primary checkout:

```sh
git fetch origin <feature-branch> main
git worktree add <new-path> <feature-branch>
```

Check claims and PR feedback before writing. Integrate freshly fetched main and
run the ordinary exact-source checks and pre-push gate for review fixes. Historic
receipts remain evidence for their recorded source; recreating a checkout does not
make them valid for changed source or a changed base. Repeat cleanup at the next
review handoff when its prerequisites hold. Keep auto-merge disabled.

## Consequences

Disk is reclaimed during review rather than after merge. Review fixes require
checkout recreation and dependency restoration. Warm caches and local preview
availability may be lost intentionally after handoff; code and non-reproducible
evidence must remain recoverable. This is a documented manual lifecycle, not a
claim that a cleanup scheduler or hook has been implemented.

## Validation

Review against an open draft PR, an advanced/missing remote head, dirty/untracked
and ignored files, an active preview, a locked/primary checkout, a failed backup
verification and a recreated review worktree. Every failed prerequisite must leave
the original worktree intact. The previous authorized removal of twelve merged
worktrees is historical evidence for careful cleanup, not proof of this new policy's
post-publication execution.

Also review a patch-equivalent worker without a remote ref, a merge-resolution
difference, a bundle with missing prerequisites, restoration accidentally borrowing
local objects, identical combined diffs with different staging, differing untracked
or ignored files, symlink/mode differences, an archive-only unique-source handoff,
missing path-specific cleanup authorization, and a source/runtime race after
verification. Failed checks retain the checkout with an owner, blocker and next
review checkpoint; passing recovery checks never authorize force removal.
