# Security, corpus replay and binding review: execution record

Status: owner-review delivery; complete security assurance remains unavailable. Target only
`stage-1-contract-and-fixtures`. Last checked base: `d4389b4` (2026-11-01).
No merge, release or change to main is authorized by this record.

## Delivered locally

- Revision-bound binding preview and explicit reviewed export. Export does not
  adopt bindings or write the database. Unknown/conflicted inputs remain visible.
- Locked, deterministic corpus replay with synthetic acceptance tests. Real
  Glufs validation remains unavailable: issue #23.
- Source, reachable-history, dependency, SAST and image audit collection with
  digest-pinned tools, explicit coverage and fail-closed verification.
- Package/feed, Git/process, browser/API and model-credential boundary fixes.
  Resource identities omit connection credentials; evidence mismatch diagnostics
  omit source text. Each confirmed fix has dedicated regression evidence.
- MADR-0011/0012, proposed MADR-0013, and a referenced weighted analysis-tool
  comparison. Research scores are judgments, not benchmark measurements.

## Evidence and its limits

After Docker restoration, full ordinary `make local-check` passed for exact source
`80a8dcb2603fed00c4d2dac13143a556fc85b8f4`, targeting stage base `d4389b4`.
The real application passed all nine browser binding-review/export assertions on
loopback port 5108. Local receipts: `.sandbox-cache/local-ci/last-success.txt`,
`.sandbox-cache/local-ci-80a8dcb.log` and
`.sandbox-cache/binding-browser-80a8dcb.log`. A later receipt must identify its
own tested commit; the earlier successful result cannot authorize a changed HEAD.
The local control map checks registration of 29 tests across 12 controls against
that source/CI receipt; it is not proof of exhaustive security coverage.

The integrated source `d438333` completed build, binding tests (23), Contracts
tests (106), Evaluation tests (25), C# extractor tests (125), and evaluation of
18 golden artifacts in local act. That run did **not** pass: the OrbStack Docker
socket disappeared during subsequent extraction. Its log is the local ignored
`.sandbox-cache/full-local-ci-final.log`. No successful local-CI receipt exists
for the final batch. Later code must be validated again.

The awk-free fixture script separately extracted and evaluated all ten C# golden
artifacts, with unchanged thresholds and complete expected evidence. Worker TS
credential fixes passed 134 tests plus typecheck; their twelve security cases
include failing-before evidence. The later evidence-diagnostic redaction passed
107 Contracts tests. These focused results do not replace integration CI.

The binding preview passed nine actual browser assertions, including review and
export, on the local application. This is UI evidence, not real-corpus acceptance
or proof of every security boundary. The preview used port 5108; it depends on
the Docker service remaining available.

## Open gates and operational failures

- Container findings, including unavailable upstream fixes, remain blockers in
  issue #24. Earlier SDK/Node image comparisons are historical measurements;
  they do not establish a complete current image inventory pass.
- Semgrep reports parser failures. Independent file review supplements the
  report but does not turn partial automated coverage into a passing scan.
- Collection run6 found an unsupported Trivy image flag. The corrected collector
  separates filesystem development dependencies from installed image packages;
  its 51 harness tests passed, but full image collection must be rerun.
- Run6 was also invalidated by newly generated Ruflo database files in `.swarm`.
  These were runtime-state additions, not changed application source. Preserve
  this failed receipt; do not edit it into a successful one.
- Run8 measured eight pinned arm64 images against one recorded database, with
  12,483 package/advisory rows. These are scanner measurements, not a count of
  exploitable application paths. Project dependency reports contained zero
  vulnerability findings across all requested severities. The partial image
  measurements and limitations are recorded in issue #24.
- Ollama acquisition was cancelled at low host disk space. Ryuk returned no
  supported package inventory; that is unavailable coverage, not a clean result.
  The whole run8 collection was invalidated by new generated spike build output.
  `9a54cfb` adds an exact generated-output exclusion and retains unsupported image
  coverage as a failure; its 55 harness tests passed. Raw reports remain intact.
- Docker became unavailable during CI and was subsequently restored. The
  interrupted receipt remains invalid; final validation uses a fresh run.
- Follow-up TypeScript filesystem/configuration/fingerprint boundaries and exact
  generated-memory exclusions now pass 53 audit harness tests, 147 TypeScript
  tests and typechecking on `f3fa6a5`; no tests skipped. The JSON-boundary
  regression reproduced four failures before the fix. No fixture or golden
  changes were made. File checks assume stable input, not protection against
  concurrent hostile filesystem mutation. Log: `.sandbox-cache/resume-focused.log`.
- Git replacement refs were confirmed to substitute different source beneath a
  requested commit in both public APIs. `ffed5d3` disables replacements for source
  viewing and evidence verification. Both regressions failed before the fix;
  isolated Contracts 108/108 and Explorer 151/151 tests passed afterward. Full
  integration results belong to the exact-source local CI receipt, not those
  focused worker results.
- Brain lifecycle hooks reported failed exact turn readback. An explicit
  checkpoint was stored and read back as `project-state-current-1793555198027`,
  but this does not prove automatic turn capture, learning or routing savings.

## Resume order

Finish isolated boundary fixes and independent review, restore Docker, integrate
against current stage, then run focused regressions, unchanged golden gates,
ordinary local act CI and fresh complete audit collection. Hold Git refs stable
while sealing reachable history. Record unresolved findings individually; no
blanket waiver or lowering of thresholds. The operator subsequently revised the
publication requirement: inaccessible private inputs must not prevent review.
MADR-0011 now separates owner-review publication from complete security assurance.
Publish the current batch as a draft after fresh stage alignment and the ordinary
pre-push gate, with all unresolved findings and coverage limits disclosed. Strict
audit verification remains failed; it is not relabelled green.

Keep parser coverage, upstream image remediation and unavailable Glufs acceptance
separate from product implementation. No claim that the project is vulnerability
free follows from any one scanner or this record.

## Cost and coverage follow-up

Native workers used isolated worktrees for security implementation and independent
read-only review; the coordinator integrated changes. No external paid inference
was invoked. Native token usage and dollar savings were unavailable, so no savings
or routing-learning uplift is claimed. Recorded AgentDB checkpoint readback proves
that entry's persistence only.

The reviewed Semgrep profile remains a sink inventory, not a complete taint
analysis. A possible next package reuses existing Roslyn for unchanged modern C#
syntax and independently checks exact targets and parse diagnostics. This is a
proposal, not implemented coverage. A scanner parser limitation is not a reason
to rewrite valid application syntax or silently exclude files.

A read-only storage inventory found 248 registered entries across 37 repositories
under the operator's projects/workspaces. Twelve inactive, clean, merged linked
worktrees of this repository were explicitly authorized for removal after verified
private backups. Other candidates require separate authorization. Branch refs,
primary checkouts, dirty and active worktrees remain protected. Exact removal and
backup receipts are local/private; no ignored environment or memory data belongs
in GitHub. MADR-0005 documents this procedure. Reclaiming caches does not resolve
container advisories or scanner coverage gaps.
