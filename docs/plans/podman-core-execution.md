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

Runtime capability is separate from evidence truth. Brain source grounding:
`sparc/plugins/sparc/skills/sparc-review/SKILL.md`: exact evidence bindings and
independent verification, not a claim that a SPARC runtime is installed.
Mechanical work uses existing scripts; native subscription workers review it.
No external paid calls initiated; subscription cost and savings are unknown.
