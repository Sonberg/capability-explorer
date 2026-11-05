# Podman core profile

This is the native core and analysis profile of issue #34, not complete project Podman support.
Docker is preferred when available; automatic selection uses Podman when Docker
is not installed. The existing full local CI/security gate still uses Docker and
remains mandatory before pushing a PR against current main.

## Commands

```sh
scripts/container-engine-doctor
make dev DEV_OLLAMA=0
CE_CONTAINER_ENGINE=podman CE_PODMAN_CONNECTION=my-rootless-machine scripts/container-engine-doctor
CE_CONTAINER_ENGINE=podman CE_PODMAN_CONNECTION=my-rootless-machine scripts/sandbox --offline node node --version
CE_CONTAINER_ENGINE=podman CE_PODMAN_CONNECTION=my-rootless-machine scripts/security-check
CE_CONTAINER_ENGINE=podman CE_PODMAN_CONNECTION=my-rootless-machine make dev DEV_OLLAMA=0
CE_CONTAINER_ENGINE=podman CE_PODMAN_CONNECTION=my-rootless-machine make analyze CAPABILITY=weekly-sales
make test-container-engine-core
```

Install Podman and create/start your own rootless machine using the official
Podman instructions. The project does not install a privileged helper, change
global connection defaults or create a Docker socket alias. When unset,
`CE_CONTAINER_ENGINE=auto` selects Docker if its CLI is installed, otherwise
Podman; this tests executable availability, not daemon health. The selected engine
is validated and never replaced after a failure.
Set `CE_CONTAINER_ENGINE=docker|podman` to pin it. On Linux omit
`CE_PODMAN_CONNECTION`; native rootless mode is forced. On macOS, automatic
selection accepts exactly one running qualified Podman machine; use an explicit
connection for analysis and to disambiguate multiple running machines. Unknown or empty
engine values fail.

macOS connection metadata must mark a machine and identify an SSH connection on
127.0.0.1 to its user-scoped Podman socket. Arbitrary remote/rootful connections
are outside this profile. The doctor prints versions, platform, rootless state,
connection name and a fingerprint; it does not print SSH keys or raw endpoints.
The validated machine URL and identity path are captured as explicit arguments,
so a later change to its named connection cannot redirect the same invocation.
Docker core currently requires a local Unix endpoint. TLS/remote Docker is
outside this initial adapter scope.

Native `make dev` starts the same host worker for either engine; the existing
commands and UI Analyse action are unchanged. `scripts/analyze`, `analyze-all`
and `analyze-worker` accept `CE_CONTAINER_ENGINE=auto|docker|podman`.
`scripts/container-engine --analysis-snapshot` freezes JSON containing the backend,
executable, argument prefix and fingerprint. Host-only `CE_ANALYSIS_RUNTIME` carries
that snapshot; every sandbox and cleanup invocation validates it and reprobes the
selected context. No alias, Docker-compatibility symlink or socket alias is used.

Both engines execute one capability and one extractor at a time, even for old
frozen worker/job settings of 2 or 4. Newly admitted settings are 1; legacy options
in the range 1-4 validate but are capped at 1. Uncertain execution remains `unknown`;
serialization is not permission to reset or consume blocked production work.

## Qualification matrix

| Path | Docker | Podman |
|---|---|---|
| Doctor and direct sandbox without socket | Existing default, live core probe | Live macOS arm64 rootless-machine probe |
| Lockfile security scan | Existing mandatory gate | Same pinned Trivy arguments; live result recorded separately |
| Linux native rootless core | Existing Docker CI | Refusal/argv tests only; live host unavailable |
| Native local `dev` services, explorer and host worker | Local Unix Docker profile; no live daemon available in this qualification | Qualified rootless Linux / explicit loopback macOS machine profile; isolated macOS analysis observed below |
| Native single and aggregate analysis | Docker fixture tests passed; live Docker not run | Isolated macOS single + aggregate passed after serialization; valid artifacts |
| `EXECUTION=sandbox` dev | Existing Docker gate | Explicit refusal; no socket alias |
| Nested act and .NET Testcontainers | Existing Docker gate | Explicit refusal; no socket alias |
| Full security collection and database benchmarks | Existing Docker paths | Explicit refusal |

`scripts/test-container-engine-live.mjs` is opt-in and uses host Node only for
this diagnostic. It creates a synthetic source directory under
`CE_RUNTIME_PROBE_ROOT` (default `/private/tmp/ce-podman-validation`), which must
be shared with the selected machine. It checks a controlled local network
canary online, then proves offline isolation, non-root UID, dropped capabilities,
no-new-privileges, read-only analyzed sources, no engine socket, input hash
preservation and caller-owned cache writes. No models/private repositories are
used. It writes `.sandbox-cache/runtime-core-<engine>.json` with relevant source
hashes and actual doctor metadata. Re-run after changing those inputs.
Old success is removed before a rerun, and the context fingerprint is compared
before and after the diagnostic. This observation is not an atomic runtime lock.

The isolated analysis observation used Podman 6.0.2 client / 6.1.2 server on macOS.
Concurrent probes failed and their isolated `unknown` evidence was retained; no
production queue was consumed. Single and aggregate probes passed with valid
artifacts after serialization. Package fetching, private feeds, database import
and model effects were not live-qualified. Docker fixture tests passed, but no
Docker daemon was available, so neither live Docker nor full CI is claimed.

The new source-hash-qualified harness is
[`tests/native-analysis-runtime.live.ts`](../tests/native-analysis-runtime.live.ts),
explicitly opted into with `--live`:

```sh
CE_CONTAINER_ENGINE=podman CE_PODMAN_CONNECTION=my-rootless-machine node --experimental-strip-types tests/native-analysis-runtime.live.ts --live
```

It preserves probe directories and writes `.sandbox-cache/runtime-analysis-podman.json`.
The integration owner will rerun final gates and the new source-hash-qualified
probe; the earlier observation is not a receipt for later source changes.
The core test migration from `tests/container-engine-core.test.mjs` to
[`tests/container-engine-core.test.mts`](../tests/container-engine-core.test.mts)
uses the existing locked TypeScript 7.0.2 for qualification, with no new dependency
cache. Shared compiler/download caches are retained: no global purge, reclaimed
space 0 bytes.

The trusted `/work` checkout is writable for builds; analyzed `SANDBOX_SOURCES`
are mounted read-only. Online Podman uses its native private network. Custom
`SANDBOX_NETWORK` is unqualified and rejected online; offline always uses none.
For host-owned analysis, offline source exports and runtime trees are read-only;
the private journal has an explicit mount, and Podman uses `keep-id` with explicit
UID/GID. This does not change the general build sandbox's writable checkout.

## Limits

Probe-only receipts are not full-pipeline, ce.v1 parity, security-complete or
release certificates. A successful lock scan is not a complete image/SAST/history
audit. Existing external-corpus/image gaps remain open. All-severity scanning,
dev dependencies and failure exit codes are preserved; no ignore policy is added.
Behind a corporate TLS proxy, explicitly provide a trusted certificate file via
`SANDBOX_EXTRA_CA_FILE=/absolute/path/ca.pem`. The scanner mounts it read-only;
TLS certificate and hostname verification remain enabled. The file must also be
accessible to your machine. Do not put private keys in this public CA file.

The live diagnostic uses a disposable container and owned temporary source; stop
only your own validation machine when finished. Keep private receipts outside a
worktree before removing it under MADR-0014. No broad VM/volume cleanup is implied.

See [MADR-0021](adr/0021-explicit-container-engine-core.md) and
[execution criteria](plans/podman-core-execution.md).
