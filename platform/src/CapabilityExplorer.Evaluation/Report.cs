using System.Globalization;

namespace CapabilityExplorer.Evaluation;

public readonly record struct Ratio(int Hit, int Total)
{
    /// <summary>An empty category counts as satisfied.</summary>
    public double Value => Total == 0 ? 1.0 : (double)Hit / Total;

    public static Ratio operator +(Ratio a, Ratio b) => new(a.Hit + b.Hit, a.Total + b.Total);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Hit}/{Total} ({Value * 100:0.0}%)");
}

public sealed record Metrics
{
    /// <summary>Golden nodes found (same symbolKey and nodeKind).</summary>
    public Ratio NodeRecall { get; init; }

    /// <summary>Golden edges found (same from, relation, to — or same from/relation/reason when the target is unknown).</summary>
    public Ratio EdgeRecall { get; init; }

    /// <summary>Golden edges found with matching resolution, unresolvedReason and derivation.</summary>
    public Ratio EdgeAccuracy { get; init; }

    /// <summary>Matched nodes and edges whose evidence overlaps the golden evidence (same path, overlapping lines).</summary>
    public Ratio EvidenceLocated { get; init; }

    public Ratio CalculationRecall { get; init; }

    /// <summary>Golden calculation attributes reproduced: same status and, where known, same value.</summary>
    public Ratio AttributeAccuracy { get; init; }

    /// <summary>Matched calculations with the same normalised-IR hash.</summary>
    public Ratio IrHashMatch { get; init; }

    /// <summary>Actual facts in exhaustive categories that are in the golden set.</summary>
    public Ratio Precision { get; init; }

    /// <summary>Artifacts whose completeness status, incomplete paths and coverage diagnostics match.</summary>
    public Ratio Coverage { get; init; }

    public int EvidenceErrors { get; init; }

    public static Metrics operator +(Metrics a, Metrics b) => new()
    {
        NodeRecall = a.NodeRecall + b.NodeRecall,
        EdgeRecall = a.EdgeRecall + b.EdgeRecall,
        EdgeAccuracy = a.EdgeAccuracy + b.EdgeAccuracy,
        EvidenceLocated = a.EvidenceLocated + b.EvidenceLocated,
        CalculationRecall = a.CalculationRecall + b.CalculationRecall,
        AttributeAccuracy = a.AttributeAccuracy + b.AttributeAccuracy,
        IrHashMatch = a.IrHashMatch + b.IrHashMatch,
        Precision = a.Precision + b.Precision,
        Coverage = a.Coverage + b.Coverage,
        EvidenceErrors = a.EvidenceErrors + b.EvidenceErrors,
    };

    public static Metrics Empty { get; } = new();
}

public enum FindingKind
{
    MissingNode,
    NodeKindMismatch,
    MissingEdge,
    ResolutionMismatch,
    UnresolvedReasonMismatch,
    DerivationMismatch,
    EvidenceNotLocated,
    MissingCalculation,
    AttributeMissed,
    AttributeWrong,
    AttributeFabricated,
    IrHashMismatch,
    UnexpectedFact,
    CoverageMismatch,
    MissingDiagnostic,
    EvidenceError,
}

public sealed record Finding(FindingKind Kind, string Subject, string Detail)
{
    public override string ToString() => $"{Kind,-26} {Subject}{(Detail.Length > 0 ? $"  — {Detail}" : "")}";
}

public enum PairingStatus
{
    Evaluated,
    MissingActual,
    InvalidActual,
}

public sealed record ArtifactEvaluation(
    string Label,
    string RepositoryId,
    string CommitSha,
    string UnitPath,
    PairingStatus Status,
    string? ActualExtractor,
    Metrics Metrics,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Problems);

public sealed record EvaluationReport(
    IReadOnlyList<ArtifactEvaluation> Artifacts,
    IReadOnlyList<string> UnpairedActual,
    IReadOnlyList<string> InvalidUnpairedActual,
    Metrics Totals,
    IReadOnlyList<string> ThresholdFailures)
{
    public bool Passed => ThresholdFailures.Count == 0;
}
