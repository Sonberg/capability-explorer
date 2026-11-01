namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>
/// Finds package assemblies in a NuGet global-packages layout (&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/lib/&lt;tfm&gt;),
/// following nuspec dependencies. Read-only: it never downloads, restores or runs package code.
/// </summary>
public sealed record PackageResolution(IReadOnlyList<string> Assemblies, IReadOnlyList<PackageRef> Missing, IReadOnlyList<LockedPackage> Resolved)
{
    public IReadOnlyList<ResolutionIssue> Issues { get; init; } = [];
    public string InputFingerprint { get; init; } = "";
}

public sealed class PackageResolver(string? packagesRoot, IReadOnlyDictionary<string, string>? frameworkPackages = null)
{
    private static readonly string[] AssemblyFolders = ["ref", "lib"];

    /// <summary>
    /// The reference assemblies for a project's packages. With a <paramref name="locked"/> set, exactly those versions
    /// are used and the folder's other contents are ignored; a locked package missing from the folder is an error,
    /// never silently different output. Without one, versions are picked from the folder (cache-dependent).
    /// <see cref="PackageResolution.Resolved"/> lists what was used, for the configuration hash.
    /// </summary>
    public PackageResolution Resolve(IEnumerable<PackageRef> packages, IReadOnlyList<LockedPackage>? locked = null, string? targetFramework = "net10.0")
    {
        var assemblies = new List<string>();
        var resolved = new List<LockedPackage>();
        var missing = new List<PackageRef>();
        var issues = new List<ResolutionIssue>();
        var inspected = new List<LockedPackage>();
        if (locked is not null)
        {
            var roots = packages.ToList();
            var byId = locked.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var l in locked)
            {
                PackageSafety.Validate(l.Id, l.Version);
                var root = packagesRoot is null ? null : Path.Combine(packagesRoot, l.Id.ToLowerInvariant(), l.Version.ToLowerInvariant());
                if (root is not null) PackageSafety.NoLinks(packagesRoot!, root);
                if (root is null || !Directory.Exists(root))
                {
                    throw new InvalidOperationException($"locked package {l.Id} {l.Version} is not in the packages folder; run the packages step again");
                }

            }

            missing.AddRange(roots.Where(p => !byId.ContainsKey(p.Id) && !FrameworkPackageCoverage.Satisfies(frameworkPackages, p.Id, p.Version)));
            var queue = new Queue<string>(roots.Select(p => p.Id));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.TryDequeue(out var id))
            {
                if (!seen.Add(id) || !byId.TryGetValue(id, out var package)) continue;
                var root = Path.Combine(packagesRoot!, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
                PackageSafety.Tree(root);
                assemblies.AddRange(AssembliesIn(root, targetFramework, package.Id, issues));
                resolved.Add(package);
                foreach (var dependency in FrameworkSelection.DependencyElements(root, targetFramework))
                {
                    if ((string?)dependency.Attribute("id") is not { } depId) continue;
                    if (!byId.ContainsKey(depId) && !FrameworkPackageCoverage.Satisfies(frameworkPackages, depId, (string?)dependency.Attribute("version")))
                    {
                        issues.Add(new("UNRESOLVED_TRANSITIVE_PACKAGE", $"Locked package {package.Id} requires {depId} for {targetFramework}, which is absent from the lock; run the packages step again"));
                    }
                    queue.Enqueue(depId);
                }
            }
        }
        else
        {
            var graph = new CachedPackageGraph(packagesRoot, targetFramework, frameworkPackages).Resolve(packages);
            missing.AddRange(graph.Missing);
            issues.AddRange(graph.Issues);
            inspected.AddRange(graph.Inspected);
            resolved.AddRange(graph.Selected);
            foreach (var package in graph.Selected)
            {
                var root = Path.Combine(packagesRoot!, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
                assemblies.AddRange(AssembliesIn(root, targetFramework, package.Id, issues));
            }
        }

        var inputFingerprint = Fingerprint(inspected.Concat(resolved).Distinct());
        return new PackageResolution(
            assemblies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            missing,
            resolved.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Version, StringComparer.Ordinal).ToList())
        {
            Issues = issues,
            InputFingerprint = inputFingerprint,
        };
    }

    private string Fingerprint(IEnumerable<LockedPackage> packages)
    {
        var inputs = new List<string>();
        foreach (var package in packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var root = Path.Combine(packagesRoot!, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
            var prefix = $"{package.Id.ToLowerInvariant()}@{package.Version.ToLowerInvariant()}";
            foreach (var file in Directory.EnumerateFiles(root, "*.nuspec"))
                inputs.Add($"{prefix}/{Path.GetFileName(file)}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))}");
            foreach (var folder in AssemblyFolders.Select(f => Path.Combine(root, f)).Where(Directory.Exists))
            {
                foreach (var dir in Directory.EnumerateDirectories(folder)) inputs.Add($"{prefix}/dir:{Path.GetRelativePath(root, dir).Replace('\\', '/')}");
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "_._"))
                    inputs.Add($"{prefix}/{Path.GetRelativePath(root, file).Replace('\\', '/')}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))}");
            }
        }
        return ArtifactBuilder.Sha256(string.Join("\n", inputs.Order(StringComparer.Ordinal)));
    }

    private static List<string> AssembliesIn(string root, string? target, string id, List<ResolutionIssue> issues)
    {
        if (FrameworkSelection.IncompatibleDependencyGroups(root, target))
        {
            issues.Add(new("PACKAGE_NO_COMPATIBLE_DEPENDENCIES", $"Package {id} declares only incompatible dependency groups for {target ?? "the unresolved target"}"));
        }

        foreach (var folder in AssemblyFolders)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            var candidates = Directory.EnumerateDirectories(dir)
                .Where(d => Directory.EnumerateFiles(d).Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "_._"))
                .Select(Path.GetFileName).OfType<string>().ToList();
            if (FrameworkSelection.Nearest(target, candidates) is not { } nearest) continue;
            // An explicit _._ empty group intentionally suppresses compile references, including lib fallback.
            return Directory.EnumerateFiles(Path.Combine(dir, nearest), "*.dll").Order(StringComparer.Ordinal).ToList();
        }

        var compileFiles = AssemblyFolders.Select(f => Path.Combine(root, f)).Where(Directory.Exists)
            .Any(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "_._"));
        if (compileFiles)
        {
            issues.Add(new("PACKAGE_NO_COMPATIBLE_ASSETS", $"Package {id} has compile assets but no compatible ref/lib group for {target ?? "the unresolved target"}"));
        }
        // Packages with no lib/ref assets (meta-packages, analyzers, build-only packages) need no compile assembly.
        return [];
    }

}
