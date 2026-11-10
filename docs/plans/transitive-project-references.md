Active authorized Brain Build: issue #6 defect 1, transitive C# project visibility. This follows PR #8, now independently verified as mergeable with hosted CI SUCCESS. Fresh main remains98be7a4. The operator explicitly authorized current main plus PR #8 as a dependency while it remains open; do not merge it automatically. Revisit #3, HANDOFF and all active claims before starting.

## GOAL

<goal>For ordinary acyclic SDK C# project-reference graphs, preserve transitive compilation references so callers resolve reachable project types and methods without inventing direct dependency edges.</goal>

## ASSUMPTIONS

- <repository>Sonberg/capability-explorer</repository>; Tigon32; fresh origin/main plus explicitly authorized PR #8 c6b85a6. Refresh main at integration and before CI/PR; do not silently omit newer commits.
- Scope is issue #6 defect 1 only. Historical Glufs diagnostic counts are not a reproduced benchmark.
- Preserve portable/package assembly resolution, framework selection, fixtures, locks, schema and thresholds. No package-version unification, older-framework fallback, Next.js rules, URL normalization or bindings.
- Existing cached compilation, missing-reference and cycle behavior remains. Conditional ProjectReference, ReferenceOutputAssembly, DisableTransitiveProjectReferences and full MSBuild evaluation are separate work.
- Compiler visibility closure does not imply direct source dependency: keep DEPENDS_ON edges from declared references only.

## STACK

- Existing CSharpExtractor.Build in platform/src/CapabilityExplorer.Extractors.CSharp/CSharpExtractor.cs: it currently adds direct compilation references and propagates only PortableExecutableReference objects, dropping transitive compilation references. Reuse Roslyn objects already in the project; deduplicate compilation references by underlying compilation/project identity, not Display alone.
- Existing temporary-project xUnit harness in platform/tests/CapabilityExplorer.Extractors.Tests; no packages or execution of analyzed code. Reuse scripts/sandbox and make.
- SPARC gate/evidence method, grounded by Brain search sparc/docs/tutorial.md and sparc/CARD/sparc-card (methodology documentation/card, not proof of new runtime tooling).
- Native Codex workers and canonical AgentDB ledger per AGENTS.md and MADR0001. No additional runtime service.

## PHASES

S — acceptance40/boundaries40/ownership20: inspect main, issue6 and claims; write exact chain/diamond/missing/unrelated cases. Gate: make test-extractors baseline.
P — closure40/identity40/failures20: describe deterministic propagation over cached acyclic graph, avoid duplicate compilations and new undeclared graph edges. Gate: add failing public-API regression for A→B→C and demonstrate it fails for the intended missing resolution, not invalid source.
A — contract40/isolation40/compatibility20: implementation worker owns only CSharpExtractor.cs; test worker owns only new ProjectReferencesTests.cs, each in an isolated worktree. Queen owns integration, shared docs and checkpoint writes. Independent read-only review checks graph closure and package boundaries. Gate: build plus focused extractor tests.
R — cases40/determinism30/boundaries30: chain resolves C method/type through B without direct A→C; diamond resolves shared C without duplicate diagnostics and repeated byte differences; missing project remains MISSING_PROJECT_REFERENCE with partial coverage; unrelated discovered project stays invisible; direct DEPENDS_ON semantics unchanged. Gate: make test-extractors evaluate-cs.
C — full CI40/main alignment30/evidence20/handoff10: fetch/integrate main, commit clean source, run installed full local act pre-push, then make pr-check immediately before creating PR. If main advanced, integrate and rerun. Record exact source/base and independent hosted result. No auto-merge.

## STANDING RULES

One writer per isolated worktree; exact issue6 claims acknowledged before implementation, root owns shared files/checkpoints. Justify scores with evidence before grading /100; below95 fix/regrade, max5 iterations. Never pass skipped/empty suites or weaken thresholds. Batch optional questions; no secrets or paid fallback. Read latest exact-key project checkpoint; write source, evidence, decisions and next action and read it back. READY only after real public extractor execution. Status requests get done/in-flight/blocked/parked table.

