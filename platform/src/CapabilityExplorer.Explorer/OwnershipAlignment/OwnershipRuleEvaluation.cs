namespace CapabilityExplorer.Explorer;

internal sealed partial class OwnershipEvaluation
{
    private string? NotApplicable(string repository, string ruleId, string[] systems)
    {
        var matches = rules!.NotApplicable.Where(row => row.RuleId == ruleId && systems.Contains(row.System, StringComparer.Ordinal) &&
            row.RepositoryIds.Contains(repository, StringComparer.Ordinal) && OwnershipInputValidation.Timestamp(row.ExpiresAt) > now)
            .OrderBy(row => row.System, StringComparer.Ordinal).ThenBy(row => row.ExpiresAt, StringComparer.Ordinal).ToArray();
        return matches.Length == 0 ? null : string.Join("; ", matches.Select(row =>
            $"{row.System}: {row.Justification}; declared approver {row.ApprovedBy}; expires {row.ExpiresAt}"));
    }

    private void Requirement(string repository, OwnershipRequirement rule)
    {
        string[] systems = [rule.System];
        var key = new OwnershipCheckKey("require", RuleId: rule.RuleId);
        OwnershipRole[] expected = [new(rule.System, rule.Role)];
        var notApplicable = NotApplicable(repository, rule.RuleId, systems);
        if (notApplicable is not null)
        {
            Add(repository, key, "not-applicable", "not-applicable", systems, expected, rule.ResolutionPartyRef, notApplicable);
            return;
        }
        var identity = Identity(repository, rule.System);
        if (feeds[rule.System] != "current-full" || identity is not ("identity-linked" or "missing-source-record"))
        {
            Add(repository, key, feeds[rule.System] != "current-full" ? feeds[rule.System] : identity,
                "not-evaluable", systems, expected, rule.ResolutionPartyRef);
            return;
        }
        var records = Records(repository, rule.System).Where(record => record.Role == rule.Role).ToArray();
        var parties = records.Where(record => record.Party is not null).Select(record => record.Party!).Distinct().ToArray();
        var kind = records.Length == 0 ? "missing-source-record" : parties.Length == 0 ? "missing-owner" :
            parties.Length < rule.MinParties || rule.MaxParties is { } maximum && parties.Length > maximum ? "cardinality-violation" : "aligned";
        Add(repository, key, kind, kind == "aligned" ? "aligned" : "action-needed", systems, expected, rule.ResolutionPartyRef,
            $"Expected distinct source parties: minimum {rule.MinParties}, maximum {rule.MaxParties?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unbounded"}; observed {parties.Length}.");
    }

    private void Relation(string repository, OwnershipRelation rule)
    {
        var systems = new[] { rule.Left.System, rule.Right.System }.Distinct(StringComparer.Ordinal).ToArray();
        var expected = new[] { rule.Left, rule.Right };
        var key = new OwnershipCheckKey("relation", RuleId: rule.RuleId);
        var notApplicable = NotApplicable(repository, rule.RuleId, systems);
        if (notApplicable is not null)
        {
            Add(repository, key, "not-applicable", "not-applicable", systems, expected, rule.ResolutionPartyRef, notApplicable);
            return;
        }
        var badFeed = systems.FirstOrDefault(system => feeds[system] != "current-full");
        if (badFeed is not null)
        {
            Add(repository, key, feeds[badFeed], "not-evaluable", systems, expected, rule.ResolutionPartyRef);
            return;
        }
        var badIdentity = systems.Select(system => Identity(repository, system))
            .FirstOrDefault(identity => identity is not ("identity-linked" or "missing-source-record"));
        if (badIdentity is not null)
        {
            Add(repository, key, badIdentity, "not-evaluable", systems, expected, rule.ResolutionPartyRef);
            return;
        }
        var prerequisiteIds = rules!.Requirements.Where(required => expected.Any(role =>
            role.System == required.System && role.Role == required.Role)).Select(required => required.RuleId).ToHashSet(StringComparer.Ordinal);
        var failed = findings.Where(finding => finding.RepositoryId == repository && finding.CheckKey.Type == "require" &&
            finding.RuleId is not null && prerequisiteIds.Contains(finding.RuleId) && finding.Status != "aligned").ToArray();
        if (failed.Length > 0)
        {
            Add(repository, key, "prerequisite-not-met", "not-evaluable", systems, expected, rule.ResolutionPartyRef,
                string.Join(", ", failed.Select(finding => finding.FindingId)));
            return;
        }
        if (rule.Relation == "independent")
        {
            Add(repository, key, "aligned", "aligned", systems, expected, rule.ResolutionPartyRef);
            return;
        }
        var left = Map(repository, rule.Left);
        var right = Map(repository, rule.Right);
        if (left is null || right is null)
        {
            Add(repository, key, "unknown-party-mapping", "action-needed", systems, expected, rule.ResolutionPartyRef);
            return;
        }
        var matches = rule.Relation switch
        {
            "same-party-set" => left.SetEquals(right),
            "left-within-right" => left.IsSubsetOf(right),
            "delegated" => left.All(party => right.Contains(party) || rules.Delegations.Any(row =>
                row.FromPartyRef == party && right.Contains(row.ToPartyRef) && row.Scope.RuleId == rule.RuleId &&
                row.Scope.RepositoryIds.Contains(repository, StringComparer.Ordinal) &&
                OwnershipInputValidation.Timestamp(row.ExpiresAt) > now)),
            _ => throw new InvalidOperationException("Unvalidated ownership relation."),
        };
        Add(repository, key, matches ? "aligned" : "owner-conflict", matches ? "aligned" : "action-needed",
            systems, expected, rule.ResolutionPartyRef);
    }

    private HashSet<string>? Map(string repository, OwnershipRole role)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var party in Records(repository, role.System).Where(record => record.Role == role.Role && record.Party is not null)
            .Select(record => record.Party!).Distinct())
        {
            var refs = PartyRefs(role.System, party);
            if (refs.Count != 1) return null;
            result.UnionWith(refs);
        }
        return result.Count == 0 ? null : result;
    }
}
