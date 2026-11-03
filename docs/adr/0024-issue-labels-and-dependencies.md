# Issue labels and explicit delivery dependencies

* Status: proposed; operator-authorized label pilot, pending owner review
* Date: 2026-11-03
* Decision owner: repository maintainers
* Related: [work coordination](0001-github-work-coordination.md), [PR lifecycle](0005-pr-lifecycle-and-conflict-prevention.md)

## Context and problem statement

The remaining backlog is difficult to scan: most issues have no labels, and
dependencies, unavailable inputs and review gates are buried in prose.
The operator requested labels and a MADR while delivering substantial work one
issue at a time. Reducing the issue count must not hide incomplete acceptance.

## Decision drivers

Human-readable filters, explicit dependency direction, few maintained labels,
evidence-backed priority, one active delivery and no new service or paid model.
Labels must not become authorization, proof of completion or a replacement for
reviewed issue relationships.

## Considered options

1. Keep free-text descriptions and default GitHub labels only.
2. Use a small label taxonomy plus explicit dependency links and handoffs.
3. Generate one label per dependency, agent, issue or workflow transition.

## Decision outcome

Propose option 2. Apply it as an operator-authorized pilot; owner review accepts
the durable policy. Do not mark this ADR accepted merely because it was committed.
Use labels already present where they fit; do not rename or delete existing
labels during migration.

| Dimension | Labels | Rule |
|---|---|---|
| Work type | Existing `bug`, `enhancement`, `documentation`, `question`; new `type:research`, `type:governance` | One primary type; optional existing secondary labels remain valid. |
| Area | `area:analysis`, `area:platform`, `area:explorer`, `area:security`, `area:tooling` | One or more affected areas, not every possible downstream consumer. |
| Workflow | `status:triage`, `status:ready`, `status:active`, `status:blocked`, `status:review` | Exactly one for an open issue after migration. Untagged work starts in triage, not implicitly ready. |
| Priority | `priority:critical`, `priority:high`, `priority:normal`, `priority:later` | Optional until assessed; priority is not effort or an invented business ROI score. |
| Dependency | `dependency:blocked`, `dependency:blocker` | Only for a documented blocking issue relationship, not mere topical similarity. |
| Gate | `gate:release` | A release obligation, not a claim that implementation work is blocked. |

### Dependency and blocker protocol

Use GitHub's native blocked-by/blocking relationships where available. Always
include a short human-readable dependency section in the affected issue body or
a linked, current coordination comment:

```text
Blocked by: #<number> -- required acceptance or artifact and why it is required.
Blocks: #<number> -- downstream acceptance that cannot proceed.
Related: #<number> -- coordination only; not a hard prerequisite.
External blocker: missing input/owner decision, owner and unblock condition.
```

Mirror a confirmed hard edge on both issues. `dependency:blocked` describes the
dependent; `dependency:blocker` describes its prerequisite. Each may carry both.
Do not invent an issue solely to label an external blocker. External input or
owner-decision blockers use `status:blocked` with their unblock condition; they
need not carry a dependency label. Related issues alone get neither dependency
label. Avoid cycles; resolve a cycle by narrowing or sequencing acceptance before
starting dependent work.

Closing a prerequisite does not automatically make a dependent ready: verify the
required merged artifact and all remaining blockers. Remove stale dependency
labels when their last blocking edge is satisfied; retain historical links.
If native relationships are unavailable, explicit links remain the record; do
not claim a native edge was created.

### Scheduling and lifecycle

The queen/integration owner records the substantial-first ranking, dependencies,
readiness and rationale in the selected issue. Business impact and dependency
value take precedence over easy closures; effort and uncertainty are visible
tradeoffs. WSJF estimates may inform scheduling, but remain provisional and do
not override a release gate or an unavailable prerequisite.

Only verified, bounded acceptance with available inputs is `status:ready`.
One delivery issue is `status:active`; bounded read-only triage or a small policy
PR does not claim another implementation lane. Assign isolated writing workers
under MADR-0001. Move to `status:review` only with an owner-review PR and actual
validation evidence. A failed gate returns work to active or blocked.

Labels do not authorize a merge, bypass security, grant worker capabilities or
establish a lock. Owner merge and acceptance evidence determine completion.
Close duplicates as superseded with a keeper link, not as implemented. Remove
workflow/dependency labels at closure; preserve type, area and historical
priority. Do not mass-close partial handoffs or blocked work to improve counts.

### Pilot and maintenance

Initially classify all open issues by type/area and use triage where readiness
has not been reconciled. Record known external blockers explicitly. Do not infer
hard dependency edges from shared labels or references. Preserve existing issue
bodies and claims; use concise coordination comments rather than replacing
research or acceptance with generated summaries.

At claim, blocker, review and closure boundaries, the integration owner reconciles
labels, exact relationships and issue/PR state using the authorized GitHub
account. No polling daemon, automatic priority model or label-triggered execution
is introduced. Later automation needs a separate reviewed change and must treat
GitHub text and labels as untrusted input.

## Consequences

Labels make filtering cheap while exact links explain why an issue waits.
The taxonomy adds manual maintenance and can drift; phase-boundary reconciliation
and triage defaults make uncertainty visible instead of implying readiness.
Initial label application is operational metadata, not evidence that the ADR has
been accepted or the backlog's implementation has progressed.

## Validation and acceptance

Review the live label names/descriptions, a sample classified issue, an external
blocker with an unblock condition and any confirmed hard dependency pair.
Confirm there are no inferred dependency edges or unsupported completion claims.
Documentation-only content needs no new test tooling; ordinary repository
publication gates still apply. Owner review decides whether to accept this ADR.
