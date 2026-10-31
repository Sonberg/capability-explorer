using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Basic-auth credentials for a feed. They never appear in ToString, logs, the lock or artifacts.</summary>
public sealed class FeedCredentials(string username, string password)
{
    public string Username { get; } = username;

    public string Password { get; } = password;

    public override string ToString() => "***";
}

/// <param name="Name">Name from nuget.config.</param>
/// <param name="Url">NuGet v3 service index, or the flat container itself when <paramref name="BaseAddress"/> is set.</param>
public sealed record PackageFeed(string Name, string Url, FeedCredentials? Credentials = null, string? BaseAddress = null)
{
    public static PackageFeed NuGetOrg { get; } = new("nuget.org", "https://api.nuget.org/v3/index.json", null, "https://api.nuget.org/v3-flatcontainer/");

    public override string ToString() => $"{Name} ({Url})";
}

/// <summary>
/// Reads package sources from nuget.config files in the exported tree (as plain XML, never through NuGet or MSBuild).
/// Credentials come only from %ENV_VAR% references that the packages step was given; nothing is read from disk
/// or from the host, and only https feeds receive credentials.
/// </summary>
public static partial class NuGetConfig
{
    public static IReadOnlyList<PackageFeed> Discover(string sourceRoot, IEnumerable<string> projectDirs, Func<string, string?> env)
    {
        var root = Path.GetFullPath(sourceRoot);
        var dirs = new List<string> { root };
        foreach (var d in projectDirs.Select(Path.GetFullPath).Order(StringComparer.Ordinal))
        {
            var chain = new List<string>();
            for (var c = d; c.StartsWith(root, StringComparison.Ordinal) && c.Length > root.Length; c = Path.GetDirectoryName(c)!)
            {
                chain.Add(c);
            }

            chain.Reverse();
            dirs.AddRange(chain.Where(c => !dirs.Contains(c)));
        }

        var sources = new List<(string Key, string Url)>();
        var credentials = new Dictionary<string, (string? User, string? Password)>(StringComparer.OrdinalIgnoreCase);
        var cleared = false;
        foreach (var dir in dirs)
        {
            var file = Directory.EnumerateFiles(dir, "*.config").FirstOrDefault(f => string.Equals(Path.GetFileName(f), "nuget.config", StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                continue;
            }

            XElement config;
            try
            {
                config = XDocument.Load(file).Root!;
            }
            catch (XmlException)
            {
                continue;
            }

            foreach (var section in config.Elements())
            {
                switch (section.Name.LocalName)
                {
                    case "packageSources":
                        foreach (var e in section.Elements())
                        {
                            var key = (string?)e.Attribute("key");
                            switch (e.Name.LocalName)
                            {
                                case "clear": sources.Clear(); cleared = true; break;
                                case "remove": sources.RemoveAll(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)); break;
                                case "add" when key is not null && (string?)e.Attribute("value") is { } url:
                                    sources.RemoveAll(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
                                    sources.Add((key, url));
                                    break;
                            }
                        }

                        break;
                    case "packageSourceCredentials":
                        foreach (var s in section.Elements())
                        {
                            string? user = null, password = null;
                            foreach (var a in s.Elements().Where(x => x.Name.LocalName == "add"))
                            {
                                var value = Expand((string?)a.Attribute("value"), env);
                                switch ((string?)a.Attribute("key"))
                                {
                                    case "Username": user = value; break;
                                    case "ClearTextPassword": password = value; break;
                                }
                            }

                            credentials[XmlConvert.DecodeName(s.Name.LocalName)] = (user, password);
                        }

                        break;
                }
            }
        }

        var feeds = new List<PackageFeed>();
        if (!cleared)
        {
            feeds.Add(PackageFeed.NuGetOrg);
        }

        foreach (var (key, url) in sources)
        {
            // Local folders, http and unparsable sources are skipped: only https v3 feeds, and only they get credentials.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var creds = credentials.TryGetValue(key, out var c) && !string.IsNullOrEmpty(c.User) && c.Password is not null ? new FeedCredentials(c.User, c.Password) : null;
            feeds.Add(new PackageFeed(key, url, creds));
        }

        return feeds;
    }

    private static string? Expand(string? value, Func<string, string?> env) =>
        value is null ? null : EnvReference().Replace(value, m => env(m.Groups[1].Value) ?? "");

    [GeneratedRegex("%([A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex EnvReference();
}
