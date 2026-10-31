namespace CapabilityExplorer.Contracts;

/// <summary>
/// Structural rules for symbol keys (schema/README.md). Keys are opaque strings to storage; these checks only
/// catch malformed keys and keys that claim to belong to another repository.
/// </summary>
public static class SymbolKey
{
    public const string External = "@ext";

    private static readonly Dictionary<string, int> MinimumSegments = new(StringComparer.Ordinal)
    {
        ["cs"] = 4,    // cs|<repo>|<assembly>|<documentation comment id>
        ["ts"] = 5,    // ts|<repo>|<package>|<module path>|<declaration path>#<meaning>
        ["proj"] = 3,  // proj|<repo>|<unit path>
        ["svc"] = 3,   // svc|<repo>|<service name>
        ["http"] = 3,  // http|<service ref>|<METHOD> <normalised route>
        ["sql"] = 3,   // sql|<store ref>|<schema>.<object>[#column]
        ["chan"] = 4,  // chan|<provider>|<namespace ref>|<entity>
        ["store"] = 3, // store|<provider>|<instance>[|<database>]
    };

    public static string Scheme(string key) => key[..Math.Max(0, key.IndexOf('|', StringComparison.Ordinal))];

    public static string[] Segments(string key) => key.Split('|');

    /// <summary>Returns a problem description, or null when the key is well formed.</summary>
    public static string? Check(string key)
    {
        var segments = Segments(key);
        if (!MinimumSegments.TryGetValue(segments[0], out var min))
        {
            return $"unknown symbol key scheme '{segments[0]}'";
        }

        if (segments.Length < min)
        {
            return $"'{segments[0]}' keys need at least {min} segments, got {segments.Length}";
        }

        if (segments.Any(s => s.Length == 0))
        {
            return "symbol key has an empty segment";
        }

        if (segments[0] == "ts" && !segments[^1].Contains('#', StringComparison.Ordinal))
        {
            return "ts keys must end with #<meaning> (value, type or namespace)";
        }

        if (segments[0] == "http" && !System.Text.RegularExpressions.Regex.IsMatch(segments[2], "^[A-Z]+ /"))
        {
            return "http keys must end with '<METHOD> /<route>'";
        }

        return null;
    }

    /// <summary>The repository a code-level key belongs to, or null for keys whose ownership is not a repository.</summary>
    public static string? OwningRepository(string key) => Scheme(key) switch
    {
        "cs" or "ts" or "proj" or "svc" => Segments(key)[1],
        _ => null,
    };
}
