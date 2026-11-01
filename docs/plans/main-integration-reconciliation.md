# PR #27: stage-to-main reconciliation

## Scope and authority

The operator explicitly requires main as the sole PR merge destination and asked
to make existing owner PR #27 mergeable. It has stage as its source; no duplicate
PR is created. Main base: `54f0117c9e61a8bdd54c8fa87455d77738e0235e`.
Stage input: `427040d101fc658c364f96d7ebaadff8065ae3b2`. At inspection these
histories had 10 main-only and 150 stage-only commits. Sixteen conflicted files
were resolved in isolation. No direct main push, merge, reset or auto-merge.

MADR-0016 supersedes every earlier stage-only operational instruction. Old
stage receipts are historical. The catalog feature on
`feat/declared-integration-contracts` (`8a8a6b9`) stays separate, with preview5110;
it must be ported onto merged main before its own PR.

## Semantic resolutions

* C#: retain main constructors, new/base/this references and include-tests support;
  retain stage framework-aware package selection, reachable locked closure,
  credential-safe HTTP identities and provenance. Update owner focused mapping
  tests to exercise the real NuGet reducer and explicit project roots. Explicit
  empty assets remain deliberate suppression, not false incompatible-package facts.
* TypeScript: retain owner local-constant Next handler aliases and statically
  callable external handlers, with module ownership where no local function exists.
  Preserve source confinement, include-tests configuration, portable hashes and
  conservative factory/re-export handling. Canonical catch-all keys retain distinct
  `{*}`/`{**}` identities and raw display forms; the legacy route helper remains
  compatible. Focused old expectations are corrected; product fixtures, revision
  locks and goldens are not regenerated.
* UI: retain owner cited chat/focus, exposed/consumed integrations, deprecations
  and ranked Concepts. Retain stage shared LikeC4, reviewed Candidates/flows and
  binding review. Plain Glossary opens Concepts; Candidates and bookmarked terms
  retain source-flow navigation. Project language remains distinct from analyzed
  repository language. Chat reserves space only while open.
* Model transport: keep owner's tool-chat interface and 600-second chat timeout,
  retaining stage redirect denial, bounded buffers, credential/endpoint guards and
  generic response errors. Default model provider remains none.
* Build: preserve trusted native development and sandbox CI as separate recipe
  profiles. CI explicitly selects sandbox and needs only Docker on the runner.
  Image inventory follows the split Make files. Existing dependency pins from
  both branches are combined, including main's Markdown and Agent Framework.
* Delivery: main-only hosted guard, isolated fresh-main fetches, exact-source/base
  receipts and direct-main push refusal. Stage is only the source of existing #27.

No main file was deleted by the reconciliation. Merge history retains both input
commits. This is an intentional transition diff, not the next product feature.

## Validation and phase evidence

Evidence is retained in ignored `.sandbox-cache/reconciliation-*.log` files.
Independent workers used isolated worktrees; root alone integrated shared files.
No paid external inference was called. Native session cost/savings is unknown.

| Phase | Evidence and deductions | Final score |
|---|---|---|
| Specification | Exact PR/base/head, preservation matrix and main-only authority explicit. Deduct2: owner review of combined behavior remains external. | 98/100 |
| Pseudocode | Semantic resolution plan and bounded re-analysis/receipt paths; stale/unsafe inputs fail closed. Deduct2: existing source/race assumptions remain scoped. | 98/100 |
| Architecture | Combined build has zero warnings/errors; independent review confirms model credential/transport and execution boundaries. Deduct3: trusted native and sandbox profiles require explicit selection outside CI. | 97/100 |
| Refinement | C#159, TS155, Explorer222, Interpretation37, Capabilities43, main-alignment50 and re-analysis9 checks pass without skips; actual browser27 assertions cover preserved UI, no model calls or writes. Deduct3: full private-corpus/model/DB journeys not exercised. | 97/100 |
| Completion | Ordinary final-source pre-push act CI, exact-main receipt and PR27 hosted/mergeability verification pending. No completion score before that evidence. | pending |

Browser receipt and screenshots: `.sandbox-cache/main-reconciliation-browser`;
the receipt hashes the actual served JavaScript asset. Owner source files and
unchanged protected fixtures remain the acceptance baseline. Full local CI is run
through the normal push hook, avoiding a redundant full manual run immediately
before the unchanged push. Completion observations are recorded against the final
SHA in the PR handoff and exact-key project checkpoint.

What was not tested: private Glufs access, real model inference, persistent
deprecation writes, database-backed similarity, every viewport, or a complete
security audit. Known image/parser/inventory findings in #24 remain open; these
do not turn review publication into a security-complete claim.

## Owner merge and post-merge action

Update #27 only by a normal fast-forward of its observed stage head. Verify target
main, source SHA, hosted checks, mergeability and disabled auto-merge. Owner performs
the merge. If either remote changes, reconcile and repeat applicable validation.

After owner merge, fetch main and verify #27's actual merge SHA. Use a clean
isolated checkout of that source, then run `make EXECUTION=sandbox dev` with default
`DEV_REANALYZE=1`. Analysis runs in sandboxes before import; no stale receipt survives
a failed re-analysis attempt. Compare the private application/repository/config/
binding/artifact receipt with the owner. A dirty source flag or inaccessible input
is explicit, never a false identical-analysis claim. This post-merge run is not
claimed performed before the owner merge exists.
