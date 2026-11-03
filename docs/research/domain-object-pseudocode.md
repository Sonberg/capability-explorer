# Pseudocode: deterministic assessment precedence

This is a proposed bounded reference algorithm for research fixtures, not a
production evaluator. Cost is `O(F + L + P + V + C + E log E)` for `F` fields,
`L` relations, `P` proposals, `V` reviews, `C` criteria, and `E` evidence items
in one bounded record. Limits on bytes, nesting, references, and graph size
must be configured before any future source ingestion. Exceeding a limit
returns partial/unknown, never a successful no-leakage result.

## Ordered procedure

1. Bind immutable source identity: repository plus full revision and unit/symbol,
   or document pointer/span; hash exact source bytes. Fixture inputs use a
   distinct synthetic-fixture identity. A digest is not authenticity proof.
2. Bind extractor/tool, adapter/binding, configuration, rule, rubric, glossary,
   context-map, and exposure-policy versions. Validate shapes strictly;
   reject unknown fields and unsupported schema keywords.
3. Normalize evidence references and sort them by UTF-8 bytes. Reject missing
   evidence IDs, duplicate record identities, wrong-repository/revision anchors,
   malformed spans, and untrusted paths. Do not resolve external references.
4. Preserve source facts and diagnostics as immutable observations. Store
   deterministic candidates and model suggestions in separate proposal records;
   suggestions never satisfy reviewed gates.
5. Apply only explicit declarations or current reviewed context assignments.
   Do not infer a context from repository, namespace, service, topic, or spelling.
   Multiple plausible contexts preserve ambiguity and produce no identity link.
6. Preserve each declared field and nested shape with supported/partial/
   dynamic/recursive/external/unknown status. Do not expand cycles without a
   bound or silently omit unsupported children.
7. Build projection/consumer relations only from source-bound explicit mapping
   evidence or current scoped review. Require endpoints and mapped field paths
   to exist; require a transformation when names/semantics differ. Calls,
   channel/topic matches, or envelopes cannot establish field propagation.
8. Evaluate exposure independently for each crossed boundary and field path.
   Require rule, policy owner, audience and current decision evidence. A missing
   review or unknown field yields unknown/needs-review, not approved. A declared
   approved internal-only scope does not imply public Internet exposure.
9. Invalidate reviews when any bound source, tool, rule, rubric, glossary,
   context-map, or exposure-policy version changes. Stale reviews remain visible
   but satisfy no current-language, context, field-semantic, or policy gate.
10. Resolve criteria in the selected profile. Fail on any failed hard gate;
    unknown required evidence yields insufficient-evidence for that tier;
    not-applicable is accepted only with rationale and cannot exempt exposure
    policy. Suggestions and schema/envelope validity count as no semantic pass.
11. Apply exposure ceilings before any grade: policy-violation blocks every
    grade; unknown/needs-review cannot be called approved or silently bronze.
    Never average, weight, or compensate a hard-gate failure.
12. Compute the highest cumulative profile tier whose applicable mandatory
    gates all pass with current evidence. Internal domain events omit external
    infrastructure gates unless the owner-selected profile makes them relevant.
13. Emit sorted findings/results and an identity digest covering the exact
    source plus all tool, binding, configuration, rule, rubric, glossary,
    context, policy, applicability, and review inputs.
14. On a missing link, stale decision, unsupported shape, conflict, budget
    breach, parse failure, or insufficient evidence, return explicit unknown,
    partial, blocked, or insufficient-evidence with diagnostics. Never infer
    absence of exposure or successful runtime propagation from missing evidence.

## Invalidation identity

`assessmentId = SHA-256(canonical(sourceIdentity, tool, binding, configHash,
rulesVersion, rubricVersion, glossaryVersion, contextMapVersion,
exposurePolicyVersion, applicabilityDecisions, currentReviewIds))`.
Canonicalization sorts object keys and set-like lists by ordinal UTF-8 bytes;
it does not reorder semantically ordered field paths or transformations.
Source authenticity, trust, and reviewer authorization are outside a hash's
guarantee.

## Failure and bounded-complexity notes

Schema mismatch, unsupported shape, missing context, inconsistent references,
stale review, conflicting mapping, or policy denial each returns a stable
diagnostic and blocks the affected gate. Recursive structures are represented
by a cycle/unsupported marker and source reference, not infinitely expanded.
Dynamic serialization remains unknown unless a separately reviewed runtime
evidence source is authorized. Do not execute analyzed code or contact a broker
to fill gaps. The research fixtures exercise this decision order without
claiming the evaluator is deployed or measures actual designs.
