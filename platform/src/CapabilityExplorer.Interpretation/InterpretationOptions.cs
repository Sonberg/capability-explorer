using Azure.Identity;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Configuration for the optional intelligence layer. The default provider is "none": every feature of the
/// platform works without a model, and nothing is sent anywhere.
/// </summary>
public sealed record InterpretationOptions
{
    /// <summary>none | recorded | foundry.</summary>
    public string Provider { get; init; } = "none";

    /// <summary>Foundry resource endpoint, e.g. https://my-resource.services.ai.azure.com.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Foundry deployment name.</summary>
    public string? Deployment { get; init; }

    /// <summary>Foundry embeddings deployment (similarity search); embeddings are off when unset.</summary>
    public string? EmbeddingDeployment { get; init; }

    /// <summary>Name of the environment variable holding an API key. If it is unset, Entra ID (DefaultAzureCredential) is used.</summary>
    public string ApiKeyVariable { get; init; } = "CE_FOUNDRY_API_KEY";

    /// <summary>An API key given directly (saved settings); wins over <see cref="ApiKeyVariable"/>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Directory of recorded responses (provider "recorded").</summary>
    public string? Recordings { get; init; }

    /// <summary>When set with a live provider, responses are also written here as recordings.</summary>
    public string? RecordTo { get; init; }

    /// <summary>Directory for accepted proposals keyed by input hash; in-memory only when unset.</summary>
    public string? CacheDirectory { get; init; }

    public bool Enabled => !string.Equals(Provider, "none", StringComparison.OrdinalIgnoreCase);

    private string? Key => ApiKey is { Length: > 0 } ? ApiKey : Environment.GetEnvironmentVariable(ApiKeyVariable) is { Length: > 0 } key ? key : null;

    /// <summary>Creates the configured model, or null when the layer is disabled. Misconfiguration fails fast.</summary>
    public IInterpretationModel? CreateModel(HttpClient? http = null)
    {
        IInterpretationModel? model = Provider.ToLowerInvariant() switch
        {
            "none" or "" => null,
            "recorded" => new RecordedModel(Recordings ?? throw new InvalidOperationException("interpretation:recordings is required for the recorded provider")),
            "foundry" when Deployment is null && EmbeddingDeployment is not null => null, // embeddings only
            "foundry" => new FoundryChatModel(
                http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) },
                new Uri(Endpoint ?? throw new InvalidOperationException("interpretation:endpoint is required for the foundry provider")),
                Deployment ?? throw new InvalidOperationException("interpretation:deployment is required for the foundry provider"),
                Key,
                Key is null ? new DefaultAzureCredential() : null),
            _ => throw new InvalidOperationException($"unknown interpretation provider '{Provider}' (expected none, recorded or foundry)"),
        };
        return model is not null && RecordTo is not null ? new RecordingModel(model, RecordTo) : model;
    }

    /// <summary>The embeddings model, or null unless the foundry provider has an endpoint and an embedding deployment.</summary>
    public FoundryEmbeddingModel? CreateEmbeddingModel(HttpClient? http = null) =>
        string.Equals(Provider, "foundry", StringComparison.OrdinalIgnoreCase) && Endpoint is { Length: > 0 } && EmbeddingDeployment is { Length: > 0 }
            ? new FoundryEmbeddingModel(http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) }, new Uri(Endpoint), EmbeddingDeployment, Key, Key is null ? new DefaultAzureCredential() : null)
            : null;
}
