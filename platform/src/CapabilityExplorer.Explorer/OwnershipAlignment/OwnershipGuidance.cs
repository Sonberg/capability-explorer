namespace CapabilityExplorer.Explorer;

internal static class OwnershipGuidance
{
    internal static (string Reason, string Action, string Verification) For(string kind, OwnershipCheckKey check, string? detail)
    {
        var source = check.System ?? "the involved systems";
        return kind switch
        {
            "aligned" => ("The reviewed ownership rule is satisfied by current, complete evidence.",
                "Keep ownership evidence and reviewed mappings current.", "The next current-full snapshots still satisfy this rule."),
            "current-full" or "identity-linked" or "rule-present" or "rules-valid" =>
                ("An evaluation precondition is observed; this alone does not establish ownership alignment.",
                    "Review the ownership rule findings.", "All four sources and their reviewed ownership checks are evaluated."),
            "feed-missing" => ($"No {source} snapshot is configured.",
                $"Configure an authorized {source} snapshot; do not infer an owner from another system.", "A current-full snapshot loads in this source slot."),
            "stale" => ($"The {source} evidence is older than its reviewed freshness limit.",
                $"Refresh the authorized {source} snapshot and reevaluate.", "A new current-full snapshot is within its freshness limit."),
            "partial" or "absence-unknown" => ($"The available {source} evidence cannot establish absence or alignment.",
                $"Obtain complete, current {source} evidence and reevaluate.", "Current-full evidence resolves the unknown observation."),
            "freshness-unknown" => ("No valid reviewed freshness policy is available.",
                "Configure or repair the complete rules file before evaluating freshness.", "Valid rules provide freshness limits for all four sources."),
            "owner-conflict" => ("Reviewed cross-system party sets do not satisfy the ownership relation.",
                "Have the designated owners reconcile ownership in the source systems or review a scoped delegation; then refresh the evidence.",
                "New current-full evidence satisfies the reviewed relation without relying on an invalidated exception."),
            "missing-owner" or "cardinality-violation" => ($"Recorded ownership does not satisfy the required party cardinality. {detail}",
                $"Record the required accountable parties in {source}, then refresh evidence.", "The current-full snapshot satisfies the reviewed minimum and maximum ownership cardinality."),
            "missing-source-record" => ("No matching reviewed source subject or required ownership record is present in complete evidence.",
                $"Review the exact entity mapping and record the required ownership in {source}; refresh evidence.", "A reviewed entity mapping and current-full ownership record satisfy the check."),
            "unknown-party-mapping" or "ambiguous-party-mapping" =>
                ("An observed party does not resolve to exactly one reviewed cross-system party.",
                    "Have the designated mapping owner review the exact party identities; never infer equivalence from matching names.",
                    "Every involved party resolves to exactly one reviewed party reference."),
            "ambiguous-identity" => ("A source subject maps to multiple repositories without an exact reviewed shared-subject declaration. Other repositories: 1+.",
                "Review the exact entity crosswalk and, if intentional, declare the shared subject without widening report grants.",
                "Each entity has one reviewed repository mapping or an exact reviewed shared-subject declaration."),
            "unreviewed-crosswalk" => ("The relevant entity mapping is draft, not reviewed.",
                "Obtain designated-owner review of the exact entity mapping.", "A reviewed, unambiguous mapping is available."),
            "no-rule-for-system" => ($"No reviewed ownership requirement covers {source}.",
                $"Have the designated policy owner define the source-native accountability requirements for {source}.",
                "Valid reviewed requirements cover this source; different legitimate roles remain distinct."),
            "prerequisite-not-met" => ($"Required ownership checks have not passed: {detail}",
                "Resolve the referenced requirement findings before comparing party sets.", "Both sides have passing, non-empty ownership requirements."),
            "not-applicable" => ($"An unexpired declared rule-specific non-applicability applies; this is not alignment. {detail}",
                "Review the justification with the designated policy owner before expiry.", "The scoped non-applicability remains valid or current evidence satisfies the rule."),
            _ => ($"Ownership evaluation is blocked by {kind}.",
                "Restore authorized readable inputs and valid reviewed crosswalk/rules; rerun the report. No ownership change is made by this report.",
                "The blocked input loads and its dependent checks can be evaluated.")
        };
    }
}
