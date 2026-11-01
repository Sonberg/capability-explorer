# Merge PRs only into main and reconcile the existing stage history

* Status: accepted for the operator workflow by explicit directive; owner review of implementation in PR #27
* Date: 2026-11-01
* Decision owner: operator; repository owner retains merge authority
* Related: [coordination](0001-github-work-coordination.md), [local CI](0003-local-ci-before-push.md), [PR lifecycle](0005-pr-lifecycle-and-conflict-prevention.md), [retention](0014-pr-publication-worktree-retention.md)

## Context and problem statement

The operator explicitly superseded the earlier stage-only direction: **every PR
must merge into `main`**. The existing stage work must become safely mergeable
into main. PR [#27](https://github.com/Sonberg/capability-explorer/pull/27) already
proposes `stage-1-contract-and-fixtures` into main; creating another PR for the same
transition would duplicate review and invite conflicts.

At transition inspection, main `54f0117c9e61a8bdd54c8fa87455d77738e0235e`
had ten commits absent from stage `427040d101fc658c364f96d7ebaadff8065ae3b2`,
while stage had 150 commits absent from main. Main contains owner chat, constructor,
concept-ranking, deprecation and native-development work. Stage contains reviewed
LikeC4, glossary flows, mapping, security and delivery work. Both must be preserved.

## Decision drivers

One permanent merge destination, preservation of both histories and contributor
intent, exact-source validation, owner review, frugal local CI, and reproducible
post-merge analysis rather than stale artifacts.

## Considered options

1. Continue stage-only delivery and reconcile later.
2. Retarget without reconciling divergent code and validation policies.
3. Reconcile both histories in isolation, update existing PR #27, then deliver
   subsequent feature PRs only into main.

## Decision outcome

Choose option 3. **Main is the sole PR base and merge destination.** This is a hard
rule, not a default with an agent-selected exception. It supersedes stage-only
operational statements in earlier MADRs, prompts, handoffs and checkpoints.
Historical receipts remain historical; do not rewrite their original SHAs or targets.

* Start future isolated feature branches from freshly fetched main. Integrate
  current main before local CI and immediately before PR preparation.
* Explicitly create PRs with `--base main`. Hosted PR CI rejects every other base;
  local alignment/receipt validation fetches and binds main, including during
  concurrent worktree ref changes. Stale stage receipts cannot pass as main evidence.
* Preserve main through owner-reviewed PRs. No agent direct pushes, force pushes,
  resets, branch deletion, automatic merges or auto-merge. A new destination does
  not grant new merge authority. These local/workflow checks do not claim to be
  server-side branch protection.
* Existing PR #27 may retain stage as its source branch for this transition. Build
  a merge commit containing both exact histories on an isolated branch, resolve
  conflicts semantically, and push its tested head normally to stage only when
  it is a fast-forward of the observed source. A changed remote head requires
  reconciliation and renewed validation; never overwrite another contribution.
* Keep the new declared integration-contract catalog separate. Its commits and
  local preview remain recoverable; port it onto merged main after #27 lands.
  Do not smuggle unfinished new features into the history reconciliation.

## Conflict and execution policy

Preserve owner constructor/chat/deprecation/concept behavior alongside stage
security, framework selection, conservative identities and reviewed evidence.
Where old tests encode a superseded incorrect identity or include unrelated lock
entries, adapt focused tests with explicit rationale; never lower golden thresholds
or regenerate protected product fixtures to hide merge failures.

Keep trusted native development available through its existing host tools. CI and
agent validation use the sandbox profile; untrusted repository analysis always
runs through `scripts/sandbox`. Separate Make recipe profiles preserve both
workflows, avoiding duplicate SDK/Node setup in the Docker-only CI runner.
No new browser/model image is added to ordinary CI. Markdown and agent-framework
dependencies already on main are retained; lockfiles are regenerated from combined
inputs and remain subject to the dependency gate.

## Post-merge shared analysis

After the owner merges #27, verify its actual main merge SHA, fetch main, and use a
clean isolated checkout of that source. Run `make dev` with `EXECUTION=sandbox`
and re-analysis enabled. It must execute analysis before import; merely importing
existing artifacts does not satisfy this handoff. Keep analyzed repositories
sandboxed and use the same capability configuration, full repository revisions,
tool/image versions, package locks and binding inputs for operator and owner.

Record a reproducibility receipt: application SHA, capability/configuration hash,
available repository SHAs, artifact hashes/counts, command outcomes and explicit
unavailable inputs. Share only appropriate redacted metadata through issue #3;
do not upload private code, credentials or raw reports. Same localhost URLs alone
do not prove two machines display the same analysis. A failure or inaccessible
repository remains visible and cannot be called successful re-analysis.

Apply MADR-0014 cleanup to recoverable inactive worktrees after review publication,
preserving branches and private evidence. Active previews and sessions require
handoff first. Earlier documents using duplicate historical `0005` prefixes must
be cited by full filename; do not renumber existing decisions during this merge.

## Validation

Require main-only target and stale-receipt rejection tests, current-main ancestry,
all existing code/security/golden gates, retained constructor/chat/concept tests,
focused conflict regression cases and an actual browser check of combined UI.
Run full local act CI once for the final committed candidate through the ordinary
push hook, verify `make pr-check`, and inspect PR #27's exact head/base, hosted
checks and mergeability separately. Only then report it ready for owner merge.

## Consequences

The reconciliation diff is intentionally larger than a new feature PR because it
brings the previously separate stage history into main. Future work uses one
integration destination. Human review remains necessary, and concurrent changes
can invalidate mergeability or CI evidence until refreshed.
