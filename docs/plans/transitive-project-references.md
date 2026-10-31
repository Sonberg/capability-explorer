Active authorized Brain Build: issue #6 defect 1, transitive C# project visibility. This follows PR #8, now independently verified as mergeable with hosted CI SUCCESS. Fresh main remains98be7a4. PR #8 is still open: begin implementation from main after it lands so the required alignment guards are present; do not merge it automatically. Revisit #3, HANDOFF and all active claims before starting.

## GOAL

<goal>For ordinary acyclic SDK C# project-reference graphs, preserve transitive compilation references so callers resolve reachable project types and methods without inventing direct dependency edges.</goal>

## ASSUMPTIONS

- <repository>Sonberg/capability-explorer</repository>; Tigon32; fresh origin/main only. Integrate required pending work after it lands; do not silently stack on an outdated branch.
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
