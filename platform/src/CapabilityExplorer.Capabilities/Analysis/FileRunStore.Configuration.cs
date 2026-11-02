using System.Text.Json.Nodes;

namespace CapabilityExplorer.Capabilities.Analysis;

public sealed partial class FileRunStore
{
    /// <summary>Freezes shared definitions with local capability overrides while retaining shared metadata.</summary>
    public static byte[] CaptureConfiguration(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        var sharedBytes = AnalysisPaths.ReadBounded(path);
        var shared = ParseDefinitions(sharedBytes);
        var localPath = Path.ChangeExtension(path, ".local" + Path.GetExtension(path));
        AnalysisPaths.NoLinks(localPath);
        if (!File.Exists(localPath)) return sharedBytes;
        var local = ParseDefinitions(AnalysisPaths.ReadBounded(localPath));
        var merged = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var definition in shared["capabilities"]!.AsArray())
            merged.Add(definition!["id"]!.GetValue<string>(), definition.DeepClone());
        foreach (var definition in local["capabilities"]!.AsArray())
            merged[definition!["id"]!.GetValue<string>()] = definition.DeepClone();
        if (merged.Count > 1000) throw new InvalidDataException("effective capability list exceeds bounds");
        shared["capabilities"] = new JsonArray(merged.Values.ToArray());
        var frozen = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(shared, AnalysisPaths.Json);
        if (frozen.Length > AnalysisPaths.MaxBytes) throw new InvalidDataException("effective capability configuration exceeds 1 MiB");
        return frozen;
    }

    private static JsonObject ParseDefinitions(byte[] bytes)
    {
        if (JsonNode.Parse(bytes) is not JsonObject definitions || definitions["capabilities"] is not JsonArray capabilities || capabilities.Count > 1000)
            throw new InvalidDataException("invalid capability configuration");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in capabilities)
        {
            if (definition is not JsonObject capability || capability["id"] is not JsonValue value || !value.TryGetValue<string>(out var id))
                throw new InvalidDataException("invalid capability identity");
            AnalysisPaths.Capability(id);
            if (!identities.Add(id)) throw new InvalidDataException("duplicate capability identity");
        }
        return definitions;
    }
}
