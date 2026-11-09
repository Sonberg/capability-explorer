using System.Collections.Immutable;

namespace CapabilityExplorer.Interpretation.BoundedClassification;

public enum ClassificationOutcome
{
    Decision,
    Abstain,
    ValidationFailure,
    Unavailable,
}

public enum ClassificationReason
{
    None,
    UnsupportedSchema,
    InvalidInput,
    ShapeMismatch,
    PayloadTooLarge,
    IdentityMismatch,
    NonFinite,
    OutOfDistribution,
    LowScore,
    LowMargin,
    NumericalFailure,
    ArtifactUnavailable,
    InvalidArtifact,
    UpdateConflict,
}

public sealed record ClassificationResult<TLabel>(
    ClassificationOutcome Outcome,
    ClassificationReason Reason,
    TLabel? SelectedClass,
    ImmutableArray<double> Scores,
    string? ArtifactId)
    where TLabel : struct, Enum
{
    public string Schema => "ce.classification-result.v1";
    public string ScoreKind => "normalized-score";
}

public sealed record ClassificationArtifactLoad<TLabel>(
    ClassificationArtifact<TLabel>? Artifact,
    ClassificationReason Reason)
    where TLabel : struct, Enum;
