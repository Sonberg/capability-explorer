using System.Text.Json.Serialization;

namespace CapabilityExplorer.Explorer;

/// <summary>Six read-only, bounded task-query operations (schema ce.task-query.v1). Every result, including every
/// error, is this one envelope: nothing escapes without snapshot, coverage and diagnostics.</summary>
public static class TaskQueryOperations
{
    public const string SnapshotResolve = "snapshot_resolve";
    public const string ModelsList = "models_list";
    public const string DecisionReferences = "decision_references";
    public const string ContractConsumers = "contract_consumers";
    public const string EvidenceList = "evidence_list";
    public const string SourceExcerpt = "source_excerpt";

    public static readonly IReadOnlyList<string> All = [SnapshotResolve, ModelsList, DecisionReferences, ContractConsumers, EvidenceList, SourceExcerpt];
}

/// <summary>ok/partial are 200; missing is 404 (never "absence"); stale is 409; denied is 403; invalid is 400;
/// limit is 413; cancelled is 504 (caller abort gets no body/response at all); unavailable is 503.</summary>
public enum TaskQueryStatus { Ok, Partial, Missing, Stale, Denied, Invalid, Limit, Cancelled, Unavailable }

public static class TaskQueryStatusCodes
{
    public static int HttpStatus(TaskQueryStatus status) => status switch
    {
        TaskQueryStatus.Ok or TaskQueryStatus.Partial => StatusCodes.Status200OK,
        TaskQueryStatus.Missing => StatusCodes.Status404NotFound,
        TaskQueryStatus.Stale => StatusCodes.Status409Conflict,
        TaskQueryStatus.Denied => StatusCodes.Status403Forbidden,
        TaskQueryStatus.Invalid => StatusCodes.Status400BadRequest,
        TaskQueryStatus.Limit => StatusCodes.Status413PayloadTooLarge,
        TaskQueryStatus.Cancelled => StatusCodes.Status504GatewayTimeout,
        TaskQueryStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Same mapping, from the wire string an envelope actually carries (<see cref="TaskQueryEnvelope.Status"/>):
    /// the HTTP front end never needs to hold on to the enum once a result is serialized.</summary>
    public static int HttpStatus(string wire) => wire switch
    {
        "ok" or "partial" => StatusCodes.Status200OK,
        "missing" => StatusCodes.Status404NotFound,
        "stale" => StatusCodes.Status409Conflict,
        "denied" => StatusCodes.Status403Forbidden,
        "invalid" => StatusCodes.Status400BadRequest,
        "limit" => StatusCodes.Status413PayloadTooLarge,
        "cancelled" => StatusCodes.Status504GatewayTimeout,
        "unavailable" => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(wire)),
    };

    public static string Wire(this TaskQueryStatus status) => status switch
    {
        TaskQueryStatus.Ok => "ok",
        TaskQueryStatus.Partial => "partial",
        TaskQueryStatus.Missing => "missing",
        TaskQueryStatus.Stale => "stale",
        TaskQueryStatus.Denied => "denied",
        TaskQueryStatus.Invalid => "invalid",
        TaskQueryStatus.Limit => "limit",
        TaskQueryStatus.Cancelled => "cancelled",
        TaskQueryStatus.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

public sealed record TaskQuerySnapshotSelection(string Repo, string Sha);

public sealed record TaskQuerySnapshotInfo(
    string Id,
    long Generation,
    IReadOnlyList<TaskQuerySnapshotSelection> Selection,
    string SelectionDigest,
    string Freshness,
    string? CurrentId);

public sealed record TaskQueryCoverageRepository(string RepositoryId, string CommitSha, string Status);

public sealed record TaskQueryCoverage(IReadOnlyList<TaskQueryCoverageRepository> Repositories, string Completeness, string OutsideSelection = "unknown");

public sealed record TaskQueryPage(int Limit, string? NextCursor, object Total);

public sealed record TaskQueryDiagnostic(string Code, string Message);

/// <summary>The one envelope shape for every operation result and every error (schema ce.task-query.v1).</summary>
public sealed record TaskQueryEnvelope(
    string Operation,
    string Status,
    string Mode,
    TaskQuerySnapshotInfo? Snapshot,
    TaskQueryCoverage Coverage,
    TaskQueryPage? Page,
    object? Data,
    IReadOnlyList<TaskQueryDiagnostic> Diagnostics,
    string? Reason = null)
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "ce.task-query.v1";
}

public sealed record TaskQueryDecisionTarget(string ModelId, string DecisionId);

public sealed record TaskQueryDecisionReference(
    string ModelId,
    string ElementId,
    string Label,
    string Reference,
    string Status,
    TaskQueryDecisionTarget? Target,
    string EvidenceClass,
    bool Reviewed);

public sealed record TaskQueryConsumer(string Key, string Name, string Kind, string RepositoryId, string CommitSha);

public sealed record TaskQueryConsumerEdge(string Id, string Relation, string Resolution, string? UnresolvedReason);

public sealed record TaskQueryConsumerConnection(
    string StructuralCompatibility,
    string SemanticCorrespondence,
    string ConfiguredBinding,
    string ObservedCommunication);

public sealed record TaskQueryConsumerDto(
    TaskQueryConsumer Consumer,
    TaskQueryConsumerEdge Edge,
    TaskQueryConsumerConnection Connection,
    object Configuration,
    IReadOnlyList<EvidenceDto> Evidence,
    string AssertionClass);

public sealed record TaskQuerySourceLine(int N, string Text);

public sealed record TaskQuerySourceExcerpt(
    string Repo,
    string Commit,
    string Path,
    int HighlightStart,
    int HighlightEnd,
    int TotalLines,
    IReadOnlyList<TaskQuerySourceLine> Lines);
