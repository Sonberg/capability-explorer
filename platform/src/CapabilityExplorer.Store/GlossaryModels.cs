namespace CapabilityExplorer.Store;

/// <summary>Reviewer names are supplied attribution, not authenticated identities.</summary>
public sealed record GlossaryTermWrite(Guid Id, string Name, string Definition, string? Group, string Reviewer);
public sealed record GlossaryObservedOccurrence(string RepositoryId, string CommitSha, string UnitPath, string SymbolKey, string ArtifactContentSha256, string OccurrenceId);
public sealed record GlossaryLinkWrite(Guid Id, Guid TermId, string RepositoryId, string UnitPath, string SymbolKey, string Role, string? Note, GlossaryObservedOccurrence Observed, string Reviewer);
public sealed record GlossaryIssueWrite(Guid Id, Guid TermId, string Kind, string Summary, string? Note, string Status, string Reviewer);

public sealed record GlossaryTerm(Guid Id, string Name, string Definition, string? Group, string Reviewer, long Version, bool Deleted);
public sealed record GlossaryLink(Guid Id, Guid TermId, string RepositoryId, string UnitPath, string SymbolKey, string Role, string? Note, GlossaryObservedOccurrence Observed, string Reviewer, long Version, bool Deleted);
public sealed record GlossaryIssue(Guid Id, Guid TermId, string Kind, string Summary, string? Note, string Status, string Reviewer, long Version, bool Deleted);

public enum GlossaryMutationStatus { Created, Updated, Deleted, Unchanged, NotFound, Conflict }
public sealed record GlossaryMutationResult<T>(GlossaryMutationStatus Status, T? Value);
public sealed record GlossarySnapshot(
    IReadOnlyList<GlossaryTerm> Terms, IReadOnlyList<GlossaryLink> Links, IReadOnlyList<GlossaryIssue> Issues,
    int Limit, bool TermsTruncated, bool LinksTruncated, bool IssuesTruncated);
public sealed record GlossaryAuditEvent(long Sequence, string EntityType, Guid EntityId, long Version, string Action, string Actor, string DataJson, DateTimeOffset RecordedAt);
public sealed record GlossaryAuditPage(IReadOnlyList<GlossaryAuditEvent> Events, int Limit, bool Truncated);
