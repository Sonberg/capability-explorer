# Gold set and bounded pilot gate

## Expert-reviewed evaluation set (proposed, not assembled)

Before any accuracy claim, select an owner-approved, access-authorized corpus
and freeze its repository/revision/units. Domain experts independently label
context, business-object/event/command alternatives, field paths and meaning,
audience, exposure verdicts, applicability, and profile criteria. A separate
reviewer adjudicates disagreements. Record reviewer role, evidence, rationale,
rubric/glossary/context/policy versions, disagreements, adjudication, abstention
and unsupported cases. Keep fixtures and gold labels immutable after a tool
candidate is scored; changes create a new gold-set version.

Report by profile and supported/unsupported category, with denominators and
confidence intervals:

* classification precision = correct predicted positive labels / all predicted
  positive labels; recall = correct predicted positive labels / all gold
  positive labels. Report per-class and macro/micro summaries; preserve
  abstentions and ambiguous labels rather than counting them as negatives.
* unsupported rate = cases explicitly marked unsupported or unknown / all
  eligible gold-set cases, broken out by language, construct, and shape.
* field/shape correctness = exact supported path, type/optionality/nesting
  matches / gold supported paths and shape attributes. Also report omissions,
  unsupported-path handling and false links; unknown must not be a successful
  no-leakage outcome.
* inter-reviewer agreement = independently labeled agreement before
  adjudication, with the statistic appropriate to categorical/ordinal labels,
  prevalence and abstention reported. Agreement is not truth or safety proof.
* grade agreement = exact profile/tier agreement and weighted ordinal
  disagreement, including exposure hard-gate violations separately. No
  average tier can hide an exposure-policy false negative.

Thresholds are chosen with domain, security/policy and product owners after a
real demand and representative authorized set exist. This package has no
gold-set sample, measured precision/recall, unsupported rate, shape correctness,
inter-reviewer agreement, or actual accuracy claim.

## One demand-gated bounded pilot

**Recommendation:** if an owner requests a concrete pilot, assess a single
owner-selected repository and one bounded domain boundary, using one frozen
revision and the existing extraction outputs only. The pilot has an explicit
stop date, reviewer-hour budget and no production writes. It must not begin
until its entry decision is recorded.

Required before entry:

1. Domain owner names the bounded context(s), relevant concept vocabulary,
   source system and one representative flow; no context is inferred from
   repository/namespace.
2. Domain experts approve a versioned context map and context-scoped glossary,
   including polysemes and ambiguous terms.
3. Security/policy owner supplies audience, field sensitivity and authorization
   rules plus a reviewer for every exposure decision. No private sensitive
   values enter reports or fixtures.
4. Repository owner authorizes exact revision/read scope and source permissions;
   extraction/binding versions/configurations and unsupported shapes are
   recorded. No live traffic, credential inheritance, build execution or new
   parser is presumed.
5. Independent reviewers agree on the gold-set protocol, class definitions,
   abstention/unknown handling, thresholds and stop/rollback criteria.
6. The accountable product/architecture owner decides whether to accept the
   proposed profiles, pilot scope, thresholds, and any implementation proposal.

If any prerequisite is missing, record **not started** and request only the
missing owner decision. This recommendation creates no dependency edge, no new
issue, no implementation slice, and no authority to adopt MADR-0027. Issues
#23, #24, #43, and #54 retain their separate statuses.
