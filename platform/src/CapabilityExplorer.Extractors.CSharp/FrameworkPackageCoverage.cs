using NuGet.Versioning;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>
/// PackageOverrides.txt is SDK-produced package-level compatibility data for a targeting pack.
/// An assembly's name or assembly version alone is never proof that it supplies a NuGet package.
/// </summary>
internal sealed record FrameworkPackageCoverage(IReadOnlyDictionary<string, string> Packages, string Fingerprint)
{
    public static FrameworkPackageCoverage Read(IEnumerable<string> selectedFrameworkFiles)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new List<string>();
        var roots = selectedFrameworkFiles.Select(f => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(f)))!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var data = Path.Combine(root, "data", "PackageOverrides.txt");
            if (!File.Exists(data)) continue;
            inputs.Add($"{Path.GetFileName(Path.GetDirectoryName(root))}/{Path.GetFileName(root)}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(data)))}");
            foreach (var line in File.ReadLines(data))
            {
                var parts = line.Trim().Split('|');
                if (parts.Length != 2 || !NuGetVersion.TryParse(parts[1], out var version) || version.IsPrerelease) continue;
                // The SDK assumes package patches are supplied by .NETCoreApp servicing patches.
                // This applies only to SDK-produced override metadata, never to cached package versions.
                if (Path.GetFileName(Path.GetDirectoryName(root)) is "Microsoft.NETCore.App.Ref" or "Microsoft.AspNetCore.App.Ref")
                    version = new NuGetVersion(version.Major, version.Minor, 32767);
                if (!packages.TryGetValue(parts[0], out var prior) || version > NuGetVersion.Parse(prior)) packages[parts[0]] = version.ToNormalizedString();
            }
        }
        return new(packages, ArtifactBuilder.Sha256(string.Join("\n", inputs)));
    }

    public static bool Satisfies(IReadOnlyDictionary<string, string>? packages, string id, string? range)
    {
        if (packages is null || !packages.TryGetValue(id, out var supplied) || !NuGetVersion.TryParse(supplied, out var ceiling)
            || ceiling.IsPrerelease || PackageVersionSelection.Parse(range) is not { HasLowerBound: true } requested
            || requested.MinVersion.IsPrerelease || requested.MaxVersion?.IsPrerelease == true) return false;
        return requested.IsMinInclusive ? requested.MinVersion <= ceiling : requested.MinVersion < ceiling;
    }
}
