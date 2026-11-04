using System.Globalization;

namespace CapabilityExplorer.Explorer;

public sealed record ArchitectureBudget(int NodeBudget, int TotalNodes, int ShownNodes, int TotalEdges, int ShownEdges, bool Truncated,
    int EdgeBudget, int MatchedNodes, int MatchedEdges, bool NodesTruncated, bool EdgesTruncated, IReadOnlyList<string> Pinned, IReadOnlyList<string> MissingPins);
public sealed record ArchitectureFilters(IReadOnlyList<string> HideKinds, string? Query, IReadOnlyList<string> Include);
public sealed record ArchitectureKindFacet(string Value, int Total, int Matched);
public sealed record ArchitectureFacets(IReadOnlyList<ArchitectureKindFacet> Kinds);
public sealed record ArchitectureOmittedNode(string Id, string Label, string Category, int Degree);
public sealed record ArchitectureOmitted(IReadOnlyList<ArchitectureOmittedNode> Nodes, int NodeCount, int Listed);

internal sealed record ArchitectureRequest(int NodeBudget, int EdgeBudget, IReadOnlyList<string> HideKinds, string? Query, IReadOnlyList<string> Include);

internal static class ArchitectureBudgeting
{
    internal const int MaxNodes = 400;
    internal const int MaxEdges = 800;

    internal static bool TryParse(IQueryCollection parameters, out ArchitectureRequest request, out string? error)
    {
        request = new(MaxNodes, MaxEdges, [], null, []);
        error = null;
        if (!Budget(parameters, "nodeBudget", MaxNodes, out var nodes)
            || !Budget(parameters, "edgeBudget", MaxEdges, out var edges))
            error = "Budgets must be single integers.";
        else if (parameters["q"].Count > 1 || parameters["q"].ToString().Length > 200)
            error = "q must be a single value of at most 200 characters.";
        else if (parameters["hideKind"].Count > 32 || parameters["hideKind"].Any(value =>
                     string.IsNullOrEmpty(value) || value.Any(c => c is not (>= 'a' and <= 'z') and not '-')))
            error = "hideKind accepts at most 32 nonempty [a-z-]+ values.";
        else if (parameters["include"].Count > 20 || parameters["include"].Any(value => string.IsNullOrEmpty(value) || value.Length > 4096))
            error = "include accepts at most 20 nonempty node IDs of at most 4096 characters.";
        else
        {
            var include = Normalize(parameters["include"].Select(value => value!));
            if (include.Count > nodes)
                error = "Requested pins exceed nodeBudget.";
            else
            {
                var query = parameters["q"].ToString().Trim();
                request = new(nodes, edges, Normalize(parameters["hideKind"].Select(value => value!)), query.Length == 0 ? null : query, include);
            }
        }
        return error is null;
    }

    private static bool Budget(IQueryCollection parameters, string name, int maximum, out int budget)
    {
        budget = maximum;
        if (!parameters.TryGetValue(name, out var values)) return true;
        if (values.Count != 1 || !int.TryParse(values[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            return false;
        budget = Math.Clamp(parsed, 1, maximum);
        return true;
    }

    private static List<string> Normalize(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    internal static ArchitectureGraph Apply(ArchitectureGraph graph, int? nodeBudget = null, int? edgeBudget = null,
        IReadOnlyList<string>? hideKinds = null, string? query = null, IReadOnlyList<string>? include = null)
    {
        var nodes = Math.Clamp(nodeBudget ?? MaxNodes, 1, MaxNodes);
        var edges = Math.Clamp(edgeBudget ?? MaxEdges, 1, MaxEdges);
        var filters = new ArchitectureFilters(Normalize(hideKinds ?? []),
            string.IsNullOrWhiteSpace(query) ? null : query.Trim(), Normalize(include ?? []));
        if (filters.Include.Count > nodes) throw new ArgumentException("Requested pins exceed nodeBudget.", nameof(include));
        var ids = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var pins = filters.Include.Where(ids.Contains).ToHashSet(StringComparer.Ordinal);
        if (graph.Focus is { } focus)
            pins.UnionWith(graph.Nodes.Where(n => n.Id == focus || n.SymbolKey == focus || n.Id == $"repo:{focus}").Select(n => n.Id));
        if (pins.Count > nodes) throw new ArgumentException("Focus and requested pins exceed nodeBudget.", nameof(include));
        var matched = graph.Nodes.Where(n => pins.Contains(n.Id) || Matches(n, filters.Query)).ToList();
        var matchedIds = matched.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var hidden = filters.HideKinds.ToHashSet(StringComparer.Ordinal);
        var matchedEdges = graph.Edges.Where(e => !hidden.Contains(e.Kind) && matchedIds.Contains(e.From) && matchedIds.Contains(e.To)).ToList();
        var degree = matchedEdges.SelectMany(e => new[] { e.From, e.To }).GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var neighbours = matchedEdges.Where(e => pins.Contains(e.From) || pins.Contains(e.To))
            .SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.Ordinal);
        var ranked = matched.OrderByDescending(n => pins.Contains(n.Id)).ThenByDescending(n => neighbours.Contains(n.Id))
            .ThenByDescending(n => degree.GetValueOrDefault(n.Id)).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        var shown = ranked.Take(nodes).ToList();
        var keep = shown.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var shownEdges = matchedEdges.Where(e => keep.Contains(e.From) && keep.Contains(e.To))
            .OrderByDescending(e => pins.Contains(e.From) || pins.Contains(e.To)).ThenByDescending(e => e.Count)
            .ThenBy(e => e.Id, StringComparer.Ordinal).Take(edges).ToList();
        var nodesTruncated = shown.Count < matched.Count;
        var edgesTruncated = shownEdges.Count < matchedEdges.Count;
        var omitted = ranked.Skip(nodes).Select(n => new ArchitectureOmittedNode(n.Id, n.Label, n.Category, degree.GetValueOrDefault(n.Id))).Take(200).ToList();
        var matchedEdgeIds = matchedEdges.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return graph with
        {
            Nodes = shown,
            Edges = shownEdges,
            Budget = new(nodes, graph.Nodes.Count, shown.Count, graph.Edges.Count, shownEdges.Count, nodesTruncated || edgesTruncated,
                edges, matched.Count, matchedEdges.Count, nodesTruncated, edgesTruncated, Normalize(pins), filters.Include.Where(id => !ids.Contains(id)).ToList()),
            Filters = filters,
            Facets = new(graph.Edges.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ArchitectureKindFacet(g.Key, g.Count(), g.Count(e => matchedEdgeIds.Contains(e.Id)))).ToList()),
            Omitted = new(omitted, matched.Count - shown.Count, omitted.Count),
            Notes = nodesTruncated || edgesTruncated
                ? graph.Notes.Concat([$"Showing {shown.Count} of {matched.Count} matched nodes ({graph.Nodes.Count} total) and {shownEdges.Count} of {matchedEdges.Count} matched edges ({graph.Edges.Count} total); budgets {nodes} nodes / {edges} edges. Filter or drill down to inspect omitted items."]).ToList()
                : graph.Notes,
        };
    }

    private static bool Matches(ArchNode node, string? query) => query is null
        || new[] { node.Id, node.Label, node.Category, node.Sublabel, node.Group, node.SymbolKey, node.RepositoryId }
            .Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
}
