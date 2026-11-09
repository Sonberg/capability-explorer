using System.Text.Json.Serialization;

namespace CapabilityExplorer.Explorer;

public static class OwnershipSystems
{
    public static IReadOnlyList<string> All { get; } = ["github", "leanix", "servicenow", "backstage"];
}

public sealed record OwnershipParty(string Kind, string Id);
public sealed record OwnershipRecord(
    string SourceRecordId, string SubjectId, string Role, OwnershipParty? Party, string? Link = null);
public sealed record OwnershipSnapshot(
    string Schema, string System, string SnapshotId, string CapturedAt, string Coverage,
    IReadOnlyList<OwnershipRecord> Records, string? SourceRevision = null);
public sealed record OwnershipSubject(
    string RepositoryId, string System, string SubjectId, string Review, string? LandscapeSubject = null);
public sealed record OwnershipSharedSubject(
    string System, string SubjectId, IReadOnlyList<string> RepositoryIds, string Review);
public sealed record OwnershipPartyMember(string System, string Kind, string Id);
public sealed record OwnershipPartyMapping(
    string PartyRef, IReadOnlyList<OwnershipPartyMember> Members, string Review);
public sealed record OwnershipCrosswalk(
    string Schema, string Version, string ReviewedBy, string ReviewedAt,
    IReadOnlyList<OwnershipSubject> Subjects, IReadOnlyList<OwnershipSharedSubject> SharedSubjects,
    IReadOnlyList<OwnershipPartyMapping> Parties);
public sealed record OwnershipRole(string System, string Role);
public sealed record OwnershipRequirement(
    string RuleId, string System, string Role, int MinParties, int? MaxParties,
    string? ResolutionPartyRef = null);
public sealed record OwnershipRelation(
    string RuleId, OwnershipRole Left, OwnershipRole Right, string Relation,
    string? ResolutionPartyRef = null);
public sealed record OwnershipDelegationScope(string RuleId, IReadOnlyList<string> RepositoryIds);
public sealed record OwnershipDelegation(
    string DelegationId, string FromPartyRef, string ToPartyRef, OwnershipDelegationScope Scope,
    string ExpiresAt);
public sealed record OwnershipNotApplicable(
    string RuleId, string System, IReadOnlyList<string> RepositoryIds,
    string Justification, string ApprovedBy, string ExpiresAt);
public sealed record OwnershipExceptionRule(
    string ExceptionId, string FindingId, string Fingerprint, string Reason,
    string ApprovedBy, string ExpiresAt);
public sealed record OwnershipRules(
    string Schema, string Version, string ReviewedBy, string ReviewedAt,
    IReadOnlyDictionary<string, string> Freshness,
    IReadOnlyList<OwnershipRequirement> Requirements, IReadOnlyList<OwnershipRelation> Relations,
    IReadOnlyList<OwnershipDelegation> Delegations, IReadOnlyList<OwnershipNotApplicable> NotApplicable,
    IReadOnlyList<OwnershipExceptionRule> Exceptions);

public sealed record OwnershipInput<T>(string State, string? Digest = null, T? Value = null) where T : class;
public sealed record OwnershipInputs(
    IReadOnlyList<string> Repositories,
    IReadOnlyDictionary<string, OwnershipInput<OwnershipSnapshot>> Snapshots,
    OwnershipInput<OwnershipCrosswalk> Crosswalk, OwnershipInput<OwnershipRules> Rules,
    IReadOnlyList<string>? GrantedRepositories = null);

public sealed record OwnershipCheckKey(
    string Type, string? System = null, string? RuleId = null, string? Kind = null, string? Id = null);
public sealed record OwnershipObservedRole(
    string System, string Role, OwnershipParty? Party, bool Unruled = false);
public sealed record OwnershipEvidence(
    string System, string SourceRecordId, string SnapshotId, string Digest,
    string CapturedAt, string Freshness, string Coverage, string? Link = null);
public sealed record OwnershipResolutionOwner(string PartyRef);
public sealed record OwnershipAppliedExceptionInfo(string ExceptionId, string ExpiresAt);
public sealed record OwnershipFinding(
    string FindingId, string RepositoryId, OwnershipCheckKey CheckKey, string Kind, string Status,
    IReadOnlyList<OwnershipEvidence> Evidence, IReadOnlyList<OwnershipObservedRole> ObservedRoles,
    IReadOnlyList<OwnershipRole> ExpectedRoles, string Fingerprint, string Reason,
    string NextAction, string Verification,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] OwnershipResolutionOwner? ResolutionOwner,
    string Triage, string? RuleId = null, string? RulesVersion = null, string? CrosswalkVersion = null,
    OwnershipAppliedExceptionInfo? Exception = null);
public sealed record OwnershipSourceCoverage(
    string System, string State, string? SnapshotId = null, string? CapturedAt = null, string? Coverage = null);
public sealed record OwnershipCoverage(IReadOnlyList<OwnershipSourceCoverage> Systems);
public sealed record OwnershipGroup(string Key, IReadOnlyList<string> FindingIds);
public sealed record OwnershipDiagnostic(string Code, string Message);
public sealed record OwnershipReport(
    string Status, string Alignment, string Audience, string InputVector, string GeneratedAt,
    OwnershipCoverage Coverage, IReadOnlyList<OwnershipFinding> Findings,
    IReadOnlyList<OwnershipGroup> Groups, IReadOnlyList<OwnershipDiagnostic> Diagnostics,
    string? Reason = null, string? NextAction = null)
{
    public string Schema => "ce.ownership-alignment.v1";
}

public sealed class OwnershipAlignmentOptions
{
    public bool Enabled { get; set; }
    public string Root { get; set; } = "";
    public string[] Repositories { get; set; } = [];
    public Dictionary<string, string> Snapshots { get; set; } = new(StringComparer.Ordinal);
    public string? Crosswalk { get; set; }
    public string? Rules { get; set; }
}
