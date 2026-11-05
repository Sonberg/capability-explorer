using System.Text.RegularExpressions;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

/// <summary>The configured intelligence layer; <see cref="Explanations"/> is null when no model is configured.</summary>
public sealed record InterpretationLayer(InterpretationOptions Options, ExplanationService? Explanations, IChatModel? Chat = null)
{
    public bool Enabled => Explanations is not null;
    public GlossaryService? Glossary { get; init; }
}

/// <summary>
/// The model-backed features as currently configured. Model settings saved from the UI replace the layer
/// at runtime; handlers resolve <see cref="Layer"/> per request, so a save applies without a restart.
/// </summary>
public sealed partial class Intelligence
{
    private readonly InterpretationOptions _configured;
    private volatile State _state;

    private sealed record State(InterpretationLayer Layer, OpenAiEmbeddingModel? Embeddings, ModelSettings? Saved);

    /// <param name="configured">From configuration; used while nothing is saved, or when it names a provider explicitly.</param>
    /// <param name="settingsPath">Where the UI saves model settings (shared with <c>ce embed</c> and <c>make ollama</c>).</param>
    /// <param name="store">Similarity search; null without a database (CE_DATABASE).</param>
    public Intelligence(InterpretationOptions configured, string settingsPath, EmbeddingStore? store)
    {
        _configured = configured;
        SettingsPath = settingsPath;
        Store = store;
        try
        {
            _state = Build(configured.Enabled ? null : ModelSettings.Load(settingsPath));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or UriFormatException)
        {
            // A broken settings file must not stop the explorer: start without it; saving from the UI overwrites it.
            Problem = $"{settingsPath}: {e.Message}";
            _state = Build(null);
        }
    }

    /// <summary>Why the saved settings were ignored at startup, if they were.</summary>
    public string? Problem { get; private set; }

    public string SettingsPath { get; }

    public EmbeddingStore? Store { get; }

    /// <summary>The database behind <see cref="Store"/>, for importing artifacts (Rebuild intelligence).</summary>
    public Npgsql.NpgsqlDataSource? DataSource { get; init; }

    public InterpretationLayer Layer => _state.Layer;

    public OpenAiEmbeddingModel? Embeddings => _state.Embeddings;

    public ModelSettings? Saved => _state.Saved;

    public EmbeddingConfiguration? ResolveEmbeddingConfiguration() =>
        EmbeddingConfiguration.Resolve(_configured, SettingsPath);

    /// <summary>Validates, saves and applies. A blank API key keeps the saved one.</summary>
    public string? Save(ModelSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = request.Endpoint?.Trim();
        if (endpoint is { Length: > 0 } && !(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && IsAllowed(uri) && uri.Query.Length == 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0))
        {
            return "endpoint must be an https URL (http only for a local server such as localhost, host.docker.internal or a container name), without a query";
        }

        foreach (var (name, value) in new[] { ("chat model", request.ChatModel), ("embedding model", request.EmbeddingModel) })
        {
            if (value is { Length: > 0 } && !ModelName().IsMatch(value.Trim()))
            {
                return $"{name} may contain letters, digits, '.', '_', ':', '/' and '-' only";
            }
        }

        if (endpoint is { Length: > 0 } && string.IsNullOrWhiteSpace(request.ChatModel) && string.IsNullOrWhiteSpace(request.EmbeddingModel))
        {
            return "set a chat model, an embedding model, or both";
        }

        if (Saved?.ApiKey is { Length: > 0 } && !request.ClearApiKey && string.IsNullOrEmpty(request.ApiKey)
            && !SameAuthority(Saved.Endpoint, endpoint))
            return "Changing the endpoint authority requires an explicit new API key or clearing the saved key";
        var apiKey = request.ClearApiKey ? null : request.ApiKey is { Length: > 0 } key ? key.Trim() : Saved?.ApiKey;
        var settings = new ModelSettings(endpoint is { Length: > 0 } ? endpoint : null, request.ChatModel?.Trim(), request.EmbeddingModel?.Trim(), apiKey);
        State state;
        try
        {
            state = Build(settings); // before anything is written: settings that cannot build a model are not saved
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }

        settings.Save(SettingsPath);
        _state = state;
        Problem = null;
        return null;
    }

    /// <summary>https anywhere; plain http only to a local or same-network server (an API key must not cross the internet unencrypted).</summary>
    private static bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host == "host.docker.internal" || !uri.Host.Contains('.', StringComparison.Ordinal)));

    private static bool SameAuthority(string? oldEndpoint, string? newEndpoint) =>
        Uri.TryCreate(oldEndpoint, UriKind.Absolute, out var before) && Uri.TryCreate(newEndpoint, UriKind.Absolute, out var after)
        && string.Equals(before.GetLeftPart(UriPartial.Authority), after.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private State Build(ModelSettings? saved)
    {
        var options = saved?.ApplyTo(_configured) ?? _configured;
        var model = options.CreateModel();
        var explanations = model is not null ? new ExplanationService(model, options.CacheDirectory) : null;
        return new State(new InterpretationLayer(options, explanations, model as IChatModel)
        {
            Glossary = model is null ? null : new GlossaryService(model, options.CacheDirectory),
        }, options.CreateEmbeddingModel(), saved);
    }

    // Ollama names look like "qwen2.5:7b" or "library/nomic-embed-text"; Foundry deployments are plainer.
    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,100}$")]
    private static partial Regex ModelName();
}

public sealed record ModelSettingsRequest(string? Endpoint, string? ChatModel, string? EmbeddingModel, string? ApiKey, bool ClearApiKey = false);

public sealed record SimilarHit(string Key, string Name, string Kind, string RepositoryId, string Unit, bool HasCalculation, double Score);

public sealed record CalculationRef(string Key, string Name, string RepositoryId, string CommitSha, string Language, string OutputType);

public sealed record ComparisonDto(
    CalculationRef Left,
    CalculationRef Right,
    bool SameExpression,
    IReadOnlyList<Difference> Differences,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> UnknownOnBoth);

public sealed record ExplainRequest(string A, string B, string? View, string? Cap = null);

public static class Comparisons
{
    public static CalculationComparison? Compare(ViewIndex view, string a, string b)
    {
        CalculationSide? Side(string key)
        {
            if (!view.Calculations.TryGetValue(key, out var c))
            {
                return null;
            }

            var name = view.Node(key)?.Record.Name ?? Map.DisplayName(key);
            return new CalculationSide(key, name, c.Artifact.RepositoryId, c.Artifact.CommitSha, c.Artifact.Artifact.Header.Language, c.Record.Raw);
        }

        return Side(a) is { } left && Side(b) is { } right ? CalculationComparer.Compare(left, right) : null;
    }

    public static ComparisonDto ToDto(CalculationComparison c)
    {
        static CalculationRef Ref(CalculationSide s, string type) => new(s.Key, s.Name, s.RepositoryId, s.CommitSha, s.Language, type);
        return new ComparisonDto(Ref(c.Left, c.LeftOutputType), Ref(c.Right, c.RightOutputType), c.SameExpression, c.Differences, c.Agreements, c.UnknownOnBoth);
    }
}
