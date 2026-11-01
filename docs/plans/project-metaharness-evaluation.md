# Project and MetaHarness evaluation — 2026-11-01

The project's evidence-first static architecture is worth continuing. Native
workers, isolated worktrees, sandboxed tests and exact-source delivery checks have
provided useful results. Ruflo/Brain is useful as coordination and persistent
context; the installed MetaHarness is not demonstrated as an autonomous optimizer
or release authority. Keep it advisory until runtime measurements justify more.

## Scope and findings

This review examined mapping branch `e04c9a6`, reconciled with stage `5ce7f21`,
and the installed local Brain/Ruflo workflow. Subsequent fixes are recorded in the
mapping execution plan. Scores below describe that review snapshot, not an
unqualified grade for every product feature or the final PR. The earlier SPARC
95/100 grades assessed phase contracts; they were not whole-project grades.

1. **High: cached package selection could invent compatibility.** An executable
   sandbox probe requested Example.Package 2.0.0 with only 1.0.0 cached and returned
   1.0.0 with empty Missing/Issues. `PackageResolver.Pick` fell back below the minimum
   and used a hand parser. Sixteen new boundary cases initially produced eleven
   failures. This was missed by the previous green suite. Fix before PR: use pinned
   NuGet.Versioning, enforce bounds/prerelease rules and disclose graph conflicts.
   Full NuGet graph solving remains outside the contract.
2. **High for unattended operation: advisory hooks are not authorization gates.**
   Brain reported decision-gate and ground-ruvnet hooks killed by the Codex wrapper
   and failing open. This does not invalidate the independent Git/CI gates: the
   stage-advance test actually blocked a push after successful CI. Do not delegate
   release authority to advisory hooks; expose hook failures and retain exact-source
   mandatory checks.
3. **Medium: cost and optimization claims lack runtime receipts.** No routing
   receipts file was present at `~/.claude/metaharness/routing-receipts.jsonl`, and
   no OpenRouter key was present. Native workers were manually delegated, not
   automatically selected by MetaHarness. Native session spend was unavailable.
   Neither dollar savings nor learning-driven quality uplift was measured.
4. **Medium: worktree context is not demonstrably portable.** The canonical
   checkout and isolated integration worktree received different archetype/fit
   recommendations. Agent setup in the canonical checkout is not tracked in the
   feature worktree. Explicit task instructions and a canonical memory path worked;
   automatic propagation was not established. Two memory-store paths were reported,
   and one recall timed out. Select and verify one intended project context before
   each worker starts; avoid silently claiming that every store was searched.
5. **Medium: project validation is stronger than its breadth of evidence.**
   Synthetic replay establishes repeatability and named mapping changes, not private
   Glufs mapping improvement. The real-corpus replay/binding-preview package is the
   next product step. The diagram's physical-display/final-paint target remains
   unverified; earlier 5120-width measurements were 269 ms p95 against a 200 ms
   target. The newer 148.2 ms routing-ready DOM measure has a different boundary.
6. **Medium: full CI is less reproducible than the sealed replay.** The workflow
   pulls mutable SDK/Node tags and local act can reuse cached images. The delivery
   receipt records the act runner digest, not all sandbox image digests/platforms.
   Pin and record the actual runtime inputs in a focused CI follow-up. Ordinary CI
   runs replay-harness tests, while the full baseline/candidate replay is a separate
   explicit gate; integrate that source-matched proof where its cost is justified.

## What the instruments actually established

| Instrument | Observed result | Interpretation |
|---|---|---|
| Ordinary local act/Trivy gate | Passed on e04c9a6; zero configured high/critical findings | Exact tested source, not future edits or a penetration test |
| Sealed synthetic replay | 21 assertions, 17 exact changes, 38 source spans, repeated bytes equal | Fixed synthetic HTTP/Next corpus, not the private estate |
| Brain turnRecordingStatus | `ok`, 20/20 exact readbacks | Recording plumbing works; not proof of better decisions |
| Explicit AgentDB checkpoint readback | Exact-key persistence worked | Deliberate persistence, not guaranteed timely semantic recall |
| MetaHarness score, canonical checkout | fit61, compile22, coverage65, safety90, memory35; scaffoldReady false, 5/6 constraints | Static recommended-harness heuristics |
| MetaHarness score, integration worktree | fit53, compile22, coverage65, safety100, memory31; scaffoldReady false | Configuration-sensitive, not a runtime safety improvement |
| OIA dry-run audit | threat composite `clean`; genome `needs-work`, exit1 | Clean threat scan is not overall readiness; exit1 is a verdict, not a crash |

