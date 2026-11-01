# Remove recoverable worktrees after PR publication

* Status: applied to this operator's agent-assisted work; proposed for repository-wide adoption
* Date: 2026-11-01
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

1. Verify the PR URL, explicit base `stage-1-contract-and-fixtures`, published head
   SHA and remote feature ref. The exact local HEAD must be recoverable from that
   remote ref and match the PR head. A successful push alone is insufficient.
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
5. From outside the checkout, immediately recheck source, remote recoverability,
   activity and archive integrity, then use `git worktree remove <path>` without
   force. Skip primary or locked worktrees and any failed prerequisite. Do not use
   blanket pruning or deletion to overcome a refusal.
6. Record actual free space before and after; allocated sizes are estimates and
   must not be presented as measured reclaimed space.

An active checkout is marked **cleanup pending**, with its reason and owner, until
these checks pass. Publishing a PR must not terminate an in-progress user preview
or erase the session executing the handoff. Do not ask again for routine qualifying
cleanup under this authorization; ask only if a necessary action exceeds it.

### Continuing review

Recreate a temporary worktree from the retained local branch after verifying its
published identity, or fetch the remote branch and create a new local tracking
branch if needed. For an existing retained branch, from the primary checkout:

```sh
git fetch origin <feature-branch> stage-1-contract-and-fixtures
git worktree add <new-path> <feature-branch>
```

Check claims and PR feedback before writing. Integrate freshly fetched stage and
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
