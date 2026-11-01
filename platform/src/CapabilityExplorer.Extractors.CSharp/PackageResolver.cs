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

public sealed class PackageResolver(string? packagesRoot)
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
        if (locked is not null)
        {
            var roots = packages.ToList();
            var byId = locked.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var l in locked)
            {
                var root = packagesRoot is null ? null : Path.Combine(packagesRoot, l.Id.ToLowerInvariant(), l.Version.ToLowerInvariant());
                if (root is null || !Directory.Exists(root))
                {
                    throw new InvalidOperationException($"locked package {l.Id} {l.Version} is not in the packages folder; run the packages step again");
                }

            }

            missing.AddRange(roots.Where(p => !byId.ContainsKey(p.Id)));
            var queue = new Queue<string>(roots.Select(p => p.Id));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.TryDequeue(out var id))
            {
                if (!seen.Add(id) || !byId.TryGetValue(id, out var package)) continue;
                var root = Path.Combine(packagesRoot!, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
                assemblies.AddRange(AssembliesIn(root, targetFramework, package.Id, issues));
                resolved.Add(package);
                foreach (var dependency in FrameworkSelection.DependencyElements(root, targetFramework))
                {
                    if ((string?)dependency.Attribute("id") is not { } depId) continue;
                    if (!byId.ContainsKey(depId))
                    {
                        issues.Add(new("UNRESOLVED_TRANSITIVE_PACKAGE", $"Locked package {package.Id} requires {depId} for {targetFramework}, which is absent from the lock; run the packages step again"));
                    }
                    queue.Enqueue(depId);
                }
            }
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in packages)
            {
                if (!Visit(p.Id, p.Version, assemblies, resolved, seen, issues, targetFramework, depth: 0))
                {
                    missing.Add(p);
                }
            }
        }

        return new PackageResolution(
            assemblies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            missing,
            resolved.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Version, StringComparer.Ordinal).ToList())
        {
            Issues = issues,
            InputFingerprint = Fingerprint(resolved),
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

    private bool Visit(string id, string? versionRange, List<string> assemblies, List<LockedPackage> resolved, HashSet<string> seen, List<ResolutionIssue> issues, string? target, int depth)
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
        assemblies.AddRange(AssembliesIn(root, target, id, issues));

        foreach (var dep in FrameworkSelection.DependencyElements(root, target))
        {
            if ((string?)dep.Attribute("id") is { } depId)
            {
                if (!Visit(depId, (string?)dep.Attribute("version"), assemblies, resolved, seen, issues, target, depth + 1))
                {
                    issues.Add(new("UNRESOLVED_TRANSITIVE_PACKAGE", $"Package {id} requires {depId} {(string?)dep.Attribute("version")}, which is unavailable"));
                }
            }
        }

        return true;
    }

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
