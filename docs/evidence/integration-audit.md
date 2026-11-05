# Integration Source Comparison

Date: 2026-11-04. This versioned note deliberately omits private repository
identities, API routes, schedules, source excerpts and deployment configuration.
It is a methodology and implementation qualification, not an external export
of analyzed source content.

## Local Deliverables

The operator-requested detailed audit and exact source identities are retained
in ignored local storage:

- `.sandbox-cache/integration-audit-evidence/integration-audit.md`: detailed
  findings, per-repository rollup, source-side relationship matrix and timers.
- `.sandbox-cache/integration-audit-evidence/comparison.json`: every recorded
  boundary fact with exact source evidence checks, declared integration nodes,
  producer/config identities and unresolved route-only candidate status.
- `.sandbox-cache/integration-audit-evidence/comparison-pre-evidence-fix.json`:
  immutable comparison that exposed the corrected producer bug.
- Separate local SDK-call qualification, test/source snapshots, browser
  screenshots and original run/reconciliation receipts.

The generated comparison is atomically replaced and owner-readable/writable
only. Private source details are not reproduced here merely because the
workspace's native artifacts are accessible to the operator.

## Method And Scope

Each comparison input must be a completed managed analysis run. The resolved
manifest and published artifact hashes are checked first. Recorded HTTP,
message and data-boundary citations are checked against exact analyzed Git
blobs using LF-normalized, 1-based UTF-16, end-exclusive spans. Matching hashes
prove the cited bytes, not runtime behavior, missing-edge completeness or a
deployed target. Recognized external SDK compiler calls and a bounded source
syntax census are kept separate from integration facts.

The reviewed forms include HTTP clients/endpoints, configured destinations,
message producers/consumers, timer attributes, browser navigation, GraphQL,
request-message clients and storage SDKs. Explicitly unsupported source and
unavailable repositories are not relabelled analyzed. Each capability's source
revision and producer recipe are retained independently; this is not an atomic
deployment snapshot.

The completed comparison covers eight available capabilities, twenty repositories,
111 artifacts and 6,837 boundary facts. Forty-nine artifacts report partial
coverage and 238 of 341 requests have no target key. These are static record
counts, not distinct deployed integrations. Source-only forms reveal additional
coverage gaps even where emitted evidence is correct.

## Implementation And Verification

The task delivered a persistent manual graph overlay, including source/target
repository and observed-node pickers. Manual links are clearly labelled and do
not mutate native facts or claim protocol compatibility. See
[graph qualification](manual-graph-connections.md).

The audit also exposed and fixed wrong-file URL-constant evidence in the C#
HTTP rule. A public two-file regression failed before implementation, then
passed with the same assertion; the standard full extractor suite passed
216/216. A final managed refresh completed and published corrected private
artifacts without patching their bytes in place. All 6,837 recorded boundary
facts have matching source citations. The separate pass over 3,277 recognized
SDK compiler-call observations retains eight invalid mixed-line-ending citations
explicitly as supplementary limitations. The bounded source census has 1,605
candidates and is not universal semantic integration discovery.

The scope also includes the existing bounded-symbol-key issue and the
cross-filesystem publication repair. See [issue 54 qualification](issue54-symbol-keys.md)
and [publication qualification](analysis-publication-storage.md).

Full Explorer validation passed 307/307 cases, including malformed-overlay
preservation. TypeScript 7 build and desktop /
mobile browser scenarios passed, including repository/node persistence, same-name
node disambiguation, background-refresh draft preservation and mobile editor
placement. The static frugal-CI policy check preserves 57 canonical commands;
the full Docker-only workflow was not executed in this native qualification.
Exact final local implementation/configuration snapshot SHA-256:
`bdf5fcd4ff86c7d01fd3abc1f470896be5d0d9ce5b3d72e5fc000bffcca5765e`.
Graph evidence documents the limits of historical pre-edit snapshot capture;
no missing red receipts were fabricated after implementation.