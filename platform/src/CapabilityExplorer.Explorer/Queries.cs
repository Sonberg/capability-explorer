using System.Text.Json;
using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

public sealed record SearchHit(string Key, string Name, string Kind, string RepositoryId, string Unit, bool Generated, bool HasCalculation);

public sealed record HistoryEntry(string CommitSha, string Label, string? Subject, string State);

public sealed record CalculationDto(
    string Key,
    string Name,
    string RepositoryId,
    string CommitSha,
    string Unit,
    string Language,
    JsonElement Output,
    JsonElement Inputs,
    JsonElement Expr,
    JsonElement Attributes,
    string? IrHash,
    IReadOnlyList<string> Includes,
    IReadOnlyList<EvidenceDto> Evidence,
    IReadOnlyDictionary<string, IReadOnlyList<EvidenceDto>> AttributeEvidence);

public sealed record SymbolDetail(
    NodeDto Node,
    IReadOnlyList<EdgeDto> Outgoing,
    IReadOnlyList<EdgeDto> Incoming,
    CalculationDto? Calculation,
    IReadOnlyList<HistoryEntry> History);

/// <param name="Base">What the request goes through: host, configuration name, client or service (see Architecture.BaseOf).</param>
/// <param name="Group">Who it talks to, for grouping (see <see cref="Queries.Vendor"/>): "Fortnox", "Klarna", "Shopify", …</param>
/// <param name="SourcePath">Source file where the request occurrence was extracted.</param>
public sealed record HttpLink(EdgeDto Request, IReadOnlyList<SymbolRef> Candidates, string Match, string Note, string Base = "", string Group = "", string? SourcePath = null);

public sealed record EndpointDto(NodeDto Endpoint, IReadOnlyList<SymbolRef> Handlers);

public sealed record ServiceDto(NodeDto Service, IReadOnlyList<EndpointDto> Endpoints);

public sealed record ResourceKey(string Key, bool Bound, IReadOnlyList<EdgeDto> Edges, NodeDto? Declaration);

public sealed record ResourceGroup(string Name, string Kind, IReadOnlyList<ResourceKey> Keys, string? Note);

public sealed record Integrations(IReadOnlyList<ServiceDto> Services, IReadOnlyList<HttpLink> Http, IReadOnlyList<ResourceGroup> Channels, IReadOnlyList<ResourceGroup> Data, IReadOnlyList<MarkDto> Marks);

public sealed record ArtifactSummary(
    int Id, string Label, string RepositoryId, string CommitSha, string Revision, string Unit, string UnitPath, string Language,
    string Extractor, string Status, Counts Counts, Coverage Coverage, IReadOnlyList<DiagnosticRecord> Diagnostics, bool InDefaultView);

public static class Queries
{
    public static IReadOnlyList<SearchHit> Search(ViewIndex view, string? query, string? kind, string? repository, int limit = 100) =>
        SearchSlice(view, query, kind, repository, 0, limit).Items;

    public static (IReadOnlyList<SearchHit> Items, int Total) SearchSlice(ViewIndex view, string? query, string? kind, string? repository, int offset, int limit)
    {
        var q = (query ?? "").Trim();
        var hits = view.Nodes.Values.Select(list => list[0])
            .Where(o => kind is null or "" || o.Record.NodeKind == kind)
            .Where(o => repository is null or "" || o.Artifact.RepositoryId == repository)
            .Select(o => (o, Rank: Rank(o.Record, q)))
            .Where(x => x.Rank > 0)
            .OrderByDescending(x => x.Rank)
            .ThenBy(x => x.o.Record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.o.Record.SymbolKey, StringComparer.Ordinal)
            .ToArray();
        var start = Math.Clamp(offset, 0, hits.Length);
        var page = hits.Skip(start).Take(limit).Select(x => new SearchHit(
            x.o.Record.SymbolKey, x.o.Record.Name, x.o.Record.NodeKind, x.o.Artifact.RepositoryId, x.o.Artifact.Artifact.Header.Unit.Name,
            x.o.Record.Generated ?? false, view.Calculations.ContainsKey(x.o.Record.SymbolKey))).ToList();
        return (page, hits.Length);
    }

