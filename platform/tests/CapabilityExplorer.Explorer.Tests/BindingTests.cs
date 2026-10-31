using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Binding;
using CapabilityExplorer.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Explorer over the golden artifacts plus the repository's example bindings.json.</summary>
public sealed class BoundExplorerFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        builder.UseSetting("bindings", Path.Combine(Fixtures.RepositoryRoot, "bindings.json"));
    }
}

public sealed class BinderTests
{
    private const string Hint = "@unbound:pricing-node/@fixture/pricing:env=FORECAST_API_URL";

    private static (LoadedFrom, Artifact) Load(string repo, string revision, string unit)
    {
        var file = $"{repo}/{revision}/{unit.Replace('/', '_').Replace('@', '_')}.jsonl";
        var artifact = ArtifactValidator.Validate(Fixtures.Golden(file)).Artifact!;
        return (new LoadedFrom(repo, artifact.Header.CommitSha, artifact.Header.Unit.Path), artifact);
    }

    private static BindingFile Map(string target = "forecast-cs/Forecast.Api") =>
        BindingFile.Parse($$"""{"version":1,"bindings":[{"hint":"{{Hint}}","target":"{{target}}","note":"n"}]}""");

    [Fact]
    public void Binds_the_env_hint_to_the_service_and_marks_a_declared_endpoint_bound()
    {
        var result = Binder.Bind([Load("pricing-node", "v1-base", "@fixture/pricing"), Load("forecast-cs", "v1-base", "Forecast.Api")], Map());
        var edge = Assert.Single(result.Edges);
        Assert.Equal(BindingStatus.Bound, edge.Status);
        Assert.Equal("http|forecast-cs/Forecast.Api|GET /api/forecasts/{}", edge.Edge.To);
        Assert.Equal("REQUESTS", edge.Edge.Relation);
        Assert.Equal("human", edge.Edge.Derivation);
        Assert.Equal("resolved", edge.Edge.Resolution);
        Assert.Equal(edge.Original.Evidence, edge.Edge.Evidence);
        Assert.Empty(result.UnusedBindings);
    }

    [Fact]
    public void The_v2_route_change_is_a_broken_binding_not_a_silent_drop()
    {
        var result = Binder.Bind([Load("pricing-node", "v1-base", "@fixture/pricing"), Load("forecast-cs", "v2-contract", "Forecast.Api")], Map());
        var edge = Assert.Single(result.Edges);
        Assert.Equal(BindingStatus.Broken, edge.Status);
        Assert.Equal("unresolved", edge.Edge.Resolution);
        Assert.Equal("missing-reference", edge.Edge.UnresolvedReason);
        Assert.Equal("http|forecast-cs/Forecast.Api|GET /api/forecasts/{}", edge.Edge.To);
    }

    [Fact]
    public void Never_rewrites_extractor_output_and_is_deterministic()
    {
        var pricing = Load("pricing-node", "v1-base", "@fixture/pricing");
        var before = pricing.Item2.Edges.ToList();
        var a = Binder.Bind([pricing], Map());
        var b = Binder.Bind([pricing], Map());
        Assert.Equal(before, pricing.Item2.Edges);
        Assert.All(pricing.Item2.Edges, e => Assert.NotEqual("human", e.Derivation));
        Assert.Equal(a.Edges.Select(e => e.Edge.Id), b.Edges.Select(e => e.Edge.Id));
        Assert.NotEqual(a.Edges[0].Original.Id, a.Edges[0].Edge.Id);
    }

    [Fact]
    public void A_hint_scoped_to_another_unit_does_not_bind_and_is_reported_unused()
    {
        var file = BindingFile.Parse("""{"bindings":[{"hint":"@unbound:pricing-node/other:env=FORECAST_API_URL","target":"forecast-cs/Forecast.Api"}]}""");
        var result = Binder.Bind([Load("pricing-node", "v1-base", "@fixture/pricing")], file);
        Assert.Empty(result.Edges);
        Assert.Single(result.UnusedBindings);
    }

