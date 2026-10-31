# Coordinate PR updates and preserve review context

* Status: applied to this operator's agent-assisted work; proposed for repository-wide adoption
* Date: 2026-10-31
* Decision owner: repository owner and contributors
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [MADR-0001](0001-github-work-coordination.md), [MADR-0003](0003-local-ci-before-push.md)

## Context and problem statement

Several contributors and agents can prepare correct changes against different
versions of main. Individually green PRs can conflict when combined, duplicate
work, or invalidate review context when their base changes. New contributors
need a repeatable delivery process rather than session-specific instructions.

PRs #7, #8 and #9 demonstrated an independent feature alongside a dependent pair:
#9 depended on #8. All are now merged. The operator reports making these merges
and informing the owner for subsequent validation; that validation has not been
independently confirmed here. This human-performed exception does not grant an
agent future merge authority or establish that merging is itself approval.

## Decision drivers

* Keep the owner's review and merge decisions explicit.
* Avoid overlapping writers and unnecessary stacks of pending PRs.
* Validate current combined source, not stale branch or CI evidence.
* Preserve published history and explain changes to reviewers.
* Use existing Git/GitHub and local checks without a new coordination service.

## Considered options

1. Independent long-lived branches, refreshing only when GitHub reports conflict.
2. Explicit ownership/dependencies, regular main integration, exact-source checks,
   and human-controlled review and merge.
3. Agents automatically rebase, force-push and merge whenever checks pass.

## Decision outcome

Choose option 2. This ADR extends MADR-0001's work claims and MADR-0003's validation
policy. It does not change branch protection, GitHub permissions or hooks.
Repository-wide adoption requires explicit owner/contributor review recorded
here; committing or merging the document alone must not be called acceptance.

### Authority and responsibilities

The default is owner review before the owner merges. Agents may implement,
independently review, reconcile conflicts, and update authorized feature branches;
they must not merge PRs, enable auto-merge, rewrite published history or delete
branches/worktrees without explicit authorization for that action. A passed check,
claim, review recommendation or earlier human merge is not such authorization.

A human exception records who merged, which SHA, why the normal review order
changed, how the owner was informed, and any outstanding validation. Do not invent
missing reasons or imply post-merge review has occurred. No standing merge
permission follows from that exception.

Each package has one integration owner. Workers use separate worktrees with
acknowledged, disjoint paths. The integration owner alone changes shared contracts,
manifests/locks, pipeline wiring, HANDOFF and common documentation. Review feedback
that overlaps a worker claim is sequenced or explicitly handed over first.

### New contributor workflow

1. Read README, HANDOFF, MADR-0001 and this ADR. Use your own authorized GitHub
   account; Tigon32 is the configured account for this operator, not a shared login.
2. Fetch main, inspect open PRs and issue claims, then create a clean isolated
   branch/worktree from the fetched base. Record base SHA, exact scope, dependency
   PRs, integration owner and checks in the issue before editing.
3. Freeze shared interfaces before parallel work. Prefer focused new modules and
   avoid incidental formatting in files another contributor is reviewing.
4. Commit logical changes and run focused tests as implementation evolves. Explain
   behavior, compatibility and tests in the review guide; worker commits normally
   integrate into one cohesive package PR rather than one public PR per agent.
5. Reconcile main and dependency branches before full CI and before requesting
   review. Follow the validation sequence below. Pushes/PRs still require the
   operator's authorization; this workflow does not grant release authority.
6. Request human review with exact source/base evidence, dependencies, known limits
   and unresolved questions. Address feedback through additional commits and
   describe what changed; do not silently dismiss or resolve another reviewer's
   concern. Release implementation claims explicitly at handoff.

### Main alignment and validation sequence

Run from a clean feature worktree; preserve unrelated contributor changes.

```sh
git fetch origin main
git merge origin/main
# Resolve any conflicts semantically, test them, then commit the resolution.
# Once the intended source is committed and clean:
git push -u origin <feature-branch>  # installed hook runs full local CI
make pr-check
# Only then create the PR, change its base, or hand the updated PR back for review.
```

`local-check` fetches main and requires it in HEAD before CI, fetches again afterward,
and records exact HEAD/main on success. Code pushes to existing PRs are gated by
that same pre-push hook; `make pr-check` then verifies the receipt before review
handoff. It also runs immediately before PR creation or base/metadata changes
that affect review. Routine issue/PR discussion comments do not require a CI run.

