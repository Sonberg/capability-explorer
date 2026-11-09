using System.Collections.Immutable;
using System.Threading;

namespace CapabilityExplorer.Interpretation.BoundedClassification;

public sealed class ClassificationSlot<TLabel>
    where TLabel : struct, Enum
{
    private Snapshot _snapshot = new(null, null);

    public string? CurrentArtifactId => Volatile.Read(ref _snapshot).Current?.ArtifactId;

    public ClassificationReason TryActivate(
        ClassificationArtifact<TLabel> artifact,
        string? expectedCurrentId)
    {
        if (artifact is null)
        {
            return ClassificationReason.InvalidArtifact;
        }

        while (true)
        {
            var observed = Volatile.Read(ref _snapshot);
            if (!string.Equals(observed.Current?.ArtifactId, expectedCurrentId, StringComparison.Ordinal))
            {
                return ClassificationReason.UpdateConflict;
            }

            if (string.Equals(observed.Current?.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal))
            {
                return ClassificationReason.None;
            }

            var next = new Snapshot(artifact, observed.Current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, observed), observed))
            {
                return ClassificationReason.None;
            }
        }
    }

    public ClassificationReason Rollback(string expectedCurrentId)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _snapshot);
            if (!string.Equals(observed.Current?.ArtifactId, expectedCurrentId, StringComparison.Ordinal))
            {
                return ClassificationReason.UpdateConflict;
            }

            if (observed.Previous is null)
            {
                return ClassificationReason.ArtifactUnavailable;
            }

            var next = new Snapshot(observed.Previous, observed.Current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, observed), observed))
            {
                return ClassificationReason.None;
            }
        }
    }

    public ClassificationResult<TLabel> Classify(
        string schema,
        string artifactId,
        ReadOnlySpan<double> features)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Current?.Classify(schema, artifactId, features) ??
            new ClassificationResult<TLabel>(
                ClassificationOutcome.Unavailable,
                ClassificationReason.ArtifactUnavailable,
                null,
                ImmutableArray<double>.Empty,
                null);
    }

    private sealed record Snapshot(
        ClassificationArtifact<TLabel>? Current,
        ClassificationArtifact<TLabel>? Previous);
}
