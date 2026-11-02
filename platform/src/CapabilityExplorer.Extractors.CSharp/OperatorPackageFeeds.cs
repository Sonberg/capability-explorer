using System.Text.Json;

namespace CapabilityExplorer.Extractors.CSharp;

public sealed record OperatorPackageFeed(string Id, string Kind, string Name, string Url, string? Scope, string? Username, string? Secret);

public sealed record OperatorPackageFeedSettings(IReadOnlyList<OperatorPackageFeed> Feeds)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static OperatorPackageFeedSettings Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new([]);
        var settings = JsonSerializer.Deserialize<OperatorPackageFeedSettings>(json, Json) ?? new([]);
        if (settings.Feeds.Count > 32) throw new InvalidDataException("At most 32 package feeds can be configured");
        foreach (var feed in settings.Feeds)
        {
            if (feed.Kind is not ("nuget" or "npm") || string.IsNullOrWhiteSpace(feed.Name)
                || !Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidDataException("Invalid package feed settings");
            if (feed.Secret?.Any(c => c is '\r' or '\n' or '\0') == true)
                throw new InvalidDataException("Package feed credentials contain invalid characters");
            if (feed.Kind == "nuget" && !string.IsNullOrEmpty(feed.Secret) && string.IsNullOrWhiteSpace(feed.Username))
                throw new InvalidDataException("NuGet feed credentials require a username");
        }
        return settings;
    }

    public IReadOnlyList<PackageFeed> NuGetFeeds() => Feeds
        .Where(feed => feed.Kind == "nuget")
        .Select(feed => new PackageFeed(feed.Name, feed.Url,
            string.IsNullOrEmpty(feed.Secret) ? null : new FeedCredentials(feed.Username!, feed.Secret)))
        .ToList();
}