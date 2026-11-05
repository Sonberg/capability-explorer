using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task Architecture_honours_node_budget_and_reports_shown_vs_total()
    {
        var g = await Graph("level=system&nodeBudget=2");
        var budget = g.GetProperty("budget");
        Assert.True(budget.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, budget.GetProperty("nodeBudget").GetInt32());
        Assert.Equal(2, budget.GetProperty("shownNodes").GetInt32());
        Assert.True(budget.GetProperty("totalNodes").GetInt32() > budget.GetProperty("shownNodes").GetInt32());
        Assert.Equal(budget.GetProperty("shownNodes").GetInt32(), g.GetProperty("nodes").GetArrayLength());
        Assert.Contains(g.GetProperty("notes").EnumerateArray().Select(n => n.GetString()), note => note!.Contains("Showing 2 of", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("nodeBudget=no")]
    [InlineData("edgeBudget=1.5")]
    [InlineData("edgeBudget=")]
    [InlineData("nodeBudget=2&nodeBudget=3")]
    [InlineData("q=one&q=two")]
    [InlineData("hideKind=Call")]
    [InlineData("hideKind=call%2Cdata")]
    [InlineData("include=")]
    [InlineData("nodeBudget=1&include=a&include=b")]
    public async Task Invalid_architecture_parameters_are_bad_requests(string query)
    {
        using var response = await _client.GetAsync($"/api/architecture?{query}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Architecture_rejects_oversized_and_excess_repeated_filters()
    {
        foreach (var query in new[]
        {
            $"q={new string('x', 201)}",
            $"include={new string('x', 4097)}",
            string.Join("&", Enumerable.Repeat("hideKind=call", 33)),
            string.Join("&", Enumerable.Range(0, 21).Select(i => $"include=node{i}")),
        })
        {
            using var response = await _client.GetAsync($"/api/architecture?{query}");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Api_filters_pin_focus_and_report_missing_ids_with_clamped_budgets()
    {
        var g = await Graph("level=component&focus=cs%7Cforecast-cs%7CForecast.Api%7CT%3AForecast.Api.Forecasting.StrategyLoader&q=no-such-match&include=missing&nodeBudget=1&edgeBudget=-1");
        var node = Assert.Single(g.GetProperty("nodes").EnumerateArray());
        Assert.Equal("cs|forecast-cs|Forecast.Api|T:Forecast.Api.Forecasting.StrategyLoader", node.GetProperty("symbolKey").GetString());
        var budget = g.GetProperty("budget");
        Assert.Equal(1, budget.GetProperty("matchedNodes").GetInt32());
        Assert.Equal(1, budget.GetProperty("edgeBudget").GetInt32());
        Assert.Equal("missing", Assert.Single(budget.GetProperty("missingPins").EnumerateArray()).GetString());
        Assert.Equal(node.GetProperty("id").GetString(), Assert.Single(budget.GetProperty("pinned").EnumerateArray()).GetString());

        var capped = (await Graph("level=system&nodeBudget=2000&edgeBudget=8000")).GetProperty("budget");
        Assert.Equal(400, capped.GetProperty("nodeBudget").GetInt32());
        Assert.Equal(800, capped.GetProperty("edgeBudget").GetInt32());
    }

    [Fact]
    public async Task Api_rejects_resolved_pin_plus_automatic_focus_exceeding_node_budget()
    {
        const string focus = "cs|forecast-cs|Forecast.Api|T:Forecast.Api.Forecasting.StrategyLoader";
        const string pin = "mem:cs|forecast-cs|Forecast.Api|M:Forecast.Api.Forecasting.StrategyLoader.Resolve(System.IServiceProvider,System.String)";
        using var response = await _client.GetAsync($"/api/architecture?level=component&focus={Uri.EscapeDataString(focus)}&include={Uri.EscapeDataString(pin)}&nodeBudget=1");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Focus and requested pins exceed nodeBudget.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Api_kind_and_text_filters_keep_honest_counts_and_metadata()
    {
        var g = await Graph("level=system&hideKind=http&hideKind=call&q=FORECAST&include=repo%3Aweb-ts");
        Assert.DoesNotContain(Edges(g), e => e.GetProperty("kind").GetString() is "http" or "call");
        Assert.Contains(g.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("id").GetString() == "repo:web-ts");
        Assert.Equal(["call", "http"], g.GetProperty("filters").GetProperty("hideKinds").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("FORECAST", g.GetProperty("filters").GetProperty("query").GetString());
        Assert.Equal(0, g.GetProperty("facets").GetProperty("kinds").EnumerateArray().Single(k => k.GetProperty("value").GetString() == "http").GetProperty("matched").GetInt32());
        var budget = g.GetProperty("budget");
        Assert.True(budget.GetProperty("matchedNodes").GetInt32() < budget.GetProperty("totalNodes").GetInt32());
        Assert.Equal(g.GetProperty("nodes").GetArrayLength(), budget.GetProperty("shownNodes").GetInt32());
        Assert.Equal(g.GetProperty("edges").GetArrayLength(), budget.GetProperty("shownEdges").GetInt32());
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
