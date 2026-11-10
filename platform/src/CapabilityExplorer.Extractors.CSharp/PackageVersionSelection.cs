using NuGet.Versioning;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Bounded, offline selection of the lowest cached version satisfying one explicit request.</summary>
internal static class PackageVersionSelection
{
    public static VersionRange? Parse(string? request) => !string.IsNullOrWhiteSpace(request)
        && VersionRange.TryParse(request, out var range) && !range.IsFloating ? range : null;

    public static bool Allows(VersionRange range, NuGetVersion version) => range.Satisfies(version)
        && (!version.IsPrerelease || range.MinVersion?.IsPrerelease == true || range.MaxVersion?.IsPrerelease == true);

    public static bool AllowsLocked(string? request, string pinned)
    {
        if (!NuGetVersion.TryParse(pinned, out var version)) return false;
        if (request is null) return true;
        if (string.IsNullOrWhiteSpace(request) || !VersionRange.TryParse(request, out var range)) return false;
        return range.Float is { } floating
            ? range.Satisfies(version) && floating.Satisfies(version)
            : Allows(range, version);
    }

    public static string? Pick(string directory, IReadOnlyList<VersionRange> ranges) => Directory.EnumerateDirectories(directory)
        .Select(Path.GetFileName).OfType<string>()
        .Select(name => (Name: name, Version: NuGetVersion.TryParse(name, out var version) ? version : null))
        .Where(v => v.Version is not null && ranges.All(range => Allows(range, v.Version)))
        .OrderBy(v => v.Version!, VersionComparer.VersionRelease).ThenBy(v => v.Name, StringComparer.Ordinal)
        .Select(v => v.Name).FirstOrDefault();
}
