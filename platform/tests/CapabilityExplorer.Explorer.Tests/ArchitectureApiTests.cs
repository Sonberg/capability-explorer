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

        // forecast-cs v2-contract moved its routes: pricing-node's call is shown as unmatched, not hidden.
        Assert.Contains(Edges(g), e => e.GetProperty("from").GetString() == "repo:pricing-node" && e.GetProperty("to").GetString() == "unmatched:GET /api/forecasts/{}");

        // Same-named resources are linked only by an explicit "same name" hint.
        Assert.Contains(Edges(g), e => e.GetProperty("kind").GetString() == "sameName" && e.GetProperty("facts").GetArrayLength() == 0);
    }

    [Fact]
    public async Task Repository_level_aggregates_member_edges_into_component_edges()
    {
        var g = await Graph("level=repository&focus=forecast-cs");
        var calls = Edges(g).Single(e => e.GetProperty("from").GetString() == "comp:cs|forecast-cs|Forecast.Api|T:Forecast.Api.Endpoints.ForecastEndpoints"
                                         && e.GetProperty("to").GetString() == "comp:cs|forecast-cs|Forecast.Api|T:Forecast.Api.Data.Repository`1");
        Assert.Equal(2, calls.GetProperty("count").GetInt32()); // ListAsync and AddAsync
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
        Assert.Equal(["System", "forecast-cs", "StrategyLoader"], g.GetProperty("breadcrumbs").EnumerateArray().Select(c => c.GetProperty("label").GetString()));
    }
}