    /// <summary>
    /// Who a request talks to, from what the code says: a host's domain name (api.fortnox.se → Fortnox), or for a typed
    /// client the caller's namespace under its project (Glufs.Infrastructure.Klarna.* → Klarna); otherwise the base as is.
    /// </summary>
    internal static string Vendor(string toKey, string fromKey)
    {
        var via = Architecture.BaseOf(toKey);
        var hint = toKey.Split('|').ElementAtOrDefault(1) ?? "";
        if (hint.Contains(":client=", StringComparison.Ordinal) && fromKey.Split('|') is [.., var unit, var member])
        {
            // "M:Glufs.Infrastructure.Klarna.OrderManagementRestClient.CancelOrder(...)" under unit Glufs.Infrastructure.
            var name = member.Length > 2 && member[1] == ':' ? member[2..] : member;
            var rest = name.StartsWith(unit + ".", StringComparison.Ordinal) ? name[(unit.Length + 1)..] : "";
            var parts = rest.Split('(')[0].Split('.');
            return parts.Length > 2 ? Title(parts[0]) : via; // namespace, type, member: else no namespace names a vendor
        }

        // ponytail: second-to-last label is the domain; wrong for two-part suffixes (co.uk) — use a public-suffix list if that matters.
        var labels = via.Split('.');
        return hint.StartsWith("@unbound:", StringComparison.Ordinal) && labels.Length >= 2 && !via.Contains(' ', StringComparison.Ordinal) && labels[^1].All(char.IsAsciiLetter)
            ? Title(labels[^2])
            : via;
    }

