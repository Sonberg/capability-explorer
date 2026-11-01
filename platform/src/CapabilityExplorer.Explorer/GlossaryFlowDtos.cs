namespace CapabilityExplorer.Explorer;

/// <summary>Direction follows recorded relation arrows, never inferred runtime value movement.</summary>
public sealed record GlossaryFlowOptions(string Direction = "both", int MaxHops = 4, int MaxNodes = 250, int MaxEdges = 500);

/// <summary>An exact reviewed term membership supplied by the review store, separate from source facts.</summary>
public sealed record GlossaryFlowHumanAssertion(
    string Id, string TermId, string TermName, string Kind, string OccurrenceId, string Reviewer, string Rationale);

public sealed record GlossaryFlowSelection(string RepositoryId, string CommitSha, string Unit, string UnitPath,
    string AnalysisStatus, int FilesTotal, int FilesAnalyzed, int? UnresolvedEdges);

public sealed record GlossaryFlowSeed(string OccurrenceId, string NodeId);

/// <summary>Exact source identity; human term hubs have null source coordinates.</summary>
public sealed record GlossaryFlowNode(string Id, string? RepositoryId, string? CommitSha, string? Unit,
    string? UnitPath, string? SymbolKey, IReadOnlyList<string> OccurrenceIds, IReadOnlyList<EvidenceDto> Evidence);

/// <summary>Source/binding context and human assertions are distinct; none claims field-value equality.</summary>
public sealed record GlossaryFlowEdge(string Id, string From, string To, string Category, string Relation,
    string? RepositoryId, string? CommitSha, string? UnitPath, string? RecordId,
    string Resolution, string Derivation, string? Rule, string? UnresolvedReason,
    IReadOnlyList<EvidenceDto> Evidence, string? BindingHint = null, string? BindingTarget = null,
    string? BindingNote = null, string? AssertionId = null, string? TermId = null,
    string? Reviewer = null, string? Rationale = null, string? OccurrenceId = null, string? InputRef = null,
    string? OriginalRecordId = null);

public sealed record GlossaryFlowReason(string Code, string Message, string? NodeId = null, string? EdgeId = null);

/// <summary>Complete describes available navigation, not completeness of runtime or field-value analysis.</summary>
public sealed record GlossaryFlowCoverage(bool Complete, bool Truncated, bool ValueFlowComplete,
    IReadOnlyList<GlossaryFlowReason> Reasons);

public sealed record GlossaryFlowResult(ArchitectureGraph Graph, IReadOnlyList<GlossaryFlowSelection> Selection,
    IReadOnlyList<GlossaryFlowSeed> Seeds, IReadOnlyList<GlossaryFlowNode> Nodes,
    IReadOnlyList<GlossaryFlowEdge> Edges, GlossaryFlowCoverage Coverage);
