using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using Microsoft.Extensions.AI;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class ChatToolRetrievalTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LaterRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Start = "cs|client|Chat|M:Chat.Start";
    private const string Endpoint = "http|service/Chat|POST /orders";
    private const string Handler = "cs|service|Chat|M:Chat.Handle";
    private const string Charge = "cs|service|Chat|M:Chat.Charge";
    private const string Vendor = "http|@unbound:service/Chat:base=payments.example|POST /charges";

    [Fact]
    public async Task Given_a_route_candidate_when_finding_a_path_then_citations_share_focus_numbers_and_keep_uncertainty()
    {
        using var graph = FlowGraph();
        var context = new ChatContext(graph.Index.View(null));
        context.AddSymbol(ChatContext.UpfrontLimit, Start, "focus");

        var result = await Call(context, "find_path", ("from", "Start"), ("to", "Charge"));

        var path = Assert.Single(context.Sources, s => s.Kind == "Path");
        Assert.Equal((2, "path", "path:Start:Charge"), (path.N, path.Found, path.Key));
        Assert.StartsWith("[2] Chain", result, StringComparison.Ordinal);
        Assert.Contains("(3 hops)", result, StringComparison.Ordinal);
        Assert.Contains("route match only: a candidate", result, StringComparison.Ordinal);
        Assert.Contains("<-HANDLES-", result, StringComparison.Ordinal);
        Assert.Contains("Start (client)", result, StringComparison.Ordinal);
        Assert.Contains("Charge (service)", result, StringComparison.Ordinal);
        Assert.Equal(new[] { Start, "path:Start:Charge", Endpoint, Handler, Charge }, context.Sources.Select(s => s.Key));
        Assert.Equal("focus", context.Sources.Single(s => s.Key == Start).Found);
        Assert.All(context.Sources.Where(s => s.Key is Endpoint or Handler or Charge), s => Assert.Equal("path", s.Found));
        var sources = context.Sources.ToArray();

        Assert.Equal(result, await Call(context, "find_path", ("from", "Start"), ("to", "Charge")));
        Assert.Equal(sources, context.Sources);
        Assert.Equal(Enumerable.Range(1, sources.Length), sources.Select(s => s.N));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_an_entry_and_vendor_call_when_retrieving_a_flow_then_only_route_matches_are_labelled_candidates(bool bound)
    {
        using var graph = FlowGraph(bound);
        var context = new ChatContext(graph.Index.View(null));
        context.AddIntegration(ChatContext.UpfrontLimit, "Payments");
        context.AddFlow(ChatContext.UpfrontLimit, "Payments");

        var flow = Assert.Single(context.Sources, s => s.Kind == "Flow");
        Assert.Equal(("flow:payments", "flow"), (flow.Key, flow.Found));
        Assert.Contains("Start (client)", flow.Text, StringComparison.Ordinal);
        Assert.Contains("Handle (service)", flow.Text, StringComparison.Ordinal);
        Assert.Contains("Charge (service)", flow.Text, StringComparison.Ordinal);
        Assert.Contains("-REQUESTS POST /charges-> Payments", flow.Text, StringComparison.Ordinal);
        Assert.Equal(!bound, flow.Text.Contains("route match only: a candidate", StringComparison.Ordinal));
        var count = context.Sources.Count;

        context.AddFlow(ChatContext.UpfrontLimit, "Payments");
        Assert.Equal(count, context.Sources.Count);
        Assert.Equal(flow, Assert.Single(context.Sources, s => s.Kind == "Flow"));
    }

    [Fact]
    public void Given_missing_vendor_or_disconnected_entry_when_retrieving_a_flow_then_no_flow_is_invented()
    {
        using var graph = FlowGraph(connected: false);
        var context = new ChatContext(graph.Index.View(null));

        context.AddFlow(ChatContext.UpfrontLimit, "Payments");
        context.AddFlow(ChatContext.UpfrontLimit, "AbsentVendor");

        Assert.Empty(context.Sources);
    }

    [Fact]
    public async Task Given_a_missing_or_disconnected_symbol_when_tools_look_it_up_then_the_reason_is_explicit()
    {
        using var graph = FlowGraph(connected: false);
        var context = new ChatContext(graph.Index.View(null));

        Assert.Equal("nothing in the analysed code matches Missing",
            await Call(context, "find_path", ("from", "Missing"), ("to", "Charge")));
        Assert.Equal("nothing in the analysed code matches Missing",
            await Call(context, "find_path", ("from", "Start"), ("to", "Missing")));
        Assert.Contains("no chain of up to 6 relations", await Call(context, "find_path", ("from", "Start"), ("to", "Charge")), StringComparison.Ordinal);
        Assert.Equal("no such symbol; try search_symbols", await Call(context, "get_symbol", ("symbol", "Missing")));
        Assert.Equal("no such symbol (or item limit reached); try search_symbols",
            await Call(context, "get_connected_code", ("symbol", "Missing")));
        Assert.Empty(context.Sources);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public async Task Given_a_long_chain_when_finding_a_path_then_the_six_hop_boundary_is_respected(int hops, bool found)
    {
        var nodes = Enumerable.Range(0, 8).Select(i => Node(Key(i), $"Step{i}")).ToArray();
        var edges = Enumerable.Range(0, 7).Select(i => Edge(Key(i), Key(i + 1))).ToArray();
        using var graph = new SyntheticGraph(Rows("client", Revision, nodes, edges));
        var context = new ChatContext(graph.Index.View(null));

        var result = await Call(context, "find_path", ("from", "Step0"), ("to", $"Step{hops}"));

        if (found)
        {
            Assert.StartsWith("[1] Chain", result, StringComparison.Ordinal);
            Assert.Contains($"({hops} hops)", result, StringComparison.Ordinal);
            Assert.Contains($"Step{hops} (client)", result, StringComparison.Ordinal);
            Assert.Equal("path", Assert.Single(context.Sources, s => s.Kind == "Path").Found);
        }
        else
        {
            Assert.Contains("(not observed)", result, StringComparison.Ordinal);
            Assert.Empty(context.Sources);
        }
    }

    [Theory]
    [InlineData(-5, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(99, true)]
    public async Task Given_cycles_and_callers_when_retrieving_connected_code_then_depth_is_clamped_and_citations_are_unique(int depth, bool secondLevel)
    {
        var nodes = Enumerable.Range(0, 5).Select(i => Node(Key(i), $"Step{i}")).Append(Node(Key(5), "Container", "Type")).ToArray();
        var edges = new[]
        {
            Edge(Key(0), Key(1)), Edge(Key(1), Key(2)), Edge(Key(2), Key(3)),
            Edge(Key(4), Key(0)), Edge(Key(1), Key(0)), Edge(Key(5), Key(0), "DECLARES"),
            Edge(Key(0), "cs|@ext|Library|M:Library.External"),
        };
        using var graph = new SyntheticGraph(Rows("client", Revision, nodes, edges));
        var context = new ChatContext(graph.Index.View(null));
        context.AddSymbol(ChatContext.UpfrontLimit, Key(0), "focus");

        var result = await Call(context, "get_connected_code", ("symbol", "sTeP0"), ("depth", depth));

        Assert.Contains("[1] Method Step0", result, StringComparison.Ordinal);
        Assert.Contains("(this -CALLS-> it)", result, StringComparison.Ordinal);
        Assert.Contains("(it -CALLS-> this)", result, StringComparison.Ordinal);
        Assert.Equal("focus", context.Sources[0].Found);
        Assert.Equal(new[] { Key(1), Key(4) }.Concat(secondLevel ? [Key(2)] : []).Order(StringComparer.Ordinal),
            context.Sources.Skip(1).Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.All(context.Sources.Skip(1), s => Assert.Equal(("connected", "client"), (s.Found, s.Repository)));
        Assert.Equal(context.Sources.Count, context.Sources.Select(s => s.N).Distinct().Count());
        Assert.DoesNotContain(context.Sources, s => s.Key == Key(3) || s.Key == Key(5) || s.Key.Contains("@ext", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Given_an_isolated_symbol_when_retrieving_connected_code_then_its_citation_survives_without_invented_neighbours()
    {
        using var graph = new SyntheticGraph(Rows("client", Revision, [Node(Key(0), "Alone")], []));
        var context = new ChatContext(graph.Index.View(null));

        var result = await Call(context, "get_connected_code", ("symbol", Key(0)));

        Assert.StartsWith("[1] Method Alone", result, StringComparison.Ordinal);
        Assert.Contains("no connected code in the analysed revisions", result, StringComparison.Ordinal);
        Assert.Equal(("tool", Key(0)), (Assert.Single(context.Sources).Found, context.Sources[0].Key));
    }

    [Fact]
    public async Task Given_many_neighbours_when_retrieving_connected_code_then_only_eight_are_numbered()
    {
        var nodes = Enumerable.Range(0, 13).Select(i => Node(Key(i), $"Step{i}")).ToArray();
        var edges = Enumerable.Range(1, 12).Select(i => Edge(Key(0), Key(i))).ToArray();
        using var graph = new SyntheticGraph(Rows("client", Revision, nodes, edges));
        var context = new ChatContext(graph.Index.View(null));

        var result = await Call(context, "get_connected_code", ("symbol", Key(0)), ("depth", 2));

        Assert.Equal(9, context.Sources.Count);
        Assert.Equal(8, context.Sources.Count(s => s.Found == "connected"));
        Assert.All(context.Sources, s => Assert.Contains($"[{s.N}]", result, StringComparison.Ordinal));
        Assert.DoesNotContain(context.Sources, s => s.Key == Key(9));
    }

    [Fact]
    public async Task Given_a_nearly_full_context_when_tools_retrieve_then_existing_numbers_survive_and_new_items_stop_at_forty()
    {
        var nodes = Enumerable.Range(0, 42).Select(i => Node(Key(i), $"Step{i}")).ToArray();
        using var graph = new SyntheticGraph(Rows("client", Revision, nodes, [Edge(Key(0), Key(40)), Edge(Key(0), Key(41))]));
        var context = new ChatContext(graph.Index.View(null));
        for (var i = 0; i < 39; i++) context.AddSymbol(ChatContext.TotalLimit, Key(i), "name");
        var before = context.Sources.ToArray();

        var result = await Call(context, "get_connected_code", ("symbol", Key(0)));

        Assert.Equal(40, context.Sources.Count);
        Assert.Equal(before, context.Sources.Take(39));
        Assert.Contains("[40] (this -CALLS-> it)", result, StringComparison.Ordinal);
        Assert.Equal("connected", context.Sources[^1].Found);
        Assert.Equal("item limit reached; answer from what you have", await Call(context, "get_symbol", ("symbol", Key(41))));
        Assert.DoesNotContain(context.Sources, s => s.Key == Key(41));
    }

    [Fact]
    public async Task Given_other_repositories_and_revisions_when_retrieving_then_only_the_selected_view_contributes_items()
    {
        using var graph = new SyntheticGraph(
            Rows("client", Revision, [Node(Start, "Selected")], [Edge(Start, Charge)]),
            Rows("client", LaterRevision, [Node(Start, "OtherRevision")], []),
            Rows("service", Revision, [Node(Charge, "OutsideScope")], []));
        var scope = new ViewScope("client-only", new HashSet<string>(StringComparer.Ordinal) { "client" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["client"] = Revision });
        var view = graph.Index.View(null, scope);
        var context = new ChatContext(view);

        var result = await Call(context, "get_connected_code", ("symbol", Start));

        Assert.Contains("Method Selected", result, StringComparison.Ordinal);
        Assert.Equal("Selected", Assert.Single(context.Sources).Name);
        Assert.Equal("no such symbol; try search_symbols", await Call(context, "get_symbol", ("symbol", Charge)));
        Assert.Equal("nothing in the analysed code matches OutsideScope",
            await Call(context, "find_path", ("from", "Selected"), ("to", "OutsideScope")));
        Assert.Single(context.Sources);
    }

    [Fact]
    public async Task Given_a_flow_outside_capability_scope_when_retrieving_then_neither_flow_nor_path_widens_the_scope()
    {
        using var graph = FlowGraph();
        var scope = new ViewScope("client-only", new HashSet<string>(StringComparer.Ordinal) { "client" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        var context = new ChatContext(graph.Index.View($"service:{Revision}", scope));

        context.AddFlow(ChatContext.UpfrontLimit, "Payments");
        var result = await Call(context, "find_path", ("from", "Start"), ("to", "Charge"));

        Assert.Equal("nothing in the analysed code matches Charge", result);
        Assert.Empty(context.Sources);
    }

    [Fact]
    public async Task Given_only_declarations_and_an_external_bridge_when_finding_a_path_then_neither_establishes_a_chain()
    {
        const string external = "cs|@ext|Library|M:Library.Shared";
        using var graph = new SyntheticGraph(Rows("client", Revision,
            [Node(Key(0), "Left"), Node(Key(1), "Right"), Node(Key(2), "Container", "Type")],
            [Edge(Key(2), Key(0), "DECLARES"), Edge(Key(2), Key(1), "DECLARES"), Edge(Key(0), external), Edge(Key(1), external)]));
        var context = new ChatContext(graph.Index.View(null));

        var result = await Call(context, "find_path", ("from", "Left"), ("to", "Right"));

        Assert.Contains("(not observed)", result, StringComparison.Ordinal);
        Assert.Empty(context.Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_equal_hop_chains_when_artifact_order_changes_then_symbol_and_relation_ties_are_stable(bool incoming)
    {
        JsonObject[] edges = incoming
            ? [Edge(Key(2), Key(0)), Edge(Key(3), Key(2)), Edge(Key(1), Key(0), "REFERENCES"), Edge(Key(1), Key(0)), Edge(Key(3), Key(1))]
            : [Edge(Key(0), Key(2)), Edge(Key(2), Key(3)), Edge(Key(0), Key(1), "REFERENCES"), Edge(Key(0), Key(1)), Edge(Key(1), Key(3))];
        foreach (var reverse in new[] { false, true })
        {
            using var graph = new SyntheticGraph(Rows("client", Revision,
                [Node(Key(0), "Start"), Node(Key(1), "Input"), Node(Key(2), "Update"), Node(Key(3), "Finish")],
                reverse ? edges.Reverse().ToArray() : edges));
            var result = await Call(new ChatContext(graph.Index.View(null)), "find_path", ("from", "Start"), ("to", "Finish"));

            Assert.Contains("(2 hops)", result, StringComparison.Ordinal);
            Assert.Contains($"{(incoming ? "<-CALLS-" : "-CALLS->")} Input (client)", result, StringComparison.Ordinal);
            Assert.DoesNotContain("Update (client)", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Given_tied_start_symbols_when_their_order_changes_then_the_same_shortest_chain_is_selected()
    {
        using var graph = new SyntheticGraph(Rows("client", Revision,
            [Node(Key(1), "Input"), Node(Key(2), "Update"), Node(Key(3), "Finish")],
            [Edge(Key(2), Key(3)), Edge(Key(1), Key(3))]));
        var view = graph.Index.View(null);
        foreach (var starts in new[] { new[] { Key(2), Key(1), Key(2) }, new[] { Key(1), Key(2) } })
        {
            var hops = GraphPaths.Shortest(view, starts, new HashSet<string>(StringComparer.Ordinal) { Key(3) });
            Assert.NotNull(hops);
            Assert.Equal(new[] { Key(1), Key(3) }, hops.Select(h => h.Key));
        }
    }

    [Fact]
    public void Given_tied_request_links_when_their_order_changes_then_the_same_candidate_chain_is_selected()
    {
        using var graph = new SyntheticGraph(Rows("client", Revision,
            [Node(Key(0), "Start"), Node(Key(1), "Input"), Node(Key(2), "Update"), Node(Key(3), "Finish")],
            [Edge(Key(2), Key(3)), Edge(Key(1), Key(3))]));
        foreach (var reverse in new[] { false, true })
        {
            var alternatives = new List<(string To, string Via)> { (Key(2), "candidate-only"), (Key(1), "candidate-only") };
            if (reverse) alternatives.Reverse();
            var links = new Dictionary<string, List<(string To, string Via)>>(StringComparer.Ordinal) { [Key(0)] = alternatives };
            var hops = GraphPaths.Shortest(graph.Index.View(null), [Key(0)],
                new HashSet<string>(StringComparer.Ordinal) { Key(3) }, links);
            Assert.NotNull(hops);
            Assert.Equal(new[] { Key(0), Key(1), Key(3) }, hops.Select(h => h.Key));
            Assert.Equal("candidate-only", hops[1].Via);
        }
    }

    private static async Task<string> Call(ChatContext context, string tool, params (string Name, object? Value)[] arguments)
    {
        var intelligence = new Intelligence(new(), Path.Combine(Fixtures.RepositoryRoot, "unused-chat-settings.json"), null, metadataOnly: true);
        var function = ChatTools.Create(context, intelligence, CancellationToken.None).OfType<AIFunction>().Single(f => f.Name == tool);
        var result = await function.InvokeAsync(new AIFunctionArguments(arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal)));
        return Assert.IsType<string>(Assert.IsType<JsonElement>(result).GetString());
    }

    private static string Key(int i) => $"cs|client|Chat|M:Chat.Step{i}";

    private static SyntheticGraph FlowGraph(bool bound = false, bool connected = true)
    {
        var target = bound ? Endpoint : "http|@unbound:client/Chat:env=SERVICE_URL|POST /orders";
        var serviceEdges = new List<JsonObject> { Edge(Handler, Charge), Edge(Charge, Vendor, "REQUESTS") };
        if (connected) serviceEdges.Add(Edge(Handler, Endpoint, "HANDLES"));
        return new SyntheticGraph(
            Rows("client", Revision, [Node(Start, "Start")], [Edge(Start, target, "REQUESTS")]),
            Rows("service", Revision, [Node(Endpoint, "POST /orders", "Endpoint"), Node(Handler, "Handle"), Node(Charge, "Charge")], serviceEdges));
    }

    private static JsonObject Node(string key, string name, string kind = "Method") => new()
    {
        ["schema"] = "ce.v1", ["kind"] = "node", ["symbolKey"] = key, ["nodeKind"] = kind,
        ["name"] = name, ["evidence"] = new JsonArray("ev:source"),
    };

    private static JsonObject Edge(string from, string to, string relation = "CALLS") => new()
    {
        ["schema"] = "ce.v1", ["kind"] = "edge", ["from"] = from, ["to"] = to,
        ["relation"] = relation, ["derivation"] = "compiler", ["resolution"] = "resolved", ["evidence"] = new JsonArray("ev:source"),
    };

    private static List<JsonObject> Rows(string repository, string revision, JsonObject[] nodes, IReadOnlyList<JsonObject> edges)
    {
        var records = new List<JsonObject>
        {
            new()
            {
                ["schema"] = "ce.v1", ["kind"] = "header", ["repositoryId"] = repository, ["commitSha"] = revision,
                ["unit"] = new JsonObject { ["path"] = "Chat.csproj", ["name"] = "Chat" }, ["language"] = "csharp",
                ["extractor"] = new JsonObject { ["name"] = "synthetic-chat", ["version"] = "1" },
                ["configHash"] = new string('0', 64), ["inputHash"] = new string('1', 64),
            },
            new()
            {
                ["schema"] = "ce.v1", ["kind"] = "evidence", ["id"] = "ev:source",
                ["repositoryId"] = repository, ["commitSha"] = revision, ["path"] = "Chat.cs",
                ["span"] = new JsonObject { ["startLine"] = 1, ["startColumn"] = 1, ["endLine"] = 1, ["endColumn"] = 2 },
                ["snippetSha256"] = new string('2', 64),
                ["extractor"] = new JsonObject { ["name"] = "synthetic-chat", ["version"] = "1" },
            },
        };
        foreach (var (node, i) in nodes.Select((node, i) => (node, i))) { node["id"] = $"n:{i}"; records.Add(node); }
        foreach (var (edge, i) in edges.Select((edge, i) => (edge, i))) { edge["id"] = $"e:{i}"; records.Add(edge); }
        records.Add(new JsonObject
        {
            ["schema"] = "ce.v1", ["kind"] = "complete", ["status"] = "full",
            ["counts"] = new JsonObject { ["node"] = nodes.Length, ["edge"] = edges.Count, ["evidence"] = 1, ["calculation"] = 0, ["diagnostic"] = 0 },
            ["coverage"] = new JsonObject { ["filesTotal"] = 1, ["filesAnalyzed"] = 1, ["unresolvedEdges"] = 0 },
        });
        return records;
    }

    private sealed class SyntheticGraph : IDisposable
    {
        private readonly string _directory = Path.Combine(Fixtures.RepositoryRoot, "platform", "tests", "CapabilityExplorer.Explorer.Tests",
            "artifacts", "chat-retrieval-" + Guid.NewGuid().ToString("N"));

        public SyntheticGraph(params List<JsonObject>[] artifacts)
        {
            Directory.CreateDirectory(_directory);
            try
            {
                foreach (var (records, i) in artifacts.Select((records, i) => (records, i)))
                {
                    var text = string.Join('\n', records.Select(r => r.ToJsonString())) + "\n";
                    var validation = ArtifactValidator.Validate(text);
                    Assert.True(validation.IsValid, string.Join("; ", validation.Problems));
                    File.WriteAllText(Path.Combine(_directory, $"{i:D3}.jsonl"), text);
                }
                Index = ExplorerIndex.Load([_directory], null);
                Assert.Empty(Index.Problems);
                Assert.Equal(artifacts.Length, Index.Artifacts.Count);
            }
            catch
            {
                Directory.Delete(_directory, true);
                throw;
            }
        }

        public ExplorerIndex Index { get; }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
