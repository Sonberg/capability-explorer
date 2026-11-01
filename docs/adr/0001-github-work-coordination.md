# GitHub work claims and isolated queen–worker delivery

* Status: proposed
* Date: 2026-10-31
* Decision owner: repository contributors; Tigon32 proposes this protocol
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [PR #2](https://github.com/Sonberg/capability-explorer/pull/2)

## Context and problem statement

Several contributors work on the same branch. Local agent memory and an agent's
private plan do not tell another contributor which files are being changed.
Parallel implementations of the same capability waste time and produce conflicts.
We need visible ownership without adding a coordination service or treating issue
comments as atomic locks. The existing handoff identifies shared integration files.

## Decision drivers

* One writer per worktree and one acknowledged owner per overlapping path.
* A durable, contributor-visible history of intent, changes and evidence.
* Cheap coordination, bounded concurrency, deterministic validation.
* Explicit distinction between proposed work, active work and merged work.
* No authority inferred from silence, a lease, model votes or local memory.

## Considered options

1. Private plans and local AgentDB only.
2. GitHub issue claims, isolated worktrees and a designated integration owner.
3. A new distributed locking service with automatic expiry and reassignment.

## Decision outcome

Propose option 2. GitHub is the contributor-facing record. Ruflo/AgentDB mirrors
local coordination and evidence; it does not replace GitHub or grant permissions.
This ADR remains proposed until contributors explicitly accept it in issue #3 or
the reviewing PR. Adoption by one session does not bind another contributor.

### Claim protocol

1. Verify `gh api user --jq .login` is `Tigon32` for this operator. Other
   contributors use their own authorized accounts. Read open issues, PRs, recent
   comments and `docs/HANDOFF.md`; fetch the target branch before claiming work.
2. Open or reuse one issue per work package. Record the owner, UTC time, exact base
   SHA, branch/worktree, goal, exclusions, exact owned paths, shared files,
   dependencies, acceptance commands and next update time.
3. The designated integration owner acknowledges each implementation claim in an
   issue comment before that worker writes. A parent claim may acknowledge its
   own worker allocation explicitly. Check other claims immediately before the
   acknowledgement. A conflicting existing claim requires its owner's explicit
   handoff or agreement; silence is not consent.
4. Use states `proposed`, `claimed`, `active`, `blocked`, `ready-for-review`,
   `released`, `merged`. Every transition names the previous state and the owner.
   Planning claims reserve documentation only, not the future implementation.
5. Post updates at phase boundaries, on scope changes or blockers, and at least
   every 60 minutes during active work. Long-running work names the next update
   time. A missed update makes a claim uncertain, not automatically available.
6. Resolve overlap by narrowing paths or sequencing changes before writing.
   If owners cannot agree, pause only the conflicting lane and ask the designated
   maintainer to decide. GitHub comments are not a distributed lock service.
7. Handoff includes commit SHAs, actual commands and exit codes, untested areas,
   outstanding risks, and remaining ownership. Release claims explicitly. Mark
   `merged` only with an actual merge reference.

### Target-branch alignment before validation and PRs

Operator correction (2026-11-01): the sole delivery target is
`main`. This supersedes earlier main-based instructions.
Never push, merge, revert or rewrite main. Explicitly create PRs with
`--base main`; owner review/merge authority is unchanged.

At the operator's request, each delivery fetches `origin/main` and integrates it
before the full local CI gate. `scripts/check-target-alignment` rejects a HEAD that
does not contain the freshly fetched commit, including fetch failures. It never
merges, rebases, or resolves conflicts automatically. After integration, commit
and run the full gate against that clean source. Do not create a PR from an old
branch just because its earlier checks passed.

Before PR creation, base changes or review handoff, run `make pr-check`. Code
updates are gated by the installed pre-push hook; discussion-only comments do not
require CI. See [MADR-0005](0005-pr-lifecycle-and-conflict-prevention.md) for the
full sequence, merge authority, conflicts and dependency retargeting. The command refreshes
main and checks the local CI receipt against both HEAD and that main SHA. If main
advanced, integrate it and rerun the gate. Record both SHAs in the handoff. GitHub
can change after a check, so this is not an atomic remote merge guarantee; hosted
CI and the final mergeability check remain required. After opening a PR, revisit
issue3/HANDOFF and relevant open issues, use Brain Prompt to bound the next slice,
and acknowledge its exact ownership before implementation.

### Agent execution and shared state

The queen is the sole integration writer and assigns workers exact scopes.
Each writing worker gets its own worktree rooted at the recorded base; read-only
reviewers can share a checkout. Shared manifests, lockfiles, evaluator settings,
Makefile, pipeline scripts and handoff documentation belong to the integration
owner. A dependency failure suspends dependent workers until the contract is
reconciled. Preserve failed worktrees for diagnosis.

Use a hierarchical swarm and a shared hive-memory record for assignments,
interface decisions, evidence and dissent. Ruflo records coordination; native
Codex subagents perform work. Select supported CLI/MCP operations by live help;
never assume a recorded worker is an executing process. Consensus informs the
queen, while executable acceptance gates decide correctness. No vote overrides
a failing gate or expands authority. Start with at most four active agents
including the queen, bounded by the host's actual capacity.

GitHub comments should contain summaries and artifact references, not secrets,
raw user prompts, private source dumps or token values. Reconcile GitHub state
before restoring an old AgentDB checkpoint. A checkpoint is historical evidence,
not permission to resume an expired or conflicting assignment.

### Comment template

```text
State: proposed -> claimed | Owner: <account / worker> | UTC: <timestamp>
Issue: <url> | Base: <sha> | Branch: <branch> | Worktree: <local identifier>
Goal / exclusions: <bounded behavior>
Owned paths: <exact paths or narrowly scoped directory prefixes>
Shared files / integration owner: <paths / owner>
Dependencies / conflicts checked: <issue links, commits, result>
Validation: <commands; actual results when available>
Next update: <UTC deadline> | Handoff / release: <state and next action>
```

### Consequences

Positive: ownership and handoffs are visible to humans and agents, parallel work
is bounded, and reviews can reproduce evidence from exact commits. No new service
or paid model dependency is required.

Negative: acknowledgement adds a small delay, comments can become stale, and
GitHub offers no atomic path locking. The protocol depends on cooperation and
must not be represented as mechanically enforced fencing or consensus.

## Validation and acceptance

A reviewing contributor should verify a sample claim, acknowledgement, scope
change and release in issue #3; missing transitions remain missing evidence.
Adopt this ADR through explicit review, then change its status and record the
decision link. Do not call it accepted merely because the file was committed.

## More information

* [Next work package](../plans/deterministic-sql-calculations.md)
* [Existing handoff](../HANDOFF.md)
* Grounded Ruflo references: `ruflo/.agents/skills/hive-mind/SKILL.md`,
  `ruflo/.agents/skills/hive-mind-advanced/SKILL.md` and
  `ruflo/.claude/agents/hive-mind/queen-coordinator.md` (Brain search, 2026-10-31).
