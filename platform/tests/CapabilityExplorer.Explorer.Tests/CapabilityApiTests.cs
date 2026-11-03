using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Capabilities;
using CapabilityExplorer.Capabilities.Analysis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Explorer with its own capability file and workspace, pointing at the fixture repositories.</summary>
public sealed class CapabilityExplorerFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-state", Guid.NewGuid().ToString("N"))).FullName;

    public CapabilityExplorerFactory()
    {
        string Repo(string id) => Fixtures.RepoDirectory(id).Replace("\\", "/", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_dir, "capabilities.json"), $$"""
            { "capabilities": [ { "id": "demand-pricing", "name": "Demand pricing", "repositories": [
              { "id": "forecast-cs", "location": "{{Repo("forecast-cs")}}", "kind": "local", "ref": "v1-base" },
              { "id": "pricing-node", "location": "{{Repo("pricing-node")}}", "kind": "local", "ref": "HEAD" } ] } ] }
            """);
        CompleteFixtureCheck();
    }

    public string ConfigurationPath => Path.Combine(_dir, "capabilities.json");

    public void CompleteFixtureCheck()
    {
        var journal = new FileRunStore(RequestsDirectory);
        journal.AdmitCheck("demand-pricing", ConfigurationPath);
        using var owner = journal.AcquireWorker();
        var check = journal.ClaimCheck(owner)!;
        var snapshot = new CapabilitySnapshot("demand-pricing", DateTimeOffset.UtcNow,
        [
            new LockedRepository("forecast-cs", "v1-base", Fixtures.Lock["forecast-cs"]["v1-base"], null, null, null),
            new LockedRepository("pricing-node", "HEAD", Fixtures.Lock["pricing-node"]["v2-move"], null, null, null),
        ]);
        journal.CompleteCheck(owner, new AttemptIdentity(check.RequestId, check.AttemptId!), snapshot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("artifacts", Fixtures.ExpectedDirectory);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        builder.UseSetting("capabilities", Path.Combine(_dir, "capabilities.json"));
        builder.UseSetting("workspace", Path.Combine(_dir, "workspace"));
        builder.UseSetting("requests", RequestsDirectory);
    }

    public string RequestsDirectory => Path.Combine(_dir, "requests");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public sealed class CapabilityApiTests : IDisposable
{
    private readonly CapabilityExplorerFactory factory = new();
    private readonly HttpClient _client;

    public CapabilityApiTests() => _client = factory.CreateClient();

    [Fact]
    public async Task Checking_locks_every_repository_and_reports_whether_it_was_analysed()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/capabilities/demand-pricing");
        var repos = status.GetProperty("repositories").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal(Fixtures.Lock["forecast-cs"]["v1-base"], repos["forecast-cs"].GetProperty("lockedSha").GetString());
        Assert.Equal(Fixtures.Lock["pricing-node"]["v2-move"], repos["pricing-node"].GetProperty("lockedSha").GetString());
        Assert.All(repos.Values, r => Assert.True(r.GetProperty("analysed").GetBoolean()));
    }

    [Fact]
    public async Task A_capability_scopes_file_views_while_integrations_require_the_database()
    {
        var view = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/view?cap=demand-pricing");
        Assert.Equal(["forecast-cs", "pricing-node"], view!.Keys.Order());
        Assert.Equal(Fixtures.Lock["forecast-cs"]["v1-base"], view["forecast-cs"]); // locked ref, not the latest

        using var integrations = await _client.GetAsync(new Uri("/api/integrations?cap=demand-pricing", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, integrations.StatusCode);

        var hits = await _client.GetFromJsonAsync<JsonElement>("/api/search?q=average&cap=demand-pricing");
        Assert.DoesNotContain(hits.EnumerateArray(), h => h.GetProperty("repositoryId").GetString() is "api-cs" or "worker-cs" or "web-ts");
    }

    [Fact]
    public async Task Capabilities_can_be_created_and_deleted_from_repository_lines()
    {
        var lines = $"{Fixtures.RepoDirectory("api-cs")} v2-rename\n# comment\n{Fixtures.RepoDirectory("web-ts")}";
        var created = await _client.PostAsJsonAsync("/api/capabilities", new { name = "Weekly sales", description = "test", repositories = lines });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var response = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.GetProperty("created").GetBoolean());
        Assert.True(response.GetProperty("queued").GetBoolean());
        Assert.Equal("queued", response.GetProperty("check").GetProperty("state").GetString());
        var body = response.GetProperty("capability");
        Assert.Equal("weekly-sales", body.GetProperty("id").GetString());
        Assert.False(body.GetProperty("repositories")[0].TryGetProperty("lockedSha", out _));
        Assert.Equal(response.GetProperty("check").GetProperty("statusUrl").GetString(), created.Headers.Location?.OriginalString);

        var meta = await _client.GetFromJsonAsync<JsonElement>("/api/meta");
        Assert.Contains(meta.GetProperty("capabilities").EnumerateArray(), c => c.GetProperty("id").GetString() == "weekly-sales");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync(new Uri("/api/capabilities/weekly-sales", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri("/api/capabilities/weekly-sales", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task The_landscape_shows_every_capability_and_links_those_that_share_a_repository()
    {
        var created = await _client.PostAsJsonAsync("/api/capabilities", new { name = "Forecast ops", repositories = Fixtures.RepoDirectory("forecast-cs") });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
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
    public async Task Analyze_queues_one_request_for_the_host_worker_can_be_stopped_and_rebuild_needs_the_database()
    {
        var url = new Uri("/api/capabilities/demand-pricing/analyze", UriKind.Relative);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync(url, null)).StatusCode);
        var queued = await _client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal("queued", queued.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(queued.GetProperty("runId").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync(url, null)).StatusCode);
        // Legacy callers omit runId and still cancel the latest queued run.
        Assert.Equal("stopped", (await (await _client.DeleteAsync(url)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync(url)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync(new Uri("/api/capabilities/nope/analyze", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync(new Uri("/api/capabilities/demand-pricing/rebuild", UriKind.Relative), null)).StatusCode);
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

    public void Dispose()
    {
        _client.Dispose();
        factory.Dispose();
    }
}