If main advances or source changes, integrate and repeat validation. Never reuse
a receipt from another HEAD/base, skip hooks, weaken a gate, or run local-check
and then immediately push unchanged HEAD just to duplicate the same full gate.
Manual `make local-check` is available when no push is intended. Contributors
must install the hook in their own clone; tracked documentation cannot do that.

Remote CI and mergeability must be checked separately against the latest PR head
and base. A local text merge simulation does not prove the combined code builds.
A main update after the last check can still occur; these controls reduce conflicts
but are not an atomic remote merge lock. Before the human merge, recheck the actual
target state and refresh the branch if the evidence has become stale.

### Dependencies and bounded PR queues

Prefer independent PRs against main. Use one integration PR per coherent package,
with logical reviewable commits. Do not create more overlapping public PRs while
an earlier owner review is unresolved; independent local work may continue.

A dependent PR must name its parent, base branch, exact dependency SHA and required
merge order. A stack is an explicit coordination choice, not a default way to keep
all workers publishing. Avoid stacks deeper than one pending parent; deeper stacks
require an explicit maintainer decision recorded in the issue. Do not change a
parent's overlapping files from a child without an acknowledged handoff.

After the parent is merged by a human:

1. Fetch main and the parent/child states. Record the actual merge SHA and method
   if known; never infer ancestry from similar commit messages.
2. Integrate current main into the child without rewriting its published history.
   A squash/rebase merge may contain the same content under different ancestry:
   inspect the net diff and resolve duplicate/conflicting content intentionally.
   Do not blindly cherry-pick the parent again or assume it can be dropped safely.
3. Run focused integration checks, commit, and push through full local CI. Run
   `make pr-check`, retarget to main when appropriate, and inspect Files changed
   for leaked parent changes or missing child changes. Do not assume automatic
   GitHub retargeting produces the intended review diff.
4. Explain the old/new base and resolution to reviewers, then verify hosted checks
   and mergeability on the new base. Retargeting can make review comments outdated;
   preserve their context and re-request review of affected changes.

### Resolving conflicts

The integration owner reconciles both branches' intended behavior, coordinating
with authors when intent is unclear. Preserve distinct requirements from both
sides; never use wholesale ours/theirs as a substitute for review. Do not resolve
conflicts by lowering thresholds or deleting another contributor's work.

For generated files or locks, resolve their source inputs first and regenerate
with repository commands through the sandbox. Preserve fixture revision contracts;
announce any necessary exception before regenerating source-bound artifacts.
After a resolution, review the actual diff and run relevant tests plus the required
full local gate before publishing the new commit. Explain the resolution in the PR.

### Handoff, merge and follow-up

Use this compact PR handoff (fill with real observations, not desired results):

```text
Source HEAD / verified main / target base:
Depends on: none | PR number + dependency SHA + merge order
Changed behavior and reviewer guide:
Local checks: command, exit status, receipt HEAD/main
Hosted checks: current head/base, observed status
Conflict/base updates: none | old/new base and semantic resolution
Not tested / unresolved review or validation:
Claims released / remaining owner:
Merge authority: repository owner; auto-merge disabled
```

After a human merge, record its actual SHA and outstanding owner validation, release
remaining claims, and refresh dependent branches/HANDOFF. Plan the next package
from current main and current issues rather than the previous feature branch.
Do not start another implementation merely because an old checkpoint says next.
Do not automatically delete worktrees or branches at handoff.

## Consequences

Positive: newcomers can reproduce the same workflow, dependent PRs have visible
ordering, and reviewers can assess tested source and conflict decisions.
Negative: synchronizing branches and re-running mandatory gates takes time;
cooperative claims cannot eliminate races or guarantee conflict-free merges.
The default owner review can limit publication throughput; parallel local work
and bounded public queues keep that limit visible rather than bypassing it.

## Validation and adoption

Review the workflow against an independent PR, a dependent PR, a parent squash
merge, a main update during CI, and a human-performed merge exception. Existing
local CI harness tests cover stale main, failed refreshes, changed source and
receipt identity; no automated test can prove a human review occurred.

Owner/contributor adoption: pending. Record the explicit review decision and date
here before changing the repository-wide status. Historical merges #7/#8/#9 do
not serve as approval of a document written afterward.

## References

* [GitHub: keep a PR in sync](https://docs.github.com/en/pull-requests/how-tos/create-pull-requests/keeping-your-pull-request-in-sync-with-the-base-branch)
* [GitHub: change a PR base](https://docs.github.com/en/pull-requests/how-tos/create-pull-requests/changing-the-base-branch-of-a-pull-request)
* [Operator merge protocol](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6550082487)
