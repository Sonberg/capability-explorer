using CapabilityExplorer.Contracts;

namespace CapabilityExplorer.Explorer;

internal sealed record IntegrationBinding(string Key, string Hint, string Target, string? Note, bool Bound);
internal sealed record IntegrationEdge(EdgeDto Edge, IntegrationBinding? Binding, string? SourcePath);

internal static class IntegrationGraph
{
    private static readonly string[] Relations = ["REQUESTS", "PUBLISHES", "CONSUMES", "READS", "WRITES", "MAPS_TO"];

    internal static Integrations Build(IReadOnlyList<NodeDto> nodes, IReadOnlyList<IntegrationEdge> observations,
        IReadOnlyList<MarkDto> marks)
    {
        var byKey = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var edges = observations.Select(o => o.Edge).ToList();
        SymbolRef Ref(string key) => byKey.TryGetValue(key, out var n)
            ? new(key, n.Name, n.Kind, true, n.RepositoryId)
            : new(key, Map.DisplayName(key), null, false, SymbolKey.OwningRepository(key));
        var endpoints = nodes.Where(n => n.Kind == "Endpoint").ToList();
        var services = nodes.Where(n => n.Kind == "Service").Select(s => new ServiceDto(s,
            edges.Where(e => e.From.Key == s.Key && e.Relation == "EXPOSES" && e.To is not null)
                .Select(e => byKey.GetValueOrDefault(e.To!.Key)).OfType<NodeDto>()
                .Select(ep => new EndpointDto(ep, edges.Where(h => h.To?.Key == ep.Key && h.Relation == "HANDLES")
                    .Select(h => Ref(h.From.Key)).ToList()))
                .OrderBy(ep => ep.Endpoint.Name, StringComparer.Ordinal).ToList()))
            .OrderBy(s => s.Service.RepositoryId, StringComparer.Ordinal).ToList();
        var http = observations.Where(o => o.Edge.Relation == "REQUESTS").Select(o =>
        {
            var e = o.Edge;
            HttpLink link;
            if (e.To is null) link = new(e, [], "dynamic", "target not statically known");
            else if (o.Binding is { } binding)
            {
                var hint = binding.Hint.StartsWith("@unbound:", StringComparison.Ordinal) ? binding.Hint["@unbound:".Length..] : binding.Hint;
                var why = $"service map: {hint} → {binding.Target}{(binding.Note is null ? "" : $" ({binding.Note})")}";
                link = binding.Bound && byKey.ContainsKey(binding.Key)
                    ? new(e, [Ref(binding.Key)], "binding", why)
                    : new(e, [Ref(binding.Key)], "broken", $"{why}; the bound endpoint is not declared in the selected revision");
            }
            else if (byKey.ContainsKey(e.To.Key)) link = new(e, [Ref(e.To.Key)], "bound", "client key equals endpoint key");
            else
            {
                var candidates = endpoints.Where(ep => Tail(ep.Key) == Tail(e.To.Key)).Select(ep => Ref(ep.Key)).ToList();
                link = candidates.Count > 0
                    ? new(e, candidates, "route", "same method and normalised route; base URL is unbound, so the service is a candidate")
                    : new(e, [], "none", "no endpoint with this method and route in the selected revisions");
            }
            return link with
            {
                Base = e.To is null ? "dynamic" : Architecture.BaseOf(e.To.Key),
                Group = e.To is null ? "Dynamic" : Queries.Vendor(e.To.Key, e.From.Key),
                SourcePath = o.SourcePath,
            };
        }).OrderBy(l => l.Group, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.SourcePath, StringComparer.Ordinal)
            .ThenBy(l => l.Base, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Request.RepositoryId, StringComparer.Ordinal)
            .ThenBy(l => l.Request.From.Name, StringComparer.Ordinal).ToList();
        ResourceKey Resource(string key, bool columns = false) => new(key,
            !key.Contains("@unbound:", StringComparison.Ordinal),
            edges.Where(e => e.Relation != "DECLARES" && (e.To?.Key == key ||
                columns && e.To?.Key.StartsWith(key + "#", StringComparison.Ordinal) == true))
                .OrderBy(e => e.Relation, StringComparer.Ordinal).ToList(), byKey.GetValueOrDefault(key));
        var channelKeys = edges.Where(e => e.To?.Key.StartsWith("chan|", StringComparison.Ordinal) == true).Select(e => e.To!.Key)
            .Concat(nodes.Where(n => n.Key.StartsWith("chan|", StringComparison.Ordinal)).Select(n => n.Key)).Distinct(StringComparer.Ordinal);
        var channels = channelKeys.GroupBy(k => Entity(Tail(k)), StringComparer.Ordinal).Select(g => new ResourceGroup(
            g.Key, "channel", g.Select(k => Resource(k)).ToList(),
            g.Count() > 1 ? "Same name, different keys: not proven to be the same channel (namespace or entity type differs, or is unbound)." : null)).ToList();
        var dataKeys = edges.Where(e => e.To?.Key.StartsWith("sql|", StringComparison.Ordinal) == true && Relations.Contains(e.Relation))
            .Select(e => e.To!.Key).Concat(nodes.Where(n => n.Kind is "Table" or "Routine").Select(n => n.Key))
            .Select(Table).Distinct(StringComparer.Ordinal);
        var data = dataKeys.GroupBy(Tail, StringComparer.Ordinal).Select(g => new ResourceGroup(g.Key, "data",
            g.Select(k => Resource(k, true)).ToList(),
            g.Count() > 1 ? "Same object name under different store bindings: the same table only once a binding says so." : null))
            .OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
        return new(services, http, channels, data, marks);
    }

    private static string Tail(string key) => key[(key.LastIndexOf('|') + 1)..];
    private static string Entity(string name) => name.Contains('/', StringComparison.Ordinal) ? name[(name.IndexOf('/', StringComparison.Ordinal) + 1)..] : name;
    private static string Table(string key) => key.Contains('#', StringComparison.Ordinal) ? key[..key.IndexOf('#', StringComparison.Ordinal)] : key;
}
