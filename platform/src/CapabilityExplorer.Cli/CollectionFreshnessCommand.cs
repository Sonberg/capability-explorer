using System.Globalization;
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
        var temporal = options.ContainsKey("as-of") || options.ContainsKey("policy");
        if (options.Count != (temporal ? 4 : 2) ||
            !options.TryGetValue("previous", out var previousPath) ||
            !options.TryGetValue("current", out var currentPath) ||
            temporal && (!options.ContainsKey("as-of") || !options.ContainsKey("policy")))
        {
            Console.Error.WriteLine("usage: ce collection-freshness --previous <input-vector.json> --current <input-vector.json> [--as-of <ISO-8601-offset-time> --policy <observation-policy.json>]");
            return 2;
        }
        try
        {
            var previous = await ReadAsync<CollectionInputVector>(previousPath);
            var current = await ReadAsync<CollectionInputVector>(currentPath);
            if (temporal)
            {
                string[] formats =
                [
                    "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                    "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
                ];
                if (!DateTimeOffset.TryParseExact(options["as-of"], formats, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var asOf))
                    throw new ArgumentException("--as-of requires an ISO-8601 timestamp with an explicit UTC or offset");
                var policy = await ReadAsync<ObservationPolicy>(options["policy"]);
                var maxAge = policy.MaxAgeSeconds is { } seconds
                    ? TimeSpan.FromTicks(checked(seconds * TimeSpan.TicksPerSecond)) : (TimeSpan?)null;
                var context = new CollectionObservationContext(asOf, policy.ObservedAt, policy.CollectedAt,
                    policy.AttemptId, policy.SourceEffectiveAt, policy.PublishedAt,
                    policy.PolicyId, policy.PolicySha256, maxAge, policy.LastSuccessful);
                var observation = CollectionFreshness.Evaluate(previous, current, context);
                Console.WriteLine(JsonSerializer.Serialize(observation, Json));
                return observation.Status == "current" ? 0 : 1;
            }
            var result = CollectionFreshness.Evaluate(previous, current);
            Console.WriteLine(JsonSerializer.Serialize(result, Json));
            return result.Status == "current" ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                      ArgumentException or OverflowException)
        {
            Console.Error.WriteLine($"collection-freshness: {error.Message}");
            return 1;
        }
    }

    private static async Task<T> ReadAsync<T>(string path)
    {
        var input = new FileInfo(Path.GetFullPath(path));
        if (input.Length > 1024 * 1024) throw new IOException("Input vector exceeds 1 MiB");
        await using var stream = input.OpenRead();
        var bytes = new byte[1024 * 1024 + 1];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count == bytes.Length) throw new IOException("Freshness input exceeds 1 MiB");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, count), Json)
            ?? throw new JsonException("Freshness input cannot be null");
    }

    private sealed record ObservationPolicy(
        DateTimeOffset ObservedAt, DateTimeOffset CollectedAt, string AttemptId,
        DateTimeOffset? SourceEffectiveAt = null, DateTimeOffset? PublishedAt = null,
        string? PolicyId = null, string? PolicySha256 = null, long? MaxAgeSeconds = null,
        LastSuccessfulReceipt? LastSuccessful = null);
}
