using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Explorer with its own capability file and workspace, pointing at the fixture repositories.</summary>
public sealed class CapabilityExplorerFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ce-capabilities").FullName;

    public CapabilityExplorerFactory()
    {
        string Repo(string id) => Fixtures.RepoDirectory(id).Replace("\\", "/", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_dir, "capabilities.json"), $$"""
            { "capabilities": [ { "id": "demand-pricing", "name": "Demand pricing", "repositories": [
              { "id": "forecast-cs", "location": "{{Repo("forecast-cs")}}", "kind": "local", "ref": "v1-base" },
              { "id": "pricing-node", "location": "{{Repo("pricing-node")}}", "kind": "local", "ref": "HEAD" } ] } ] }
            """);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        builder.UseSetting("capabilities", Path.Combine(_dir, "capabilities.json"));
        builder.UseSetting("workspace", Path.Combine(_dir, "workspace"));
    }
}

public sealed class CapabilityApiTests(CapabilityExplorerFactory factory) : IClassFixture<CapabilityExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<JsonElement> Check(string id)
    {
        var response = await _client.PostAsync(new Uri($"/api/capabilities/{id}/check", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Checking_locks_every_repository_and_reports_whether_it_was_analysed()
    {
        var status = await Check("demand-pricing");
        var repos = status.GetProperty("repositories").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal(Fixtures.Lock["forecast-cs"]["v1-base"], repos["forecast-cs"].GetProperty("lockedSha").GetString());
        Assert.Equal(Fixtures.Lock["pricing-node"]["v2-move"], repos["pricing-node"].GetProperty("lockedSha").GetString());
        Assert.All(repos.Values, r => Assert.True(r.GetProperty("analysed").GetBoolean()));
    }

    [Fact]
    public async Task A_capability_scopes_every_view_to_its_repositories_at_their_locked_revisions()
    {
        await Check("demand-pricing");
        var view = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/view?cap=demand-pricing");
        Assert.Equal(["forecast-cs", "pricing-node"], view!.Keys.Order());
        Assert.Equal(Fixtures.Lock["forecast-cs"]["v1-base"], view["forecast-cs"]); // locked ref, not the latest

        var integrations = await _client.GetFromJsonAsync<JsonElement>("/api/integrations?cap=demand-pricing");
        var services = integrations.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("service").GetProperty("repositoryId").GetString()).ToHashSet();
        Assert.Equal(["forecast-cs", "pricing-node"], services.Order());
        // At the locked forecast revision, pricing's call to /api/forecasts/{} has a candidate endpoint.
        var link = integrations.GetProperty("http").EnumerateArray().Single(l => l.GetProperty("request").GetProperty("from").GetProperty("name").GetString() == "getForecast");
        Assert.Equal("route", link.GetProperty("match").GetString());

        var hits = await _client.GetFromJsonAsync<JsonElement>("/api/search?q=average&cap=demand-pricing");
        Assert.DoesNotContain(hits.EnumerateArray(), h => h.GetProperty("repositoryId").GetString() is "api-cs" or "worker-cs" or "web-ts");
    }

    [Fact]
    public async Task Capabilities_can_be_created_and_deleted_from_repository_lines()
    {
        var lines = $"{Fixtures.RepoDirectory("api-cs")} v2-rename\n# comment\n{Fixtures.RepoDirectory("web-ts")}";
        var created = await _client.PostAsJsonAsync("/api/capabilities", new { name = "Weekly sales", description = "test", repositories = lines });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("weekly-sales", body.GetProperty("id").GetString());
        Assert.Equal(Fixtures.Lock["api-cs"]["v2-rename"], body.GetProperty("repositories")[0].GetProperty("lockedSha").GetString());

        var meta = await _client.GetFromJsonAsync<JsonElement>("/api/meta");
        Assert.Contains(meta.GetProperty("capabilities").EnumerateArray(), c => c.GetProperty("id").GetString() == "weekly-sales");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync(new Uri("/api/capabilities/weekly-sales", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri("/api/capabilities/weekly-sales", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task The_landscape_shows_every_capability_and_links_those_that_share_a_repository()
    {
        var created = await _client.PostAsJsonAsync("/api/capabilities", new { name = "Forecast ops", repositories = Fixtures.RepoDirectory("forecast-cs") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        try
        {
            var g = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?level=landscape&cap=demand-pricing");
            var nodes = g.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("id").GetString()!);
            Assert.Contains("cap:demand-pricing", nodes.Keys);
            Assert.Equal("system", nodes["cap:forecast-ops"].GetProperty("drill").GetProperty("level").GetString());
            var shares = g.GetProperty("edges").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "shares");
            Assert.Equal("shares forecast-cs", shares.GetProperty("label").GetString());

            // One level down, the capability names the crumb above its repositories.
            var system = await _client.GetFromJsonAsync<JsonElement>("/api/architecture?cap=demand-pricing");
            Assert.Equal("system", system.GetProperty("level").GetString());
            Assert.Equal(["Landscape", "Demand pricing"], system.GetProperty("breadcrumbs").EnumerateArray().Select(c => c.GetProperty("label").GetString()));
        }
        finally
        {
            await _client.DeleteAsync(new Uri("/api/capabilities/forecast-ops", UriKind.Relative));
        }
    }

    [Fact]
    public async Task Bad_input_is_rejected_with_reasons()
    {
        var bad = await _client.PostAsJsonAsync("/api/capabilities", new { name = "Bad", repositories = "https://gitlab.com/a/b" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("only https://github.com", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var conflict = await _client.PostAsJsonAsync("/api/capabilities", new { name = "Clash", repositories = "forecast-cs=/somewhere/else" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var noName = await _client.PostAsJsonAsync("/api/capabilities", new { name = " ", repositories = "/x" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
    }
}
