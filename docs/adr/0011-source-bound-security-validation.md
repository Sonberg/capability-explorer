# Source-bound security validation with explicit unresolved findings

* Status: applied in this workstream; proposed for owner review
* Date: 2026-11-01
* Related: MADR-0003/0005/0008/0010; issue #3

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

An owner-review PR requires fresh stage alignment, the unchanged ordinary pre-push
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
