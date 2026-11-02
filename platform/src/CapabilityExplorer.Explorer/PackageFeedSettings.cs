using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CapabilityExplorer.Capabilities;

namespace CapabilityExplorer.Explorer;

public sealed record PackageFeedSettingsStatus(string SettingsPath, IReadOnlyList<PackageFeedDescriptor> Feeds, string? Problem);

/// <summary>Reads only the host's bounded, public catalogue. Never opens legacy credential settings.</summary>
public sealed class PackageFeedSettingsStore(string catalogPath)
{
    public const string DefaultPath = ".sandbox-cache/package-feed-catalog/catalog.json";
    private const int MaxBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
    private static readonly Regex SafeId = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeScope = new("^@[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    public PackageFeedSettingsStatus Status()
    {
        try
        {
            var path = Path.GetFullPath(catalogPath);
            if (Path.GetFileName(path).Equals("settings.json", StringComparison.OrdinalIgnoreCase)
                || path.Split(Path.DirectorySeparatorChar).Contains("package-feeds", StringComparer.OrdinalIgnoreCase))
                return Problem("Legacy package feed settings are host-only. Configure the public catalogue instead.");
            for (var entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
            {
                if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    return Problem("Package feed catalogue must not use symbolic links.");
            }
            if (!File.Exists(path))
                return Problem("Public package feed catalogue is missing. Configure or migrate feeds on the host.");
            var info = new FileInfo(path);
            if (info.Length <= 0 || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                return Problem("Package feed catalogue must be a nonempty regular JSON file.");
            if (info.Length > MaxBytes) return Problem("Package feed catalogue exceeds its size limit.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) return Problem("Package feed catalogue exceeds its size limit.");
            var bytes = new byte[MaxBytes + 1];
            var count = 0;
            int read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
            if (count > MaxBytes) return Problem("Package feed catalogue exceeds its size limit.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            if (!Schema(document.RootElement))
                return Problem("Package feed catalogue has an invalid schema.");
            var catalog = JsonSerializer.Deserialize<PackageFeedCatalog>(bytes.AsSpan(0, count), Json);
            if (catalog is not { Version: 1, Feeds: not null } || !Valid(catalog.Feeds))
                return Problem("Package feed catalogue has an unsupported version or invalid descriptors.");
            return new(catalogPath, catalog.Feeds, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Problem("Package feed catalogue is unreadable or malformed. Repair it on the host.");
        }
    }

    private PackageFeedSettingsStatus Problem(string message) => new(catalogPath, [], message);

    private static bool Schema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root)
            || !root.TryGetProperty("version", out _) || !root.TryGetProperty("feeds", out var feeds)
            || feeds.ValueKind != JsonValueKind.Array || feeds.GetArrayLength() > 32) return false;
        foreach (var feed in feeds.EnumerateArray())
        {
            if (feed.ValueKind != JsonValueKind.Object || !UniqueProperties(feed)) return false;
            foreach (var field in new[] { "id", "kind", "name", "url", "hasCredential" })
                if (!feed.TryGetProperty(field, out _)) return false;
        }
        return true;
    }

    private static bool UniqueProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return element.EnumerateObject().All(p => names.Add(p.Name));
    }

    private static bool Valid(IReadOnlyList<PackageFeedDescriptor> feeds)
    {
        if (feeds.Count > 32) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var defaultNpm = false;
        foreach (var feed in feeds)
        {
            if (feed is null || feed.Id is null || !SafeId.IsMatch(feed.Id) || !ids.Add(feed.Id)
                || feed.Kind is not ("nuget" or "npm")
                || string.IsNullOrWhiteSpace(feed.Name) || feed.Name.Length > 100 || feed.Name.Any(char.IsControl)
                || feed.Url is null || feed.Url.Length > 4096
                || !Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
                || feed.Username is { Length: > 1024 } || feed.Username?.Any(char.IsControl) == true
                || feed.Scope is { Length: > 4096 }) return false;
            if (feed.Kind == "nuget")
            {
                if (feed.Scope is not null) return false;
            }
            else if (string.IsNullOrWhiteSpace(feed.Scope))
            {
                if (defaultNpm) return false;
                defaultNpm = true;
            }
            else
            {
                foreach (var scope in feed.Scope.Split(',', StringSplitOptions.TrimEntries))
                    if (!SafeScope.IsMatch(scope) || !scopes.Add(scope)) return false;
            }
        }
        return true;
    }
}
