namespace CapabilityExplorer.Explorer;

public static class OwnershipAlignmentEngine
{
    public static OwnershipReport Evaluate(OwnershipInputs inputs, DateTimeOffset now, string audience,
        CancellationToken cancellationToken = default)
    {
        if (audience is not ("engineering" or "architecture")) throw new ArgumentOutOfRangeException(nameof(audience));
        var evaluation = new OwnershipEvaluation(inputs, now, cancellationToken);
        evaluation.Build();
        return evaluation.Project(audience);
    }
}

internal sealed partial class OwnershipEvaluation
{
    private readonly OwnershipInputs inputs;
    private readonly DateTimeOffset now;
    private readonly CancellationToken ct;
    private readonly OwnershipRules? rules;
    private readonly OwnershipCrosswalk? crosswalk;
    private readonly string rulesState;
    private readonly string? semanticRulesDigest;
    private readonly Dictionary<string, string> feeds = new(StringComparer.Ordinal);
    private readonly Dictionary<OwnershipPartyMember, HashSet<string>> partyIndex = [];
    private readonly List<OwnershipFinding> findings = [];
    private readonly List<OwnershipDiagnostic> diagnostics =
    [
        new("manual-trust", "Crosswalk and rules review fields are declared by the configured files and not verified. Findings are only as trustworthy as the operator-provided inputs.")
    ];
    private int evidenceCount;

