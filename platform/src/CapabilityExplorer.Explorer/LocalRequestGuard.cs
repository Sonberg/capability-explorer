using System.Net;

namespace CapabilityExplorer.Explorer;

/// <summary>Trusted-local browser boundary, not authentication. Remote exposure is unsupported.</summary>
public static class LocalRequestGuard
{
    public static bool Allowed(HttpRequest request)
    {
        var host = request.Host.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !(IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))) return false;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))) return false;
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none")) return false;
        // A browser form/navigation may omit Origin on GET; never let it invoke paid/model or private API reads.
        if (request.Path.StartsWithSegments("/api") && request.Headers["Sec-Fetch-Mode"].ToString() == "navigate") return false;
        return true;
    }
}
