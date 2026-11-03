# Portable, source-bound decision navigation

* Status: proposed for owner review; no existing decision is accepted by this record
* Date: 2026-11-03
* Scope: bounded decision-ledger contract, synthetic portability and repository inventory
* Related: MADR-0026; the separate scoped branch lifecycle update is in MADR-0014

## Context

The current decision index and diagram are useful navigation, but their groups
are not a verified ontology. Numeric aliases collide, adoption scopes differ,
and implementation, approval and qualification must not be conflated. Cross-
project navigation needs identities and evidence without rewriting historical
records or assuming every project authors Markdown.

## Proposed decision

Use the versioned strict JSON sidecar, configured canonical-source profiles,
controlled multi-facet vocabulary and deterministic offline validator/generator
in [`decision-ledger-architecture.md`](../research/decision-ledger-architecture.md).
Authored Markdown remains authoritative **in this repository**; a configured
JSON canonical source may be authoritative elsewhere. Generated views have no
independent decision authority.

Separate project-qualified stable record identity, local alias, source locator
and exact byte-snapshot revision. Every status, facet and explicit relationship
carries scope, recorded authority and source passage evidence. Acceptance needs
a separate scope-qualified receipt; source-recorded acceptance is not globally
authenticated approval. Implementation and verification remain unassessed.

Keep directed source `related-from` separate from symmetric conceptual
`related-to`. References do not become dependencies. Extensions, scoped
amendments, scoped overrides and whole-scope supersession remain distinct.
Inverse source declarations may corroborate one normalized edge; they do not
double its count. No identity, category, acceptance or asserted implication
chain is inferred from paths, names, hyperlinks, merge state or implementation.

Choose a bounded typed JSON graph rather than an RDF/OWL runtime. The linked
evaluation compares actual competency questions and maintenance costs. Borrowing
SKOS and PROV distinctions is not standards conformance. Future RDF export needs
a concrete consumer; OWL reasoning is not presently required.

## Migration and authority boundaries

Preserve all authored titles, exact raw statuses, the two distinct `0005`
decisions and the missing `0015`. This record accepts none of them. Existing
Markdown mentions are not silently imported as reviewed typed edges. Unknown
source metadata/status and unavailable targets remain explicit diagnostics.

The integration owner chooses checked-in views and shared CI wiring. This
proposal is accompanied by separately reviewed lifecycle updates in
MADR-0014/0026; it does not accept those proposals or widen merge authority.
Branch eligibility is a fail-closed policy only. The worker cannot delete refs,
enable automatic deletion, publish, merge or change protection/settings.

## Consequences and verification

Benefits: repeatable inventory and stale-receipt checks, portable scoped identity,
honest unknowns, disposable deterministic views and no new runtime dependency.
Costs: sidecars need review, bounded adapters do not parse every dialect, byte
changes require new receipts, and source evidence proves declarations rather
than truth or actor authentication.

Validate with `node scripts/decisions/check.mjs all` and explicit dry-run
generation. Focused fixtures cover heterogeneous Markdown/frontmatter and JSON
collections, Unicode/spaces, renames, repeated aliases, source changes, unknown
status, directional/inverse relations, invalid cycles and read-only stale checks.
Real-workspace adoption, semantic equivalence, authenticated approvals, complete
CI and hosted publication are not established by this proposed record.

Integration enrolls every local record with a reviewed-proposed topic facet and
the selected source-backed navigation edges, preserving their scoped meanings.
Classification is a proposal, not inferred from discovery. Draft is red and
historical states gray; prerequisite-cycle checks also cover mixed
depends-on/extends cycles. Checked-in views must match during the offline gate.
