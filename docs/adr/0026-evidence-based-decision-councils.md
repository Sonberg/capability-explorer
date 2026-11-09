# Evidence-based decision councils and a navigable MADR ledger

* Status: accepted
* Date: 2026-11-03
* Decision owner: repository maintainers
* Related: [coordination](0001-github-work-coordination.md),
  [PR lifecycle](0005-pr-lifecycle-and-conflict-prevention.md),
  [independent tool evaluation](0019-independent-tool-evaluation.md),
  [issue dependencies](0024-issue-labels-and-dependencies.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for Evidence-based decision councils and a navigable MADR ledger; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:e6095b9b2087f419d7837c8232bc9aab66813e70e4660d1a0dc86921907c4f32`.
Previous decision status (historical): `proposed; no existing decision is accepted by this record`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and decision drivers

Historically, many records remained proposed, sometimes alongside implemented or
operator-authorized work. Merge, implementation and local adoption do not prove
repository-wide decision acceptance. The operator requests a frugal council of
subject-matter experts (SMEs), adversaries, advisers and a judge, with uncertainty
remaining proposed until more evidence or an explicit user decision.

Readers also need one title/link/status index and an evidence-backed relationship
map. Duplicate numeric IDs and scoped acceptance must remain visible rather than
being silently normalized or inflated.

## Options and proposed decision

1. Keep ad hoc owner reviews and a directory listing.
2. Use a bounded, evidence-based council with explicit authority, a review
   receipt and a maintained [index and diagram](README.md).
3. Accept by model majority, implementation, elapsed time or PR merge.

Propose option 2. Option 3 is rejected: agreement is not evidence or authority.
This is a project-specific extension of ADR practice, not a MADR standard.
No council runner, automatic status writer or standing paid swarm is introduced.

### Functions and independence

| Function | Responsibility |
|---|---|
| Owner/author | Frame the decision, alternatives, constraints, scope and material claims; supply inspectable evidence. Never judge their own proposal. |
| SME | Check claims in the affected domain against source-bound evidence; report gaps and uncertainty, not just a score. |
| Adversary | Challenge assumptions, failure paths, contradictory evidence, compatibility and unresolved constraints. Must not be the author. |
| Adviser | Compare proportionality, alternatives, operational costs and reversibility; may share the SME role with disclosure. |
| Independent judge | Evaluate the packet, evidence and objection dispositions against the predeclared criteria; cannot author the record or serve as its SME, adversary or adviser. |
| Decision authority | A previously authorized maintainer/user, or a judge with explicit, bounded delegation from that authority. Supplies the authority for acceptance, not merely a vote. |

Use the smallest council covering the material domains: normally SME/adviser,
adversary and judge, with a separate author. Functions may be human or agent
where existing policy permits. Distinct names, prompts or models do not establish
independence. Disclose shared authorship, models, sources and conflicts; obtain
initial reviews independently before reconciliation. Count repeated claims from
one source once. Add an SME only for an uncovered material domain, not to
manufacture a quorum.

### Evidence and acceptance algorithm

1. Freeze the record path, exact reviewed revision/content hash, scope,
   applicable policy and authority/delegation. Define acceptance criteria,
   constraints and material claims before the review.
2. Link each material claim to inspectable, applicable evidence: source/version,
   test or research result, alternatives and limitations. A proposed design may
   be accepted without implementation if it makes no untested operational claim.
3. Capture the SME, adversary and adviser assessments, then reconcile each
   material objection with evidence and rationale. Silence, timeout, missing
   review or majority vote never disposes of an objection.
4. The independent judge checks completeness, independence, authority and every
   material objection. Missing evidence, material doubt, contradictions,
   unresolved objections or a failing required gate mean **remain proposed**,
   with named follow-up evidence and owner. No average score can hide a failure.
5. With all criteria met and no material uncertainty unresolved, a judge may
   authorize acceptance only under recorded, pre-existing delegation covering
   this record/revision/scope. Otherwise the judge recommends acceptance and
   the authorized maintainer decides; the record remains proposed meanwhile.
6. An authorized user may explicitly decide despite documented uncertainty,
   within existing policy. Record the authority, rationale, open findings and
   accepted scope as an authority decision, not an evidence-clean council
   outcome. Waivable security risk requires its designated human risk owner's
   explicit scoped exception and expiry; non-waivable constraints still block.
7. An authorized writer changes the record's status to `accepted`, adds the
   dated receipt/decision link and refreshes the index/diagram in the same PR.
   The receipt binds the reviewed content and final status-change commit.
   Ordinary CI, owner merge and release gates remain separate and unchanged.

One initial round and one targeted reconciliation round are the default budget.
Remaining uncertainty stays proposed rather than triggering repeated model
polling. Reuse unchanged, still-applicable evidence; review only impacted claims
after a change, while retaining the complete record. No new spend or tools are
authorized by a council assignment.

### Durable receipt

Store this in the reviewing PR or record; link it from the status metadata:

```text
Record: exact path; reviewed revision/content hash; decision scope
Basis: effective policy/version; criteria; evidence links/hashes and validity
Reviewers: identities/functions; conflicts and correlation disclosures
Findings: each material objection; disposition; rationale; evidence; open items
Judge: identity; verdict; remaining uncertainty; next evidence if proposed
Authority: identity; delegation/explicit decision link; scope; UTC timestamp
Acceptance basis: evidence-clean council | explicit authority decision
Exceptions: risk owner; permitted scope; controls; expiry, or none
Recheck triggers: changed design, dependencies, constraints or invalid evidence
Status change: final commit/PR; index update; supersession links if applicable
Boundary: design decision only; no implementation, merge or release assurance
```

Changing the decision invalidates a prior verdict for the changed claims.
Preserve accepted decisions historically; a material replacement gets a new
record with an explicit supersession link, not a rewritten rationale. Recording
supersession metadata does not erase the earlier acceptance evidence.

### Status and navigation maintenance

The accepted [portable ledger decision](0028-portable-decision-ledger.md) specifies explicit
project-qualified identities, source-bound sidecars and deterministic views.
Maintain sidecar receipts and regenerate/check views with every canonical
record mutation. Canonical format is configured per collection, not universally
Markdown. Preserve directed source relationships and scoped overrides; taxonomy
facets and generated groups cannot infer authority or hard prerequisites.
Unclassified records stay visible; classification requires reviewed evidence.
Source hashes prove recorded bytes, not the truth or authentication of claims.

The record is authoritative; the index reflects its declared status and scope.
Green means accepted **only within its recorded scope**, yellow proposed/pending,
and red draft/not ready for review, not a severity score. No color implies
implementation, release readiness or a dependency being satisfied.
Rejected/deprecated/superseded records stay visible in a neutral historical
category with a replacement link where applicable.

Use unique file paths as identity; do not silently renumber the two existing
`0005` records or fill the missing `0015`. New IDs must avoid both merged records
and IDs reserved in open PRs. Keep title/link/status rows and diagram nodes
complete for this checkout, with in-review additions explicitly separated.
Status colors and textual labels must agree.

Diagram edges distinguish a cited prerequisite/extension, a related reference
and a scoped override/supersession. A reference is not automatically a hard
dependency, and an override of one clause is not whole-record supersession.
Keep an edge evidence ledger and update it with the graph. Use a readable
selection of relationships, explicitly not an exhaustive dependency analysis.

### MADR lifecycle: CRUD includes navigation

The index and diagram are part of the decision-record lifecycle, not optional
follow-up documentation. Every mutating operation updates the record and
navigation in one reviewed change; reading requires no change or approval:

| Operation | Record and evidence | Index and diagram |
|---|---|---|
| Create | Reserve a unique ID/path after checking main and open PRs; write draft/proposed status, owner, context and scope. | Add its title/link/status row and colored, text-labeled node; add only evidenced edges and ledger entries; refresh counts. |
| Read | Start at the index, then inspect the source record, scope, decision receipt and cited evidence. | Use the map to navigate, never to infer authority, implementation or an undocumented hard dependency. |
| Update | Review changed claims and stale evidence; use the acceptance protocol for status changes. Material replacements of accepted decisions require a new record. | Update title/path/status/scope, counts, node label/color, incoming/outgoing edges and edge evidence in the same PR. Preserve historical links or redirects after a rename. |
| Delete/retire | Normally withdraw/reject a proposal or deprecate/supersede an accepted record with rationale and replacement link; preserve decision history. Physical removal of an unaccepted draft requires explicit maintainer authorization and a retained withdrawal receipt. | Retired records remain in the historical ledger/map. If a draft file is physically removed, retain a linked tombstone/withdrawal entry, remove its live node/edges or replace with a historical node, and repair every inbound reference and count. |

Never delete an accepted decision to erase history, silently reuse its ID or
leave dangling links/edges. Changes to lifecycle rules also maintain this index
and undergo review; this proposal creates no automated CRUD capability.

## Consequences, validation and adoption

This makes acceptance inspectable and keeps doubt visible, at the cost of
maintaining a small index and review receipts. It can still suffer correlated
agent errors; independent evidence and human authority matter more than agent
count. Review the source record for qualifications rather than treating color
as certification.

Validate inventory completeness, exact titles/status scope, local links, diagram
node/color coverage and each edge against its cited passage. Review a proposed
case with missing evidence and a scoped accepted case; neither may be promoted
or widened by indexing. Check create/update/retire operations for matching
record, row, node, count and relationship changes; a missing navigation update
requires rework. Ordinary publication gates still apply.

This policy cannot accept itself, delegate new powers to its own council or
retroactively accept existing proposals. Existing decision/merge authority
governs its adoption. The 2026-11-09 operator approval above supplies that
explicit authority decision, not an evidence-clean council verdict. The two
historical advisory reports are input to drafting,
not an independent judge verdict or an acceptance receipt.

## Sources and evidence limits

- [Nygard: Documenting Architecture Decisions](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions.html):
  proposed/accepted lifecycle and recording replacement decisions.
- [AWS: Architectural decision record process](https://docs.aws.amazon.com/prescriptive-guidance/latest/architectural-decision-records/adr-process.html):
  review, rework remaining proposed, explicit stakeholder acceptance and
  superseding accepted decisions. Verified 2026-11-03.
- [MADR](https://adr.github.io/madr/): concise decision-record structure, not
  evidence for this project's council roles or delegated authority.

Council roles, fail-closed evidence rules and delegation are project
choices, not claims that these sources prescribe agent governance.
Retained EA knowledge was unavailable (OAuth 401); no retained enterprise-policy
or pre-existing delegation is asserted. RuvNet Brain retrieval was weak for this
specific acceptance policy; it supplies no missing authority or implementation.