## COST GUARDRAILS

Scripts first. One Sol-high implementer and one Sol-medium test worker after the contract, plus queen (three active agents). Reuse an idle worker slot for independent Sol-high review; escalate to Astra only for unresolved consequential ambiguity. Max4 including queen, never parallel writers in one worktree. No redundant nested swarms, models, database or new CI images. No automatic provider API routing; monetary cost unknown unless real receipts exist. Separately paid work projected above$1 or20 calls requires explicit approval. Focused tests while changing; full gate through push once at integration, no immediate duplicate local-check on unchanged HEAD.

## DONE CRITERIA

S: make test-extractors
P: make test-extractors (record the intended pre-fix regression failure; after the fix require exit0 and nonzero executed tests before advancement)
A: make test-extractors
R: make test-extractors evaluate-cs
C: git push -u origin <claimed-branch> (installed complete local act hook)
Overall: make pr-check; git diff --exit-code; clean source and matching HEAD/main receipt.

Expected output: narrow fix plus meaningful synthetic regressions, source-bound evidence, independent review, PR, issue6 handoff with claims released. Do not claim all Glufs mapping gaps fixed; no production reanalysis without a separate authorized scope. After creating that PR, review remaining issue6 items and produce the next bounded Brain Prompt.


## Execution evidence

Operator explicitly authorized PR8 as a dependency while open; no automatic
remote merge. Baseline main98be7a4: make test-extractors passed7/7, no skips,
build0 warnings/errors (log /private/tmp/ce-transitive-baseline.log). Dependency
c6b85a6 then integrated; its own test evidence13/13 is recorded in PR8, not
misrepresented as the main-only baseline. Exact path claims acknowledged on
issue6 comment6549870408. S acceptance40/boundaries40/ownership20:100/100,
iteration1. Brain pass: sparc/CARD/sparc-card; methodology only, no new runtime
stack. No paid provider calls; native cost unavailable. Not yet tested: proposed
transitive behavior. No loop-checkpoint helper is supplied by this repository;
canonical Ruflo exact-key checkpoints are used per the accepted prompt.

Frozen algorithm contract: recursively build/cache source compilations as before;
propagate each direct compilation plus its existing CompilationReferences in
stable discovery/reference order. Retain the exact existing PE filter/policy.
Deduplicate compilation references by Compilation object identity (not Display),
and PE/other references by existing Display behavior. Each project closure costs
O(reachable references plus current reference collection); total closure storage
can be O(V squared) in dense DAGs. No new global scan or package resolution.
Cycles/missing references follow existing guard. Direct declared DEPENDS_ON
emission remains untouched. Tests must show actual pre-fix failure, then passing
chain/diamond/negative controls and repeat byte equality after integration.


Pseudocode gate: the frozen closure/identity/error contract above covers all
acceptance cases and bounds dense-DAG storage. Pre-fix test commitc7c8130 produced
15 passed/2 expected failed/0 skipped, build0 warnings/errors: chain/diamond
reported COMPILER_CS0012 and missing resolved calls, while negative controls
passed. Log /private/tmp/ce-transitive-tests-prefix.log (sha256
f1cd0648f0012cdace20951f5a838e042e4068b2e71b6caa1ed68783d05d925b).
After implementationb6c3688 integration as710ac7d, `make test-extractors` passed
17/17 with0 skips and build0 warnings/errors; log /private/tmp/ce-transitive-fixed.log.
P100/100, iteration1: closure40/identity40/failures20. Brain closeout returned
sparc/CARD/sparc-card; no new runtime primitive needed. No paid provider calls;
native spend unknown. Broad framework/duplicate-assembly semantics untested.


