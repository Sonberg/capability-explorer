using Microsoft.CodeAnalysis;

namespace CapabilityExplorer.Extractors.CSharp;

/// <summary>
/// ce.v1 symbol keys for Roslyn symbols: documentation comment IDs, scoped to repository and assembly.
/// IDs that exceed the ce.v1 limit are replaced by a full SHA-256 suffix; shorter IDs remain readable.
/// Oversized assembly segments are hashed with the complete scoped identity.
/// </summary>
internal sealed class SymbolKeys(string repositoryId, IReadOnlySet<string> repoAssemblies)
{
    private const int MaxKeyLength = 2048; // UTF-16 code units, conservative for the ce.v1 schema limit.

    public string Of(ISymbol symbol)
    {
        var original = symbol.OriginalDefinition;
        var id = original.GetDocumentationCommentId() ?? $"?:{original.ToDisplayString()}";
        var assembly = original.ContainingAssembly?.Name ?? "unknown";
        var scope = repoAssemblies.Contains(assembly) && original.Locations.Any(l => l.IsInSource)
            ? repositoryId
            : CapabilityExplorer.Contracts.SymbolKey.External;
        var prefix = $"cs|{scope}|{assembly}|";
        var key = prefix + id;
        if (key.Length <= MaxKeyLength)
        {
            return key;
        }

        var bounded = prefix + "@sha256:" + ArtifactBuilder.Sha256(id);
        if (bounded.Length <= MaxKeyLength)
        {
            return bounded;
        }

        var assemblyKey = "@sha256:" + ArtifactBuilder.Sha256(assembly);
        var identityKey = "@sha256:" + ArtifactBuilder.Sha256($"{scope}\0{assembly}\0{id}");
        return $"cs|{scope}|{assemblyKey}|{identityKey}";
    }
}
