using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapabilityExplorer.Explorer;

public sealed record PackageFeedInput(
    string? Id,
    string? Kind,
    string? Name,
    string? Url,
    string? Scope,
    string? Username,
    string? Secret,
    bool ClearCredential = false);

public sealed record PackageFeedSettingsRequest(IReadOnlyList<PackageFeedInput>? Feeds);

public sealed record PackageFeedStatus(
    string Id,
    string Kind,
    string Name,
    string Url,
    string? Scope,
    string? Username,
    bool HasCredential);

public sealed record PackageFeedSettingsStatus(string SettingsPath, IReadOnlyList<PackageFeedStatus> Feeds, string? Problem);

public sealed class PackageFeedSettingsStore
{
    public const string DefaultPath = ".sandbox-cache/package-feeds/settings.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex SafeId = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeScope = new("^@[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private List<StoredFeed> _feeds = [];

    public PackageFeedSettingsStore(string settingsPath)
    {
        SettingsPath = settingsPath;
        try
        {
            if (File.Exists(settingsPath))
            {
                _feeds = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(settingsPath), Json)?.Feeds ?? [];
                Problem = Validate(_feeds.Select(ToInput).ToList(), _feeds.ToDictionary(f => f.Id, StringComparer.Ordinal)).Problem;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Problem = $"Could not read package feed settings: {e.GetType().Name}";
        }
    }

    public string SettingsPath { get; }

    public string? Problem { get; private set; }

    public PackageFeedSettingsStatus Status()
    {
        lock (_gate)
        {
            return new(SettingsPath, _feeds.Select(f => new PackageFeedStatus(f.Id, f.Kind, f.Name, f.Url, f.Scope, f.Username, !string.IsNullOrEmpty(f.Secret))).ToList(), Problem);
        }
    }

    public string? Save(PackageFeedSettingsRequest request)
    {
        if (request.Feeds is null) return "feeds is required";
        lock (_gate)
        {
            var current = _feeds.ToDictionary(f => f.Id, StringComparer.Ordinal);
            var validation = Validate(request.Feeds, current);
            if (validation.Problem is not null) return validation.Problem;

            var next = validation.Feeds!;
            SaveFile(next);
            _feeds = next;
            Problem = null;
            return null;
        }
    }

    private void SaveFile(List<StoredFeed> feeds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!);
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = OperatingSystem.IsWindows()
                ? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                : new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
            {
                JsonSerializer.Serialize(stream, new StoredSettings(feeds), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static (List<StoredFeed>? Feeds, string? Problem) Validate(IReadOnlyList<PackageFeedInput> inputs, IReadOnlyDictionary<string, StoredFeed> current)
    {
        if (inputs.Count > 32) return (null, "at most 32 package feeds can be configured");
        var feeds = new List<StoredFeed>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasDefaultNpm = false;

        foreach (var input in inputs)
        {
            var id = input.Id?.Trim();
            var kind = input.Kind?.Trim().ToLowerInvariant();
            var name = input.Name?.Trim();
            var urlText = input.Url?.Trim();
            if (id is null || !SafeId.IsMatch(id) || !ids.Add(id)) return (null, "feed IDs must be unique lowercase letters, digits, '.', '_' or '-'");
            if (kind is not ("nuget" or "npm")) return (null, "feed kind must be nuget or npm");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) return (null, "feed name is required and must be at most 100 characters");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                return (null, "feed URL must be an HTTPS URL without credentials, query, or fragment");

            string? scopeProblem = null;
            var scope = kind == "npm" ? NormalizeScopes(input.Scope, out scopeProblem) : null;
            if (scopeProblem is not null) return (null, scopeProblem);
            if (kind == "npm")
            {
                if (scope is null)
                {
                    if (hasDefaultNpm) return (null, "only one npm registry can be the default registry");
                    hasDefaultNpm = true;
                }
                else
                {
                    foreach (var value in scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!scopes.Add(value)) return (null, $"npm scope {value} is configured more than once");
                }
            }

            var username = input.Username?.Trim();
            if (username?.Any(char.IsControl) == true) return (null, "username contains invalid control characters");
            if (username is { Length: > 1024 }) return (null, "username is too long");
            var secret = input.Secret;
            if (secret?.Length > 8192 || secret?.Any(c => c is '\r' or '\n' or '\0') == true) return (null, "credential is too long or contains invalid characters");
            if (string.IsNullOrWhiteSpace(secret) && !input.ClearCredential && current.TryGetValue(id, out var previous))
            {
                if (previous.Kind != kind || !SameUrl(previous.Url, uri.AbsoluteUri))
                    return (null, "changing a feed URL or type requires entering a new credential or clearing the saved one");
                secret = previous.Secret;
                if (string.IsNullOrEmpty(username)) username = previous.Username;
            }
            if (input.ClearCredential) secret = null;
            if (kind == "nuget" && !string.IsNullOrEmpty(secret) && string.IsNullOrWhiteSpace(username)) return (null, "NuGet credentials require a username; use your token as the password when applicable");
            if (kind == "npm" && !string.IsNullOrEmpty(username) && string.IsNullOrEmpty(secret)) return (null, "npm username authentication requires a password or token");

            feeds.Add(new StoredFeed(id, kind, name, uri.AbsoluteUri, scope, username, string.IsNullOrEmpty(secret) ? null : secret));
        }

        return (feeds, null);
    }

    private static string? NormalizeScopes(string? value, out string? problem)
    {
        problem = null;
        var parts = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (parts.Any(s => !SafeScope.IsMatch(s)))
        {
            problem = "npm scopes must use the form @scope-name, separated by commas";
            return null;
        }
        return parts.Length == 0 ? null : string.Join(",", parts);
    }

    private static bool SameUrl(string before, string after) => string.Equals(before.TrimEnd('/'), after.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static PackageFeedInput ToInput(StoredFeed feed) => new(feed.Id, feed.Kind, feed.Name, feed.Url, feed.Scope, feed.Username, feed.Secret);

    private sealed record StoredSettings(List<StoredFeed> Feeds);
    private sealed record StoredFeed(string Id, string Kind, string Name, string Url, string? Scope, string? Username, string? Secret);
}