    private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int Rank(NodeRecord n, string q)
    {
        if (q.Length == 0)
        {
            return 1;
        }

        if (string.Equals(n.Name, q, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (n.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (n.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return n.SymbolKey.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    public static SymbolDetail? Symbol(ViewIndex view, string key)
    {
        var node = view.Node(key);
        if (node is null)
        {
            return null;
        }

        var outgoing = view.Outgoing[key].Select(e => Map.Edge(view, e)).ToList();
        var incoming = view.Incoming[key].Select(e => Map.Edge(view, e)).ToList();
        var calc = view.Calculations.TryGetValue(key, out var c) ? Calculation(c) : null;
        return new SymbolDetail(Map.Node(node), outgoing, incoming, calc, History(view.Index, node));
    }

    /// <summary>
    /// Where the symbol key exists across every loaded revision of its repository. A key missing from a revision
    /// whose unit artifact is partial is "not observed", never "absent".
    /// </summary>
    private static List<HistoryEntry> History(ExplorerIndex index, Occurrence<NodeRecord> node)
    {
        var unitPath = node.Artifact.Artifact.Header.Unit.Path;
        return index.Revisions.Where(r => r.RepositoryId == node.Artifact.RepositoryId).Select(r =>
        {
            var artifacts = r.ArtifactIds.Select(id => index.Artifacts[id]).ToList();
            var state = artifacts.Any(a => a.Artifact.Nodes.Any(n => n.SymbolKey == node.Record.SymbolKey))
                ? "present"
                : artifacts.FirstOrDefault(a => a.Artifact.Header.Unit.Path == unitPath) is { } unit
                    ? unit.Artifact.Complete.Status == "partial" ? "notObserved" : "absent"
                    : "notAnalysed";
            return new HistoryEntry(r.CommitSha, r.Label, r.Subject, state);
        }).ToList();
    }

    public static CalculationDto Calculation(Occurrence<CalculationRecord> c)
    {
        var raw = c.Record.Raw;
        var node = c.Artifact.Artifact.Nodes.FirstOrDefault(n => n.SymbolKey == c.Record.SymbolKey);
        var attributeEvidence = raw.GetProperty("attributes").EnumerateObject().ToDictionary(
            a => a.Name,
            a => Map.Evidence(c.Artifact, a.Value.TryGetProperty("evidence", out var ev) ? ev.EnumerateArray().Select(x => x.GetString()!) : null));
        return new CalculationDto(
            c.Record.SymbolKey,
            node?.Name ?? Map.DisplayName(c.Record.SymbolKey),
            c.Artifact.RepositoryId,
            c.Artifact.CommitSha,
            c.Artifact.Artifact.Header.Unit.Name,
            c.Artifact.Artifact.Header.Language,
            raw.GetProperty("output"),
            raw.GetProperty("inputs"),
            raw.GetProperty("expr"),
            raw.GetProperty("attributes"),
            raw.TryGetProperty("irHash", out var h) ? h.GetString() : null,
            raw.TryGetProperty("includes", out var inc) ? inc.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
            Map.Evidence(c.Artifact, raw.GetProperty("evidence").EnumerateArray().Select(x => x.GetString()!)),
            attributeEvidence);
    }

    public static IReadOnlyList<CalculationDto> Calculations(ViewIndex view) =>
        view.Calculations.Values.Select(Calculation).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.RepositoryId, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Services, endpoints and the resources that connect repositories. Cross-repo HTTP matches are
    /// <em>candidates</em> by normalised route only: the client's base URL is unbound unless the service map (bindings.json) names it.
    /// </summary>
    public static Integrations Integrations(ViewIndex view, IReadOnlyList<MarkDto>? marks = null)
    {
        var edges = view.Edges.Select(e =>
        {
            var dto = Map.Edge(view, e);
            var bound = view.BoundFor(e);
            var binding = bound is null ? null : new IntegrationBinding(bound.Edge.To!, bound.Binding.Hint,
                bound.Binding.Target, bound.Binding.Note, bound.Status == CapabilityExplorer.Binding.BindingStatus.Bound);
            return new IntegrationEdge(dto, binding, SourcePathResolver.ForRequest(view, dto.From.Key, dto.Evidence.Select(x => x.Path)));
        }).ToList();
        return IntegrationGraph.Build(view.Nodes.Values.Select(l => Map.Node(l[0])).ToList(), edges, marks ?? []);
    }

    private static string KeyTail(string key) => key[(key.LastIndexOf('|') + 1)..];

    /// <summary>Every key a mark can point at that this artifact shows: nodes, edge targets, and the group keys ("group|http|Klarna").</summary>
    internal static void PresenceKeys(IReadOnlyList<NodeRecord> nodes, IReadOnlyList<EdgeRecord> edges, HashSet<string> into)
    {
        foreach (var n in nodes)
        {
            into.Add(n.SymbolKey);
        }

        foreach (var e in edges.Where(e => e.To is not null))
        {
            var to = e.To!;
            into.Add(to);
            if (e.Relation == "REQUESTS")
            {
                into.Add($"group|http|{Vendor(to, e.From)}");
            }
            else if (to.StartsWith("chan|", StringComparison.Ordinal))
            {
                into.Add($"group|channel|{EntityName(KeyTail(to))}");
            }
            else if (to.StartsWith("sql|", StringComparison.Ordinal))
            {
                into.Add($"group|data|{KeyTail(TableKey(to))}");
            }
        }
    }

    private static string EntityName(string entity) => entity.Contains('/', StringComparison.Ordinal) ? entity[(entity.IndexOf('/', StringComparison.Ordinal) + 1)..] : entity;

    private static string TableKey(string key) => key.Contains('#', StringComparison.Ordinal) ? key[..key.IndexOf('#', StringComparison.Ordinal)] : key;

    public static IReadOnlyList<ArtifactSummary> Artifacts(ExplorerIndex index) => index.Artifacts.Select(a =>
    {
        var h = a.Artifact.Header;
        var revision = index.FindRevision(h.RepositoryId, h.CommitSha)!;
        return new ArtifactSummary(
            a.Id, a.Label, h.RepositoryId, h.CommitSha, revision.Label, h.Unit.Name, h.Unit.Path, h.Language,
            $"{h.Extractor.Name} {h.Extractor.Version}", a.Artifact.Complete.Status, a.Artifact.Complete.Counts, a.Artifact.Complete.Coverage,
            a.Artifact.Diagnostics, index.DefaultSelection.TryGetValue(h.RepositoryId, out var sha) && sha == h.CommitSha);
    }).ToList();
}
