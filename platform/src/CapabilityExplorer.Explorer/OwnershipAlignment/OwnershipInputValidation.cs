using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapabilityExplorer.Explorer;

public static partial class OwnershipInputValidation
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    public static bool IsRepository(string? value) => value is not null && RepositoryPattern().IsMatch(value);

    public static DateTimeOffset Timestamp(string value)
    {
        Require(value is not null && value.Length <= 40 && TimestampPattern().IsMatch(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    public static int FreshnessDays(string value)
    {
        Require(value is not null && value.Length is >= 3 and <= 5 && value[0] == 'P' && value[^1] == 'D' &&
            value.AsSpan(1, value.Length - 2).IndexOfAnyExceptInRange('0', '9') < 0 &&
            int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var days) &&
            days is >= 1 and <= 365);
        return int.Parse(value.AsSpan(1, value.Length - 2), CultureInfo.InvariantCulture);
    }

    public static void Snapshot(OwnershipSnapshot value, string system, DateTimeOffset now)
    {
        Require(value.Schema == "ce.ownership-snapshot.v1" && value.System == system);
        Id(value.SnapshotId);
        Require(Timestamp(value.CapturedAt) <= now.AddMinutes(5) && value.Coverage is "full" or "partial");
        if (value.SourceRevision is not null) Id(value.SourceRevision);
        var records = Items(value.Records, 10_000);
        Require(records.Select(record => record.SourceRecordId).Distinct(StringComparer.Ordinal).Count() == records.Count);
        foreach (var record in records)
        {
            Id(record.SourceRecordId); Id(record.SubjectId); Id(record.Role, 128);
            if (record.Party is { } party)
            {
                Require(party.Kind is "user" or "team" or "group");
                Id(party.Id);
            }
            if (record.Link is { } link)
                Require(link.Length <= 2048 && Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && !link.Any(char.IsControl));
        }
    }

    public static void Crosswalk(OwnershipCrosswalk value)
    {
        Require(value.Schema == "ce.ownership-crosswalk.v1");
        Id(value.Version); Id(value.ReviewedBy); _ = Timestamp(value.ReviewedAt);
        var subjects = Items(value.Subjects, 5000);
        var shared = Items(value.SharedSubjects, 5000);
        var parties = Items(value.Parties, 5000);
        Require(subjects.Count + shared.Count + parties.Count <= 5000);
        Require(parties.Select(party => party.PartyRef).Distinct(StringComparer.Ordinal).Count() == parties.Count);
        foreach (var subject in subjects)
        {
            Require(IsRepository(subject.RepositoryId)); System(subject.System); Id(subject.SubjectId); Review(subject.Review);
            if (subject.LandscapeSubject is not null) Id(subject.LandscapeSubject);
        }
        foreach (var subject in shared)
        {
            System(subject.System); Id(subject.SubjectId); Review(subject.Review);
            foreach (var repository in Items(subject.RepositoryIds, 5000)) Require(IsRepository(repository));
            Require(subject.RepositoryIds.Count > 1 &&
                subject.RepositoryIds.Distinct(StringComparer.Ordinal).Count() == subject.RepositoryIds.Count);
        }
        Require(shared.Select(row => (row.System, row.SubjectId)).Distinct().Count() == shared.Count);
        foreach (var party in parties)
        {
            Id(party.PartyRef); Review(party.Review);
            foreach (var member in Items(party.Members, 5000))
            {
                System(member.System); Require(member.Kind is "user" or "team" or "group"); Id(member.Id);
            }
        }
    }

    public static void Rules(OwnershipRules value)
    {
        Require(value.Schema == "ce.ownership-rules.v1");
        Id(value.Version); Id(value.ReviewedBy); _ = Timestamp(value.ReviewedAt);
        Require(value.Freshness is not null && value.Freshness.Count == 4 &&
            OwnershipSystems.All.All(value.Freshness.ContainsKey));
        foreach (var duration in value.Freshness.Values) _ = FreshnessDays(duration);
        var requirements = Items(value.Requirements, 128);
        var relations = Items(value.Relations, 128);
        Require(requirements.Count + relations.Count <= 128);
        var ids = requirements.Select(rule => rule.RuleId).Concat(relations.Select(rule => rule.RuleId)).ToArray();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length);
        foreach (var rule in requirements)
        {
            Id(rule.RuleId); System(rule.System); Id(rule.Role, 128);
            Require(rule.MinParties is >= 1 and <= 10_000 && (rule.MaxParties is null ||
                rule.MaxParties >= rule.MinParties && rule.MaxParties <= 10_000));
            if (rule.ResolutionPartyRef is not null) Id(rule.ResolutionPartyRef);
        }
        foreach (var rule in relations)
        {
            Id(rule.RuleId);
            foreach (var role in new[] { rule.Left, rule.Right })
            {
                Require(role is not null); System(role.System); Id(role.Role, 128);
                Require(requirements.Any(required => required.System == role.System && required.Role == role.Role));
            }
            Require(rule.Relation is "same-party-set" or "left-within-right" or "delegated" or "independent");
            if (rule.ResolutionPartyRef is not null) Id(rule.ResolutionPartyRef);
        }
        var delegations = Items(value.Delegations, 512);
        Require(delegations.Select(row => row.DelegationId).Distinct(StringComparer.Ordinal).Count() == delegations.Count);
        foreach (var row in delegations)
        {
            Id(row.DelegationId); Id(row.FromPartyRef); Id(row.ToPartyRef); _ = Timestamp(row.ExpiresAt);
            Require(row.Scope is not null && ids.Contains(row.Scope.RuleId, StringComparer.Ordinal));
            foreach (var repository in Items(row.Scope.RepositoryIds, 100)) Require(IsRepository(repository));
        }
        foreach (var row in Items(value.NotApplicable, 512))
        {
            Require(ids.Contains(row.RuleId, StringComparer.Ordinal)); System(row.System);
            Require(requirements.Any(rule => rule.RuleId == row.RuleId && rule.System == row.System) ||
                relations.Any(rule => rule.RuleId == row.RuleId &&
                    (rule.Left.System == row.System || rule.Right.System == row.System)));
            foreach (var repository in Items(row.RepositoryIds, 100)) Require(IsRepository(repository));
            Id(row.Justification, 2048); Id(row.ApprovedBy); _ = Timestamp(row.ExpiresAt);
        }
        var exceptions = Items(value.Exceptions, 512);
        Require(exceptions.Select(row => row.ExceptionId).Distinct(StringComparer.Ordinal).Count() == exceptions.Count);
        foreach (var row in exceptions)
        {
            Id(row.ExceptionId); Id(row.Reason, 2048); Id(row.ApprovedBy); _ = Timestamp(row.ExpiresAt);
            Require(row.FindingId is { Length: 27 } && row.FindingId.StartsWith("of_", StringComparison.Ordinal) &&
                IsHex(row.FindingId[3..]) && row.Fingerprint is { Length: 64 } && IsHex(row.Fingerprint));
        }
    }

    private static bool IsHex(string value) => value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void System(string value) => Require(OwnershipSystems.All.Contains(value, StringComparer.Ordinal));
    private static void Review(string value) => Require(value is "reviewed" or "draft");
    private static void Id(string? value, int max = 256) =>
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl));

    private static IReadOnlyList<T> Items<T>(IReadOnlyList<T>? values, int max)
    {
        Require(values is not null && values.Count <= max && values.All(value => value is not null));
        return values!;
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid)
    {
        if (!valid) throw new JsonException("Invalid ownership input.");
    }
}
