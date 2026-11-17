# Source-bound security validation with explicit unresolved findings

* Status: accepted
* Date: 2026-11-01
* Related: MADR-0003/0005/0008/0010; issue #3

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for Source-bound security validation with explicit unresolved findings; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:17a639a0d79ad3bdaa86bf713c13acf99553c18984d5f63ba33170a0afde98af`.
Previous decision status (historical): `applied in this workstream; proposed for owner review`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and drivers

The previous Trivy gate covered High/Critical dependency vulnerabilities. It did
not establish history-secret, source-security, container-OS or configuration
coverage. Manual review reproduced unsafe package/source boundaries and credential
flows. A successful scanner process is insufficient evidence that all required
targets were examined or that findings were resolved.

## Considered options

1. Keep the existing dependency-only check and describe its limited scope.
2. Add source-bound local collection, independent triage and fail-closed verification.
3. Require cloud scanning, source uploads and hosted model inference.

## Decision

Choose option 2. Keep existing sandbox execution and trusted-local deployment.
Use digest-pinned Trivy for all-severity dependencies/images and supported source
secret/configuration checks, Gitleaks for redacted source/reachable-history checks,
and Semgrep with reviewed local rules. Manual review covers unsupported constructs
and sensitive boundaries. Semgrep sink inventory is not interprocedural taint proof;
Trivy finding no supported configuration files is not a configuration security pass.

Collectors record source inventory, history refs, tool/rules/configuration hashes,
actual image references/platforms, vulnerability DB identity/freshness, targets,
exit status, raw-report hash and coverage gaps. Run scanners without credentials,
Docker socket or write access to input. Export image archives on the host. Host
orchestration may invoke Git and Docker; builds and tests stay in scripts/sandbox.
Receipts establish consistency, not cryptographic proof against a malicious host.

Keep raw findings and private source in ignored local storage. Publish only redacted
metadata and bounded outcomes. A false positive requires finding-specific evidence
and a separate reviewer. No blanket suppressions or severity-based silent exclusion.
Confirmed findings remain open until a source-bound fix and passing retest exist.
Unavailable upstream fixes remain blockers to a security-complete claim; scan
completion, lack of network exposure and non-production use alone are not waivers.

### Owner-review publication versus security assurance

On 2026-11-01 the operator clarified that repositories we cannot access must not
prevent a review PR: access may never become available. This supersedes the batch's
earlier requirement for a complete zero-unresolved audit before any PR publication.

An owner-review PR requires fresh main alignment, the unchanged ordinary pre-push
CI/dependency gate, relevant regression evidence, and explicit disclosure of audited
inputs, unavailable inputs, incomplete coverage and unresolved findings. It may be
published as a draft while those limitations remain open. Publication is code review,
not security certification, release approval, deployment or permission to auto-merge.

Unavailable private repositories are `not assessed`, with the reason and follow-up
issue recorded. They are not scanner failures in our accessible project, and obtaining
access is not a prerequisite for a PR. Use controlled, pinned fixtures to verify the
generic implementation and bound all corpus claims to the inputs actually examined.

Known image advisories and parser/inventory gaps remain visible in issue #24 and the
review description; they are not silently resolved or suppressed. The strict
`security-baseline`/`security-verify` commands retain their failure behavior for missing
coverage or unresolved findings. A review PR does not require those strict assurance
commands to turn green. Mandatory local tests and the existing all-severity project
dependency gate remain unchanged. Any future security-complete/release claim requires
its own explicitly scoped evidence and authority.

## Boundary controls

- Validate direct source records, Git revisions, package identifiers and versions.
- Use bounded Git output/time and archive entry/byte limits. Reject escaping paths,
  symlink parents and occupied export destinations. Concurrent hostile filesystem
  mutation is not claimed to be race-proof.
- Export and source browsing disable ambient Git helpers/configuration/protocols
  and lazy fetching. Browsing cannot initiate transport to obtain missing objects.
- NuGet permits HTTPS origins from an operator-owned exact-origin allowlist:
  `CE_NUGET_ALLOWED_ORIGINS` is comma separated and forwarded only to package fetch.
  Default is `https://api.nuget.org`; private feed/CDN origins require explicit
  authorization before credential expansion or retrieval. No wildcard hosts.
  Credentials are explicit environment references and never cross authority.
- Saved model endpoints cannot silently retain old credentials after authority
  changes. Default HTTP transport rejects credentialed plaintext and redirects;
  remote errors must not reflect provider response bodies into the UI.
