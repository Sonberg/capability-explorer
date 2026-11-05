using System.Text.Json;
using System.Text.Json.Nodes;

namespace CapabilityExplorer.Interpretation;

/// <summary>Shared, lazy host configuration for projection readers and writers. Never constructs a provider.</summary>
public static class EmbeddingHostConfiguration
{
    private static readonly string[] TextOptions =
    [
        "provider", "endpoint", "deployment", "embeddingModel", "embeddingRevision", "apiKeyVariable", "apiKey",
        "recordings", "recordTo", "cacheDirectory",
    ];
    private static readonly string[] BooleanOptions = ["useEnvironmentApiKey", "useDefaultAzureCredential"];
    private static readonly string[] SourceOptions = ["settings", "modelSettings", "model-config"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsOption(string key) => SourceOptions.Contains(key, StringComparer.OrdinalIgnoreCase)
        || TextOptions.Concat(BooleanOptions).Any(name => string.Equals(key, "interpretation:" + name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, string> CommandOptions(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal)) continue;
            var argument = arguments[index][2..];
            var equal = argument.IndexOf('=', StringComparison.Ordinal);
            var key = equal >= 0 ? argument[..equal] : argument;
            if (!IsOption(key)) continue;
            if (equal >= 0) options[key] = argument[(equal + 1)..];
            else if (index + 1 < arguments.Count) options[key] = arguments[++index];
        }
        return options;
    }

    /// <summary>
    /// Uses appsettings[.environment].json, interpretation__* and modelSettings environment values in every host.
    /// --model-config / CE_MODEL_CONFIG supplies a shared JSON file with interpretation and modelSettings sections.
    /// Explicit command options override that file. CE_MODEL_ENDPOINT / CE_EMBEDDING_MODEL retain final precedence.
    /// </summary>
    public static EmbeddingConfiguration? Resolve(IReadOnlyDictionary<string, string>? options = null,
        Func<string, string?>? configuration = null, Func<string, string?>? environment = null, string? contentRoot = null)
    {
        if (environment is null)
        {
            var current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
                current[(string)pair.Key] = pair.Value?.ToString();
            environment = current.GetValueOrDefault;
        }
        contentRoot ??= Environment.CurrentDirectory;
        try
        {
            if (Option(options, "settings") is not null && Option(options, "modelSettings") is not null)
                throw new EmbeddingConfigurationException();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (configuration is null)
            {
                Load(Path.Combine(contentRoot, "appsettings.json"), values, required: false);
                var name = environment("DOTNET_ENVIRONMENT") ?? environment("ASPNETCORE_ENVIRONMENT");
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.Length > 100 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                        throw new EmbeddingConfigurationException();
                    Load(Path.Combine(contentRoot, "appsettings." + name + ".json"), values, required: false);
                }
            }
            foreach (var key in Keys())
            {
                var value = configuration is not null ? configuration(key)
                    : environment(key.Replace(":", "__", StringComparison.Ordinal));
                if (value is not null) values[key] = value;
            }
            var shared = Option(options, "model-config") ?? values.GetValueOrDefault("model-config") ?? environment("CE_MODEL_CONFIG");
            if (shared is { Length: > 0 }) Load(Path.GetFullPath(shared, contentRoot), values, required: true);
            if (options is not null)
                foreach (var pair in options.Where(p => IsOption(p.Key)))
                    values[string.Equals(pair.Key, "settings", StringComparison.OrdinalIgnoreCase) ? "modelSettings" : pair.Key] = pair.Value;
            if (environment("CE_MODEL_SETTINGS") is { Length: > 0 } sharedSettings
                && Option(options, "settings") is null && Option(options, "modelSettings") is null)
                values["modelSettings"] = sharedSettings;
            var json = new JsonObject();
            foreach (var name in TextOptions)
                if (values.TryGetValue("interpretation:" + name, out var text)) json[name] = text;
            foreach (var name in BooleanOptions)
                if (values.TryGetValue("interpretation:" + name, out var text))
                    json[name] = bool.TryParse(text, out var flag) ? flag : throw new EmbeddingConfigurationException();
            var configured = json.Deserialize<InterpretationOptions>(Json)!;
            var settingsPath = values.GetValueOrDefault("modelSettings") ?? ModelSettings.DefaultPath;
            return EmbeddingConfiguration.Resolve(configured, Path.GetFullPath(settingsPath, contentRoot), environment);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            throw new EmbeddingConfigurationException();
        }
    }

    private static IEnumerable<string> Keys() => TextOptions.Concat(BooleanOptions).Select(name => "interpretation:" + name)
        .Append("modelSettings").Append("model-config");

    private static string? Option(IReadOnlyDictionary<string, string>? options, string key) =>
        options?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Load(string path, Dictionary<string, string> values, bool required)
    {
        if (!File.Exists(path))
        {
            if (required) throw new EmbeddingConfigurationException();
            return;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new EmbeddingConfigurationException();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (string.Equals(property.Name, "modelSettings", StringComparison.OrdinalIgnoreCase))
                values["modelSettings"] = Path.GetFullPath(property.Value.GetString() ?? "", Path.GetDirectoryName(path)!);
            if (string.Equals(property.Name, "model-config", StringComparison.OrdinalIgnoreCase))
                values["model-config"] = Path.GetFullPath(property.Value.GetString() ?? "", Path.GetDirectoryName(path)!);
            if (!string.Equals(property.Name, "interpretation", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var setting in property.Value.EnumerateObject())
                if (IsOption("interpretation:" + setting.Name))
                    values["interpretation:" + setting.Name] = setting.Value.ValueKind == JsonValueKind.String
                        ? setting.Value.GetString()! : setting.Value.ToString();
        }
    }
}
