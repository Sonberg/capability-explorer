using System.Xml.Linq;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>
/// Finds package assemblies in a NuGet global-packages layout (&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/lib/&lt;tfm&gt;),
/// following nuspec dependencies. Read-only: it never downloads, restores or runs package code.
/// </summary>
public sealed class PackageResolver(string? packagesRoot)
{
    private static readonly string[] AssemblyFolders = ["ref", "lib"];

    internal static readonly string[] Frameworks = ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0", "netcoreapp3.1"];

    public (IReadOnlyList<string> Assemblies, IReadOnlyList<PackageRef> Missing) Resolve(IEnumerable<PackageRef> packages)
    {
        var assemblies = new List<string>();
        var missing = new List<PackageRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
        {
            if (!Visit(p.Id, p.Version, assemblies, seen, depth: 0))
            {
                missing.Add(p);
            }
        }

        return (assemblies.Distinct(StringComparer.Ordinal).ToList(), missing);
    }

    private bool Visit(string id, string? versionRange, List<string> assemblies, HashSet<string> seen, int depth)
    {
        if (packagesRoot is null || depth > 6)
        {
            return false;
        }

        if (!seen.Add(id))
        {
            return true;
        }

        var dir = Path.Combine(packagesRoot, id.ToLowerInvariant());
        if (!Directory.Exists(dir))
        {
            return false;
        }

        var version = Pick(dir, versionRange);
        if (version is null)
        {
            return false;
        }

        var root = Path.Combine(dir, version);
        // Reference assemblies (ref/) describe the compile surface when a package ships them; lib/ otherwise.
        var tfm = AssemblyFolders.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Frameworks.Select(f => Path.Combine(d, f))).FirstOrDefault(Directory.Exists);
        if (tfm is not null)
        {
            assemblies.AddRange(Directory.EnumerateFiles(tfm, "*.dll"));
        }

        var nuspec = Directory.EnumerateFiles(root, "*.nuspec").FirstOrDefault();
        if (nuspec is not null)
        {
            var doc = XDocument.Load(nuspec);
            var groups = doc.Descendants().Where(e => e.Name.LocalName == "group").ToList();
            var group = Frameworks.Select(f => groups.FirstOrDefault(g => Normalise((string?)g.Attribute("targetFramework")) == f)).FirstOrDefault(g => g is not null);
            var deps = (group?.Elements() ?? doc.Descendants().Where(e => e.Name.LocalName == "dependencies").Elements())
                .Where(e => e.Name.LocalName == "dependency");
            foreach (var dep in deps)
            {
                if ((string?)dep.Attribute("id") is { } depId)
                {
                    Visit(depId, (string?)dep.Attribute("version"), assemblies, seen, depth + 1);
                }
            }
        }

        return true;
    }

    internal static string? Normalise(string? tfm) => tfm?.ToLowerInvariant() switch
    {
        null => null,
        var t when t.StartsWith(".netstandard", StringComparison.Ordinal) => "netstandard" + t[".netstandard".Length..],
        var t when t.StartsWith("net", StringComparison.Ordinal) && t.Contains('.', StringComparison.Ordinal) => t.Split('-')[0],
        var t => t,
    };

    /// <summary>The exact version if present, else the lowest available version satisfying a "[min, ...)" range.</summary>
    private static string? Pick(string dir, string? range)
    {
        var available = Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>().ToList();
        var min = range?.Trim('[', '(', ']', ')', ' ').Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(min) && available.Contains(min, StringComparer.OrdinalIgnoreCase))
        {
            return available.First(v => string.Equals(v, min, StringComparison.OrdinalIgnoreCase));
        }

        var parsed = available.Select(v => (v, ok: Version.TryParse(v.Split('-')[0], out var pv), pv)).Where(x => x.ok).OrderBy(x => x.pv).ToList();
        if (Version.TryParse(min?.Split('-')[0], out var floor))
        {
            return parsed.FirstOrDefault(x => x.pv >= floor).v ?? parsed.LastOrDefault().v;
        }

        return parsed.LastOrDefault().v;
    }
}
