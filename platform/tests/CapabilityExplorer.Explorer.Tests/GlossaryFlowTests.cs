using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryFlowTests
{
    private static string Key(int i) => $"cs|worker-cs|Sales.Worker|P:Domain.Invoice.Value{i}";

    [Fact]
    public void Cycles_terminate_and_preserve_all_exact_source_facts_without_asserting_value_flow()
    {
        var records = Chain(3);
        records.Add(Edge("e:cycle", Key(2), Key(0)));
        var view = Load(null, records).View(null);
        var result = Build(view, Key(0));
        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(3, result.Edges.Count);
        Assert.True(result.Coverage.Complete);
        Assert.False(result.Coverage.ValueFlowComplete);
        Assert.False(result.Coverage.Truncated);
        Assert.All(result.Graph.Edges, e => Assert.Equal("static", e.Certainty));
        Assert.All(result.Edges, e =>
        {
            Assert.Equal("source-navigation", e.Category);
            Assert.Equal("worker-cs", e.RepositoryId);
            Assert.NotNull(e.CommitSha);
            Assert.NotNull(e.UnitPath);
            Assert.NotEmpty(e.Evidence);
        });
        Assert.Contains(result.Coverage.Reasons, r => r.Code == "unsupported-field-mapping");
        Assert.All(result.Graph.Edges, e => Assert.Contains("context", e.Label));
    }

    [Theory]
    [InlineData("forward", 2)]
    [InlineData("backward", 2)]
    [InlineData("both", 3)]
    public void Direction_follows_recorded_arrows(string direction, int nodes)
    {
        var view = Load(null, Chain(3)).View(null);
        var result = Build(view, Key(1), new(Direction: direction));
        Assert.Equal(nodes, result.Nodes.Count);
        if (direction == "forward") Assert.DoesNotContain(result.Nodes, n => n.SymbolKey == Key(0));
        if (direction == "backward") Assert.DoesNotContain(result.Nodes, n => n.SymbolKey == Key(2));
    }

    [Fact]
    public void Shuffled_artifacts_facts_seeds_and_culture_preserve_bytes_and_ignore_integer_artifact_ids()
    {
        var first = Chain(4);
        var second = Clone(first);
        second.Single(r => Text(r, "kind") == "header")["unit"]!["path"] = "other/Other.csproj";
        var view = Load(null, first, second).View(null);
        var seeds = Glossary.Build(view).SelectMany(c => c.Occurrences).Select(o => o.Id).ToArray();
        var expected = JsonSerializer.Serialize(GlossaryFlow.Build(view, seeds, new()));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var shuffled = Load(null, second.AsEnumerable().Reverse().ToList(), first.AsEnumerable().Reverse().ToList()).View(null);
            Assert.Equal(expected, JsonSerializer.Serialize(GlossaryFlow.Build(shuffled, seeds.Reverse().ToArray(), new())));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void Equal_names_and_channel_keys_never_connect_repositories()
    {
        var a = Chain(1);
        const string channel = "chan|servicebus|shared-bus|invoice";
        const string sender = "cs|worker-cs|Sales.Worker|M:Domain.Invoice.Send()";
        a.Add(Node("n:sender", sender, "Send", "Method"));
        a.Add(Edge("e:call", Key(0), sender, "CALLS"));
        a.Add(Edge("e:publish", sender, channel, "PUBLISHES"));
        var b = Chain(1, "other-cs");
        b.Add(Node("n:channel", channel, "invoice", "Channel"));
        var view = Load(null, a, b).View(null);
        var result = Build(view, Key(0));
        Assert.DoesNotContain(result.Nodes, n => n.RepositoryId == "other-cs");
        Assert.Equal(2, result.Edges.Count);
        Assert.Contains(result.Coverage.Reasons, r => r.Code == "unsupported-boundary");
        Assert.False(result.Coverage.Complete);
    }

    [Fact]
    public void Different_selected_revision_cannot_reuse_an_old_seed_or_old_review()
    {
        var a = Chain(2);
        var b = Clone(a);
        SetRevision(b, new string('b', 40));
        var index = Load(null, a, b);
        var oldSha = Text(a.Single(r => Text(r, "kind") == "header"), "commitSha")!;
        var old = index.View($"worker-cs:{oldSha}");
        var seed = Glossary.Build(old).SelectMany(c => c.Occurrences).First().Id;
        var current = index.View($"worker-cs:{new string('b', 40)}");
        var result = GlossaryFlow.Build(current, [seed], new(), [new("review1", "term1", "Value", "same", seed, "supplied name", "reason")]);
        Assert.Empty(result.Nodes);
        Assert.Empty(result.Seeds);
        Assert.Contains(result.Coverage.Reasons, r => r.Code == "unknown-seed");
        Assert.Contains(result.Coverage.Reasons, r => r.Code == "unavailable-human-occurrence");
        Assert.All(result.Selection, s => Assert.Equal(new string('b', 40), s.CommitSha));
        Assert.NotEqual(Build(old, Key(0)).Graph.Nodes[0].Id, Build(current, Key(0)).Graph.Nodes[0].Id);
    }

    [Theory]
    [InlineData(2, 500, 1000, "hop-limit", 3, 2)]
    [InlineData(8, 2, 1000, "node-limit", 2, 1)]
    [InlineData(8, 500, 1, "edge-limit", 2, 1)]
    public void Each_traversal_budget_discloses_truncation(int hops, int nodes, int edges, string reason, int actualNodes, int actualEdges)
    {
        var view = Load(null, Chain(12)).View(null);
        var result = Build(view, Key(0), new("forward", hops, nodes, edges));
        Assert.Equal(actualNodes, result.Nodes.Count);
        Assert.Equal(actualEdges, result.Edges.Count);
        Assert.True(result.Coverage.Truncated);
        Assert.False(result.Coverage.Complete);
        Assert.Contains(result.Coverage.Reasons, r => r.Code == reason);
        Assert.All(result.Graph.Edges, e =>
        {
            Assert.Contains(result.Graph.Nodes, n => n.Id == e.From);
            Assert.Contains(result.Graph.Nodes, n => n.Id == e.To);
        });
    }

    [Fact]
    public void Partial_analysis_and_no_connections_are_distinct_from_unknown_seeds()
    {
        var view = Load(null, Chain(1), partial: true).View(null);
        var result = Build(view, Key(0));
        Assert.Single(result.Nodes);
        Assert.Contains(result.Coverage.Reasons, r => r.Code == "partial-analysis");
        Assert.Contains(result.Coverage.Reasons, r => r.Code == "no-connections");
        Assert.DoesNotContain(result.Coverage.Reasons, r => r.Code == "unknown-seed");
        Assert.Equal("partial", Assert.Single(result.Selection).AnalysisStatus);
    }

    [Fact]
    public void Service_map_connects_exact_endpoint_and_keeps_original_unresolved_fact_and_provenance()
    {
        const string hint = "@unbound:worker-cs/Sales.Worker:env=API_URL";
        const string endpoint = "http|other-cs/Other.Api|GET /invoice";
        const string sender = "cs|worker-cs|Sales.Worker|M:Domain.Invoice.Send()";
        var caller = Chain(1);
        caller.Add(Node("n:sender", sender, "Send", "Method"));
        caller.Add(Edge("e:call", Key(0), sender, "CALLS"));
        caller.Add(Edge("e:request", sender, $"http|{hint}|GET /invoice", "REQUESTS", "unresolved"));
        var service = Chain(1, "other-cs");
        service.Add(Node("n:endpoint", endpoint, "GET /invoice", "Endpoint"));
        var bindings = BindingFile.Parse($$"""{"bindings":[{"hint":"{{hint}}","target":"other-cs/Other.Api","note":"reviewed service URL"}]}""");
        var view = Load(bindings, caller, service).View(null);
        var result = Build(view, Key(0));
        var bound = Assert.Single(result.Edges, e => e.Category == "service-binding");
        Assert.Equal("human", bound.Derivation);
        Assert.Equal("service-map 1", bound.Rule);
        Assert.Equal("e:request", bound.OriginalRecordId);
        Assert.Equal(hint, bound.BindingHint);
        Assert.Equal("reviewed service URL", bound.BindingNote);
        Assert.Equal("other-cs", result.Nodes.Single(n => n.Id == bound.To).RepositoryId);
        Assert.Contains(result.Edges, e => e.Category == "source-navigation" && e.Resolution == "unresolved");
        Assert.Contains(view.Edges, e => e.Record.To!.Contains(hint, StringComparison.Ordinal) && e.Record.Resolution == "unresolved");
        Assert.Equal("bound", result.Graph.Edges.Single(e => e.Id == bound.Id).Certainty);
        service.RemoveAll(r => Text(r, "id") == "n:endpoint");
        var broken = Build(Load(bindings, caller, service).View(null), Key(0));
        Assert.Contains(broken.Graph.Edges, e => e.Kind == "service-binding" && e.Certainty == "broken");
        Assert.Contains(broken.Coverage.Reasons, r => r.Code == "binding-boundary");
    }

    [Fact]
    public void Ambiguous_other_unit_declarations_stop_at_boundary_instead_of_picking_first()
    {
        var caller = Chain(1);
        caller.Add(Edge("e:missing", Key(0), Key(9)));
        var unit1 = Chain(0);
        unit1.Add(Node("n:9", Key(9), "Value9"));
        unit1.Single(r => Text(r, "kind") == "header")["unit"]!["path"] = "one/One.csproj";
        var unit2 = Clone(unit1);
        unit2.Single(r => Text(r, "kind") == "header")["unit"]!["path"] = "two/Two.csproj";
        var result = Build(Load(null, caller, unit1, unit2).View(null), Key(0));
        Assert.Equal(2, result.Nodes.Count);
        Assert.Contains(result.Coverage.Reasons, r => r.Code == "ambiguous-target");
        Assert.DoesNotContain(result.Nodes, n => n.UnitPath is "one/One.csproj" or "two/Two.csproj");
    }

    [Fact]
    public void Explicit_calculation_origins_preserve_record_operand_and_evidence_without_equivalence()
    {
        var records = Chain(0);
        records.Add(Node("n:input", Key(0), "SourceValue"));
        records.Add(Node("n:output", Key(1), "OutputValue"));
        var attributes = new JsonObject();
        foreach (var name in new[] { "aggregation", "timeBucket", "denominatorSource", "rounding", "numericType", "nullHandling", "zeroDenominator", "currency" })
            attributes[name] = new JsonObject { ["status"] = "unknown" };
        records.Add(new JsonObject
        {
            ["schema"] = "ce.v1", ["kind"] = "calculation", ["id"] = "c:dependency", ["symbolKey"] = Key(1),
            ["output"] = JsonNode.Parse("""{"type":"decimal","unit":{"status":"unknown"}}"""),
            ["inputs"] = new JsonArray(new JsonObject { ["ref"] = "$0", ["type"] = "decimal", ["origin"] = Key(0) }),
            ["expr"] = JsonNode.Parse("""{"op":"var","ref":"$0","type":"decimal"}"""),
            ["attributes"] = attributes, ["evidence"] = new JsonArray("ev:source"),
        });
        var view = Load(null, records).View(null);
        var result = Build(view, Key(0));
        Assert.Equal(2, result.Nodes.Count);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("calculation-input", edge.Category);
        Assert.Equal("c:dependency", edge.RecordId);
        Assert.Equal("$0", edge.InputRef);
        Assert.Equal("calculation-record", edge.Derivation);
        Assert.Equal(Key(0), result.Nodes.Single(n => n.Id == edge.From).SymbolKey);
        Assert.Equal(Key(1), result.Nodes.Single(n => n.Id == edge.To).SymbolKey);
        Assert.NotEmpty(edge.Evidence);
        Assert.False(result.Coverage.ValueFlowComplete);
        Assert.Equal(2, result.Nodes.Single(n => n.SymbolKey == Key(0)).OccurrenceIds.Count);
        Assert.Equal(2, result.Nodes.Single(n => n.SymbolKey == Key(1)).OccurrenceIds.Count);
        var seeds = Glossary.Build(view).SelectMany(c => c.Occurrences).Where(o => o.Key == Key(0)).Select(o => o.Id).ToArray();
        var bothRoles = GlossaryFlow.Build(view, seeds, new(MaxHops: 0));
        Assert.Single(bothRoles.Nodes);
        Assert.Equal(2, bothRoles.Seeds.Count);
        Assert.Empty(bothRoles.Edges);
        Assert.True(bothRoles.Coverage.Truncated);
    }

    [Fact]
    public void Reviewed_hubs_are_linear_explicit_associations_and_preserve_derived_from_direction()
    {
        var view = Load(null, Chain(3)).View(null);
        var occurrences = Glossary.Build(view).SelectMany(c => c.Occurrences).ToDictionary(o => o.Key);
        var reviews = new[]
        {
            new GlossaryFlowHumanAssertion("r1", "t1", "Invoice value", "same", occurrences[Key(0)].Id, "operator supplied", "reviewed A"),
            new GlossaryFlowHumanAssertion("r2", "t1", "Invoice value", "alias", occurrences[Key(2)].Id, "operator supplied", "reviewed B"),
            new GlossaryFlowHumanAssertion("r3", "t2", "Derived value", "derived_from", occurrences[Key(0)].Id, "operator supplied", "reviewed dependency"),
        };
        var result = GlossaryFlow.Build(view, [occurrences[Key(0)].Id], new("forward"), reviews);
        Assert.Equal(3, result.Edges.Count(e => e.Category == "human-assertion"));
        Assert.Equal(2, result.Nodes.Count(n => n.RepositoryId is null));
        var derived = result.Edges.Single(e => e.Relation == "derived_from");
        Assert.Equal(result.Seeds[0].NodeId, derived.From);
        Assert.Null(result.Nodes.Single(n => n.Id == derived.To).RepositoryId);
        Assert.All(result.Edges.Where(e => e.Category == "human-assertion"), e =>
        {
            Assert.Equal("human-asserted", e.Resolution);
            Assert.Equal("operator supplied", e.Reviewer);
            Assert.NotNull(e.OccurrenceId);
            Assert.Empty(e.Evidence);
            Assert.Equal("human", result.Graph.Edges.Single(g => g.Id == e.Id).Certainty);
            Assert.Empty(result.Graph.Edges.Single(g => g.Id == e.Id).Facts);
        });
        var backward = GlossaryFlow.Build(view, [occurrences[Key(0)].Id], new("backward"), reviews);
        Assert.DoesNotContain(backward.Edges, e => e.Relation == "derived_from");
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(GlossaryFlow.Build(view,
            [occurrences[Key(0)].Id], new("forward"), reviews.Reverse().ToArray())));
    }

    [Theory]
    [InlineData("sideways", 4, 250, 500)]
    [InlineData("both", 9, 250, 500)]
    [InlineData("both", -1, 250, 500)]
    [InlineData("both", 4, 501, 500)]
    [InlineData("both", 4, 0, 500)]
    [InlineData("both", 4, 250, 1001)]
    public void Rejects_unsupported_direction_and_unbounded_limits(string direction, int hops, int nodes, int edges)
    {
        var view = Load(null, Chain(1)).View(null);
        Assert.Throws<ArgumentException>(() => Build(view, Key(0), new(direction, hops, nodes, edges)));
    }

    private static GlossaryFlowResult Build(ViewIndex view, string key, GlossaryFlowOptions? options = null)
    {
        var occurrence = Glossary.Build(view).SelectMany(c => c.Occurrences).First(o => o.Key == key);
        return GlossaryFlow.Build(view, [occurrence.Id], options ?? new());
    }

    private static string? Text(JsonObject o, string key) => (string?)o[key];
    private static List<JsonObject> Clone(List<JsonObject> records) => records.Select(r => (JsonObject)r.DeepClone()).ToList();
    private static JsonObject Node(string id, string key, string name, string kind = "Property") => new()
    {
        ["schema"] = "ce.v1", ["kind"] = "node", ["id"] = id, ["symbolKey"] = key,
        ["nodeKind"] = kind, ["name"] = name, ["evidence"] = new JsonArray("ev:source"),
    };
    private static JsonObject Edge(string id, string from, string to, string relation = "REFERENCES", string resolution = "resolved")
    {
        var edge = new JsonObject
        {
            ["schema"] = "ce.v1", ["kind"] = "edge", ["id"] = id, ["from"] = from, ["to"] = to,
            ["relation"] = relation, ["derivation"] = "compiler", ["resolution"] = resolution,
            ["evidence"] = new JsonArray("ev:source"),
        };
        if (resolution == "unresolved") edge["unresolvedReason"] = "configuration";
        return edge;
    }
    private static List<JsonObject> Chain(int count, string repo = "worker-cs")
    {
        var golden = Fixtures.Golden("worker-cs/v1-base/Sales.Worker.jsonl").TrimEnd().Split('\n')
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        var header = golden.Single(r => Text(r, "kind") == "header");
        var evidence = golden.First(r => Text(r, "kind") == "evidence");
        header["repositoryId"] = repo;
        evidence["repositoryId"] = repo;
        evidence["id"] = "ev:source";
        var records = new List<JsonObject> { header, evidence };
        for (var i = 0; i < count; i++)
        {
            records.Add(Node($"n:{i}", Key(i).Replace("worker-cs", repo, StringComparison.Ordinal), $"Value{i}"));
            if (i > 0) records.Add(Edge($"e:{i}", Key(i - 1).Replace("worker-cs", repo, StringComparison.Ordinal), Key(i).Replace("worker-cs", repo, StringComparison.Ordinal)));
        }
        return records;
    }
    private static void SetRevision(List<JsonObject> records, string sha)
    {
        foreach (var r in records.Where(r => Text(r, "kind") is "header" or "evidence")) r["commitSha"] = sha;
    }
    private static ExplorerIndex Load(BindingFile? bindings, List<JsonObject> first, List<JsonObject>? second = null,
        List<JsonObject>? third = null, bool partial = false)
    {
        var directory = Directory.CreateTempSubdirectory("ce-flow").FullName;
        try
        {
            var artifacts = new[] { first, second, third }.OfType<List<JsonObject>>().ToList();
            for (var i = 0; i < artifacts.Count; i++)
            {
                var records = artifacts[i];
                var complete = new JsonObject
                {
                    ["schema"] = "ce.v1", ["kind"] = "complete", ["status"] = partial ? "partial" : "full",
                    ["counts"] = new JsonObject
                    {
                        ["node"] = records.Count(r => Text(r, "kind") == "node"),
                        ["edge"] = records.Count(r => Text(r, "kind") == "edge"),
                        ["calculation"] = records.Count(r => Text(r, "kind") == "calculation"),
                        ["evidence"] = 1, ["diagnostic"] = 0,
                    },
                    ["coverage"] = new JsonObject { ["filesTotal"] = partial ? 2 : 1, ["filesAnalyzed"] = 1, ["unresolvedEdges"] = records.Count(r => Text(r, "resolution") == "unresolved") },
                };
                var text = string.Join('\n', records.OrderBy(r => Text(r, "kind") == "header" ? 0 : 1).Append(complete).Select(r => r.ToJsonString())) + "\n";
                var validation = ArtifactValidator.Validate(text);
                Assert.True(validation.IsValid, string.Join("; ", validation.Problems));
                File.WriteAllText(Path.Combine(directory, $"{i:D3}.jsonl"), text);
            }
            var index = ExplorerIndex.Load([directory], null, bindings);
            Assert.Empty(index.Problems);
            return index;
        }
        finally { Directory.Delete(directory, true); }
    }
}
