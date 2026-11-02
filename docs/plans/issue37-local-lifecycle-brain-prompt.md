# Issue #37: Brain Prompt execution contract

## GOAL

Implement the council-approved local lifecycle in issue #37: one cooperating host
worker, atomic admission, immutable run/attempt/input identities, conservative
recovery, serialized cancellation/publication and truthful outcomes. Deliver an
owner-review PR against fresh main. Baseline: `781393e32be2bb280bb2085a919c91eb08f8d8d5`.

## ASSUMPTIONS

- Capability remains a repository selection. AnalysisRun is accepted intent;
  Attempt is one execution. No automatic retry or multiworker execution.
- Local files remain the default; CE_DATABASE selects artifact storage only.
- Supported local filesystems only. No NFS, distributed leases or remote API.
- Unknown prior execution/publication blocks conflicting work until positively
  reconciled; PID absence is not sufficient authority.
- Existing images/packages, evidence schema, goldens and security gates remain.
- Owner alone merges. No force-push, auto-merge, release or paid provider calls.

## STACK

- Existing .NET 10 Capabilities library owns typed local records and OS-backed
  FileShare.None guards; CLI owns host process and publication execution.
- Explorer keeps trusted-local HTTP protection and never receives an engine socket.
- Existing Bash pipeline becomes preparation-only; canonical publication moves
  under the owning CLI's durable publication permit. Direct commands share it.
- Ruflo hierarchical coordination and Raft hive track the contract; Codex native
  workers execute isolated changes. Grounding: `ruflo/.claude/agents/hive-mind/queen-coordinator.md`.
  Tool registration/coordination is not execution or verification evidence.
- AgentDB checkpoints use the canonical primary `.swarm/memory.db` and exact-key
  readback. Grounding: `ruvnet-brain/plugin/scripts/ruflo-memory-hooks.mjs`.
- No new broker, database dependency, workflow engine or runtime image.

## PHASES

### Specification

Map A1–A11 from the issue to future executable tests:
A1 admission race; A2 second worker; A3 cancellation races; A4 crash/survivors;
A5 commit-before-confirmation; A6 publication ordering/stale completion;
A7 direct/shared-path contenders; A8 partial/enrichment/file mode;
A9 malformed/legacy/path/isolation; A10 canonical fact parity; A11 input attribution.
Gate: all criteria, non-goals, ownership and failure outcomes traced in this file
and independently inspected source. No passing runtime claim at this phase.

### Pseudocode

Under bounded metadata guard, atomically snapshot accepted configuration/bindings
and create one run record; reject another active capability. A host executor holds
a persistent OS-backed singleton handle before inspecting recovery or shared paths.
Started/publishing records with lost owners become unknown; block rather than
guessing cleanup or retry. Claim records spawn intent before launching a child.
Prepare into attempt-owned paths using accepted snapshots. Bind resolved revisions,
package/runtime/settings/artifact hashes before publication. Cancellation before
publication forbids effects. Persist publishing and hold its guard across actual
filesystem and optional PostgreSQL operations; late cancellation is too late.
Confirm each target independently. Missing confirmations remain unknown/partial.
Optional enrichment has a separate outcome. Never rewrite terminal history.

Complexity: bounded O(retained runs + input/artifact bytes), with explicit record,
input, log and enumeration limits. No database transaction spans extraction.
Gate: every A criterion has an error/recovery path and deterministic identity.

### Architecture

Shared namespace `CapabilityExplorer.Capabilities.Analysis`: FileRunStore, typed
records, persistent local lock files, input/manifest validation and disposable
publication permit. CLI host owns that permit during all effects; no environment
flag grants publication to an unowned shell. Workers never directly publish.
Explorer uses the same admission/cancellation boundary and run-scoped status.
Legacy uncertain markers remain preserved and block unsafe execution.

Gate: read-only architect/adversary review, source-bound interface contract and
actual local lock proof before claiming supported host behavior. FileStream.Lock
is excluded on macOS; FileShare.None requires actual cross-process qualification.

### Refinement

Execute focused .NET/API/process tests, script refusal and owned-container probes.
Use barriers/fault injection for races. Validate real PostgreSQL publication paths
with the existing pinned image. Run existing golden/TS/UI regression checks.
Gate: A1–A11 pass, no unresolved correctness blocker, independent code review;
unknown/unavailable profiles remain explicit rather than skipped green results.

### Completion

MADR-0022, language, handoff and changelog match the wired implementation. Run
normal exact-head/current-main act/security/pre-push and PR checks, publish the PR,
verify hosted checks and back up evidence before eligible worktree cleanup.
Gate: source-bound results, criterion-to-test trace and owner handoff; no claim of
distributed fencing, global filesystem atomicity or expanded Podman-worker support.

## STANDING RULES

Grade each phase /100 against Brain Build's rubric; below95 fix/regrade (max5).
Use actual instruments and evidence; commit after phase pass. Reviewers cannot
approve their own implementation. Root alone integrates manifests/shared scripts;
each writer has its own worktree. Keep source files under500 lines. Record every
material assumption and limitation. Continue after coordination commands.

## COST GUARDRAILS

Use native subscription workers; no external paid inference or model downloads.
No OpenRouter key is required. Subscription spend/savings are unmeasured; do not
invent receipt amounts. Reuse SDK/Node/PG images and bounded synthetic inputs.
Serialize full act jobs per daemon; parallelize independent code/reviews instead.

## DONE CRITERIA

Specification/pseudocode/architecture: `git diff --check` plus independent contract
review and full A1–A11 mapping above. Refinement: focused Analysis tests and process
checks must report nonzero executed cases, zero failures; canonical goldens pass.
Completion: `scripts/pr-check` succeeds after normal complete local act/security
gate on the final clean commit and freshly fetched main, followed by published
PR source identity and successful hosted CI. These are future gates, not results.

## Phase receipts

S: 97/100 (requirements35, constraints30, edge cases25, evidence trace7/10).
Deduction3: exact local lock and crash behavior remain implementation qualification.
Evidence: issue A1–A11, baseline source inspection and separate read-only architect
and adversary reports. Not tested: implementation or any runtime proof.

P: 97/100 (trace35, error/recovery30, boundedness20, instrumentation12/15).
Deduction3: interprocess and abrupt-death behavior require actual runtime tests.
Independent architect and adversary agreed that preparation must not publish and
that the owning .NET process retains the permit throughout actual publication.
