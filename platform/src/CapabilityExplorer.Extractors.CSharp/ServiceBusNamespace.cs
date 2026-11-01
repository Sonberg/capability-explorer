namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Only the namespace authority is architecture metadata; connection credentials never are.</summary>
internal static class ServiceBusNamespace
{
    public static string? Parse(string value)
    {
        if (value.Length > 8192 || value.Any(char.IsControl)) return null;
        var endpoint = value;
        if (value.Contains(';') || value.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var endpoints = parts.Where(p => p.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (endpoints.Length != 1 || parts.Any(p => !p.Contains('='))) return null;
            endpoint = endpoints[0]["Endpoint=".Length..];
        }
        if (!endpoint.Contains("://", StringComparison.Ordinal)) endpoint = "sb://" + endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "sb" || uri.HostNameType != UriHostNameType.Dns
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath is not ("" or "/") || uri.Port != -1) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        var partsOfHost = host.Split('.');
        // Azure namespace identity has a concrete namespace plus servicebus and a DNS suffix.
        if (partsOfHost.Length < 4 || partsOfHost[1] != "servicebus"
            || partsOfHost.Any(p => p.Length == 0 || p.Length > 63 || !char.IsAsciiLetterOrDigit(p[0])
                || !char.IsAsciiLetterOrDigit(p[^1]) || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return null;
        return host;
    }
}
