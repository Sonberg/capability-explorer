# Model evidence quality and bounded C# identities

## Specification and ownership

Base: main `734ba5fe0bc06b9122ff23e597dc38c3b5cdc679`.
One queen integrates three isolated worker branches: model extraction,
model presentation and C# identity (#54). Shared contracts, dependencies,
CI and documentation remain queen-owned.

The model scope is standard layout versus vendor extensions, qualified BPMN
incoming/outgoing references, DMN allowed-value declarations and accessible
grouped diagnostics. Rendering original XML does not establish semantic
extraction completeness. Unknown namespaces and unsupported semantics remain
visible; neither FEEL nor analyzed source is executed.

C# work starts with a non-private reproducer and must retain deterministic,
collision-resistant declaration/reference identity within ce.v1 limits.
Unavailable affected-corpus input and unknown-run recovery are separate
acceptance obligations, not satisfied by synthetic tests.

Acceptance criteria:

1. Standard BPMN/DMN diagram namespaces are not reported as vendor extensions;
   genuine unknown namespaces still produce explicit partial coverage.
2. BPMN incoming/outgoing references and DMN allowed values have supported,
   source-linked dispositions without executing their content.
3. Tables expose allowed values and diagnostics group repeated occurrences
   without hiding their individual artifact/element details.
4. Overlong C# symbols yield bounded deterministic identifiers, with distinct
   symbols remaining distinct and graph references resolving consistently.
5. The devcontainer launches non-root with Node/.NET and starts the actual app;
   it does not mount a Docker socket or provision paid services by default.
6. Existing golden/schema, browser and canonical CI checks pass at the final
   public-safe source state before publishing an owner-reviewed PR.

The development container is trusted project tooling, not an analyzed-source
sandbox or a replacement for full local/hosted CI. The earlier configuration
worktree was empty at inspection; there was no existing configuration to
commit. New configuration therefore needs real launch/toolchain proof.

## Gates and SLOs

Focused RED/GREEN tests precede integration. Use existing sandbox targets
`test-ts`, `test-extractors`, `test-contracts`, `test-explorer`,
`test-model-viewer-browser`, `test-collection-dogfood` and native golden
evaluation, followed by complete canonical local CI and hosted PR checks.
Do not add another heavyweight hosted job or weaken the current test plan.

The current internal SLO policies in #13, #43, #63 and #60 bound optimization.
Apply the exact declared workload, resource envelope and sampling rules;
headline latency alone is insufficient. Stop optimizing once the applicable
target and correctness/safety gates pass. This phase is not a new enterprise
scale benchmark, routing rewrite or release certification.

## Pending-work disposition

Only reviewed public-safe changes are transferred onto public main.
The full private evidence/wiki branch and its ancestry remain private.

| Pending content | Disposition |
|---|---|
| `.devcontainer` | Implement, validate and commit the portable configuration |
| `.gitignore` local runtime exclusions | Integrate applicable patterns without losing current main rules |
| Local MCP configuration/backups | Keep private; exclude from public commits |
| `.agents/`, `.claude/` generated tool scaffolding | Retain locally; not required product source or reviewed redistribution |
| Untracked AGENTS.md / CLAUDE.md | Retain locally; generated commands/topology conflict with current Make-based repository policy |
| Agent databases, caches and bootstrap state | Runtime evidence only, not public source |

No blanket staging, private-history merge, destructive cleanup or owner merge
is authorized. Publication is an owner-reviewed PR; a green check does not
close unrelated #24 security, #23 private-input or #75 chronology obligations.

## Development container

Open the repository with VS Code Dev Containers, or use the containers.dev CLI
with `up --workspace-folder <checkout>`. Its image reuses the repository's pinned
Node and .NET SDK inputs. It provides non-root project development without a
Docker socket or automatic database/model startup. Use `make build`,
`make backend` and `make frontend` inside the container.

