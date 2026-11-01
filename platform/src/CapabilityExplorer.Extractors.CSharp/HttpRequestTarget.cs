using System.Text.RegularExpressions;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Static URI facts only. A host hint never proves a deployment binding.</summary>
internal sealed partial record HttpRequestTarget(string Kind, string Hint, string Route, string? Scheme, string[] QueryNames)
{
    public static HttpRequestTarget? Parse(string value, string hint)
    {
        if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\')) return null;
        var parts = hint.Split('=', 2);
        var marker = "ceplaceholder";
        while (value.Contains(marker, StringComparison.OrdinalIgnoreCase) || hint.Contains(marker, StringComparison.OrdinalIgnoreCase)) marker += "x";
        var safe = Parameter().Replace(value, marker);
        var absolute = SchemePrefix().IsMatch(value);
        Uri? uri = null;
        if (absolute)
        {
            if (!TryAbsolute(safe, marker, out uri)) return null;
        }
        else
        {
            // A colon in the first segment, or a template before ://, is not a relative HTTP path.
            if (value.Split('/', '?', '#')[0].Contains(':') || value.Contains("://", StringComparison.Ordinal)) return null;
            if (parts[0] == "base")
            {
                if (!TryAbsolute(Parameter().Replace(parts[1], marker), marker, out var baseUri)
                    || !Uri.TryCreate(baseUri, safe, out uri)) return null;
            }
        }

        var path = uri is null ? value.Split('?', '#')[0] : uri.AbsolutePath.Replace(marker, "{}", StringComparison.Ordinal);
        var query = uri is null ? value.Split('#')[0].Split('?', 2).Skip(1).FirstOrDefault() : uri.Query.TrimStart('?');
        var names = (query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries).Select(q => q.Split('=')[0])
            .Where(q => q.Length > 0 && !q.Contains(marker, StringComparison.Ordinal) && !q.Contains('{')).ToArray();
        return new HttpRequestTarget(uri is null ? parts[0] : "host", uri is null ? parts[1] : uri.Authority.ToLowerInvariant(),
            Rules.NormaliseRoute(path), uri?.Scheme, names);
    }

    private static bool TryAbsolute(string value, string marker, out Uri? uri)
    {
        uri = null;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) return false;
        var authority = value[(separator + 3)..].Split('/', '?', '#')[0];
        if (authority.Length == 0 || authority.Contains(marker, StringComparison.OrdinalIgnoreCase) || authority.Any(char.IsWhiteSpace)) return false;
        return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemePrefix();
}
