using System.Xml.Linq;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>
/// Finds package assemblies in a NuGet global-packages layout (&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/lib/&lt;tfm&gt;),
/// following nuspec dependencies. Read-only: it never downloads, restores or runs package code.
/// </summary>
public sealed record PackageResolution(IReadOnlyList<string> Assemblies, IReadOnlyList<PackageRef> Missing, IReadOnlyList<LockedPackage> Resolved);

public sealed class PackageResolver(string? packagesRoot)
{
    private static readonly string[] AssemblyFolders = ["ref", "lib"];

    internal static readonly string[] Frameworks = ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0", "netcoreapp3.1"];

    /// <summary>
    /// The reference assemblies for a project's packages. With a <paramref name="locked"/> set, exactly those versions
    /// are used and the folder's other contents are ignored; a locked package missing from the folder is an error,
    /// never silently different output. Without one, versions are picked from the folder (cache-dependent).
    /// <see cref="PackageResolution.Resolved"/> lists what was used, for the configuration hash.
    /// </summary>
    public PackageResolution Resolve(IEnumerable<PackageRef> packages, IReadOnlyList<LockedPackage>? locked = null)
    {
        var assemblies = new List<string>();
        var resolved = new List<LockedPackage>();
        var missing = new List<PackageRef>();
        if (locked is not null)
        {
            foreach (var l in locked)
            {
                var root = packagesRoot is null ? null : Path.Combine(packagesRoot, l.Id.ToLowerInvariant(), l.Version.ToLowerInvariant());
                if (root is null || !Directory.Exists(root))
                {
                    throw new InvalidOperationException($"locked package {l.Id} {l.Version} is not in the packages folder; run the packages step again");
                }

                assemblies.AddRange(AssembliesIn(root));
                resolved.Add(l);
            }

            var lockedIds = locked.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            missing.AddRange(packages.Where(p => !lockedIds.Contains(p.Id)));
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in packages)
            {
                if (!Visit(p.Id, p.Version, assemblies, resolved, seen, depth: 0))
                {
                    missing.Add(p);
                }
            }
        }

        return new PackageResolution(
            assemblies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            missing,
            resolved.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Version, StringComparer.Ordinal).ToList());
    }

    private static IEnumerable<string> AssembliesIn(string root)
    {
        // Reference assemblies (ref/) describe the compile surface when a package ships them; lib/ otherwise.
        var tfm = AssemblyFolders.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Frameworks.Select(f => Path.Combine(d, f))).FirstOrDefault(Directory.Exists);
        return tfm is null ? [] : Directory.EnumerateFiles(tfm, "*.dll").Order(StringComparer.Ordinal);
    }

    private bool Visit(string id, string? versionRange, List<string> assemblies, List<LockedPackage> resolved, HashSet<string> seen, int depth)
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
        resolved.Add(new LockedPackage(id, version));
        assemblies.AddRange(AssembliesIn(root));

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
                    Visit(depId, (string?)dep.Attribute("version"), assemblies, resolved, seen, depth + 1);
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
