using System.Security.Cryptography;
using System.Text.Json;

namespace CapabilityExplorer.Explorer;

/// <summary>Bounded, occurrence-aware navigation over selected facts; never inferred value propagation.</summary>
public static partial class GlossaryFlow
{
    public static GlossaryFlowResult Build(ViewIndex view, IReadOnlyList<string> seedOccurrenceIds,
        GlossaryFlowOptions options, IReadOnlyList<GlossaryFlowHumanAssertion>? humanAssertions = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(seedOccurrenceIds);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Direction is not ("forward" or "backward" or "both") ||
            options.MaxHops is < 0 or > 8 || options.MaxNodes is < 1 or > 500 || options.MaxEdges is < 1 or > 1000)
        {
            throw new ArgumentException("Direction must be forward, backward or both; limits are 0–8 hops, 1–500 nodes and 1–1000 edges.", nameof(options));
        }
        if (seedOccurrenceIds.Count is < 1 or > 500 || seedOccurrenceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Select between 1 and 500 nonempty occurrence IDs.", nameof(seedOccurrenceIds));
        }
        var builder = new Builder(view);
        builder.AddSourceEdges();
        builder.AddCalculations();
        builder.AddHumanAssertions(humanAssertions ?? []);
        return builder.Traverse(seedOccurrenceIds, options);
    }

    private static string Identity(string prefix, params string[] parts) =>
        prefix + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parts)));

    private sealed partial class Builder
    {
        private readonly ViewIndex view;
        private readonly Dictionary<string, ArchNode> canvas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GlossaryFlowNode> nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GlossaryFlowEdge> edges = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> occurrenceNodes = new(StringComparer.Ordinal);
        private readonly List<GlossaryFlowReason> globalReasons = [];
        private readonly Dictionary<string, List<GlossaryFlowReason>> edgeReasons = new(StringComparer.Ordinal);

        public GlossaryFlowResult Traverse(IReadOnlyList<string> seedIds, GlossaryFlowOptions options)
        {
            var reasons = new List<GlossaryFlowReason>(globalReasons);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var selectedEdges = new HashSet<string>(StringComparer.Ordinal);
            var seeds = new List<GlossaryFlowSeed>();
            var queue = new Queue<(string Id, int Depth)>();
            var truncated = false;
            foreach (var occurrenceId in seedIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!occurrenceNodes.TryGetValue(occurrenceId, out var id))
                {
                    reasons.Add(new("unknown-seed", $"Occurrence {occurrenceId} is absent from the selected revisions."));
                    continue;
                }
                if (!selected.Contains(id) && selected.Count >= options.MaxNodes)
                {
                    Limit("node-limit", "The node limit excluded a selected seed.");
                    continue;
                }
                seeds.Add(new(occurrenceId, id));
                if (selected.Add(id)) queue.Enqueue((id, 0));
            }
            var adjacent = edges.Values.SelectMany(e => new[] { (Node: e.From, Edge: e), (Node: e.To, Edge: e) })
                .ToLookup(x => x.Node, x => x.Edge, StringComparer.Ordinal);
            while (queue.TryDequeue(out var current))
            {
                foreach (var edge in adjacent[current.Id].DistinctBy(e => e.Id).OrderBy(e => e.Id, StringComparer.Ordinal))
                {
                    var association = edge.Category == "human-assertion" && edge.Relation is "same" or "alias";
                    if (!association && options.Direction != "both" &&
                        (options.Direction == "forward" ? edge.From != current.Id : edge.To != current.Id)) continue;
                    var other = edge.From == current.Id ? edge.To : edge.From;
                    if (!selected.Contains(other) && current.Depth >= options.MaxHops)
                    {
                        Limit("hop-limit", "The hop limit stopped navigation at a remaining connection.");
                        continue;
                    }
                    if (!selected.Contains(other) && selected.Count >= options.MaxNodes)
                    {
                        Limit("node-limit", "The node limit stopped navigation at a remaining connection.");
                        continue;
                    }
                    if (!selectedEdges.Contains(edge.Id) && selectedEdges.Count >= options.MaxEdges)
                    {
                        Limit("edge-limit", "The edge limit stopped navigation at a remaining connection.");
                        continue;
                    }
                    selectedEdges.Add(edge.Id);
                    if (selected.Add(other)) queue.Enqueue((other, current.Depth + 1));
                }
            }
            var projectedEdges = selectedEdges.Order(StringComparer.Ordinal).Select(id => edges[id]).ToList();
            foreach (var edge in projectedEdges)
            {
                if (edgeReasons.TryGetValue(edge.Id, out var gaps)) reasons.AddRange(gaps);
            }
            if (projectedEdges.Count == 0)
                reasons.Add(new("no-connections", "No navigable facts or reviewed associations were found within these limits for the selected occurrences."));
            reasons.Add(new("unsupported-field-mapping", "These relations provide source and binding context. Field-level value propagation is not established by this projection."));
            reasons = reasons.Distinct().OrderBy(r => r.Code, StringComparer.Ordinal)
                .ThenBy(r => r.NodeId, StringComparer.Ordinal).ThenBy(r => r.EdgeId, StringComparer.Ordinal)
                .ThenBy(r => r.Message, StringComparer.Ordinal).ToList();
            var complete = !reasons.Any(r => r.Code is not ("unsupported-field-mapping" or "no-connections"));
            var graph = new ArchitectureGraph("glossary-flow", null, "Glossary occurrence connections", [],
                selected.Order(StringComparer.Ordinal).Select(id => canvas[id]).ToList(), projectedEdges.Select(CanvasEdge).ToList(),
                ["Arrows follow the recorded source relation, not runtime value movement.",
                 "Calls, reads, writes, channels and service bindings are navigation context; they do not prove field-value equality.",
                 "Human same/alias edges are reviewed associations; derived_from points from occurrence to the term that derives from it.",
                 .. reasons.Select(r => r.Message).Distinct(StringComparer.Ordinal)]);
            var selection = view.Artifacts.OrderBy(a => a.RepositoryId, StringComparer.Ordinal)
                .ThenBy(a => a.CommitSha, StringComparer.Ordinal).ThenBy(a => a.Artifact.Header.Unit.Path, StringComparer.Ordinal)
                .Select(a => new GlossaryFlowSelection(a.RepositoryId, a.CommitSha, a.Artifact.Header.Unit.Name,
                    a.Artifact.Header.Unit.Path, a.Artifact.Complete.Status, a.Artifact.Complete.Coverage.FilesTotal,
                    a.Artifact.Complete.Coverage.FilesAnalyzed, a.Artifact.Complete.Coverage.UnresolvedEdges)).ToList();
            return new(graph, selection, seeds, selected.Order(StringComparer.Ordinal).Select(id => nodes[id]).ToList(),
                projectedEdges, new(complete, truncated, false, reasons));

            void Limit(string code, string message)
            {
                truncated = true;
                reasons.Add(new(code, message));
            }
        }

        private ArchEdge CanvasEdge(GlossaryFlowEdge edge)
        {
            var certainty = edge.Category switch
            {
                "human-assertion" => "human",
                "service-binding" => edge.Resolution == "resolved" ? "bound" : "broken",
                _ => edge.Resolution is "resolved" or "static-target" ? "static" : "unresolved",
            };
            var label = edge.Category == "human-assertion" ? $"human {edge.Relation} association" : $"{edge.Relation} context";
            return new(edge.Id, edge.From, edge.To, edge.Category, label, 1, certainty,
                edge.Category == "human-assertion" ? [] : [new(edge.Relation, Ref(edge.From), Ref(edge.To),
                    edge.Resolution, edge.UnresolvedReason, edge.Derivation, edge.Rule, edge.Evidence)]);
        }

        private SymbolRef Ref(string id)
        {
            var node = nodes[id];
            return new(node.SymbolKey ?? id, canvas[id].Label, canvas[id].Category,
                !canvas[id].Badges.Contains("boundary", StringComparer.Ordinal), node.RepositoryId);
        }
    }
}
