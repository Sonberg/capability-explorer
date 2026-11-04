using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Cli;

internal static class CollectionFreshnessCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options)
    {
        if (options.Count != 2 || !options.TryGetValue("previous", out var previousPath) ||
            !options.TryGetValue("current", out var currentPath))
        {
            Console.Error.WriteLine("usage: ce collection-freshness --previous <input-vector.json> --current <input-vector.json>");
            return 2;
        }
        try
        {
            var result = CollectionFreshness.Evaluate(await ReadAsync(previousPath), await ReadAsync(currentPath));
            Console.WriteLine(JsonSerializer.Serialize(result, Json));
            return result.Status == "current" ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"collection-freshness: {error.Message}");
            return 1;
        }
    }

    private static async Task<CollectionInputVector> ReadAsync(string path)
    {
        var input = new FileInfo(Path.GetFullPath(path));
        if (input.Length > 1024 * 1024) throw new IOException("Input vector exceeds 1 MiB");
        await using var stream = input.OpenRead();
        return await JsonSerializer.DeserializeAsync<CollectionInputVector>(stream, Json)
            ?? throw new JsonException("Input vector cannot be null");
    }
}
