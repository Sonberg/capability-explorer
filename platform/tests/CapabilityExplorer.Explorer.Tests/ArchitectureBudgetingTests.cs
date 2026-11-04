using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ArchitectureBudgetingTests
{
    private static ArchNode Node(string id, string? symbol = null) =>
        new(id, "module", id, null, null, symbol, "repo", null, []);

    private static ArchEdge Edge(string id, string from, string to, int count = 1, string kind = "call") =>
        new(id, from, to, kind, id, count, "static", []);

    private static ArchitectureGraph Graph(IReadOnlyList<ArchNode> nodes, IReadOnlyList<ArchEdge> edges, string? focus = null) =>
        new("component", focus, "Synthetic", [], nodes, edges, ["original evidence note"]);

    [Theory]
    [InlineData("focus", "focus", null)]
    [InlineData("symbol", "focus", "symbol")]
    [InlineData("repo", "repo:repo", null)]
    public void Focus_survives_budget_one_even_when_query_excludes_it(string focus, string id, string? symbol)
    {
        var source = Graph([Node("a"), Node("b"), Node(id, symbol)], [Edge("ab", "a", "b")], focus);
        var g = ArchitectureBudgeting.Apply(source, 1, query: "not-found");
        Assert.Equal(id, Assert.Single(g.Nodes).Id);
        Assert.Equal(id, Assert.Single(g.Budget!.Pinned));
        Assert.Equal(1, g.Budget.MatchedNodes);
        Assert.False(g.Budget.Truncated);
    }

    [Fact]
    public void Pins_then_neighbours_then_matched_degree_then_ordinal_id_choose_nodes()
    {
        var source = Graph([Node("pin"), Node("neighbour"), Node("a"), Node("b"), Node("c")],
            [Edge("pn", "pin", "neighbour"), Edge("ab", "a", "b"), Edge("ac", "a", "c"), Edge("bc", "b", "c")]);
        var g = ArchitectureBudgeting.Apply(source, 3, include: ["pin"]);
        Assert.Equal(["pin", "neighbour", "a"], g.Nodes.Select(n => n.Id));
        Assert.Equal(["b", "c"], g.Omitted!.Nodes.Select(n => n.Id));
        Assert.Equal(2, g.Omitted.NodeCount);
        Assert.All(g.Omitted.Nodes, n => Assert.Equal(2, n.Degree));
        var reversed = ArchitectureBudgeting.Apply(source with { Nodes = source.Nodes.Reverse().ToList(), Edges = source.Edges.Reverse().ToList() }, 3, include: ["pin"]);
        Assert.Equal(g.Nodes, reversed.Nodes);
        Assert.Equal(g.Edges, reversed.Edges);
    }

    [Fact]
    public void Dense_graph_caps_nodes_and_induced_edges_before_layout_with_bounded_recovery()
    {
        var source = Graph(Enumerable.Range(0, 650).Select(i => Node($"n{i:D4}")).ToList(),
            Enumerable.Range(0, 650).SelectMany(i => Enumerable.Range(1, 24)
                .Select(j => Edge($"{i:D4}-{j:D2}", $"n{i:D4}", $"n{(i + j) % 650:D4}", j))).ToList());
        Assert.True(source.Edges.Count > 8000);
        var g = ArchitectureBudgeting.Apply(source, 2000, 8000);
        var budget = g.Budget!;
        Assert.Equal(400, g.Nodes.Count);
        Assert.Equal(800, g.Edges.Count);
        Assert.Equal(650, budget.TotalNodes);
        Assert.Equal(650, budget.MatchedNodes);
        Assert.Equal(15600, budget.TotalEdges);
        Assert.Equal(15600, budget.MatchedEdges);
        Assert.True(budget.NodesTruncated && budget.EdgesTruncated && budget.Truncated);
        Assert.Equal(250, g.Omitted!.NodeCount);
        Assert.Equal(200, g.Omitted.Listed);
        Assert.Equal(200, g.Omitted.Nodes.Count);
        Assert.All(g.Edges, e =>
        {
            Assert.Contains(g.Nodes, n => n.Id == e.From);
            Assert.Contains(g.Nodes, n => n.Id == e.To);
            Assert.Contains(e, source.Edges);
        });
        Assert.Equal("original evidence note", g.Notes[0]);
    }

    [Fact]
    public void Edge_cap_applies_even_without_node_truncation_and_pin_edges_precede_weight()
    {
        var source = Graph([Node("pin"), Node("a"), Node("b")],
            [Edge("high", "a", "b", 100), Edge("z-pin", "pin", "a", 1), Edge("a-pin", "pin", "b", 1)]);
        var g = ArchitectureBudgeting.Apply(source, edgeBudget: 2, include: ["pin"]);
        Assert.Equal(["a-pin", "z-pin"], g.Edges.Select(e => e.Id));
        Assert.False(g.Budget!.NodesTruncated);
        Assert.True(g.Budget.EdgesTruncated);
        Assert.Equal(3, g.Budget.MatchedEdges);
        Assert.Empty(g.Omitted!.Nodes);
        Assert.Equal("high", Assert.Single(ArchitectureBudgeting.Apply(source, edgeBudget: 1).Edges).Id);
    }

    [Fact]
    public void Text_matching_union_pins_and_hidden_edges_define_matched_counts_and_facets()
    {
        var source = Graph([Node("alpha"), Node("beta"), Node("extra")],
            [Edge("ab", "alpha", "beta"), Edge("ae", "alpha", "extra", kind: "data"), Edge("be", "beta", "extra")]);
        var g = ArchitectureBudgeting.Apply(source, hideKinds: ["data"], query: "ALPHA", include: ["extra", "missing"]);
        Assert.Equal(["alpha", "extra"], g.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, g.Budget!.MatchedNodes);
        Assert.Equal(0, g.Budget.MatchedEdges);
        Assert.Equal(3, g.Budget.TotalNodes);
        Assert.Equal(3, g.Budget.TotalEdges);
        Assert.Equal("extra", Assert.Single(g.Budget.Pinned));
        Assert.Equal("missing", Assert.Single(g.Budget.MissingPins));
        Assert.All(g.Facets!.Kinds, kind => Assert.Equal(0, kind.Matched));
        Assert.False(g.Budget.Truncated);
    }

    [Fact]
    public void Requested_pins_and_automatic_focus_cannot_exceed_budget()
    {
        var source = Graph([Node("a"), Node("b")], [], "a");
        Assert.Throws<ArgumentException>(() => ArchitectureBudgeting.Apply(source, 1, include: ["b"]));
        Assert.Throws<ArgumentException>(() => ArchitectureBudgeting.Apply(source, 1, include: ["missing-a", "missing-b"]));
    }

    [Fact]
    public void Ranking_uses_only_nonhidden_edges_between_matching_nodes()
    {
        var source = Graph([Node("match-a"), Node("match-b"), Node("match-c"), Node("outside")],
            [Edge("a-out-1", "match-a", "outside"), Edge("a-out-2", "outside", "match-a"),
                Edge("bc", "match-b", "match-c", kind: "data")]);
        var matched = ArchitectureBudgeting.Apply(source, 1, query: "match-");
        Assert.Equal("match-b", Assert.Single(matched.Nodes).Id);
        Assert.Equal(1, matched.Budget!.MatchedEdges);
        var hidden = ArchitectureBudgeting.Apply(source, 1, query: "match-", hideKinds: ["data"]);
        Assert.Equal("match-a", Assert.Single(hidden.Nodes).Id);
        Assert.Equal(0, hidden.Budget!.MatchedEdges);
    }

    [Fact]
    public void Query_boundary_and_repeated_filter_limits_accept_exact_maximum_and_normalize()
    {
        var parameters = new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["q"] = new string('x', 200),
            ["hideKind"] = Enumerable.Repeat("data", 32).ToArray(),
            ["include"] = Enumerable.Range(0, 20).Select(i => $"{i:D2}{new string('x', 4094)}").ToArray(),
            ["nodeBudget"] = "2147483647",
            ["edgeBudget"] = "-2147483648",
        });
        Assert.True(ArchitectureBudgeting.TryParse(parameters, out var options, out var error));
        Assert.Null(error);
        Assert.Equal(400, options.NodeBudget);
        Assert.Equal(1, options.EdgeBudget);
        Assert.Equal("data", Assert.Single(options.HideKinds));
        Assert.Equal(20, options.Include.Count);
        Assert.Equal(200, options.Query!.Length);
    }
}