Architecture gate: only9 added/2 removed extractor lines. The typed Roslyn
CompilationReference/Compilation APIs already exist in the dependency; reference
identity dedup is materialized before creating the compilation. PE predicate,
missing/cycle guard, direct dependency edge emission and all public DTOs remain
unchanged. Focused17/17 tests and build0 warnings/errors verify integration.
Independent Sol-high reviewer inspected finalb6c3688 and tests and found no
blockers. A100/100, iteration1: contract40/isolation40/compatibility20. Brain
closeout: sparc/plugins/sparc/skills/sparc-resume/SKILL.md (thin methodology retrieval, no additional runtime recommendation). No added dependency/service/model or paid routing.
Review limit: exported artifacts filter compiler diagnostics to source locations;
public tests do not directly observe all assembly-level diagnostics or reference
multiplicity. Internal identity dedup is verified by source review, not claimed
as runtime introspection. Duplicate assembly names/aliases remain out of scope.


Refinement: `make evaluate-cs` passed10/10 artifacts, nodes176/176, edges174/174,
evidence350/350, calculations11/11, attributes112/112, hashes11/11, precision58/58,
zero evidence errors, all unchanged thresholds passed. Log
/private/tmp/ce-transitive-golden.log. The real CLI extraction path executes in
this gate; no adjacent mock is substituted. All4 new public behavior tests passed
within17/17 total; diamond output byte equality and exact direct dependencies are
asserted. R100/100 iteration1: cases40/determinism30/boundaries30. No unresolved
review blockers. Brain closeout: sparc/CARD/sparc-card; no additional primitive.
No production Glufs reanalysis, dense-DAG benchmark, conditional/cyclic MSBuild
expansion or assembly unification tested. No provider API spend; native cost unknown.


Completion gate: full installed pre-push act exited0 for clean exact source
f527c9ef1933555ed82a27293bcde5cb4e066388 and main98be7a4, then pushed. Log
/private/tmp/ce-transitive-push.log. Trivy, action audit,42 harness checks,
fixture compilation,108 Node tests, all golden gates,17 extractor tests,
Explorer/interpretation/capability suites and PostgreSQL store integration passed.
Source/main alignment was verified before and after the gate. This final phase
record is documentation only and must itself pass the installed pre-push hook
before PR creation; the final receipt is reported in the issue/PR handoff.

Trace: chain/diamond -> ProjectReferencesTests named cases; repeat bytes -> diamond;
no invented direct edges -> AssertDependencies in chain/diamond; missing/unrelated
-> two negative controls; preserved fixture semantics -> evaluate-cs and full CI;
package boundaries -> unchanged predicate plus independent source review. No
migration/service/deployment required. Rollback is reverting the narrow extractor
fix; tests expose the reintroduced failure. No production deployment performed.
C100/100 iteration1: fullCI40/alignment30/trace20/handoff10. Brain closeout:
sparc/CARD/sparc-card; no new runtime primitive. Scores apply to this bounded
contract, not all issue6 mapping behavior. Untested limits remain as above.
Native worker cost unknown; no paid provider API execution or runtime model calls.

## Subsequent bounded qualification, 2026-11-10

The original deferrals above remain historical. The follow-up now honors literal
`ReferenceOutputAssembly=false` in an attribute or unconditional child element:
declared dependency edges remain, but the reference supplies no compilation,
transitive-project or package symbols. Absent metadata and literal true preserve
existing visibility. Conditional, property-valued, invalid or conflicting output
metadata is diagnosed as `UNSUPPORTED_PROJECT_REFERENCE_METADATA`, affects
coverage and does not silently grant compiler visibility. Full MSBuild evaluation
and `DisableTransitiveProjectReferences` remain outside this slice.

The parent ran the new regressions before the fix: nine intended failures with
ten existing/positive cases passing. The integrated constructor, project-reference
and resolution suite then passed 75 cases with zero skips, including an independent
Roslyn exclusion oracle and repeated byte equality. Publication and main acceptance
are separate gates; this does not establish private-corpus mapping completeness.
