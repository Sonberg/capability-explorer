using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapabilityExplorer.Evaluation;

/// <summary>A relation the golden set lists exhaustively, optionally only for some source node kinds.</summary>
public sealed record ExhaustiveRelation(string Relation, IReadOnlyList<string>? FromKinds = null)
{
    public bool Covers(string relation, string? fromKind) =>
        relation == Relation && (FromKinds is null || (fromKind is not null && FromKinds.Contains(fromKind)));
}

public sealed record EvaluationScope(IReadOnlyList<ExhaustiveRelation> ExhaustiveRelations, IReadOnlyList<string> ExhaustiveNodeKinds)
{
    public static EvaluationScope None { get; } = new([], []);
}

/// <summary>Minimum values for the totals. Ratios are 0..1. Null means "not gated".</summary>
public sealed record Thresholds
{
    public double? NodeRecall { get; init; }
    public double? EdgeRecall { get; init; }
    public double? EdgeAccuracy { get; init; }
    public double? EvidenceLocated { get; init; }
    public double? CalculationRecall { get; init; }
    public double? AttributeAccuracy { get; init; }
    public double? IrHashMatch { get; init; }
    public double? Precision { get; init; }
    public double? Coverage { get; init; }
    public int? MaxEvidenceErrors { get; init; }
    public bool RequireAllArtifacts { get; init; }
}

public sealed record EvaluationConfig(EvaluationScope Scope, Thresholds Thresholds)
{
    public static EvaluationConfig Default { get; } = new(EvaluationScope.None, new Thresholds());

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static EvaluationConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var scope = root.TryGetProperty("scope", out var s) ? s.Deserialize<EvaluationScope>(Options)! : EvaluationScope.None;
        var thresholds = root.TryGetProperty("thresholds", out var t) ? t.Deserialize<Thresholds>(Options)! : new Thresholds();
        return new EvaluationConfig(scope, thresholds);
    }
}
