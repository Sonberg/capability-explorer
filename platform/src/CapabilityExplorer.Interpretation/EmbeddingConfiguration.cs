using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CapabilityExplorer.Interpretation;

public sealed record EmbeddingIdentity(string Id, string Fingerprint, string RecipeVersion);

public sealed record EmbeddingConfiguration(InterpretationOptions Options, EmbeddingIdentity Identity)
{
    public const string RecipeVersion = "ce-embedding-text.v1";

    /// <summary>Resolves identity only. No model, credential or HTTP client is constructed.</summary>
    public static EmbeddingConfiguration? Resolve(InterpretationOptions configured, string settingsPath,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(configured);
        environment ??= Environment.GetEnvironmentVariable;
        try
        {
            var endpoint = environment("CE_MODEL_ENDPOINT") is { Length: > 0 } endpointValue ? endpointValue : null;
            var model = environment("CE_EMBEDDING_MODEL") is { Length: > 0 } modelValue ? modelValue : null;
            var options = endpoint is not null || model is not null
                ? new ModelSettings(endpoint, null, model, null).ApplyTo(configured)
                : configured.Enabled ? configured : ModelSettings.Load(settingsPath)?.ApplyTo(configured) ?? configured;
            var provider = options.Provider?.ToLowerInvariant();
            if (provider is not ("" or "none" or "recorded" or "openai" or "foundry"))
                throw new EmbeddingConfigurationException();
            string? canonical = null;
            if (options.Endpoint is not null)
            {
                if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri))
                    throw new EmbeddingConfigurationException();
                canonical = OpenAiApi.Url(uri, "embeddings").AbsoluteUri;
            }
            if (string.IsNullOrWhiteSpace(options.EmbeddingModel)) return null;
            if (provider is not ("openai" or "foundry") || canonical is null || options.EmbeddingModel.Length > 100
                || !options.EmbeddingModel.All(c => char.IsAsciiLetterOrDigit(c) || "._:/-".Contains(c, StringComparison.Ordinal)))
                throw new EmbeddingConfigurationException();
            var revision = environment("CE_EMBEDDING_REVISION") ?? options.EmbeddingRevision ?? "";
            if (revision.Length > 200 || revision.Any(char.IsControl)) throw new EmbeddingConfigurationException();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var value in new[] { provider, canonical, options.EmbeddingModel, revision, RecipeVersion })
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            return new(options, new($"{provider}:{options.EmbeddingModel}", Convert.ToHexStringLower(hash.GetHashAndReset()), RecipeVersion));
        }
        catch (Exception e) when (e is UriFormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            throw new EmbeddingConfigurationException();
        }
    }
}

public sealed class EmbeddingConfigurationException() : Exception("Embedding configuration is invalid; verify the model settings without including credentials in the endpoint.");
