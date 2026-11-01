using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryReviewApiTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Missing_database_is_explicit_read_only_and_source_flow_still_works()
    {
        var response = await _client.GetFromJsonAsync<JsonElement>("/api/glossary/review");
        Assert.False(response.GetProperty("configured").GetBoolean());
        Assert.True(response.GetProperty("readOnly").GetBoolean());
        var candidates = (await _client.GetFromJsonAsync<List<GlossaryCandidate>>("/api/glossary", GlossaryApiTests.JsonOptions))!;
        var id = candidates[0].Occurrences[0].Id;
        var route = "/api/glossary/flow?occurrence=" + Uri.EscapeDataString(id);
        Assert.Equal(await _client.GetByteArrayAsync(new Uri(route, UriKind.Relative)), await _client.GetByteArrayAsync(new Uri(route, UriKind.Relative)));
        var graph = await _client.GetFromJsonAsync<GlossaryFlowResult>(route);
        Assert.NotEmpty(graph!.Seeds);
        Assert.False(graph.Coverage.ValueFlowComplete);
        var reviewed = await _client.GetFromJsonAsync<GlossaryFlowResult>(route + "&reviewed=true");
        Assert.Contains(reviewed!.Coverage.Reasons, r => r.Code == "review-disabled");
    }

    [Theory]
    [InlineData("/api/glossary/flow")]
    [InlineData("/api/glossary/flow?occurrence=x&maxHops=9")]
    [InlineData("/api/glossary/flow?occurrence=x&direction=sideways")]
    [InlineData("/api/glossary/review?limit=1001")]
    [InlineData("/api/glossary/review?view=api-cs:unknown")]
    [InlineData("/api/glossary/review?view=bad")]
    [InlineData("/api/glossary/review?view=api-cs:v1-base,api-cs:v2-rename")]
    [InlineData("/api/glossary/review?view=api-cs:v1-base,")]
    [InlineData("/api/glossary/review?cap=does-not-exist")]
    [InlineData("/api/glossary/observation?occurrence=missing")]
    public async Task Invalid_requests_fail_explicitly(string route)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(new Uri(route, UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Reviewed_observation_is_server_derived_from_selected_source()
    {
        var candidates = (await _client.GetFromJsonAsync<List<GlossaryCandidate>>("/api/glossary?view=api-cs:v1-base", GlossaryApiTests.JsonOptions))!;
        var occurrence = candidates.SelectMany(c => c.Occurrences).First(o => o.RepositoryId == "api-cs");
        var observed = await _client.GetFromJsonAsync<GlossaryObservedOccurrence>("/api/glossary/observation?view=api-cs:v1-base&occurrence=" + Uri.EscapeDataString(occurrence.Id));
        Assert.Equal(occurrence.CommitSha, observed!.CommitSha);
        Assert.Equal(occurrence.Key, observed.SymbolKey);
        Assert.Equal(occurrence.UnitPath, observed.UnitPath);
        Assert.Equal(64, observed.ArtifactContentSha256.Length);
    }

    [Fact]
    public async Task Writes_require_browser_guard_and_cannot_fake_success_without_database()
    {
        var id = Guid.NewGuid();
        var body = new { expectedVersion = 0, name = "Reviewed", definition = "A supplied definition.", reviewer = "test" };
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PutAsJsonAsync($"/api/glossary/terms/{id}", body)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/glossary/terms/{id}") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CE-Glossary-Write", "1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.SendAsync(request)).StatusCode);
    }
}

public sealed class GlossaryWriteGuardTests
{
    [Theory]
    [InlineData("localhost", "http://localhost", "same-origin", "application/json", "1", true)]
    [InlineData("127.0.0.1:5094", "http://127.0.0.1:5094", "same-origin", "application/json", "1", true)]
    [InlineData("localhost", "", "", "application/json", "1", true)]
    [InlineData("localhost", "https://attacker.example", "cross-site", "application/json", "1", false)]
    [InlineData("attacker.example", "http://attacker.example", "same-origin", "application/json", "1", false)]
    [InlineData("localhost", "null", "same-origin", "application/json", "1", false)]
    [InlineData("localhost", "http://localhost", "same-site", "application/json", "1", false)]
    [InlineData("localhost", "http://localhost", "same-origin", "text/plain", "1", false)]
    [InlineData("localhost", "http://localhost", "same-origin", "application/json", "", false)]
    public void Browser_write_boundary(string host, string origin, string site, string contentType, string header, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http"; context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin; context.Request.Headers["Sec-Fetch-Site"] = site;
        context.Request.ContentType = contentType; context.Request.Headers["X-CE-Glossary-Write"] = header;
        Assert.Equal(expected, GlossaryWriteGuard.Allowed(context.Request));
    }
}

public sealed class GlossaryDatabaseFailureTests
{
    [Fact]
    public async Task Configured_but_unreachable_database_is_an_error_not_read_only_success()
    {
        using var factory = new ExplorerFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting(
            "database", "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;SSL Mode=Disable"));
        using var client = configured.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(new Uri("/api/glossary/review", UriKind.Relative))).StatusCode);
        var body = await client.GetStringAsync(new Uri("/api/glossary", UriKind.Relative));
        Assert.NotEmpty(body); // deterministic candidates do not depend on review storage
    }
}
