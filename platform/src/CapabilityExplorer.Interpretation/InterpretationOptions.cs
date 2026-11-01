using Azure.Identity;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Configuration for the optional intelligence layer. The default provider is "none": every feature of the
/// platform works without a model, and nothing is sent anywhere.
/// </summary>
public sealed record InterpretationOptions
{
    /// <summary>none | recorded | openai (any OpenAI-compatible API, e.g. a local Ollama) | foundry.</summary>
    public string Provider { get; init; } = "none";

    /// <summary>API base, e.g. http://localhost:11434/v1 (Ollama), or a Foundry resource such as https://my-resource.services.ai.azure.com.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Chat model (Ollama model name or Foundry deployment).</summary>
    public string? Deployment { get; init; }

    /// <summary>Embedding model (similarity search); embeddings are off when unset.</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>Name of the environment variable holding an API key. If it is unset, Entra ID (DefaultAzureCredential) is used for Azure hosts.</summary>
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

    public bool UseEnvironmentApiKey { get; init; } = true;

    /// <summary>Ambient Azure identity is opt-in for saved/UI settings; configured providers retain their existing default.</summary>
    public bool UseDefaultAzureCredential { get; init; } = true;

    private string? Key => ApiKey is { Length: > 0 } ? ApiKey : UseEnvironmentApiKey && Environment.GetEnvironmentVariable(ApiKeyVariable) is { Length: > 0 } key ? key : null;

    private bool IsOpenAi => Provider.ToLowerInvariant() is "openai" or "foundry";

    /// <summary>Entra ID only when enabled, without a key, and for an Azure host.</summary>
    private DefaultAzureCredential? Credential(Uri endpoint) =>
        UseDefaultAzureCredential && Key is null && OpenAiApi.IsAzure(endpoint) ? new DefaultAzureCredential() : null;

    /// <summary>Creates the configured model, or null when the layer is disabled. Misconfiguration fails fast.</summary>
    public IInterpretationModel? CreateModel(HttpClient? http = null)
    {
        IInterpretationModel? model = Provider.ToLowerInvariant() switch
        {
            "none" or "" => null,
            "recorded" => new RecordedModel(Recordings ?? throw new InvalidOperationException("interpretation:recordings is required for the recorded provider")),
            "openai" or "foundry" when Deployment is null && EmbeddingModel is not null => null, // embeddings only
            "openai" or "foundry" => Chat(
                http,
                new Uri(Endpoint ?? throw new InvalidOperationException($"interpretation:endpoint is required for the {Provider} provider")),
                Deployment ?? throw new InvalidOperationException($"interpretation:deployment is required for the {Provider} provider")),
            _ => throw new InvalidOperationException($"unknown interpretation provider '{Provider}' (expected none, recorded, openai or foundry)"),
        };
        return model is not null && RecordTo is not null ? new RecordingModel(model, RecordTo) : model;
    }

    private FoundryChatModel Chat(HttpClient? http, Uri endpoint, string deployment) =>
        new(http ?? OpenAiApi.CreateClient(TimeSpan.FromSeconds(600)), endpoint, deployment, Key, Credential(endpoint), Provider.ToLowerInvariant());


    /// <summary>The embeddings model, or null unless an OpenAI-compatible provider has an endpoint and an embedding model.</summary>
    public OpenAiEmbeddingModel? CreateEmbeddingModel(HttpClient? http = null) =>
        IsOpenAi && Endpoint is { Length: > 0 } && EmbeddingModel is { Length: > 0 }
            ? new OpenAiEmbeddingModel(http ?? OpenAiApi.CreateClient(), new Uri(Endpoint), EmbeddingModel, Key, Credential(new Uri(Endpoint)), Provider.ToLowerInvariant())
            : null;
}