    [Theory]
    [InlineData("""{"bindings":[{"hint":"FORECAST_API_URL","target":"x"}]}""")]
    [InlineData("""{"bindings":[{"hint":"@unbound:pricing-node/u:env=A","target":"a|b"}]}""")]
    [InlineData("""{"bindings":[{"hint":"@unbound:pricing-node/u:env=A","target":"@unbound:x/y:env=B"}]}""")]
    [InlineData("""{"bindings":[{"hint":"@unbound:pricing-node/u:env=A","target":"x"},{"hint":"@unbound:pricing-node/u:env=A","target":"y"}]}""")]
    [InlineData("""{"version":2,"bindings":[]}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    public void Rejects_malformed_binding_files(string json) => Assert.Throws<FormatException>(() => BindingFile.Parse(json));
}

public sealed class BindingApiTests(BoundExplorerFactory bound, ExplorerFactory plain) : IClassFixture<BoundExplorerFactory>, IClassFixture<ExplorerFactory>
{
    private static async Task<JsonElement> Get(HttpClient c, string path) => await c.GetFromJsonAsync<JsonElement>(path);

    private static JsonElement Link(JsonElement integrations) => integrations.GetProperty("http").EnumerateArray()
        .Single(l => l.GetProperty("request").GetProperty("from").GetProperty("name").GetString() == "getForecast");

    [Fact]
    public async Task Without_a_bindings_file_nothing_is_bound()
    {
        var link = Link(await Get(plain.CreateClient(), "/api/integrations?view=forecast-cs:v1-base,pricing-node:v1-base"));
        Assert.Equal("route", link.GetProperty("match").GetString());
    }

    [Fact]
    public async Task Cross_repo_link_is_bound_to_the_forecast_endpoint()
    {
        var c = bound.CreateClient();
        var link = Link(await Get(c, "/api/integrations?view=forecast-cs:v1-base,pricing-node:v1-base"));
        Assert.Equal("binding", link.GetProperty("match").GetString());
        Assert.Equal("http|forecast-cs/Forecast.Api|GET /api/forecasts/{}", link.GetProperty("candidates")[0].GetProperty("key").GetString());
        Assert.Contains("FORECAST_API_URL", link.GetProperty("note").GetString());

        var g = await Get(c, "/api/architecture?level=system&view=forecast-cs:v1-base,pricing-node:v1-base");
        var edge = g.GetProperty("edges").EnumerateArray().Single(e => e.GetProperty("from").GetString() == "repo:pricing-node" && e.GetProperty("kind").GetString() == "http" && e.GetProperty("facts").EnumerateArray().Any(f => f.TryGetProperty("to", out var t) && t.GetProperty("key").GetString()!.Contains("FORECAST_API_URL", StringComparison.Ordinal)));
        Assert.Equal("repo:forecast-cs", edge.GetProperty("to").GetString());
        Assert.Equal("bound", edge.GetProperty("certainty").GetString());
        // The fact is still the extractor's: unresolved, configuration. Binding adds a link, it does not edit the fact.
        Assert.Equal("unresolved", edge.GetProperty("facts")[0].GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task V2_route_change_shows_as_a_broken_link()
    {
        var c = bound.CreateClient();
        var view = "forecast-cs:v2-contract,pricing-node:v1-base";
        var link = Link(await Get(c, $"/api/integrations?view={Uri.EscapeDataString(view)}"));
        Assert.Equal("broken", link.GetProperty("match").GetString());

        var g = await Get(c, $"/api/architecture?level=system&view={Uri.EscapeDataString(view)}");
        var edge = g.GetProperty("edges").EnumerateArray().Single(e => e.GetProperty("from").GetString() == "repo:pricing-node" && e.GetProperty("kind").GetString() == "http" && e.GetProperty("facts").EnumerateArray().Any(f => f.TryGetProperty("to", out var t) && t.GetProperty("key").GetString()!.Contains("FORECAST_API_URL", StringComparison.Ordinal)));
        Assert.Equal("broken", edge.GetProperty("certainty").GetString());
        Assert.StartsWith("broken:http|forecast-cs/Forecast.Api|", edge.GetProperty("to").GetString());
    }
}
