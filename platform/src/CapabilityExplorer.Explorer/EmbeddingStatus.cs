using System.Text.Json.Serialization;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

public sealed record EmbeddingStatus(
    Guid SnapshotId, long Generation, string Capability, IReadOnlyDictionary<string, string> Selection, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SnapshotEmbeddingModel? ConfiguredModel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SnapshotEmbeddingModel? CoverageModel,
    string CoverageScope, bool SimilarityAvailable, long SnapshotArtifactCount, long SelectedArtifactCount, long CoveredArtifactCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SnapshotArtifactsSha256,
    bool CountsExact,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] EmbeddingCounts? Counts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? LastSuccessfulCompletion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReasonCode)
{
    internal static async Task<EmbeddingStatus> Read(ExplorerQueryContext context, Intelligence intelligence,
        Npgsql.NpgsqlDataSource dataSource, bool similarityAvailable, CancellationToken ct)
    {
        var resolved = intelligence.ResolveEmbeddingConfiguration();
        var model = resolved is null ? null : new SnapshotEmbeddingModel(resolved.Identity.Id, resolved.Identity.Fingerprint, resolved.Identity.RecipeVersion);
        var coverage = await new SnapshotEmbeddingStore(dataSource).StatusAsync(context.SnapshotId, context.Selection, model, ct);
        if (context.Generation is < 0 or > 9007199254740991) throw new InvalidOperationException("Generation exceeds safe numeric budget.");
        return new(context.SnapshotId, context.Generation, context.Capability, context.Selection, coverage.State, model, coverage.Model,
            "selected-view", similarityAvailable, coverage.SnapshotArtifactCount, coverage.SelectedArtifactCount, coverage.CoveredArtifactCount,
            coverage.SnapshotArtifactsSha256, coverage.CountsExact, coverage.Counts, coverage.LastSuccessfulCompletion, coverage.ReasonCode);
    }
}
