using System.Text.RegularExpressions;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;

namespace CapabilityExplorer.Explorer;

/// <summary>The configured intelligence layer; <see cref="Explanations"/> is null when no model is configured.</summary>
public sealed record InterpretationLayer(InterpretationOptions Options, ExplanationService? Explanations)
{
    public bool Enabled => Explanations is not null;
}

/// <summary>
/// The model-backed features as currently configured. Foundry settings saved from the UI replace the layer
/// at runtime; handlers resolve <see cref="Layer"/> per request, so a save applies without a restart.
/// </summary>
public sealed partial class Intelligence
{
    private readonly InterpretationOptions _configured;
    private volatile State _state;

    private sealed record State(InterpretationLayer Layer, FoundryEmbeddingModel? Embeddings, FoundrySettings? Saved);

    /// <param name="configured">From configuration; used while nothing is saved, or when it names a provider explicitly.</param>
    /// <param name="settingsPath">Where the UI saves Foundry settings (shared with <c>ce embed</c>).</param>
    /// <param name="store">Similarity search; null without a database (CE_DATABASE).</param>
    public Intelligence(InterpretationOptions configured, string settingsPath, EmbeddingStore? store)
    {
        _configured = configured;
        SettingsPath = settingsPath;
        Store = store;
        try
        {
            _state = Build(configured.Enabled ? null : FoundrySettings.Load(settingsPath));
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

    public InterpretationLayer Layer => _state.Layer;

    public FoundryEmbeddingModel? Embeddings => _state.Embeddings;

    public FoundrySettings? Saved => _state.Saved;

    /// <summary>Validates, saves and applies. A blank API key keeps the saved one; Entra ID is used when none is saved.</summary>
    public string? Save(FoundrySettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = request.Endpoint?.Trim();
        if (endpoint is { Length: > 0 } && !(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0))
        {
            return "endpoint must be an https URL without a query, e.g. https://my-resource.services.ai.azure.com";
        }

        foreach (var (name, value) in new[] { ("deployment", request.Deployment), ("embedding deployment", request.EmbeddingDeployment) })
        {
            if (value is { Length: > 0 } && !DeploymentName().IsMatch(value.Trim()))
            {
                return $"{name} may contain letters, digits, '.', '_' and '-' only";
            }
        }

        if (endpoint is { Length: > 0 } && string.IsNullOrWhiteSpace(request.Deployment) && string.IsNullOrWhiteSpace(request.EmbeddingDeployment))
        {
            return "set a chat deployment, an embedding deployment, or both";
        }

        var apiKey = request.ClearApiKey ? null : request.ApiKey is { Length: > 0 } key ? key.Trim() : Saved?.ApiKey;
        var settings = new FoundrySettings(endpoint is { Length: > 0 } ? endpoint : null, request.Deployment?.Trim(), request.EmbeddingDeployment?.Trim(), apiKey);
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

    private State Build(FoundrySettings? saved)
    {
        var options = saved?.ApplyTo(_configured) ?? _configured;
        var explanations = options.CreateModel() is { } model ? new ExplanationService(model, options.CacheDirectory) : null;
        return new State(new InterpretationLayer(options, explanations), options.CreateEmbeddingModel(), saved);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex DeploymentName();
}

public sealed record FoundrySettingsRequest(string? Endpoint, string? Deployment, string? EmbeddingDeployment, string? ApiKey, bool ClearApiKey = false);

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