VS Code forwards ports 5080 and 5173; the CLI alone does not publish these ports.
Default application listeners remain loopback. Editor/host Git credential
forwarding is a separate user configuration: this file adds no credential
mounts, but cannot override every editor or host policy. Do not treat it as an
untrusted-source isolation boundary. Container-backed CI, Testcontainers and
analysis remain host-controlled through the existing sandbox commands.

Qualification commands, exact source identities, results and remaining limits
are recorded at final integration rather than inferred from configuration.

## Specification gate

The existing baseline TypeScript typecheck and all 204 tests pass in the
repository sandbox. The baseline .NET solution builds with zero warnings and
zero errors after locked restore. Missing local dependency failures were
resolved by the existing package managers, not by weakening checks.

Specification grade: **96/100**. The four-point deduction is unavailable
private-corpus reproduction and owner-only release qualification; those limits
are explicitly outside synthetic proof. These baseline checks do not yet test
the new behavior, VS Code GUI forwarding, performance SLO compliance or a
production deployment. Brain grounding: SPARC capability card
`sparc/capability-cards.md#sparc`; no additional stack dependency is needed.

## Pseudocode gate

```text
extract model:
  parse within existing XML/source budgets; reject unsafe/malformed input
  classify by exact namespace and construct census, not local name alone
  preserve typed elements, raw declarations, source links and diagnostics
  resolve supported local references; report invalid/unresolved references
  never evaluate a condition or claim opaque content is understood

present model:
  use existing typed evidence fields to show allowed-value declarations
  group diagnostics by shared semantic classification, not just free text
  show occurrence count and expandable artifact/element details
  retain genuine partial/failed coverage and keyboard-accessible controls

construct C# identity:
  derive the existing canonical symbol key
  preserve keys already within the shared contract limit
  map oversized keys to deterministic bounded cryptographic identities
  apply the same mapping at declaration and every reference boundary
  preserve original source evidence; do not silently truncate distinct symbols

integrate:
  cherry-pick only owned public-safe commits; reject unexpected shared edits
  run focused checks and original-XML browser/collection acceptance
  commit exact source; mandatory pre-push full CI; verify PR at that SHA
```

Parsing and grouping remain linear in the bounded input/occurrence count;
hashing is linear in canonical-key length with fixed-size output. Sorting
needed for deterministic presentation is at most O(n log n). No new execution
engine, remote model call or cross-repository dependency is required.

Pseudocode grade: **96/100**, one iteration. All six criteria have a path and
error/unknown behavior; the four-point deduction is implementation validation
still pending, not proof by pseudocode. Baseline instruments remain the 204
passing TypeScript tests and clean solution build. New behavior, GUI forwarding,
private-corpus recovery and SLO compliance are not tested by this gate.
Brain pass clean: `sparc/capability-cards.md#sparc`.

## Architecture gate

The existing ce.v1 and model/collection record shapes remain unchanged. The
C# extractor's existing deterministic assembly fingerprint changes its config
hash for this build; no old immutable analysis key is reused. Collection callers
must bind fresh producer/configuration inputs; dogfood now includes the census.
No new package, engine, service or paid model was adopted.

Independent review found local QName lookup incorrectly treated `prefix:id`
as a literal ID. The follow-up resolves in-scope namespaces against the model
target namespace, retaining endpoint checks and rejecting foreign/undeclared
prefixes. The reviewer confirmed the fix; RED/GREEN regression is retained.

Architecture grade: **97/100**, two iterations. The integrated sandbox build,
contract tests, TypeScript (208) and extractor tests (221) pass. All 71 engine
profile tests pass. Actual containers.dev CLI 0.90.0 launched the image non-root;
native `make build` passed with zero warnings/errors. The running Explorer
returned real HTML and `/api/meta` JSON at loopback port 5187. The owned container
was stopped and removed. The three-point deduction is untested VS Code GUI
extension/forwarding and broader host configurations, not evidence of a current
architecture flaw. CLI control-manifest retrieval warned about the local TLS
issuer; image build/startup completed without disabling TLS.
Brain pass clean: `sparc/capability-cards.md#sparc`.
