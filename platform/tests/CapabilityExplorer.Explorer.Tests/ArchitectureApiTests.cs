using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>The layered canvas: each level built from artifact facts, with inferred links marked as such.</summary>
public sealed class ArchitectureApiTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<JsonElement> Graph(string query) => await _client.GetFromJsonAsync<JsonElement>($"/api/architecture?{query}");

    private static List<JsonElement> Edges(JsonElement g) => g.GetProperty("edges").EnumerateArray().ToList();

    [Fact]
    public async Task System_level_shows_repositories_resources_and_candidate_links()
    {
        var g = await Graph("level=system");
        var nodes = g.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("id").GetString()!);
        Assert.Contains("repo:api-cs", nodes.Keys);
        Assert.Equal("repository", nodes["repo:forecast-cs"].GetProperty("drill").GetProperty("level").GetString());

        // HTTP between repositories is a route-match candidate while base URLs are unbound.
        var http = Edges(g).Single(e => e.GetProperty("from").GetString() == "repo:web-ts" && e.GetProperty("kind").GetString() == "http");
        Assert.Equal("repo:api-cs", http.GetProperty("to").GetString());
        Assert.Equal("candidate", http.GetProperty("certainty").GetString());
        Assert.NotEmpty(http.GetProperty("facts").EnumerateArray().Single().GetProperty("evidence").EnumerateArray());

        // forecast-cs v2-contract moved its routes: pricing-node's call is shown as unmatched, not hidden, grouped by
        // the base it goes through; that group opens one level down into its routes.
        var unmatched = Edges(g).Single(e => e.GetProperty("from").GetString() == "repo:pricing-node" && e.GetProperty("label").GetString() == "GET /api/forecasts/{}");
        var group = nodes[unmatched.GetProperty("to").GetString()!];
        Assert.Equal("requests", group.GetProperty("drill").GetProperty("level").GetString());
        var routes = await Graph($"level=requests&focus={Uri.EscapeDataString(group.GetProperty("drill").GetProperty("focus").GetString()!)}");
        Assert.Contains(routes.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("id").GetString() == "unmatched:GET /api/forecasts/{}");

        // Crowded data stores fold into one node, and the same-name hints are dropped at this level.
        Assert.Contains(nodes.Keys, id => id == "fold:datastore");
        Assert.DoesNotContain(nodes.Keys, id => id.StartsWith("res:sql|", StringComparison.Ordinal));
        Assert.DoesNotContain(Edges(g), e => e.GetProperty("kind").GetString() == "sameName");
    }

    [Fact]
    public async Task Repository_level_aggregates_member_edges_into_component_edges()
    {
        var g = await Graph("level=repository&focus=forecast-cs");
        var calls = Edges(g).Single(e => e.GetProperty("from").GetString() == "comp:cs|forecast-cs|Forecast.Api|T:Forecast.Api.Endpoints.ForecastEndpoints"
                                         && e.GetProperty("to").GetString() == "comp:cs|forecast-cs|Forecast.Api|T:Forecast.Api.Data.Repository`1");
        Assert.Equal(2, calls.GetProperty("count").GetInt32()); // ListAsync and AddAsync
        var type = g.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "comp:cs|forecast-cs|Forecast.Api|T:Forecast.Api.Endpoints.ForecastEndpoints");
        Assert.Equal("src/Forecast.Api/Endpoints/ForecastEndpoints.cs", type.GetProperty("group").GetString());
        var iface = Edges(g).Single(e => e.GetProperty("to").GetString() == "comp:cs|forecast-cs|Forecast.Api|T:Forecast.Api.Forecasting.IForecastStrategy" && e.GetProperty("kind").GetString() == "call");
        Assert.Equal("static", iface.GetProperty("certainty").GetString()); // interface dispatch
        Assert.Contains(Edges(g), e => e.GetProperty("kind").GetString() == "http" && e.GetProperty("to").GetString() == "repo:api-cs");
    }

    [Fact]
    public async Task Component_level_shows_members_externals_and_unresolved_targets()
    {
        var focus = Uri.EscapeDataString("cs|forecast-cs|Forecast.Api|T:Forecast.Api.Forecasting.StrategyLoader");
        var g = await Graph($"level=component&focus={focus}");
        var categories = g.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("category").GetString()).ToList();
        Assert.Contains("external", categories);
        Assert.Contains("unmatched", categories); // the reflection-based plugin path
        Assert.Contains(Edges(g), e => e.GetProperty("certainty").GetString() == "candidate"); // keyed DI
        Assert.Equal(["Landscape", "System", "forecast-cs", "StrategyLoader"], g.GetProperty("breadcrumbs").EnumerateArray().Select(c => c.GetProperty("label").GetString()));
    }

    [Fact]
    public void Given_a_compacted_member_when_grouped_then_its_declaring_type_and_source_are_preserved()
    {
        const string type = "cs|forecast-cs|Forecast.Api|T:Forecast.Api.Forecasting.StrategyLoader";
        var fixture = Fixtures.GoldenFiles.First(path => path.StartsWith("forecast-cs/", StringComparison.Ordinal)
            && Fixtures.Golden(path).Contains(type, StringComparison.Ordinal));
        var records = Fixtures.Golden(fixture).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        var method = records.First(record => record["kind"]?.GetValue<string>() == "node"
            && record["nodeKind"]?.GetValue<string>() == "Method"
            && record["symbolKey"]!.GetValue<string>().Contains("StrategyLoader.", StringComparison.Ordinal))["symbolKey"]!.GetValue<string>();
        var compact = "cs|forecast-cs|Forecast.Api|sha256:" + new string('a', 64);
        foreach (var record in records)
            foreach (var field in new[] { "symbolKey", "from", "to" })
                if (record[field]?.GetValue<string>() == method) record[field] = compact;
        var member = records.First(record => record["kind"]?.GetValue<string>() == "node"
            && record["symbolKey"]?.GetValue<string>() == compact);
        if (!records.Any(record => record["kind"]?.GetValue<string>() == "edge"
            && record["relation"]?.GetValue<string>() == "DECLARES" && record["from"]?.GetValue<string>() == type
            && record["to"]?.GetValue<string>() == compact))
        {
            records = [.. records[..^1], new JsonObject
            {
                ["schema"] = "ce.v1", ["kind"] = "edge", ["id"] = "e:fffffffffffffffe",
                ["from"] = type, ["relation"] = "DECLARES", ["to"] = compact,
                ["derivation"] = "compiler", ["resolution"] = "resolved", ["evidence"] = member["evidence"]!.DeepClone(),
            }, records[^1]];
            var counts = records.First(record => record["kind"]?.GetValue<string>() == "complete")["counts"]!;
            counts["edge"] = counts["edge"]!.GetValue<int>() + 1;
        }
        var directory = Directory.CreateTempSubdirectory("ce-compact-member-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "forecast.jsonl"), string.Join('\n', records.Select(record => record.ToJsonString())) + "\n");
            var index = ExplorerIndex.Load([directory], null);
            Assert.Empty(index.Problems);
            var view = index.View(null);
            var graph = Architecture.Build(view, "component", type, view, [], null);
            Assert.Contains(graph.Nodes, node => node.SymbolKey == compact);
            var repository = Architecture.Build(view, "repository", "forecast-cs", view, [], null);
            Assert.Contains(repository.Nodes, node => node.SymbolKey == type
                && node.Group == "src/Forecast.Api/Forecasting/StrategyLoader.cs");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class VendorTests
{
    private const string Caller = "cs|shop|Glufs.Infrastructure|M:Glufs.Infrastructure.Klarna.OrderManagementRestClient.CancelOrder(System.String)";

    [Theory]
    [InlineData("http|@unbound:shop/Glufs.Infrastructure:base=https://api.fortnox.se/3/|POST /3/invoices", Caller, "Fortnox")]
    [InlineData("http|@unbound:web/web:host=api.playground.klarna.com|POST /checkout/v3/orders", "ts|web|web|src/pay.ts|pay#value", "Klarna")]
    [InlineData("http|@unbound:shop/Glufs.Infrastructure:client=OrderManagementRestClient|POST /ordermanagement/v1/orders/{}/cancel", Caller, "Klarna")]
    [InlineData("http|@unbound:shop/Glufs.Infrastructure:client=FooClient|GET /x", "cs|shop|Glufs.Infrastructure|M:Glufs.Infrastructure.FooClient.Get", "FooClient")]
    [InlineData("http|@unbound:web/web:env=CHECKOUT_API_URL|GET /api/cart", "ts|web|web|src/cart.ts|load#value", "env CHECKOUT_API_URL")]
    [InlineData("http|forecast.cs|Forecast.Api|GET /forecast", Caller, "forecast.cs")]
    public void Requests_group_by_who_they_talk_to(string to, string from, string vendor) =>
        Assert.Equal(vendor, CapabilityExplorer.Explorer.Queries.Vendor(to, from));
}

public sealed class BaseInfoTests
{
    [Theory]
    [InlineData("http|@unbound:shop/Infra:base=https://api.fortnox.se/3/|POST /3/invoices", "api.fortnox.se", true)]
    [InlineData("http|@unbound:web/web:host=api.klarna.com|POST /x", "api.klarna.com", true)]
    [InlineData("http|@unbound:web/web:env=CHECKOUT_API_URL|GET /x", "env CHECKOUT_API_URL", false)]
    [InlineData("http|@unbound:shop/Infra:client=FooClient|GET /x", "FooClient", false)]
    public void Only_validated_base_urls_are_hosts(string key, string name, bool isHost) =>
        Assert.Equal((name, isHost), CapabilityExplorer.Explorer.Architecture.BaseInfo(key));
}
