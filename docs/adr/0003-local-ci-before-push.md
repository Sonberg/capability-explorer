# Run project-wide build and vulnerability gates locally before push

* Status: accepted
* Date: 2026-10-31
* Decision owner: repository contributors
* Coordination: [issue #3](https://github.com/Sonberg/capability-explorer/issues/3)
* Related: [MADR-0001](0001-github-work-coordination.md), [MADR-0002](0002-ubiquitous-language.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for Run project-wide build and vulnerability gates locally before push; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:5e2a2333637a49bf0ca8b7a372a3871831bc971a901b79b4b6eb6f5230a9f384`.
Previous decision status (historical): `applied in this workstream at the operator's request; proposed for contributor-wide adoption`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem statement

Remote CI failures consume shared runner time and interrupt contributor delivery.
The operator requests local Actions execution and dependency/CVE checks before
push for the whole project. Existing `make ci` covers fixture compilation,
TypeScript, .NET contracts/evaluation/extractors, all language golden gates,
Explorer, interpretation, capability and PostgreSQL store checks. Builds already
run inside hardened `scripts/sandbox` containers.

A direct macOS invocation reached store tests but failed because their Linux
Docker-socket assumptions were absent on the host. A local Linux Actions runner
can provide that environment while preserving the existing containerized gate.

## Decision drivers

* Discover failures before consuming remote GitHub Actions minutes.
* One full-project gate shared by local and remote execution.
* Check the actual outgoing source; do not mistake a different working tree for it.
* Cache downloads, but never turn a failed or skipped check into success.
* No GitHub token or Advanced Security licence required for local dependency scanning.
* Preserve existing contributor hooks and avoid conflicting CLI extension names.

## Considered options

1. Remote GitHub CI only.
2. Manual partial checks before pushing.
3. Local `act` execution of the real CI workflow, enforced by an opt-in repository hook.
4. GitHub Dependency Review as the only dependency gate.

## Decision outcome

### Markdown-only exception authorized on 2026-11-05

The operator authorized a bounded no-build profile. `scripts/ci/docs-check.py`
is the shared classifier for the local hook and hosted `ci` job. Only additions
or edits of regular, non-executable files qualify: `docs/**/*.md` except
`docs/adr/**`, `README.md`,
`AGENTS.md`, `CLAUDE.md` and `.github/copilot-instructions.md`. Compare the entire
current-main-to-head diff, not only the latest commit or a path-filtered sample.
An empty diff, deletion, rename from code, symlink, executable mode, unknown path,
code, workflow, issue form, script or dependency change requires full CI.
ADRs have source-bound ledger obligations and remain in the full profile.
Unavailable or invalid Git identity never yields lightweight success.

The documentation profile runs classifier regressions, `git diff --check`,
UTF-8/NUL checks and local Markdown link-target checks. It does not validate
anchors, external websites, rendered diagrams, private-data absence or technical
claims. Human review still applies. It starts no application build, dependency
restore, Docker runner or model service. The required hosted result remains `ci`;
do not use workflow path filters that leave the required check pending.

Both profiles require clean exact source, current-main ancestry and fresh target
verification. Local receipts record `mode=docs|full`; `make pr-check` recomputes
the profile and rejects mismatched source, target or mode. Receipts do not skip
future checks. Hosted PR alignment remains required in both profiles. Main
pushes qualify only if their exact before/after diff is Markdown-only; existing
protected post-merge evidence rules remain unchanged for other changes.

This policy change itself requires full local and hosted CI. It supersedes the
unconditional full-build statements below only for the specified Markdown
profile; it grants no push, merge, release or contributor-adoption authority.

Choose option 3, with Trivy filesystem dependency scanning and an Actions
advisory audit. Keep remote CI as the shared independent gate; local success does
not certify hosted-runner parity or authorize a push, merge or release.

### Entrypoints and tool roles

`make local-check` runs the explicit `ci` job in `.github/workflows/ci.yml`
using the Nektos runner through `gh act`. Install the pinned extension with
`gh extension install nektos/gh-act --pin v0.2.94`. Nektos `act` is the engine;
`nektos/gh-act` is its GitHub CLI packaging, not a separate validation layer.

`wayneashleyberry/gh-act` manages and audits action dependencies; it does not run
workflows. Both extensions claim the command `gh act`. Therefore
`scripts/actions-audit` uses Wayne's v2.7.0 binary under a distinct local cache
name, verifies its release SHA-256 before each execution, and runs only a
read-only High/Critical advisory audit. It does not automatically update actions.
This auditor requires GitHub authentication: local-check runs it on the host with
the existing GitHub login before starting act. Hosted CI supplies its job token
only to the audit step. The local runner and Trivy receive no token.

The runner image is pinned by digest. `--bind` preserves the host checkout's
absolute path, so nested `scripts/sandbox` Docker mounts refer to the same files.
Use native host architecture where supported; no forced amd64 emulation is needed
on Apple Silicon. Local caches persist in `.sandbox-cache`; the hosted cache
Action is skipped only under `ACT`. Locally, existing sandbox images are reused.

Explicit empty environment, input, variable and secret files avoid implicit
project files. An explicit empty `GITHUB_TOKEN` blocks act's automatic token
lookup. Runner configuration that can inject secrets or bypass execution is
rejected by the wrapper. No publish/deploy workflow is selected implicitly.

### Vulnerability policy

`make ci` runs `scripts/security-check` before compilation. The scanner image is
pinned in `config/security/trivy-image`. Scan npm and NuGet lockfiles, including
development and test dependencies and tracked fixture locks. Only generated
cache/checkouts, installed modules, build outputs and generated web assets are
excluded. High/Critical findings fail; unfixed advisories are not suppressed.
Implicit Trivy config/ignore files cannot silently weaken the gate.

The scanner receives a read-only source mount, a writable database cache, bounded
temporary storage, no Docker socket and no account credentials. The database
updates normally; required download/update failures fail the gate. This is a
dependency vulnerability check, not proof of absence of all security defects or
an image-OS/misconfiguration/secret scan.

### Push and hook policy

`make install-git-hooks` installs a repository-local dispatcher. It preserves
existing custom hooks by refusing to overwrite them. Every contributor installs
the hook locally; committing this file cannot configure another clone.

The hook requires clean tracked/untracked source and verifies that every outgoing
non-deletion ref resolves to the checked-out HEAD. Other commits must be checked
out and tested explicitly. Deletion-only updates introduce no source and need
no build. The dispatcher uses the current checkout's tracked hook, not a temporary
integration-worktree path. A missing tracked hook fails closed.

Before starting the runner, the wrapper fetches `origin/main` and requires it to
be an ancestor of the checked-out HEAD. Fetch failure or an unintegrated main
blocks the run. It fetches again after CI; a changed main invalidates the run.
The success receipt records `head`, `target=main` and
`base` (the freshly fetched stage SHA). Legacy `main=` receipts are rejected. `make pr-check` repeats the
fresh-target check and validates that receipt before PR creation, base changes or
review handoff. Code pushes are gated by the hook; discussion comments need no CI.
See [MADR-0005](0005-pr-lifecycle-and-conflict-prevention.md) for the complete
sequence and human merge authority.
Integrate changes explicitly and rerun CI; guards never rewrite branch history.
An update arriving after the final check still requires hosted mergeability/CI.

Each check runs the full gate; package/image/database caches are reused, but old
success receipts do not bypass execution. The wrapper detects source changes
during verification and serializes runs. A hook is cooperative local enforcement:
The hook rejects every outgoing update or deletion of `refs/heads/main`, even
when included with another ref. Local checks reject work on a checkout of main.
Hosted pull-request CI rechecks base changes (`edited` events) and requires
`main`. GitHub groups base, title and body changes under
`edited`, so those edits also rerun CI. Do not mark the required CI job skipped
for metadata edits: a skipped success could conceal an earlier failed run.
Minimize post-publication metadata edits and keep the existing caches/concurrency.
Push CI
continues to cover main and also covers stage. This preserves existing main CI
without authorizing delivery there. `scripts/check-main-alignment` remains only
as a compatibility alias to the stage check; it no longer fetches main.
Git allows bypassing hooks, so remote CI remains required.

## Consequences

Positive: most build and vulnerability failures surface locally, all existing
checks remain present, and no separate paid scanning service is introduced.
The same workflow defines local and remote behavior.

Negative: local checks require Docker, disk, network for missing caches/DB updates,
and developer compute time. They can be slower on the first run. Hosted Linux
and local native-architecture runners are not identical. Vulnerability findings
or advisory service outages can block a push and require explicit investigation;
no broad ignore list is introduced to make a check appear green.

## Validation and adoption

The hook fixture suite checks outgoing refs, dirty source, failure propagation,
runner/configuration handling, concurrent runs and preservation of existing hooks.
Acceptance additionally requires a real local `act` run of the whole CI job,
including store tests and Trivy. Record actual results in the build evidence.
Adopt the hook in each clone and review this ADR through the contributor workflow.

## Sources

* [Nektos act](https://github.com/nektos/act) and [gh-act packaging](https://github.com/nektos/gh-act)
* [Wayne Ashley Berry gh-act](https://github.com/wayneashleyberry/gh-act)
* [act runner differences](https://nektosact.com/usage/runners.html)
* [CVE-2026-34041 advisory](https://github.com/advisories/GHSA-xmgr-9pqc-h5vw): affected through 0.2.85, patched in 0.2.86
* [Trivy v0.76.1 filesystem CLI](https://github.com/aquasecurity/trivy/blob/v0.76.1/docs/guide/references/configuration/cli/trivy_filesystem.md)


### CI resource budget

Use one job with no matrix or model service. Cancel superseded runs on the same
PR/ref, keeping the complete gate on the latest commit. Pre-pull only the two
build images and the exact pgvector version used by Store tests; plain PostgreSQL
is not needed. Testcontainers retains its cleanup helper. Ollama is development
only and is never started by CI.

Cache only NuGet/npm downloads, the Trivy database and verified auditor binaries;
do not cache generated artifacts, local success receipts or Docker image tarballs.
Separate hosted package caches by OS/architecture. Local Docker layers are reused.
The observed unused plain PostgreSQL image was about 416 MB unpacked, not a
measured network saving. Node's Debian image supplies Bash/Git for offline fixture
work; slim/Alpine substitutions require measured compatibility before adoption.
Build/database image tags remain mutable; only the local runner and scanner are
digest-pinned. No claim of globally optimal image size or identical tag bytes is
made. These limits are distinct from deterministic application output tests.
