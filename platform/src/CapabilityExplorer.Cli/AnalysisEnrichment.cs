using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

internal static class AnalysisEnrichment
{
    internal static async Task<(string Outcome,string Detail)> Run()
    {
        if (Environment.GetEnvironmentVariable("CE_DATABASE") is not { Length: > 0 } connection)
            return ("not-configured","PostgreSQL is not configured");
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("CE_MODEL_ENDPOINT");
            var settings = endpoint is { Length: > 0 }
                ? new ModelSettings(endpoint,null,Environment.GetEnvironmentVariable("CE_EMBEDDING_MODEL"),null)
                : ModelSettings.Load(ModelSettings.DefaultPath);
            if (settings?.ApplyTo(new InterpretationOptions()).CreateEmbeddingModel() is not { } model)
                return ("not-configured","Embedding model is not configured");
            await using var database = NpgsqlDataSource.Create(connection);
            var count = await new EmbeddingStore(database).EmbedPendingAsync(model.Id,(texts,ct) => model.EmbedAsync(texts,ct));
            return ("done",$"Embedded {count} symbols; core publication remains done");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ("failed","Optional embeddings failed (" + e.GetType().Name + "); core publication remains done; run ce embed to retry");
        }
    }
}
