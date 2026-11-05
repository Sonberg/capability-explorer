using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Npgsql;

namespace CapabilityExplorer.Cli;

internal static class EmbeddingCommand
{
    internal static async Task<int> Run(IReadOnlyDictionary<string, string> options, IReadOnlyList<string> files,
        HttpClient? embeddingHttp = null, CancellationToken ct = default)
    {
        try
        {
            if (files.Count > 0 || options.Keys.Any(key => key is not ("snapshot" or "scope" or "connection")
                    && !EmbeddingHostConfiguration.IsOption(key))
                || options.ContainsKey("snapshot") && options.ContainsKey("scope")
                || options.ContainsKey("settings") && options.ContainsKey("modelSettings"))
                throw new ArgumentException("invalid embed options");
            var configuration = EmbeddingHostConfiguration.Resolve(options);
            if (configuration is null)
            {
                Console.Error.WriteLine("no embedding model: configure shared appsettings, modelSettings, or CE_MODEL_ENDPOINT and CE_EMBEDDING_MODEL");
                return 2;
            }
            var model = configuration.Options.CreateEmbeddingModel(embeddingHttp)!;
            var connection = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("CE_DATABASE")
                ?? throw new InvalidOperationException("PostgreSQL connection is required");
            await using var db = NpgsqlDataSource.Create(connection);
            var store = new SnapshotEmbeddingStore(db, connection);
            var snapshot = options.TryGetValue("snapshot", out var pinned)
                ? Guid.TryParse(pinned, out var id) ? id : throw new ArgumentException("invalid snapshot")
                : await store.ResolvePublishedAsync(options.GetValueOrDefault("scope") ?? "*", ct);
            var identity = configuration.Identity;
            var count = await store.EmbedAsync(snapshot, new(identity.Id, identity.Fingerprint, identity.RecipeVersion),
                (texts, token) => model.EmbedAsync(texts, token), ct);
            Console.WriteLine($"Embedding projection ready for snapshot {snapshot}: embedded {count} occurrence(s) ({identity.Id}); PostgreSQL similarity search remains unavailable.");
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception e) when (e is EmbeddingConfigurationException or EmbeddingProjectionBusyException or EmbeddingProjectionFailedException
            or SnapshotConflictException or InterpretationUnavailableException or NpgsqlException or InvalidOperationException or ArgumentException or IOException)
        {
            Console.Error.WriteLine($"embed: failed ({e.GetType().Name}); verify configuration and retry the same snapshot");
            return 1;
        }
    }
}
