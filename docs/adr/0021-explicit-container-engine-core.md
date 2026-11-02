# MADR-0021: Explicit container engine and qualified execution profiles

- Status: Proposed
- Date: 2026-11-02
- Related: issue #34; MADR-0011, MADR-0016

## Context

Contributors need Podman without accidentally running Docker instead. A working
CLI or compatibility socket does not establish sandbox, nested-container or
security-audit equivalence. Existing Docker workflows must retain their gates.

## Decision

Select exactly `docker` or `podman` through `CE_CONTAINER_ENGINE`; only an unset
variable defaults to Docker. Never fall back or install a global Docker alias.
Capture and validate the selected context before execution. Metadata probes are
bounded and diagnostics omit connection credentials and raw paths.

The first Podman profile covers direct `scripts/sandbox` execution without a
socket and `scripts/security-check`. macOS requires an explicit named loopback
rootless Podman machine connection. Linux forces native rootless mode. Runtime
diagnosis reports observed facts and a context fingerprint, not certification.

Preserve non-root users, dropped capabilities, no-new-privileges, read-only root
and analyzed sources, bounded resources, offline networking and argv boundaries.
The trusted project workspace/build cache remains writable. Podman uses keep-id
for host-owned caches and fully qualified references to existing pinned images.

Until separately qualified, explicit Podman requests for full Make pipelines,
act, Testcontainers, analysis workers, dev services, full security collection and
database experiments fail before legacy Docker execution. Ordinary CI and PR
publication continue through the existing Docker gate; this is not a bypass.

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