Installed `metaharness` 0.4.2 source `dist/repo-scorecard.js` computes compile
confidence from detected languages/commands/CI; memory usefulness from file,
language and token counts; estimated cost from agent count and a fixed cheap-tier
rate. It does not run our build, test recall or measure spend. The reported
$0.024/$0.048 values are estimates and are not used as savings evidence. The
installed Ruflo `oia-audit.mjs` explicitly aggregates threat-model/MCP severity,
separately from its genome readiness result.

MetaHarness MCP tools were absent from the session registry. The installed CLI
provided the read-only score and dry-run audit; no provider inference or evolution
experiment ran. Brain source retrieval grounded the advisory role in
`ruflo/docs/metaharness-user-guide.md` and
`ruflo/plugins/ruflo-metaharness/agents/metaharness-architect.md`.

## Evidence-confidence scorecard for the reviewed snapshot

These are reviewer judgments, not benchmark outputs. Overall **76.9/100** (mean
of eight); lowest **token/cost efficiency, 65/100**. Unverified behavior reduces
confidence; it does not itself prove an implementation defect.

| Dimension | /100 | Deductions and evidence |
|---|---:|---|
| Correctness evidence | 70 | −20 silent version fallback in executable probe/PackageResolver.Pick; −10 private-corpus outcome unmeasured in execution plan |
| Test honesty | 85 | −15 eleven version-boundary failures absent from the prior green 74-test C# suite; negative tests now required before handoff |
| Docs truthfulness | 85 | −15 HANDOFF still called merged #21 local/unpushed at review time; corrected in this package |
| Security posture | 85 | −15 configured dependency scan and static audit do not test end-to-end authorization, provider isolation or malicious-input exploitation |
| Token/cost efficiency | 65 | −25 absent routing/actual-spend receipts; −10 no controlled learning or routing baseline/candidate experiment |
| Reliability/CI | 70 | −15 failed-open advisory hooks cannot enforce autonomous action policy; −10 mutable sandbox images/unrecorded runtime digests; −5 observed recall timeout and unverified worktree context propagation |
| Maintainability | 80 | −10 hand-maintained version parsing duplicated package-manager semantics; −10 two reported memory stores complicate reproducible context selection |
| User experience | 75 | −15 measured 269 ms p95 exceeds the open settling target; −10 physical 5120×2160 display and final-paint acceptance untested |

The autonomous-policy limitation caps the workflow reliability assessment at70.
It does not imply that ordinary CI or the stage-alignment gate failed open.

## Recommended sequence

1. Correct the package-resolution defect and regenerate source-bound proof; then
   run the normal full gate on the final commit and request owner review on stage.
2. Keep the smallest effective topology: coordinator plus at most three isolated
   specialized workers. Add parallelism only for independent work with measurable
   benefit. Do not add another swarm server or inference provider by default.
3. Establish portable worktree context and structured per-task receipts containing
   source, task, actual model when known, elapsed time, outcome and validation.
   Record unavailable usage as unavailable. Make hook/recall degradation visible.
4. Test routing/learning against a fixed baseline before claiming improvement.
   Keep deterministic policy, artifact and delivery checks independent of model
   recommendations and self-assigned phase scores.
5. Proceed to the source-bound real-corpus and reviewed binding-preview package,
   preserving uncertainty and leaving final binding adoption to the owner.

## What I did NOT test

No private-corpus fidelity improvement, real provider inference/evolve loop,
automatic cheap-model routing, actual dollar savings, learning quality uplift,
distributed/restart-durable authority, full MSBuild/NuGet equivalence, penetration
test, production deployment or physical-monitor final-paint acceptance was tested.
The README quickstart was not rerun from a completely empty machine. Native
Agentic-QE tools were not used. Local act proof is not a claim of remote CI success.

## Receipts

Project evidence: [mapping execution](mapping-fidelity-execution.md),
[replay receipt](../evidence/mapping-fidelity-replay.json),
[routing measurements](../spikes/likec4-routing-diagnostics.md),
`scripts/pr-check`, `scripts/check-target-alignment`, `.github/workflows/ci.yml`.
Local diagnostic artifacts are ignored under `.sandbox-cache/`:
`resolver-evaluation-probe.log`, `metaharness-score.json`,
`metaharness-worktree-score.json`, `metaharness-audit.json`,
`metaharness-genome.json`, `brain-recording-status.json`, `push-ci-aligned.log`.
Hook failures are in the user's Brain cache, `hook-failures.jsonl`.
These local logs are not portable signed attestations; their relevant observations
are transcribed above without private source or credentials.
