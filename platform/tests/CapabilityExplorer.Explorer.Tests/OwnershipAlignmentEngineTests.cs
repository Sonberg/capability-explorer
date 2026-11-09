using System.Text;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class OwnershipAlignmentEngineTests
{
    private static OwnershipReport Run(OwnershipInputs inputs, DateTimeOffset? now = null, string audience = "engineering") =>
        OwnershipAlignmentEngine.Evaluate(inputs, now ?? OwnershipTestInputs.Now, audience);

    [Fact]
    public void CompleteReviewedEvidenceAlignsAndAudienceOnlyChangesGroups()
    {
        var inputs = OwnershipTestInputs.Create();
        var engineering = Run(inputs);
        var architecture = Run(inputs, audience: "architecture");
        Assert.Equal("aligned", engineering.Alignment);
        Assert.Equal("ok", engineering.Status);
        Assert.Equal(OwnershipCanonical.Hash(engineering.Findings), OwnershipCanonical.Hash(architecture.Findings));
        Assert.Equal(engineering.InputVector, architecture.InputVector);
        Assert.Equal("svc-a", engineering.Groups.Single().Key);
        Assert.Equal("application-a", architecture.Groups.Single().Key);
        Assert.Contains(engineering.Diagnostics, diagnostic => diagnostic.Code == "manual-trust");
        Assert.All(engineering.Findings.Where(finding => finding.CheckKey.Type is "feed" or "identity" or "rules" or "rule-coverage"),
            finding => Assert.Equal("observed", finding.Status));
    }

    [Theory]
    [InlineData("not-configured", "feed-missing")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("denied", "denied")]
    [InlineData("invalid", "invalid")]
    public void FeedFailuresStayDistinctEvenWithInvalidRules(string state, string expected)
    {
        var inputs = OwnershipTestInputs.Create() with { Rules = new("invalid") };
        var report = Run(OwnershipTestInputs.Slot(inputs, "leanix", state));
        Assert.Equal(expected, OwnershipTestInputs.Find(report, "feed", "leanix").Kind);
        Assert.DoesNotContain(report.Findings, finding => finding.CheckKey.Type is "require" or "relation");
        Assert.Equal("action-needed", report.Alignment);
    }

    [Theory]
    [InlineData("not-configured", "rules-not-configured")]
    [InlineData("invalid", "rules-invalid")]
    [InlineData("denied", "rules-denied")]
    [InlineData("unavailable", "rules-unavailable")]
    public void NoPartialFreshnessFromNonValidRules(string state, string expected)
    {
        var inputs = OwnershipTestInputs.Create() with { Rules = new(state) };
        inputs = OwnershipTestInputs.Snapshot(inputs, "leanix", snapshot => snapshot with { Coverage = "partial", Records = [] });
        var report = Run(inputs);
        Assert.Equal(expected, OwnershipTestInputs.Find(report, "rules").Kind);
        Assert.All(report.Coverage.Systems, source => Assert.Equal("freshness-unknown", source.State));
        Assert.Equal("partial", report.Coverage.Systems.Single(source => source.System == "leanix").Coverage);
        Assert.Equal("absence-unknown", OwnershipTestInputs.Find(report, "identity", "leanix").Kind);
        Assert.DoesNotContain(report.Findings, finding => finding.Status == "aligned");
        Assert.All(OwnershipSystems.All, system => Assert.Equal("not-evaluable", OwnershipTestInputs.Find(report, "rule-coverage", system).Status));
    }

    [Fact]
    public void EmptyAndUncoveredRulesNeverEstablishAlignment()
    {
        var inputs = OwnershipTestInputs.Create();
        var empty = Run(inputs with { Rules = OwnershipTestInputs.Loaded(inputs.Rules.Value! with { Requirements = [], Relations = [] }) });
        Assert.Equal("not-evaluable", empty.Alignment);
        Assert.All(empty.Findings.Where(finding => finding.CheckKey.Type == "rule-coverage"),
            finding => Assert.Equal("no-rule-for-system", finding.Kind));
        var uncovered = Run(inputs with { Rules = OwnershipTestInputs.Loaded(inputs.Rules.Value! with
            { Requirements = inputs.Rules.Value!.Requirements.Where(rule => rule.System != "backstage").ToArray() }) });
        Assert.Equal("no-rule-for-system", OwnershipTestInputs.Find(uncovered, "rule-coverage", "backstage").Kind);
        Assert.NotEqual("aligned", uncovered.Alignment);
    }

    [Fact]
    public void MissingOwnerConflictAndFreshResolutionAreActionable()
    {
        var inputs = OwnershipTestInputs.Create();
        var missing = Run(OwnershipTestInputs.Snapshot(inputs, "leanix",
            snapshot => snapshot with { Records = [snapshot.Records.Single() with { Party = null }] }));
        Assert.Equal("missing-owner", OwnershipTestInputs.Find(missing, "require", rule: "required-leanix").Kind);
        Assert.Equal("prerequisite-not-met", OwnershipTestInputs.Find(missing, "relation").Kind);
        var resolved = OwnershipTestInputs.Find(Run(inputs), "require", rule: "required-leanix");
        Assert.Equal(resolved.FindingId, OwnershipTestInputs.Find(missing, "require", rule: "required-leanix").FindingId);
        Assert.Equal("aligned", resolved.Status);
        var conflictInputs = Conflict();
        var conflict = OwnershipTestInputs.Find(Run(conflictInputs), "relation");
        Assert.Equal("owner-conflict", conflict.Kind);
        Assert.Equal("action-needed", conflict.Status);
        Assert.Contains(conflict.Evidence, evidence => evidence.System == "github");
        Assert.Contains(conflict.Evidence, evidence => evidence.System == "leanix");
        Assert.Null(conflict.ResolutionOwner);
        Assert.Equal("needs-triage", conflict.Triage);
        Assert.Contains("reconcile", conflict.NextAction, StringComparison.OrdinalIgnoreCase);
        var assigned = Run(conflictInputs with { Rules = OwnershipTestInputs.Loaded(conflictInputs.Rules.Value! with
            { Relations = [conflictInputs.Rules.Value!.Relations.Single() with { ResolutionPartyRef = "party-p" }] }) });
        Assert.Equal("party-p", OwnershipTestInputs.Find(assigned, "relation").ResolutionOwner!.PartyRef);
    }

    [Fact]
    public void EmptySetsAndUnknownPartiesNeverAlign()
    {
        var inputs = OwnershipTestInputs.Create();
        foreach (var system in new[] { "github", "leanix" })
            inputs = OwnershipTestInputs.Snapshot(inputs, system, snapshot => snapshot with { Records = [snapshot.Records.Single() with { Party = null }] });
        Assert.Equal("prerequisite-not-met", OwnershipTestInputs.Find(Run(inputs), "relation").Kind);
        var unknown = OwnershipTestInputs.Create();
        unknown = unknown with { Crosswalk = OwnershipTestInputs.Loaded(unknown.Crosswalk.Value! with { Parties = [] }) };
        Assert.Equal("unknown-party-mapping", OwnershipTestInputs.Find(Run(unknown), "relation").Kind);
        var independent = unknown with { Rules = OwnershipTestInputs.Loaded(unknown.Rules.Value! with
            { Relations = [unknown.Rules.Value!.Relations.Single() with { Relation = "independent" }] }) };
        Assert.Equal("aligned", Run(independent).Alignment);
    }

    [Fact]
    public void AliasesMultipleOwnersAndAmbiguousPartyMappingsAreNotCollapsed()
    {
        var inputs = OwnershipTestInputs.Create();
        inputs = inputs with { Crosswalk = OwnershipTestInputs.Loaded(inputs.Crosswalk.Value! with
        {
            Subjects = [.. inputs.Crosswalk.Value!.Subjects, new("svc-a", "backstage", "alias", "reviewed")],
            Parties = [.. inputs.Crosswalk.Value!.Parties, new("party-q", [new("leanix", "team", "team-leanix")], "reviewed")]
        }) };
        inputs = OwnershipTestInputs.Snapshot(inputs, "backstage", snapshot => snapshot with
            { Records = [.. snapshot.Records, new("alias-record", "alias", "native-owner", new("team", "other-team"))] });
        var report = Run(inputs);
        Assert.Equal("identity-linked", OwnershipTestInputs.Find(report, "identity", "backstage").Kind);
        Assert.Equal("aligned", OwnershipTestInputs.Find(report, "require", rule: "required-backstage").Kind);
        Assert.Equal("unknown-party-mapping", OwnershipTestInputs.Find(report, "relation").Kind);
        Assert.Single(report.Findings, finding => finding.Kind == "ambiguous-party-mapping");
    }

    [Fact]
    public void SharedSubjectsRequireExactReviewWithoutDisclosingUngrantedRepositories()
    {
        var inputs = OwnershipTestInputs.Create();
        inputs = inputs with { Crosswalk = OwnershipTestInputs.Loaded(inputs.Crosswalk.Value! with
            { Subjects = [.. inputs.Crosswalk.Value!.Subjects, new("svc-z-private", "leanix", "subject-leanix", "reviewed")] }) };
        var ambiguous = Run(inputs);
        Assert.Equal("ambiguous-identity", OwnershipTestInputs.Find(ambiguous, "relation").Kind);
        Assert.DoesNotContain("svc-z-private", JsonSerializer.Serialize(ambiguous), StringComparison.Ordinal);
        var shared = inputs with { Crosswalk = OwnershipTestInputs.Loaded(inputs.Crosswalk.Value! with
            { SharedSubjects = [new("leanix", "subject-leanix", ["svc-a", "svc-z-private"], "reviewed")] }) };
        Assert.Equal("ambiguous-identity", OwnershipTestInputs.Find(Run(shared), "relation").Kind);
        Assert.DoesNotContain("svc-z-private", JsonSerializer.Serialize(Run(shared)), StringComparison.Ordinal);
        var bothGranted = Run(shared with { Repositories = ["svc-a", "svc-z-private"] });
        Assert.Equal("aligned", bothGranted.Findings.Single(finding => finding.RepositoryId == "svc-a" && finding.CheckKey.Type == "relation").Status);
        var narrowed = Run(shared with { GrantedRepositories = ["svc-a", "svc-z-private"] });
        Assert.Equal("aligned", narrowed.Alignment);
        Assert.DoesNotContain("svc-z-private", JsonSerializer.Serialize(narrowed), StringComparison.Ordinal);
    }

    [Fact]
    public void FreshnessPartialAndTimeOnlyChangesBlockDependentAlignment()
    {
        var inputs = OwnershipTestInputs.Create();
        var current = Run(inputs);
        var later = Run(inputs, OwnershipTestInputs.Now.AddDays(8));
        Assert.All(later.Coverage.Systems, source => Assert.Equal("stale", source.State));
        Assert.Equal("not-evaluable", OwnershipTestInputs.Find(later, "relation").Status);
        Assert.NotEqual(current.InputVector, later.InputVector);
        Assert.Equal(OwnershipTestInputs.Find(current, "feed", "leanix").FindingId, OwnershipTestInputs.Find(later, "feed", "leanix").FindingId);
        var partial = Run(OwnershipTestInputs.Snapshot(inputs, "leanix", snapshot => snapshot with { Coverage = "partial", Records = [] }));
        Assert.Equal("absence-unknown", OwnershipTestInputs.Find(partial, "identity", "leanix").Kind);
        Assert.Equal("not-evaluable", OwnershipTestInputs.Find(partial, "relation").Status);
    }

    [Fact]
    public void ScopedDelegationAndRuleSpecificNotApplicableExpireWithoutHidingFeeds()
    {
        var inputs = Conflict();
        var rule = inputs.Rules.Value!;
        rule = rule with
        {
            Relations = [rule.Relations.Single() with { Relation = "delegated" }],
            Delegations = [new("delegation", "party-p", "party-q", new("relation-owner", ["svc-a"]), OwnershipTestInputs.Now.AddDays(2).ToString("O"))]
        };
        inputs = inputs with { Rules = OwnershipTestInputs.Loaded(rule) };
        Assert.Equal("aligned", OwnershipTestInputs.Find(Run(inputs), "relation").Status);
        Assert.Equal("owner-conflict", OwnershipTestInputs.Find(Run(inputs, OwnershipTestInputs.Now.AddDays(3)), "relation").Kind);
        rule = rule with { NotApplicable = [new("relation-owner", "leanix", ["svc-a"], "Synthetic scope", "synthetic-review", OwnershipTestInputs.Now.AddDays(2).ToString("O"))] };
        var excepted = Run(OwnershipTestInputs.Slot(inputs with { Rules = OwnershipTestInputs.Loaded(rule) }, "leanix", "denied"));
        Assert.Equal("not-applicable", OwnershipTestInputs.Find(excepted, "relation").Status);
        Assert.Equal("denied", OwnershipTestInputs.Find(excepted, "feed", "leanix").Kind);
        Assert.Equal("action-needed", excepted.Alignment);
    }

    [Fact]
    public void MatchingExceptionIsNotSelfReferentialAndNewEvidenceInvalidatesIt()
    {
        var inputs = Conflict();
        var original = OwnershipTestInputs.Find(Run(inputs), "relation");
        var exception = new OwnershipExceptionRule("exception-1", original.FindingId, original.Fingerprint,
            "Synthetic acceptance", "synthetic-review", OwnershipTestInputs.Now.AddDays(2).ToString("O"));
        var excepted = inputs with { Rules = OwnershipTestInputs.Loaded(inputs.Rules.Value! with { Exceptions = [exception] }) };
        var applied = OwnershipTestInputs.Find(Run(excepted), "relation");
        Assert.Equal("excepted", applied.Status);
        Assert.Equal(original.Fingerprint, applied.Fingerprint);
        Assert.NotEqual(Run(inputs).InputVector, Run(excepted).InputVector);
        Assert.Equal("action-needed", OwnershipTestInputs.Find(Run(excepted, OwnershipTestInputs.Now.AddDays(3)), "relation").Status);
        var changedRules = excepted with { Rules = OwnershipTestInputs.Loaded(excepted.Rules.Value! with
            { Relations = [excepted.Rules.Value!.Relations.Single() with { Relation = "left-within-right" }] }) };
        var changedCrosswalk = excepted with { Crosswalk = OwnershipTestInputs.Loaded(excepted.Crosswalk.Value! with { ReviewedBy = "new-review" }) };
        var changedEvidence = OwnershipTestInputs.Snapshot(excepted, "leanix", snapshot => snapshot with { SourceRevision = "new-evidence" });
        var changedCapture = OwnershipTestInputs.Snapshot(excepted, "leanix", snapshot => snapshot with { CapturedAt = OwnershipTestInputs.Now.AddMinutes(1).ToString("O") });
        foreach (var changed in new[] { changedRules, changedCrosswalk, changedEvidence, changedCapture })
        {
            var report = Run(changed);
            Assert.Equal(original.FindingId, OwnershipTestInputs.Find(report, "relation").FindingId);
            Assert.NotEqual(original.Fingerprint, OwnershipTestInputs.Find(report, "relation").Fingerprint);
            Assert.Equal("action-needed", OwnershipTestInputs.Find(report, "relation").Status);
            Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "exception-invalidated");
        }
    }

    private static OwnershipInputs Conflict()
    {
        var inputs = OwnershipTestInputs.Create();
        return inputs with { Crosswalk = OwnershipTestInputs.Loaded(inputs.Crosswalk.Value! with
        {
            Parties =
            [
                new("party-p", [new("github", "team", "team-github"), new("servicenow", "team", "team-servicenow"), new("backstage", "team", "team-backstage")], "reviewed"),
                new("party-q", [new("leanix", "team", "team-leanix")], "reviewed")
            ]
        }) };
    }

    [Fact]
    public void CanonicalBytesPinSortingNullsAndDefaultEscaping()
    {
        using var document = JsonDocument.Parse("""{"z":null,"x":"<","a":[2,1]}""");
        Assert.Equal("""{"a":[1,2],"x":"\u003C","z":null}""",
            Encoding.UTF8.GetString(OwnershipCanonical.Encode(document.RootElement)));
    }

    [Fact]
    public void TypedIdentityDoesNotConfuseDelimiterCharactersOrInputOrdering()
    {
        var first = OwnershipCanonical.Hash(new { Role = "a|b", PartyId = "c", Members = new[] { "q", "p" } });
        var reordered = OwnershipCanonical.Hash(new { Members = new[] { "p", "q" }, PartyId = "c", Role = "a|b" });
        var different = OwnershipCanonical.Hash(new { Role = "a", PartyId = "b|c", Members = new[] { "p", "q" } });
        Assert.Equal(first, reordered);
        Assert.NotEqual(first, different);
    }
}
