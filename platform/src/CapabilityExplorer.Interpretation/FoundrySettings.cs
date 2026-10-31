using System.Text.Json;

namespace CapabilityExplorer.Interpretation;

/// <summary>
/// Foundry connection saved from the explorer UI, shared with <c>ce embed</c>. Lives under the git-ignored
/// .sandbox-cache and is readable by its owner only, because it may hold an API key.
/// </summary>
public sealed record FoundrySettings(string? Endpoint, string? Deployment, string? EmbeddingDeployment, string? ApiKey)
{
    public const string DefaultPath = ".sandbox-cache/foundry.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static FoundrySettings? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<FoundrySettings>(File.ReadAllText(path), Json) : null;

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
        Provider = Endpoint is { Length: > 0 } ? "foundry" : "none",
        Endpoint = Endpoint,
        Deployment = Deployment is { Length: > 0 } ? Deployment : null,
        EmbeddingDeployment = EmbeddingDeployment is { Length: > 0 } ? EmbeddingDeployment : null,
        ApiKey = ApiKey,
    };
}
