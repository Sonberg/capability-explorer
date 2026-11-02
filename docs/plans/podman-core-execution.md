# Issue #34: first runtime-core slice

Source baseline: main `1e1122b62587eda7f3e2817c1619ba29ce292923`.
Owner: root integrator; branch `feat/podman-core`. Read-only security/test
reviewers inspect this tree; no other writer uses it. Coordinate through #3/#34.

## Specification

This first PR supports explicit runtime selection, diagnosis, direct sandbox
execution without a daemon socket, and the project lockfile scan. Docker remains
the default. Unsupported Podman entrypoints must reject before touching Docker.
Nested act/Testcontainers, full audit, dev/cancellation and peripheral execution
remain separate later slices; this PR does not complete issue #34.

| ID | Acceptance | Instrument |
|---|---|---|
| R1 | Literal docker/podman selection; missing/unknown/unreachable/wrong backend fails without fallback | Isolated argv/identity mocks and real doctor |
| R2 | Direct sandbox preserves hardening, read-only analyzed sources, offline networking, argv boundaries and writable trusted build cache | Negative tests plus Docker/Podman live probes |
| R3 | Lockfile scan retains pinned image, all severities/dev dependencies, no config/ignore injection and exit status | Scanner argv/failure tests and available live scan |
| R4 | Explicit Podman socket/nested/legacy entrypoints fail early; a Docker daemon cannot satisfy a Podman request | Guard tests with sentinel Docker executable |
| R5 | Doctor identifies actual CLI/backend/platform/rootless/connection, without changing global connections or disclosing credentials | Mock and live JSON evidence |
| R6 | Docker regression and ordinary exact-source local act/security/pr-check remain mandatory | Existing tests and complete pre-push gate |

Profiles: current Docker; dedicated macOS arm64 Podman-machine core; Linux rootless
core when a host is available. Missing hosts remain unverified. Preserve all
operator changes, existing containers/volumes and product goldens. New model
calls, rootful fallback, global socket symlinks and broad cleanup are unauthorized.

Final specification grade: 97/100. Requirements30/30, baseline25/25, negative
cases25/25, boundaries17/20: Linux runtime availability remains unknown and is
excluded from any current support claim. Instrument: six criteria with explicit
positive/negative proof routes; source audit and independent read-only reviews.
Not tested: implementation, live Podman, nested CI or complete project support.

## Phase ledger

### Pseudocode

1. Reject unknown engines and unsupported entrypoints before any engine action.
2. Resolve only literal docker/podman executable and literal argv. For Linux
   Podman force native mode; for macOS require a named local machine connection.
   Scrub ambient Podman remote selectors; never change global defaults.
3. Probe bounded client/server metadata, machine/loopback/rootless scope and
   backend identity. Failed Podman never invokes Docker. Capture context once.
4. Construct hardened sandbox/scan arguments. Offline overrides ambient network;
   source mounts remain read-only and no engine socket reaches extraction.
5. Propagate process failure and cancel only owned probes. Doctor emits sanitized
   bounded JSON, never raw connection identities, environment or credentials.

Complexity O(bounded metadata + argument count); probe time/output are limited.
Errors return nonzero without partial fallback. Mock all refusal branches, then
use actual Docker and a dedicated rootless Podman machine for core assertions.
Final pseudocode grade97/100: trace35, failures30, boundedness20, identity12/15
(nested socket identity is explicitly unsupported in this slice).
Not tested: adapter, runtime probes or later feature profiles.

### Architecture

Portable Bash library `scripts/lib/container-engine.sh` owns fixed selection,
bounded probe/exec and backend metadata; `scripts/container-engine-doctor` emits
the operational profile. Sandbox and lock scan source the same library. Native
and sandbox Make profiles share a narrow feature guard; untouched direct runtime
entrypoints reject explicit Podman before Docker calls. Existing audit verifier
continues validating historical Docker receipts unchanged, not arbitrary tools.

Core Podman does not expose sockets. Linux uses `--remote=false`; macOS uses
an explicit machine connection whose metadata marks a loopback machine and whose
server reports rootless Podman. Online networking uses a verified native default;
Docker's host-gateway behavior is not copied blindly. Host-service reachability
and nested Testcontainers/act require their later profile.

Use existing pinned images (qualified registry aliases where necessary), current
Docker default and existing CI. New Node tests use only built-in modules. No
host Node/jq dependency is introduced into ordinary sandbox execution.
Final architecture grade97/100: contracts30, trust30, compatibility25, cost12/15;
remaining3 points cover unavailable Linux/nested qualification beyond this slice.
Not tested: code or runtime compatibility. Independent architecture/test reviews
confirmed the need for enforced partial-scope guards rather than aliases.

Runtime capability is separate from evidence truth. Brain source grounding:
`sparc/plugins/sparc/skills/sparc-review/SKILL.md`: exact evidence bindings and
independent verification, not a claim that a SPARC runtime is installed.
Mechanical work uses existing scripts; native subscription workers review it.
No external paid calls initiated; subscription cost and savings are unknown.
