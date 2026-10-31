using System.Text.Json;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public sealed record EvidenceDto(string Id, string RepositoryId, string CommitSha, string Path, int StartLine, int StartColumn, int EndLine, int EndColumn);

public sealed record NodeDto(
    string Key,
    string Kind,
    string Name,
    string? Signature,
    bool Generated,
    JsonElement? Props,
    string RepositoryId,
    string CommitSha,
    string Unit,
    int ArtifactId,
    IReadOnlyList<EvidenceDto> Evidence);

/// <summary>A reference to a symbol that may or may not be declared in the current view.</summary>
public sealed record SymbolRef(string Key, string Name, string? Kind, bool Declared, string? RepositoryId);

public sealed record EdgeDto(
    string Relation,
    SymbolRef From,
    SymbolRef? To,
    string Derivation,
    string Resolution,
    string? UnresolvedReason,
    string? Rule,
    JsonElement? Props,
    string RepositoryId,
    string CommitSha,
    IReadOnlyList<EvidenceDto> Evidence);

public static class Map
{
    public static IReadOnlyList<EvidenceDto> Evidence(LoadedArtifact a, IEnumerable<string>? ids) =>
        (ids ?? []).Where(a.EvidenceById.ContainsKey).Select(id =>
        {
            var e = a.EvidenceById[id];
            return new EvidenceDto(e.Id, e.RepositoryId, e.CommitSha, e.Path, e.Span.StartLine, e.Span.StartColumn, e.Span.EndLine, e.Span.EndColumn);
        }).ToList();

    public static NodeDto Node(Occurrence<NodeRecord> o) => new(
        o.Record.SymbolKey, o.Record.NodeKind, o.Record.Name, o.Record.Signature, o.Record.Generated ?? false, o.Record.Props,
        o.Artifact.RepositoryId, o.Artifact.CommitSha, o.Artifact.Artifact.Header.Unit.Name, o.Artifact.Id, Evidence(o.Artifact, o.Record.Evidence));

    public static SymbolRef Ref(ViewIndex view, string key)
    {
        var node = view.Node(key);
        return node is not null
            ? new SymbolRef(key, node.Record.Name, node.Record.NodeKind, true, node.Artifact.RepositoryId)
            : new SymbolRef(key, DisplayName(key), null, false, SymbolKey.OwningRepository(key));
    }

    public static EdgeDto Edge(ViewIndex view, Occurrence<EdgeRecord> o) => new(
        o.Record.Relation,
        Ref(view, o.Record.From),
        o.Record.To is null ? null : Ref(view, o.Record.To),
        o.Record.Derivation,
        o.Record.Resolution,
        o.Record.UnresolvedReason,
        o.Record.Rule is null ? null : $"{o.Record.Rule.Name} {o.Record.Rule.Version}",
        o.Record.Props,
        o.Artifact.RepositoryId,
        o.Artifact.CommitSha,
        Evidence(o.Artifact, o.Record.Evidence));

    /// <summary>A short, human-readable name for keys that are not declared in the view (externals, unbound resources).</summary>
    public static string DisplayName(string key)
    {
        var segments = key.Split('|');
        var last = segments[^1];
        return segments[0] switch
        {
            "cs" => CsName(last),
            "ts" => last.Split('#')[0],
            "http" or "chan" or "sql" => last,
            _ => last,
        };
    }

    private static string CsName(string docId)
    {
        var body = docId.Length > 2 && docId[1] == ':' ? docId[2..] : docId;
        var paren = body.IndexOf('(', StringComparison.Ordinal);
        var head = paren >= 0 ? body[..paren] : body;
        var parts = head.Split('.');
        return parts.Length >= 2 && docId.StartsWith("M:", StringComparison.Ordinal) ? $"{parts[^2]}.{parts[^1]}" : parts[^1];
    }
}
