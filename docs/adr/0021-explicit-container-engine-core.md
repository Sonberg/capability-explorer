# MADR-0021: Explicit container engine and qualified execution profiles

* Status: accepted
- Date: 2026-11-02
- Related: issue #34; MADR-0011, MADR-0016

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for MADR-0021: Explicit container engine and qualified execution profiles; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:5362b6996016b47480fd9e84b9e35587cc170f8b60eac2e77071ae7c74a8d6ea`.
Previous decision status (historical): `Proposed`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context

Contributors need Podman without accidentally running Docker instead. A working
CLI or compatibility socket does not establish sandbox, nested-container or
security-audit equivalence. Existing Docker workflows must retain their gates.

## Decision

Select `docker` or `podman` through `CE_CONTAINER_ENGINE`; when unset or set to
`auto`, prefer an installed Docker CLI, otherwise use Podman. Validate the
selected engine and never fall back after selection or install a global Docker
alias. Capture and validate the selected context before execution. Metadata
probes are bounded and diagnostics omit connection credentials and raw paths.

The initial Podman profile covers direct `scripts/sandbox` execution without a
socket and `scripts/security-check`. Native local development services and the
native `make dev` flow are now additionally qualified through the same validated
engine context. macOS requires an explicit named loopback rootless Podman machine
connection. Without `CE_PODMAN_CONNECTION`, macOS accepts exactly one running
Podman machine. Linux forces native rootless mode. Runtime diagnosis reports
observed facts and a context fingerprint, not certification.

Preserve non-root users, dropped capabilities, no-new-privileges, read-only root
and analyzed sources, bounded resources, offline networking and argv boundaries.
The trusted project workspace/build cache remains writable. Podman uses keep-id
for host-owned caches and fully qualified references to existing pinned images.

Explicit Podman requests for full Make build/test pipelines, sandbox execution,
act, Testcontainers, analysis workers, full security collection and database
experiments fail before legacy Docker execution. Ordinary CI and PR publication
continue through the existing Docker gate; this is not a bypass.

## Alternatives

Global aliases/sockets were rejected: they conceal backend identity and affect
other projects. Converting every command at once would couple unrelated failure
modes and require unmeasured nested-container trust. Docker-only remains valid
but does not satisfy contributors who require rootless Podman.

## Consequences and validation

Support is incremental. Mock tests cover refusal/failure paths; opt-in live
probes verify actual sandbox properties on each available engine. See
[runtime guide](../podman-core.md) for the qualification matrix and limitations.
No new runner, service image, model download or paid inference is required.

Follow-on PRs must qualify engine/socket identity, .NET Testcontainers cleanup,
act nesting, host networking, dev lifecycle and complete audit receipts before
removing the corresponding guards. Unsupported hosts remain explicitly untested.

## References

- [Podman run](https://docs.podman.io/en/latest/markdown/podman-run.1.html)
- [Podman machine](https://docs.podman.io/en/latest/markdown/podman-machine.1.html)
- [act custom engines](https://nektosact.com/usage/custom_engine.html)
