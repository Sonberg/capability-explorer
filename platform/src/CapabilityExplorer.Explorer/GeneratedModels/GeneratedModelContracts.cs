namespace CapabilityExplorer.Explorer.GeneratedModels;

public sealed record GeneratedModelRequest(
    string RunId,
    string Capability,
    string SnapshotId,
    long Generation,
    IReadOnlyDictionary<string, string> Selection,
    bool AuthorizeProvider,
    bool IncludeSourceSnippets,
    bool AuthorizeSourceSnippets);

public sealed record GeneratedModelInput(
    string Id,
    string Profile,
    string RepositoryId,
    string CommitSha,
    string UnitPath,
    string ArtifactSha256,
    string Coverage,
    string SourceStatus);

public sealed record GeneratedModelCitation(
    string ModelId,
    string CandidateId,
    string RepositoryId,
    string CommitSha,
    string UnitPath,
    int StartLine,
    int EndLine,
    string SourceSha256,
    string ArtifactSha256);

public sealed record GeneratedModelElement(string Id, string Kind, string Label, string? Condition, GeneratedModelCitation Citation);

public sealed record GeneratedModelFlow(string Id, string From, string To, string? Condition, GeneratedModelCitation Citation);

public sealed record GeneratedModelDiagram(
    string Id,
    string Kind,
    string Title,
    string ReviewStatus,
    string RepositoryId,
    string CommitSha,
    string UnitPath,
    IReadOnlyList<GeneratedModelElement> Elements,
    IReadOnlyList<GeneratedModelFlow> Flows,
    string? BpmnXml,
    string? DmnXml,
    IReadOnlyList<GeneratedModelCitation> Evidence);

public sealed record GeneratedModelProposal(
    string FromCandidateId,
    string ToCandidateId,
    string Relation,
    string Rationale,
    IReadOnlyList<string> EvidenceCandidateIds,
    string EvidenceClass,
    string ReviewStatus);

public sealed record GeneratedModelSnapshot(
    string Schema,
    string Status,
    string Capability,
    string SnapshotId,
    long Generation,
    IReadOnlyDictionary<string, string> Selection,
    string InputDigest,
    IReadOnlyList<GeneratedModelInput> Inputs,
    IReadOnlyList<GeneratedModelDiagram> Diagrams,
    IReadOnlyList<GeneratedModelProposal> Proposals,
    IReadOnlyList<string> Diagnostics,
    string Provider,
    string DataScope,
    bool SourceSnippetsIncluded,
    DateTimeOffset CreatedAt,
    string ContentHash);

public sealed record GeneratedModelRunState(
    string RunId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? Provider,
    string? DataScope,
    string SnapshotId,
    long Generation,
    IReadOnlyDictionary<string, string> Selection);

public sealed record GeneratedModelStoreDocument(
    GeneratedModelSnapshot? LatestSuccessful,
    GeneratedModelRunState? LatestRun);

public sealed record GeneratedModelRunResponse(GeneratedModelRunState Run, GeneratedModelSnapshot? Snapshot);
