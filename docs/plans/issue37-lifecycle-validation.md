# Issue #37: validation and owner handoff

This is a bounded implementation and qualification record, not full issue
closure or release approval. Proposed [MADR-0022](../adr/0022-local-analysis-lifecycle.md)
and the [operating guide](../local-analysis-lifecycle.md) define the limits.
The final local CI receipt and PR checks bind the committed source SHA; older
author receipts below qualify their stated profiles rather than a later binary.

## Acceptance trace

| Criterion | Executable evidence / result |
|---|---|
| A1 | `AnalysisRunTests.Concurrent_admission_accepts_once_and_preserves_prior_history`; `AnalysisApiTests.Concurrent_admission_accepts_one_run_and_preserves_its_identity`. |
| A2 | `AnalysisProcessTests.Worker_lock_excludes_an_independent_process_and_process_death_blocks_reuse`; native worker/direct-command contention probe described below. |
| A3 | Store cancellation transitions; `AnalysisExecutionTests.Cancellation_before_spawn_prevents_process_effects` and `Cancellation_after_actual_spawn_requests_exact_cleanup_and_preserves_uncertainty`; stale-run API cancellation tests. |
| A4 | Independent owner death before execution and `AnalysisSurvivorTests.Owner_death_preserves_live_child_and_staging_and_blocks_execution`: real surviving child and staged sentinel, followed by blocked recovery. |
| A5 | Actual PostgreSQL publication followed by missing terminal confirmation and `Process_death_after_actual_database_commit_blocks_republication`; generation remains one and recovery refuses reuse. |
| A6 | Store publication/cancellation ordering and monotonic terminal tests; real publication rejects late cancellation and preserves golden fact bytes. |
| A7 | Global ownership plus native independent `analyze-worker`, `analyze` and `analyze-all` contenders: both direct commands refuse before modifying the existing aggregate receipt. |
| A8 | `Later_repository_failure_retains_confirmed_partial_effect_without_false_success`; store enrichment outcome preserves core success; API status/file mode uses no database fixture. |
| A9 | Traversal, newline, symlink, incomplete/legacy/domain and engine-policy refusal tests. New admission policy contains credential names only; offline extraction has no credentials/socket. **Partial:** existing private-source Check still uses an Explorer GitHub token. |
| A10 | Two complete native-host/Docker extraction runs over the local `web-ts` fixture with `PACKAGES=0`, `GITHUB_AUTH=none`: accepted hashes, all resolved inputs including runtime, and artifact inventory hashes equal; run and attempt IDs differ. Existing goldens remain unchanged. |
| A11 | Frozen-input mutation, durable binding-presence, permit identity, immutable manifest/repository/artifact coverage, caller binding omission, artifact tamper and frozen worker-default policy tests. |

## Observed qualification

- Store author: 29 focused lifecycle tests passed on both native macOS and
  pinned Linux, with no skips, through worker-defaults commit `bb788769`.
- API author and integration: 21 focused API/capability tests passed on native
  macOS; the factory uses physical temporary paths, preserving symlink refusal.
- Integration execution tests passed on native macOS, including a real database
  commit followed by process death, spawn cancellation barriers and exact-label
  Docker cleanup with an unrelated sibling preserved.
- Ten Node helper/wrapper tests passed; literal argv and pre-ownership receipt
  preservation are checked independently of the managed controller.
- First full local act run at `18c927a`: golden thresholds passed for all 18
  artifacts; Contracts 202, Evaluation 25, Extractors 159, Explorer 243,
  Interpretation 37 and Capabilities 72 tests passed. Store had 154 passes and
  one failure: the new Docker CLI probe ran inside the CLI-free SDK image.
  Trivy reported zero findings in scanned dependency lockfiles.
- The corrected profile marks only that new probe `Profile=HostDocker`.
  Sandbox CI retains all existing Store tests and the socket-backed database,
  crash and lifecycle tests. Native `test-store` is unfiltered; the explicit
  host qualification command is documented in the operating guide. Final local
  act/security and hosted checks must be assessed from their final-SHA receipts,
  not inferred from the first run's partial results.

Private replay/contender receipts and command logs are retained outside Git;
the worktree cleanup handoff preserves these rather than dependency caches.
No claim is made about paid-model savings, power-loss behavior, arbitrary
descendant quiescence, distributed ownership or unqualified host combinations.

## Review outcome and outstanding acceptance

The non-author council judge found no remaining architecture blockers after
immutable-manifest, binding-presence and worker-policy corrections. Architecture
is acceptable for owner review with the documented limitations.

Full-issue Refinement remains below its 95 gate (94/100): A9's broader Explorer
credential prohibition is unresolved. Completion is not claimed for all A1–A11.
This PR is a reviewable implementation handoff, not permission to weaken the
security requirement or silently remove existing private-source functionality.
Keep #37 open for an owner-reviewed credential-boundary follow-up.

Live-child cancellation and ordinary nonzero sandbox exit currently remain
unknown and globally block new execution pending offline reconciliation, even
after exact-label cleanup. A host .NET 10 runtime is required. Native macOS
workers cannot share a journal with a Linux-VM Explorer. These are material
operating limits, not tested guarantees of automatic recovery.
