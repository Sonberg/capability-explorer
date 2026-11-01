namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Operator authorization, never derived from the analyzed repository or service-index content.</summary>
public sealed class PackageFeedPolicy
{
    private readonly HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase) { "https://api.nuget.org" };
    public PackageFeedPolicy(string? allowedOrigins = null)
    {
        foreach (var origin in (allowedOrigins ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !PackageSafety.Https(uri)
                || uri.AbsolutePath != "/" || uri.Query.Length != 0)
                throw new InvalidDataException("CE_NUGET_ALLOWED_ORIGINS requires comma-separated HTTPS origins without paths, credentials, query or fragment");
            origins.Add(uri.GetLeftPart(UriPartial.Authority));
        }
    }
    public bool Allows(Uri uri) => PackageSafety.Https(uri) && origins.Contains(uri.GetLeftPart(UriPartial.Authority));
    public void Require(Uri uri)
    {
        if (!Allows(uri)) throw new HttpRequestException("Package feed origin is not authorized; configure CE_NUGET_ALLOWED_ORIGINS explicitly");
    }
}