    internal OwnershipEvaluation(OwnershipInputs inputs, DateTimeOffset now, CancellationToken ct)
    {
        this.inputs = inputs;
        this.now = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        this.ct = ct;
        crosswalk = inputs.Crosswalk.State == "loaded" ? inputs.Crosswalk.Value : null;
        if (crosswalk is not null)
            foreach (var mapping in crosswalk.Parties.Where(mapping => mapping.Review == "reviewed"))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var member in mapping.Members)
                {
                    if (!partyIndex.TryGetValue(member, out var refs)) partyIndex.Add(member, refs = new(StringComparer.Ordinal));
                    refs.Add(mapping.PartyRef);
                }
            }
        rules = inputs.Rules.State == "loaded" ? inputs.Rules.Value : null;
        rulesState = rules is null ? "rules-" + (inputs.Rules.State == "loaded" ? "invalid" : inputs.Rules.State) : "rules-valid";
        if (rules is not null)
        {
            var owners = rules.Requirements.Select(rule => rule.ResolutionPartyRef)
                .Concat(rules.Relations.Select(rule => rule.ResolutionPartyRef)).Where(owner => owner is not null);
            if (owners.Any(owner => crosswalk is null || !crosswalk.Parties.Any(party => party.Review == "reviewed" && party.PartyRef == owner)))
            {
                rulesState = "rules-invalid";
                rules = null;
            }
            else semanticRulesDigest = OwnershipCanonical.Hash(rules with { Exceptions = [] });
        }
        foreach (var system in OwnershipSystems.All) feeds.Add(system, FeedState(system));
        if (crosswalk is not null && crosswalk.Subjects.Distinct().Count() != crosswalk.Subjects.Count)
            diagnostics.Add(new("duplicate-subject", "Identical crosswalk subject rows were deduplicated."));
    }

    internal void Build()
    {
        foreach (var repository in inputs.Repositories.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            Add(repository, new("rules"), rulesState, rules is null ? "action-needed" : "observed", []);
            foreach (var system in OwnershipSystems.All)
            {
                var state = feeds[system];
                Add(repository, new("feed", system), state,
                    state == "current-full" ? "observed" : state == "freshness-unknown" ? "not-evaluable" : "action-needed", [system]);
                var identity = Identity(repository, system);
                Add(repository, new("identity", system), identity,
                    identity == "identity-linked" ? "observed" : identity == "absence-unknown" ||
                        crosswalk is null ? "not-evaluable" : "action-needed", [system]);
                var covered = rules is not null && rules.Requirements.Any(rule => rule.System == system);
                Add(repository, new("rule-coverage", system),
                    rules is null ? "not-evaluable" : covered ? "rule-present" : "no-rule-for-system",
                    covered ? "observed" : "not-evaluable", [system]);
                foreach (var party in Records(repository, system).Where(record => record.Party is not null)
                    .Select(record => record.Party!).Distinct())
                {
                    ct.ThrowIfCancellationRequested();
                    if (PartyRefs(system, party).Count > 1)
                        Add(repository, new("party", system, Kind: party.Kind, Id: party.Id),
                            "ambiguous-party-mapping", "action-needed", [system]);
                }
            }
            if (rules is null) continue;
            foreach (var requirement in rules.Requirements.OrderBy(rule => rule.RuleId, StringComparer.Ordinal))
                Requirement(repository, requirement);
            foreach (var relation in rules.Relations.OrderBy(rule => rule.RuleId, StringComparer.Ordinal))
                Relation(repository, relation);
        }
    }

    internal OwnershipReport Project(string audience)
    {
        var ordered = findings.OrderBy(finding => finding.RepositoryId, StringComparer.Ordinal)
            .ThenBy(finding => finding.FindingId, StringComparer.Ordinal).ToArray();
        var groups = ordered.SelectMany(finding => GroupKeys(finding.RepositoryId, audience)
                .Select(key => (Key: key, finding.FindingId)))
            .GroupBy(row => row.Key, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new OwnershipGroup(group.Key, group.Select(row => row.FindingId).Order(StringComparer.Ordinal).ToArray())).ToArray();
        var action = ordered.Any(finding => finding.Status == "action-needed");
        var incomplete = ordered.Any(finding => finding.Status == "not-evaluable");
        var aligned = rules is not null && feeds.Values.All(state => state == "current-full") &&
            !action && !incomplete && inputs.Repositories.Count > 0 &&
            inputs.Repositories.All(repository => ordered.Any(finding => finding.RepositoryId == repository &&
                finding.CheckKey.Type == "require" && finding.Status is "aligned" or "excepted"));
        var vector = OwnershipCanonical.Hash(new
        {
            V = "ownership-input.v1", EvaluatedAt = now.ToString("O"), RulesState = rulesState,
            Slots = OwnershipSystems.All.Select(system => new { Slot = system, Slot(system).Digest, Slot(system).State }),
            CrosswalkDigest = inputs.Crosswalk.Digest, CrosswalkState = inputs.Crosswalk.State,
            RulesDigest = inputs.Rules.Digest, Grants = inputs.GrantedRepositories ?? inputs.Repositories,
            Repositories = inputs.Repositories,
            FreshnessStates = feeds.Select(feed => new { System = feed.Key, State = feed.Value }),
            AppliedExceptions = ordered.Where(finding => finding.Exception is not null)
                .Select(finding => new { finding.FindingId, finding.Fingerprint, finding.Exception!.ExceptionId })
        });
        return new(incomplete || feeds.Values.Any(state => state != "current-full") || rules is null ? "partial" : "ok",
            aligned ? "aligned" : action ? "action-needed" : "not-evaluable", audience, vector, now.ToString("O"),
            new(OwnershipSystems.All.Select(system => new OwnershipSourceCoverage(system, feeds[system],
                Slot(system).Value?.SnapshotId, Slot(system).Value?.CapturedAt, Slot(system).Value?.Coverage)).ToArray()),
            ordered, groups, diagnostics.Distinct().ToArray());
    }

    private OwnershipInput<OwnershipSnapshot> Slot(string system) =>
        inputs.Snapshots.TryGetValue(system, out var slot) ? slot : new("not-configured");

    private string FeedState(string system)
    {
        var slot = Slot(system);
        if (slot.State == "not-configured") return "feed-missing";
        if (slot.State != "loaded") return slot.State;
        if (slot.Value is null) return "invalid";
        if (rules is null) return "freshness-unknown";
        if (now - OwnershipInputValidation.Timestamp(slot.Value.CapturedAt) >
            TimeSpan.FromDays(OwnershipInputValidation.FreshnessDays(rules.Freshness[system]))) return "stale";
        return slot.Value.Coverage == "partial" ? "partial" : "current-full";
    }

    private OwnershipSubject[] Subjects(string repository, string system) => crosswalk is null ? [] :
        crosswalk.Subjects.Where(row => row.RepositoryId == repository && row.System == system && row.Review == "reviewed")
            .Distinct().ToArray();

    private OwnershipRecord[] Records(string repository, string system)
    {
        var subjects = Subjects(repository, system).Select(row => row.SubjectId).ToHashSet(StringComparer.Ordinal);
        return Slot(system).Value?.Records.Where(record => subjects.Contains(record.SubjectId)).ToArray() ?? [];
    }

    private string Identity(string repository, string system)
    {
        if (crosswalk is null) return inputs.Crosswalk.State == "loaded" ? "invalid" : inputs.Crosswalk.State;
        var subjects = Subjects(repository, system);
        foreach (var subject in subjects)
        {
            var repositories = crosswalk.Subjects.Where(row => row.System == system &&
                row.SubjectId == subject.SubjectId && row.Review == "reviewed")
                .Select(row => row.RepositoryId).ToHashSet(StringComparer.Ordinal);
            if (repositories.Count > 1 && (repositories.Any(id => !(inputs.GrantedRepositories ?? inputs.Repositories).Contains(id, StringComparer.Ordinal)) ||
                !crosswalk.SharedSubjects.Any(row => row.Review == "reviewed" &&
                    row.System == system && row.SubjectId == subject.SubjectId && repositories.SetEquals(row.RepositoryIds))))
                return "ambiguous-identity";
        }
        if (subjects.Length == 0 && crosswalk.Subjects.Any(row => row.RepositoryId == repository && row.System == system && row.Review == "draft"))
            return "unreviewed-crosswalk";
        if (Records(repository, system).Length == 0)
            return feeds[system] == "current-full" ? "missing-source-record" : "absence-unknown";
        return "identity-linked";
    }

    private HashSet<string> PartyRefs(string system, OwnershipParty party) =>
        partyIndex.TryGetValue(new(system, party.Kind, party.Id), out var refs) ? refs : [];

    private string[] GroupKeys(string repository, string audience)
    {
        if (audience == "engineering") return [repository];
        var subjects = crosswalk?.Subjects.Where(row => row.RepositoryId == repository && row.Review == "reviewed" &&
            row.LandscapeSubject is not null).Select(row => row.LandscapeSubject!).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        return subjects.Length == 0 ? ["unmapped"] : subjects;
    }

    private OwnershipFinding Add(string repository, OwnershipCheckKey key, string kind, string status,
        string[] systems, OwnershipRole[]? expected = null, string? resolutionParty = null, string? detail = null)
    {
        ct.ThrowIfCancellationRequested();
        var id = "of_" + OwnershipCanonical.Hash(new { V = "ownership-finding.v1", RepositoryId = repository, CheckKey = key })[..24];
        var records = systems.Distinct(StringComparer.Ordinal).SelectMany(system =>
            Records(repository, system).Select(record => (System: system, Record: record))).ToArray();
        evidenceCount += records.Length;
        if (findings.Count >= 20_000 || evidenceCount > 100_000)
            throw new OwnershipEvaluationLimitException();
        var fingerprint = OwnershipCanonical.Hash(new
        {
            V = "ownership-fingerprint.v2", FindingId = id, Kind = kind, CheckKey = key,
            CrosswalkDigest = inputs.Crosswalk.Digest, SemanticRulesDigest = semanticRulesDigest, RulesState = rulesState,
            Slots = systems.Select(system => new { System = system, Slot(system).Digest,
                Slot(system).Value?.SnapshotId, Slot(system).Value?.CapturedAt, State = feeds[system] }),
            Records = records.Select(row => new { row.System, row.Record }),
            Delegations = rules?.Delegations.Where(row => row.Scope.RuleId == key.RuleId &&
                row.Scope.RepositoryIds.Contains(repository, StringComparer.Ordinal) &&
                OwnershipInputValidation.Timestamp(row.ExpiresAt) > now).ToArray(),
            NotApplicable = rules?.NotApplicable.Where(row => row.RuleId == key.RuleId &&
                row.RepositoryIds.Contains(repository, StringComparer.Ordinal) &&
                OwnershipInputValidation.Timestamp(row.ExpiresAt) > now).ToArray()
        });
        OwnershipAppliedExceptionInfo? applied = null;
        if (key.Type is "require" or "relation" && rules is not null && status == "action-needed")
        {
            var exceptions = rules.Exceptions.Where(row => row.FindingId == id).ToArray();
            var match = exceptions.FirstOrDefault(row => row.Fingerprint == fingerprint &&
                OwnershipInputValidation.Timestamp(row.ExpiresAt) > now);
            if (match is not null) { status = "excepted"; applied = new(match.ExceptionId, match.ExpiresAt); }
            else if (exceptions.Length > 0)
                diagnostics.Add(new("exception-invalidated", "An exception does not match the current finding context or has expired."));
        }

        var guidance = OwnershipGuidance.For(kind, key, detail);
        var finding = new OwnershipFinding(id, repository, key, kind, status,
            records.Select(row => new OwnershipEvidence(row.System, row.Record.SourceRecordId,
                Slot(row.System).Value!.SnapshotId, Slot(row.System).Digest!, Slot(row.System).Value!.CapturedAt,
                feeds[row.System] == "current-full" || feeds[row.System] == "partial" ? "current" :
                    feeds[row.System] == "stale" ? "stale" : "unknown", Slot(row.System).Value!.Coverage, row.Record.Link)).ToArray(),
            records.Select(row => new OwnershipObservedRole(row.System, row.Record.Role, row.Record.Party,
                rules is null || !rules.Requirements.Any(rule => rule.System == row.System && rule.Role == row.Record.Role))).Distinct().ToArray(),
            expected ?? [], fingerprint, guidance.Reason, guidance.Action, guidance.Verification,
            resolutionParty is null ? null : new(resolutionParty), resolutionParty is null ? "needs-triage" : "assigned",
            key.RuleId, rules?.Version, crosswalk?.Version, applied);
        findings.Add(finding);
        return finding;
    }
}

internal sealed class OwnershipEvaluationLimitException : Exception;