- API-wide Host/Origin/browser checks protect the trusted-local application.
  These are not authentication or authorization for public/multi-user deployment.
- Existing SQL parameterization and explicit human binding rules remain required.

## CI, cost and compatibility

### Dependency gate acquisition and verification

Issue #104 separates the ordinary `make security-check` into source-free online
database acquisition and a network-disabled dependency scan. The standard gate
does not acquire a checks bundle: it runs only Trivy's vulnerability scanner.
Gitleaks, Semgrep and the broader Docker-only audit retain their separate scope.

Provision Cosign v3.1.4 outside the checkout and set `COSIGN_BINARY` to its absolute
executable path. The gate checks the reviewed platform-specific SHA-256 before
executing it. CI provisions the fixed Linux asset and hash in `ci.yml`; local
Linux act uses that same step. macOS ARM64 operators can download
`cosign-darwin-arm64` from the exact `sigstore/cosign` v3.1.4 release, verify SHA-256
`9f9b8cd557cf2e5b3df3373a00baa005a86b02391e3e06dd0ce37b3a1533d3a4`,
and run `gh release verify-asset v3.1.4 <binary> --repo sigstore/cosign` before
making it executable. The reviewed bootstrap establishes release origin, not
SLSA build provenance. A new verifier version/hash requires a fresh trust review.

Every invocation verifies the exact Trivy index against GitHub's OIDC issuer and
`aquasecurity/trivy/.github/workflows/reusable-release.yaml@refs/tags/v0.76.1`,
then binds the selected Linux ARM64/AMD64 child to local engine metadata. Missing
verifier, rejected signature, platform mismatch or unavailable registry fails
closed. No credentials are forwarded to scanner containers. A signature, checksum
or successful scan is not proof that upstream content is benign.

The database originates from `ghcr.io/aquasecurity/trivy-db`. Its publisher
signature is not verified: registry TLS, the verified scanner, schema-2 metadata
and sealed byte hashes are the recorded trust basis. Registry/publisher compromise
remains a residual risk. `trivy.db` is bbolt, not SQLite. Metadata must satisfy
`UpdatedAt <= DownloadedAt <= now`, `NextUpdate > UpdatedAt`,
`now < NextUpdate`, and `now < UpdatedAt + 24 hours`; no stale-data override exists.
Failure recovery is to restore registry/verifier availability or obtain fresh
valid data and rerun, not to suppress findings or reuse a previous success.

Each invocation uses a private, ephemeral acquisition cache and a separate sealed
scan cache, never a shared writable database. Cleanup targets only that run.
CPU (2), memory (2 GiB), processes (512), temporary storage (1 GiB), command output
and total wall time (20 minutes) are bounded. The acquisition bind cache has a
2-GiB/4,096-entry periodic guard and a 1-GiB free-space reserve, not a filesystem
quota; writes may transiently exceed it between checks. The measured 2026-11-06
upstream database is 1,675,603,968 bytes, exceeding the initial 1-GiB proposal.
Sealing moves closed acquisition files without duplicating database bytes, and
hashing uses bounded buffers. Runtime qualification and exact-source receipts must
disclose that limitation. No performance or full-engine compatibility claim
follows from unit tests alone.

Pin Actions revisions and multi-platform image references. Scan each distinct image
digest/platform once per recorded DB snapshot; a new DB requires fresh vulnerability
evaluation. Do not equate arm64 results to amd64. Source/rule changes invalidate
applicable receipts. Keep history inventories explicit and detect changed refs.
Share download caches, not mutable worker outputs. Keep the ordinary CI runner
Docker-only; browser and model-service images remain opt-in to their tasks.

Security fixes may reject previously accepted unsafe inputs. Document behavior
under Unreleased without changing fixtures or lowering evaluation thresholds.
No database migration, new hosting service, remote exposure or release is implied.

## Validation and limitations

Regression suites cover source export, package paths/feeds, model transport/settings,
Git browsing and local API protection. Audit harness tests must reject omitted
targets, stale/mismatched inputs, incomplete reports, unresolved findings and blanket
waivers. Full audit, integration CI and running-browser proof are separate gates;
this decision is not evidence those gates have passed.

Research/runtime records belong in the execution handoff. Real Glufs acceptance
remains unassessed under issue #23, independently of review publication. Owner review
and the stage-only branch policy apply.
