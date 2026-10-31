# Main-aligned delivery and synthetic C# coverage

## GOAL

<goal>Reject stale-main local CI and PR preparation, and verify conservative C# calculation extraction with synthetic source projects.</goal>

## ASSUMPTIONS

- <repository>Sonberg/capability-explorer</repository>, account Tigon32; origin/main is the integration base.
- Execution and commits/push/PR are authorized by the operator. Never auto-merge.
- Base98be7a4; PR5 merged; PR7 glossary is separate. Issue3 records this package.
- Preserve fixtures, schema, dependency locks, thresholds and production extractor behavior.
- The existing seven package tests do not replace direct calculation/fallback tests.
- Unavailable main is a failed prerequisite, not permission to trust a stale tracking ref.

## STACK

- Existing Bash/Git local-check and hook, Makefile and isolated Git fixture harness (scripts/local-check, tests/local-ci.test.sh).
- Existing CSharpExtractor public API and xUnit sandbox tests (platform/tests/CapabilityExplorer.Extractors.Tests).
- SPARC evidence/traceability methodology only; Brain search returned sparc/docs/tutorial.md. No new runtime package implied by that documentation.
- Native isolated Codex worker implements tests; queen owns shared scripts/docs. Ruflo/AgentDB checkpoint is coordination per AGENTS.md/MADR0001.

## PHASES

S: requirements40, failures30, ownership30. Gate: inspect main/claims and executable shell syntax.
P: deterministic ordering40, error paths40, bounded work20. Fetch main before expensive work; require ancestor; capture base; refetch after CI and reject changes. PR check repeats fetch and validates clean HEAD plus exact receipt base. Gate: shell fixture tests.
A: interfaces40, source identity40, compatibility20. One helper prints verified main SHA on stdout; failures nonzero; no merge/rebase inside guard. Tests use local bare remotes, no network. Gate: focused harness and extractor build/tests.
R: meaningful assertions40, failure paths40, review20. Synthetic calculations test public behavior and graph preservation. Gate: focused tests, unchanged C# golden evaluation and independent review.
C: full CI40, main alignment30, docs/trace20, handoff10. Gate: fresh-main clean commit full act via installed hook, then pr-check before PR creation. If main advanced, integrate, commit and rerun; never reuse outdated receipt.

## STANDING RULES

Self-verify with commands; explain evidence before scoring /100. Below95 fix and regrade, max5 iterations. No skipped/empty passing gates. Commit phase evidence; checkpoints read/write exact keys in canonical AgentDB. Questions only for actual blockers; user has already authorized execution. Keep one writer per worktree; root alone changes shared files. Refresh main before CI and before creating/updating a PR; no force-push or automatic conflict overwrite. The remote can advance after the final check, so retain hosted CI and mergeability checks.

## COST GUARDRAILS

Use scripts first, Sol high for bounded synthetic implementation and Sol medium for read-only scope/review; queen integrates. No provider routing or paid services. Native monetary spend unknown; no fabricated savings. Any separately paid operation projected above$1 or20 calls requires approval. Existing one-job CI/images/caches only. Do not run local-check and push unchanged HEAD consecutively; push already runs the gate.

## DONE CRITERIA

- S: bash -n scripts/local-check scripts/check-main-alignment scripts/pr-check
- P/A: make test-local-ci test-extractors
- R: make evaluate-cs
- C: git push -u origin feat/main-aligned-delivery (installed full act hook)
- Overall: scripts/pr-check; git diff --exit-code; clean status; receipt HEAD/base equal current verified source/main.

Scope evidence, scores, command outcomes and untested areas are appended here.


## Executed evidence

Specification and design inspected current main98be7a4, issues3/4/6, PR7 and
HANDOFF. Shell syntax passed. S100/100 (iteration1): all named requirements,
failures and ownership paths specified. P100/100 (iteration2): the fixture setup
first needed explicit author identity inside bare test remotes; no host identity
or external service added. A100/100 (iteration1): public interfaces reuse Git,
Bash and the existing extractor; no dependencies or production extractor changes.

`make test-local-ci` passes42 checks, including fresh/stale/unavailable main,
main advancing during/after CI, changed ancestor main, final fetch failure,
concurrent tracking-ref changes, receipt locking, status failures, all prior
credential/configuration and hook checks. Independent review found and resolved
three gaps: failed status mistaken for clean, receipt read racing CI, and shared
tracking-ref overwrite. The reviewer rechecked fixes and found no blockers.

Worker3b42ca3 adds6 synthetic calculation cases. `make test-extractors` passed
13/13 (7 existing plus6 new), zero failures/skips, build0 warnings/errors. First
run exposed xUnit analyzer rules; corrected assertions without suppression.
Tests verify typed IR/ordered inputs, helper exclusion, unsupported loop graph
retention and unchanged supported neighbor, and three generated-source forms.
Independent read-only review found no blockers. Broader issue6 defects remain
outside this slice. Final C# golden and full act results will be recorded in the
PR/issue handoff against the final immutable source, not asserted prematurely.

Brain closeout: sparc/docs/tutorial.md supports explicit evidence traces; no
new runtime primitive required. Not tested: real remote changes after the last
PR preflight (an unavoidable non-atomic race), every Git/OS version, production
extractor mapping defects, or broader C# graph semantics. No paid provider calls;
native worker monetary cost unknown. The existing hierarchical ledger is only
coordination; native workers performed the observed work.


Refinement gate: `make evaluate-cs` passed10/10 artifacts, nodes176/176,
edges174/174, evidence350/350, calculations11/11, attributes112/112 and hashes11/11;
all thresholds passed, zero evidence errors. R100/100 (iteration1), grounded in
42 harness checks, 13 extractor tests, unchanged golden metrics and independent
review. Log: /private/tmp/ce-aligned-evaluate-cs.log. Final full-CI and PR-preflight
receipts belong to the immutable commit and will be linked from the issue/PR.
