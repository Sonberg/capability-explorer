using System.Reflection;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>Reads assembly manifests as data; never loads or executes analysed assemblies.</summary>
internal static class ReferenceSelection
{
    public static IReadOnlyList<MetadataReference> FileReferences(IEnumerable<string> paths, List<ResolutionIssue> issues)
    {
        var references = new List<MetadataReference>();
        foreach (var path in paths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception e) when (e is BadImageFormatException or IOException)
            {
                issues.Add(new("INVALID_ASSEMBLY_REFERENCE", $"Reference {Path.GetFileName(path)} is not a readable managed assembly"));
            }
        }
        return references;
    }

    public static IReadOnlyList<string> FrameworkFiles(string packs, ProjectModel project, List<ResolutionIssue> issues)
    {
        if (FrameworkSelection.Parse(project.TargetFramework) is not { } target) return [];
        var pack = target.Framework switch
        {
            ".NETCoreApp" => "Microsoft.NETCore.App.Ref",
            ".NETStandard" => "NETStandard.Library.Ref",
            _ => null,
        };
        if (pack is null)
        {
            issues.Add(new("UNSUPPORTED_FRAMEWORK_REFERENCE", $"Reference packs for {project.TargetFramework} are not supported by this static extractor"));
            return [];
        }

        var files = Pack(pack);
        if (project.IsWeb || project.FrameworkReferences.Contains("Microsoft.AspNetCore.App", StringComparer.Ordinal)) files.AddRange(Pack("Microsoft.AspNetCore.App.Ref"));
        return files;

        List<string> Pack(string name)
        {
            var root = Path.Combine(packs, name);
            var candidates = Directory.Exists(root) ? Directory.EnumerateDirectories(root)
                .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d), out var version) ? version : null))
                .Where(d => d.Version is not null && d.Version.Major == target.Version.Major && d.Version.Minor == target.Version.Minor)
                .OrderByDescending(d => d.Version).ThenBy(d => d.Path, StringComparer.Ordinal).ToList() : [];
            var framework = target.GetShortFolderName().Split('-')[0];
            var dir = candidates.Select(d => Path.Combine(d.Path, "ref", framework)).FirstOrDefault(Directory.Exists);
            if (dir is null)
            {
                issues.Add(new("MISSING_FRAMEWORK_REFERENCE_PACK", $"Reference pack {name} for {project.TargetFramework} is unavailable; a newer framework is never substituted"));
                return [];
            }

            return Directory.EnumerateFiles(dir, "*.dll").Order(StringComparer.Ordinal).ToList();
        }
    }

    private sealed record AssemblyFile(string Path, AssemblyName Identity, string Hash, bool Framework)
    {
        public string Family => $"{Identity.Name}|{Identity.CultureName}|{Convert.ToHexString(Identity.GetPublicKeyToken() ?? [])}";
    }

    public static IReadOnlyList<MetadataReference> Resolve(IEnumerable<MetadataReference> references, IReadOnlySet<string> frameworkPaths, List<ResolutionIssue> issues)
    {
        var result = new List<MetadataReference>();
        var compilations = new HashSet<Compilation>(ReferenceEqualityComparer.Instance);
        var files = new List<AssemblyFile>();
        foreach (var reference in references)
        {
            if (reference is CompilationReference cr)
            {
                if (compilations.Add(cr.Compilation)) result.Add(reference);
            }
            else if (reference is PortableExecutableReference { FilePath: { } path })
            {
                try
                {
                    files.Add(new(path, AssemblyName.GetAssemblyName(path), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), frameworkPaths.Contains(path)));
                }
                catch (Exception e) when (e is BadImageFormatException or IOException)
                {
                    issues.Add(new("INVALID_ASSEMBLY_REFERENCE", $"Reference {Path.GetFileName(path)} is not a readable managed assembly"));
                }
            }
        }

        foreach (var group in files.GroupBy(f => f.Identity.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var unique = group.OrderByDescending(f => f.Framework).ThenBy(f => f.Path, StringComparer.Ordinal).DistinctBy(f => f.Hash).ToList();
            AssemblyFile? selected = unique.Count == 1 ? unique[0] : null;
            // Only known shared-framework overlaps use the SDK's newer-assembly/framework-on-tie policy.
            // Other package conflicts have no proven winner and must remain unresolved.
            if (selected is null && unique.Any(f => f.Framework) && unique.All(f => f.Identity.GetPublicKeyToken() is { Length: > 0 })
                && unique.Select(f => f.Family).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                var newest = unique.Max(f => f.Identity.Version);
                var winners = unique.Where(f => f.Identity.Version == newest).ToList();
                var frameworks = winners.Where(f => f.Framework).ToList();
                selected = frameworks.Count == 1 ? frameworks[0] : frameworks.Count == 0 && winners.Count == 1 ? winners[0] : null;
            }

            if (selected is null)
            {
                issues.Add(new("ASSEMBLY_REFERENCE_CONFLICT", $"Conflicting references to {group.Key}: {string.Join("; ", unique.Select(f => $"{f.Identity.FullName} sha256={f.Hash}").Order(StringComparer.Ordinal))}; all conflicting copies are omitted"));
                continue;
            }

            result.Add(MetadataReference.CreateFromFile(selected.Path));
        }

        foreach (var group in compilations.GroupBy(c => c.AssemblyName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            issues.Add(new("ASSEMBLY_REFERENCE_CONFLICT", $"Multiple referenced projects declare assembly {group.Key}; all conflicting project copies are omitted"));
            result.RemoveAll(r => r is CompilationReference cr && group.Contains(cr.Compilation));
        }

        // A repository assembly and a package with the same name also have no proven winner.
        var names = compilations.Select(c => c.AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in files.Select(f => f.Identity.Name).Where(n => names.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new("ASSEMBLY_REFERENCE_CONFLICT", $"Project and package references both declare assembly {name}; all conflicting copies are omitted"));
            result.RemoveAll(r => r is CompilationReference cr && string.Equals(cr.Compilation.AssemblyName, name, StringComparison.OrdinalIgnoreCase)
                || r is PortableExecutableReference { FilePath: { } path } && files.Any(f => f.Path == path && f.Identity.Name == name));
        }

        return result;
    }

    public static string Fingerprint(IEnumerable<MetadataReference> references, IEnumerable<string> candidatePaths) => ArtifactBuilder.Sha256(string.Join("\n", references
        .OfType<PortableExecutableReference>().Select(r => r.FilePath).OfType<string>().Concat(candidatePaths).Distinct(StringComparer.Ordinal)
        .Select(p => $"{Path.GetFileName(p)}|{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))}")
        .Order(StringComparer.Ordinal)));
}
