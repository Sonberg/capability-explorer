# View revision-bound original models without granting execution authority

* Status: proposed
* Date: 2026-11-04
* Decision owner: repository maintainers
* Coordination: issues #12 and #78
* Related: MADR-0017, MADR-0029 and MADR-0033

## Context

Authored model evidence preserves identities and facts, but is not a lossless
original XML or diagram-layout interchange. A digest alone cannot supply
original bytes. Rendering, evidence coverage, content permission and business
approval must remain distinct.

## Proposed decision

Keep `ce.models.v1` unchanged. Negotiate the separate closed
`ce.model-source.v1` profile with explicit `model-content` permission. Preserve
the exact UTF-8 bytes, including BOM, under a fixed 1 MiB source limit and bind
the source hash to the exact companion artifact, repository, revision and path.
Both independent collection consumers verify the attachment and crosslink.
Old evidence does not retroactively acquire original content.

Store immutable evidence/source products and their explicit source disposition
inside immutable analysis-snapshot membership. Derive revisions/defaults from
native and model membership together, including model-only imports. Publish
files-mode native/model/capability state atomically. Neither mode reads arbitrary
repository paths for model-view requests.

Expose paged model summaries separately from selected evidence and source.
Bind opaque IDs and continuation cursors to the selected scope and current
snapshot/generation. Reject stale, denied, missing, invalid and oversized inputs
explicitly; never return a successful unverified fallback or silently truncate
complete evidence. Limits are 20 default/50 maximum summaries, 1 MiB serialized
list, 16 MiB evidence and 8 MiB source-response wrappers.

Use released locked `bpmn-js` 18.31.1 NavigatedViewer and `dmn-js` 18.0.1 Viewer,
not modelers. Retain the bpmn.io license notice and unmodified, fully visible,
unobscured watermark/link. Read-only import is not standards conformance,
process execution, FEEL evaluation, business approval or authenticated rights.

Run the viewer in an opaque sandbox with trusted bundled scripts, restrictive
CSP and network connections denied. Preflight source bytes, structure and
supported namespaces before rendering, conservatively limiting XML elements
to 500. Reject DTDs/entities and unsupported active/external constructs. Remove
the old frame and reject stale scope/identity messages on selection changes.
This does not provide a hard CPU deadline or separate-process memory guarantee:
preflight and rendering can share a browser renderer thread.

Keep accessible ordered evidence and authored DMN cells authoritative. Upstream
renderer chrome, such as a generated annotation column, is not source evidence.
Show missing layout/content honestly, distinguish candidates as unapproved and
non-executable, and never replace immutable originals with generated layout.

## Alternatives and consequences

Reconstructing original XML from lossy facts is rejected. A generic architecture
graph is not a standards-aware model viewer. Apache KIE remains an alternative
requiring separate read-only/version-conversion, license and bundle qualification.
Automatic layout is not required for this bounded delivery; adding it requires
a separate derived identity and measured resource/repeatability evidence.

The explicit attachment, snapshot membership and browser boundary add maintenance
cost. Local content permission records operator intent, not central enrollment or
authority to expose the trusted-local server remotely. Source-backed availability
does not establish operational freshness or whole-standard coverage.

## Verification

Run existing collection-consumer and model contract/API/store suites, actual
collector CLI dogfood, `tests/issue78-model-dogfood.ts`, and the focused production
UI/boundary browser checks in the existing serial pinned CI graph. Validate exact
source hashes, denial/tamper/stale paths, watermark and ordered text alternatives.
Use Opus 5.5 for delivered UI requirement/design validation; implementation may
use other models. Record actual runtime/source identities and limitations.
This proposed decision does not itself certify implementation, independent
approval, authenticated rights, accessibility conformance, merge or release.
