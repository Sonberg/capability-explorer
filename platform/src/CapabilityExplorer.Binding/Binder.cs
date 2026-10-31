using System.Security.Cryptography;
using System.Text;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Binding;

public enum BindingStatus
{
    /// <summary>The bound key is a declared symbol, or a named resource that is not required to be declared.</summary>
    Bound,

    /// <summary>The binding points at an endpoint that no selected revision declares (for example a renamed route).</summary>
    Broken,
}

/// <summary>
/// A new <c>human</c>-derived edge produced by a binding. <see cref="Edge"/> reuses the original's evidence ids (they
/// resolve inside <see cref="Artifact"/>); <see cref="Original"/> is the unchanged extractor edge it was derived from.
/// </summary>
public sealed record BoundEdge(
    LoadedFrom Artifact,
    EdgeRecord Original,
    EdgeRecord Edge,
    BindingEntry Binding,
    BindingStatus Status);

/// <summary>Identifies the artifact an edge came from without tying the binder to a particular store.</summary>
public sealed record LoadedFrom(string RepositoryId, string CommitSha, string UnitPath, object? Handle = null);

public sealed record BindingResult(IReadOnlyList<BoundEdge> Edges, IReadOnlyList<BindingEntry> UnusedBindings);

public static class Binder
{
    public const string RuleName = "service-map";
    public const string RuleVersion = "1";

    /// <summary>
    /// Replaces the unbound segment of every edge target the file names. Output depends only on the artifacts and the
    /// file, never on order. Extractor records are not modified: results are separate edges.
    /// </summary>
    public static BindingResult Bind(IEnumerable<(LoadedFrom From, Artifact Artifact)> artifacts, BindingFile file)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(file);
        var list = artifacts.ToList();
        var byHint = file.Entries.ToDictionary(e => e.Hint, StringComparer.Ordinal);

        // A declared symbol counts under its own key and under the key its unbound segment would bind to.
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, a) in list)
        {
            foreach (var n in a.Nodes)
            {
                declared.Add(n.SymbolKey);
                if (Rewrite(n.SymbolKey, byHint) is { } r)
                {
                    declared.Add(r.Key);
                }
            }
        }

        var bound = new List<BoundEdge>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, artifact) in list)
        {
            foreach (var edge in artifact.Edges)
            {
                if (edge.To is null || Rewrite(edge.To, byHint) is not { } rewritten)
                {
                    continue;
                }

                used.Add(rewritten.Entry.Hint);
                var broken = edge.Relation == "REQUESTS" && !declared.Contains(rewritten.Key);
                var id = "e:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"bind|{edge.Id}|{rewritten.Entry.Hint}|{rewritten.Entry.Target}")))[..16];
                var created = edge with
                {
                    Id = id,
                    To = rewritten.Key,
                    Derivation = "human",
                    Resolution = broken ? "unresolved" : "resolved",
                    UnresolvedReason = broken ? "missing-reference" : null,
                    Rule = new ToolRef(RuleName, RuleVersion),
                };
                bound.Add(new BoundEdge(from, edge, created, rewritten.Entry, broken ? BindingStatus.Broken : BindingStatus.Bound));
            }
        }

        bound.Sort((a, b) => string.CompareOrdinal(a.Edge.Id, b.Edge.Id));
        return new BindingResult(bound, file.Entries.Where(e => !used.Contains(e.Hint)).ToList());
    }

    private static (string Key, BindingEntry Entry)? Rewrite(string key, Dictionary<string, BindingEntry> byHint)
    {
        if (!key.Contains("@unbound:", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = key.Split('|');
        for (var i = 1; i < segments.Length; i++)
        {
            if (byHint.TryGetValue(segments[i], out var entry))
            {
                segments[i] = entry.Target;
                return (string.Join('|', segments), entry);
            }
        }

        return null;
    }
}
