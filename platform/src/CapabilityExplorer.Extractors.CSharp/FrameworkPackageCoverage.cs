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
                if (parts.Length != 2 || StableVersion(parts[1]) is not { } version) continue;
                if (!packages.TryGetValue(parts[0], out var prior) || version > StableVersion(prior)) packages[parts[0]] = parts[1];
            }
        }
        return new(packages, ArtifactBuilder.Sha256(string.Join("\n", inputs)));
    }

    public static bool Satisfies(IReadOnlyDictionary<string, string>? packages, string id, string? range)
    {
        if (packages is null || !packages.TryGetValue(id, out var supplied) || StableVersion(supplied) is not { } ceiling
            || string.IsNullOrWhiteSpace(range)) return false;
        var value = range.Trim();
        var inclusive = true;
        if (value.StartsWith('[') || value.StartsWith('('))
        {
            if (!value.EndsWith(']') && !value.EndsWith(')')) return false;
            inclusive = value[0] == '[';
            var bounds = value[1..^1].Split(',');
            if (bounds.Length == 1)
            {
                return inclusive && value.EndsWith(']') && StableVersion(bounds[0].Trim()) is { } exact && exact <= ceiling;
            }
            if (bounds.Length != 2 || StableVersion(bounds[0].Trim()) is not { } lower) return false;
            if (bounds[1].Trim() is { Length: > 0 } upperText)
            {
                if (StableVersion(upperText) is not { } upper || upper < lower
                    || upper == lower && (!inclusive || !value.EndsWith(']'))) return false;
            }
            return inclusive ? lower <= ceiling : lower < ceiling;
        }
        return StableVersion(value) is { } minimum && minimum <= ceiling;
    }

    private static Version? StableVersion(string value) => value.All(c => char.IsAsciiDigit(c) || c == '.')
        && Version.TryParse(value, out var parsed)
        ? new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision)) : null;
}
