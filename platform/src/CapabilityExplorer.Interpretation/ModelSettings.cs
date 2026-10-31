using System.Text.Json;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Model connection saved from the explorer's Settings page, shared with <c>ce embed</c>. Written by <c>make ollama</c>
/// for a local Ollama. Lives under the git-ignored .sandbox-cache, readable by its owner only (it may hold an API key).
/// </summary>
public sealed record ModelSettings(string? Endpoint, string? ChatModel, string? EmbeddingModel, string? ApiKey)
{
    public const string DefaultPath = ".sandbox-cache/model.json";

    /// <summary>Ollama on the host, as seen from the sandbox container (or from the host itself via localhost).</summary>
    public const string OllamaEndpoint = "http://host.docker.internal:11434/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ModelSettings? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ModelSettings>(File.ReadAllText(path), Json) : null;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = OperatingSystem.IsWindows()
                   ? new FileStream(temp, FileMode.Create)
                   : new FileStream(temp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        {
            JsonSerializer.Serialize(stream, this, Json);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Settings win over the configured provider; caches and recordings are kept.</summary>
    public InterpretationOptions ApplyTo(InterpretationOptions options) => options with
    {
        Provider = Endpoint is not { Length: > 0 } ? "none" : OpenAiApi.IsAzure(new Uri(Endpoint)) ? "foundry" : "openai",
        Endpoint = Endpoint,
        Deployment = ChatModel is { Length: > 0 } ? ChatModel : null,
        EmbeddingModel = EmbeddingModel is { Length: > 0 } ? EmbeddingModel : null,
        ApiKey = ApiKey,
    };
}
