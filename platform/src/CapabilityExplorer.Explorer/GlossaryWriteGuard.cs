using System.Net;

namespace CapabilityExplorer.Explorer;

/// <summary>Browser request protection for the existing trusted local deployment, not user authentication.</summary>
public static class GlossaryWriteGuard
{
    public static bool Allowed(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host.Host;
        var localHost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (!localHost || request.Headers["X-CE-Glossary-Write"].ToString() != "1" || !request.HasJsonContentType()) return false;
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none")) return false;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return true; // Explicit non-browser callers still require JSON and the write header.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && string.Equals(uri.GetLeftPart(UriPartial.Authority), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
