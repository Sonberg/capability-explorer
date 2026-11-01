using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>
/// One assembly per simple name in a compilation, the way the SDK resolves conflicts: the highest assembly version wins
/// and, on equal versions, the earlier reference (framework before packages before project-propagated references).
/// Without this a compilation holds the framework's copy, the NuGet copy and other projects' versions side by side,
/// which the compiler reports as CS0433 (type in both) and CS0121 (ambiguous call).
/// Only metadata (assembly name and version) is read; nothing is loaded or executed.
/// </summary>
internal static class ReferenceUnifier
{
    private static readonly ConcurrentDictionary<string, (string Name, Version Version)?> Identities = new(StringComparer.Ordinal);

    /// <summary>The references with duplicate assemblies removed, in the original order. Non-file and unreadable references are kept as they are.</summary>
    public static List<MetadataReference> Unify(IEnumerable<MetadataReference> references)
    {
        var list = references.DistinctBy(r => r.Display, StringComparer.Ordinal).ToList();
        var winner = new Dictionary<string, (int Index, Version Version)>(StringComparer.OrdinalIgnoreCase);
        var identities = new (string Name, Version Version)?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is PortableExecutableReference { FilePath: { } path } && IdentityOf(path) is { } id)
            {
                identities[i] = id;
                if (!winner.TryGetValue(id.Name, out var current) || id.Version > current.Version)
                {
                    winner[id.Name] = (i, id.Version);
                }
            }
        }

        return list.Where((_, i) => identities[i] is not { } id || winner[id.Name].Index == i).ToList();
    }

    internal static (string Name, Version Version)? IdentityOf(string path) => Identities.GetOrAdd(path, static p =>
    {
        try
        {
            using var stream = File.OpenRead(p);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            var def = reader.GetAssemblyDefinition();
            return (reader.GetString(def.Name), def.Version);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    });
}
