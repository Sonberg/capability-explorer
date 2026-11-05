using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

internal static class AnalysisEnrichment
{
    internal static async Task<(string Outcome,string Detail)> Run(Guid? snapshotId,
        IReadOnlyDictionary<string, string>? options = null, HttpClient? embeddingHttp = null, CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable("CE_DATABASE") is not { Length: > 0 } connection)
            return ("not-configured","PostgreSQL is not configured");
        try
        {
            if (snapshotId is null) return ("not-configured", "No confirmed PostgreSQL snapshot; core publication remains done");
            var configuration = EmbeddingHostConfiguration.Resolve(options);
            if (configuration is null)
                return ("not-configured","Embedding model is not configured");
            var model = configuration.Options.CreateEmbeddingModel(embeddingHttp)!;
            await using var database = NpgsqlDataSource.Create(connection);
            var identity = configuration.Identity;
            var count = await new SnapshotEmbeddingStore(database, connection).EmbedAsync(snapshotId.Value,
                new(identity.Id, identity.Fingerprint, identity.RecipeVersion), (texts, token) => model.EmbedAsync(texts, token), ct);
            return ("done",$"Embedded {count} occurrences for snapshot {snapshotId}; core publication remains done");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ("failed","Optional embeddings failed (" + e.GetType().Name + "); core publication remains done; run ce embed --snapshot " + snapshotId + " to retry");
        }
    }
}
