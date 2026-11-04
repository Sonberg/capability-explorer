---
type: Delivery Plan
title: Six bounded collector delivery packages
description: Three existing issue updates and three new issues, without a platform rewrite.
status: draft
tags: [delivery, collection, priorities]
generated: {by: copilot-cli/1.0.105, at: "2026-11-04T01:10:00+01:00"}
sources:
  - id: platform
    resource: https://github.com/Sonberg/capability-explorer/issues/33
  - id: models
    resource: https://github.com/Sonberg/capability-explorer/issues/12
  - id: identity
    resource: https://github.com/Sonberg/capability-explorer/issues/6
---
# Bounded scope

Keep collection in this project and central interpretation, products and
presentation downstream. Reuse language-neutral contracts and synthetic test
semantics before considering algorithm ports. No Python runtime, duplicate
scheduler, live consumer integration or broad UI work is required.

| Package | Issue action | Priority | Deliverable |
|---|---|---|---|
| A | Update #33 | Foundation/high | Collector export contract and offline conformance |
| B | Update #12 | High | Authored BPMN/DMN ingestion with explicit support matrix |
| C | Created #75 | High | Bounded static process/decision candidates |
| D | Created #74 | Normal | Missing declaration profiles and per-family coverage |
| E | Update #6 | Normal | Revision-bound identity/link qualification |
| F | Created #76 | Later | Evidence-bound DDD/domain candidates |

All six issue actions were authorized on 2026-11-04 and read back for
verification. #33/#6 retain their original requirements; #12 now targets authored
ingestion while #75 owns static candidates. No issue was deleted.

The existing issues supply public platform, model and mapping context;
their unrelated requirements should remain intact.[^platform][^models][^identity]

# Sequencing

A's minimum profile and B/C's synthetic expected-model work can begin together.
B and C are independent investigations. External exports of either must conform
to A; this is not a mandate to finish a central platform first.
D inventories existing extraction before adding missing families. E extends
mapping tests without deleting existing resolution work. F can reuse C predicate
evidence but does not block B/C. Existing release/security gates remain in force.

The [bounded qualification](qualification.md) amends acceptance criteria for
these same six packages. No research finding alone authorizes new dependencies,
donor publication, model execution or production integration.

# Completion expectations

Freeze exact profiles, examples and supported subsets before implementation.
Use semantic red/green tests and unchanged native goldens, independent consumer
conformance, explicit partial/unsupported outcomes and exact source evidence.
Update [architecture](../architecture/index.md) and relevant wiki topics in
the same change; current implementation must remain distinct from these plans.

[^platform]: Existing #33 platform requirements; next slice narrowed to collection export, broader downstream work retained as reference.
[^models]: Existing #12 authored-model and code-derived distinction, refined by the bounded qualification.
[^identity]: Existing #6 mapping/resolution gaps; additive identity qualification must not replace its original fixes.
