using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

/// <summary>Real HTTP handlers and PostgreSQL, without a network or model-backed interpretation provider.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class GlossaryHttpJourneyTests(PostgresFixture db)
{
    private static string View => "view=api-cs:" + Fixtures.Lock["api-cs"]["v1-base"];

    [Fact]
    public async Task Http_lifecycle_survives_host_restart_with_conflicts_and_soft_deletions()
    {
        var termId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var issueId = Guid.NewGuid();
        GlossaryObservedOccurrence observed;
        var term = new { expectedVersion = 0, name = "<script>supplied label</script>", definition = "HTTP-reviewed definition", group = "Sales", reviewer = "Reviewer A" };
        using (var host = new GlossaryHttpFactory(db.ConnectionString))
        using (var client = host.CreateClient())
        {
            var initial = await ReadAsync(client);
            Assert.True(initial.Configured);
            Assert.False(initial.ReadOnly);
            observed = await ObservationAsync(client);
            var created = await MutateAsync(client, HttpMethod.Put, $"terms/{termId}", term);
            Assert.Equal("Created", created.GetProperty("status").GetString());
            Assert.Equal(term.name, created.GetProperty("value").GetProperty("name").GetString());
            Assert.Equal(1, created.GetProperty("value").GetProperty("version").GetInt64());
            var replay = await MutateAsync(client, HttpMethod.Put, $"terms/{termId}", term);
            Assert.Equal("Unchanged", replay.GetProperty("status").GetString());
            await MutateAsync(client, HttpMethod.Put, $"links/{linkId}", new GlossaryLinkRequest(0, termId, observed.OccurrenceId, "same", "Exact reviewed source", "Reviewer A", observed), scope: View);
            await MutateAsync(client, HttpMethod.Put, $"issues/{issueId}", new GlossaryIssueRequest(0, termId, "type", "Source types require review", null, "open", "Reviewer A"));
            await MutateAsync(client, HttpMethod.Put, $"issues/{issueId}", new GlossaryIssueRequest(1, termId, "type", "Source types require review", "Checked explicitly", "resolved", "Reviewer B"));
            await MutateAsync(client, HttpMethod.Put, $"terms/{termId}", new GlossaryTermRequest(1, "Updated business term", term.definition, term.group, "Reviewer B"));
            await MutateAsync(client, HttpMethod.Put, $"terms/{termId}", new GlossaryTermRequest(1, "Stale editor", term.definition, term.group, "Reviewer C"), HttpStatusCode.Conflict);
            await MutateAsync(client, HttpMethod.Delete, $"issues/{issueId}", new GlossaryDeleteRequest(2, "Reviewer B"));
        }

        using (var restarted = new GlossaryHttpFactory(db.ConnectionString))
        using (var client = restarted.CreateClient())
        {
            var persisted = await ReadAsync(client);
            var savedTerm = Assert.Single(persisted.Snapshot.Terms, t => t.Id == termId);
            Assert.Equal("Updated business term", savedTerm.Name);
            Assert.Equal(2, savedTerm.Version);
            Assert.Equal("Reviewer B", savedTerm.Reviewer);
            Assert.Equal(observed, Assert.Single(persisted.Snapshot.Links, l => l.Id == linkId).Observed);
            Assert.Equal("current", Assert.Single(persisted.LinkStates, l => l.Id == linkId).State);
            Assert.DoesNotContain(persisted.Snapshot.Issues, i => i.Id == issueId);
            await MutateAsync(client, HttpMethod.Delete, $"links/{linkId}", new GlossaryDeleteRequest(1, "Reviewer B"), scope: View);
            await MutateAsync(client, HttpMethod.Delete, $"terms/{termId}", new GlossaryDeleteRequest(2, "Reviewer B"));
            var after = await ReadAsync(client);
            Assert.DoesNotContain(after.Snapshot.Terms, t => t.Id == termId);
            Assert.DoesNotContain(after.Snapshot.Links, l => l.Id == linkId);
        }

        var audit = await new GlossaryStore(db.DataSource).GetAuditAsync(termId);
        Assert.Equal(["create", "update", "delete"], audit.Events.Select(e => e.Action));
        Assert.Equal(["Reviewer A", "Reviewer B", "Reviewer B"], audit.Events.Select(e => e.Actor));
    }

    [Fact]
    public async Task Same_commit_reanalysis_between_observation_and_save_returns_409_until_explicit_rereview()
    {
        var directory = Directory.CreateTempSubdirectory("ce-glossary-http-analysis").FullName;
        try
        {
            var file = Path.Combine(directory, "artifact.jsonl");
            await File.WriteAllTextAsync(file, Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl"));
            using var host = new GlossaryHttpFactory(db.ConnectionString, directory);
            using var client = host.CreateClient();
            var observed = await ObservationAsync(client);
            var termId = Guid.NewGuid();
            var linkId = Guid.NewGuid();
            await MutateAsync(client, HttpMethod.Put, $"terms/{termId}", new GlossaryTermRequest(0, "Reanalysis", "Reviewed term", null, "Reviewer A"));

            var records = (await File.ReadAllLinesAsync(file)).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
            var node = records.First(record => (string?)record["kind"] == "node" && (string?)record["symbolKey"] == observed.SymbolKey);
            node["name"] = "ChangedByReanalysisForHttpReview";
            await File.WriteAllTextAsync(file, string.Join('\n', records.Select(record => record.ToJsonString())) + "\n");
            Assert.True(host.Services.GetRequiredService<ExplorerState>().Refresh());
            var current = await client.GetFromJsonAsync<GlossaryObservedOccurrence>("/api/glossary/observation?" + View + "&occurrence=" + Uri.EscapeDataString(observed.OccurrenceId));
            Assert.NotNull(current);
            Assert.Equal(observed.OccurrenceId, current.OccurrenceId);
            Assert.Equal(observed.CommitSha, current.CommitSha);
            Assert.NotEqual(observed.ArtifactContentSha256, current.ArtifactContentSha256);

            await MutateAsync(client, HttpMethod.Put, $"links/{linkId}", new GlossaryLinkRequest(0, termId, observed.OccurrenceId, "same", null, "Reviewer A", observed), HttpStatusCode.Conflict, View);
            Assert.Null(await new GlossaryStore(db.DataSource).GetLinkAsync(linkId));
            await MutateAsync(client, HttpMethod.Put, $"links/{linkId}", new GlossaryLinkRequest(0, termId, current.OccurrenceId, "same", null, "Reviewer A", current), scope: View);
            Assert.Equal(current, (await new GlossaryStore(db.DataSource).GetLinkAsync(linkId))!.Observed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Capability_scope_rejects_outside_observation_update_and_delete_without_mutating_link()
    {
        using var host = new GlossaryHttpFactory(db.ConnectionString);
        using var client = host.CreateClient();
        var observed = await ObservationAsync(client);
        var termId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await MutateAsync(client, HttpMethod.Put, $"terms/{termId}", new GlossaryTermRequest(0, "Scoped term", "Reviewed term", null, "Reviewer A"));
        var link = new GlossaryLinkRequest(0, termId, observed.OccurrenceId, "same", null, "Reviewer A", observed);
        await MutateAsync(client, HttpMethod.Put, $"links/{linkId}", link, scope: View);
        using var excluded = await client.GetAsync(new Uri("/api/glossary/observation?cap=forecast-only&occurrence=" + Uri.EscapeDataString(observed.OccurrenceId), UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, excluded.StatusCode);
        await MutateAsync(client, HttpMethod.Put, $"links/{linkId}", link with { ExpectedVersion = 1, Note = "Outside-scope edit" }, HttpStatusCode.NotFound, "cap=forecast-only");
        await MutateAsync(client, HttpMethod.Delete, $"links/{linkId}", new GlossaryDeleteRequest(1, "Reviewer B"), HttpStatusCode.NotFound, "cap=forecast-only");
        await MutateAsync(client, HttpMethod.Put, $"links/{Guid.NewGuid()}", link, HttpStatusCode.BadRequest, "cap=forecast-only");
        var retained = await new GlossaryStore(db.DataSource).GetLinkAsync(linkId);
        Assert.False(retained!.Deleted);
        Assert.Equal(1, retained.Version);
        Assert.Null(retained.Note);
    }

    private static async Task<GlossaryReviewView> ReadAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<GlossaryReviewView>("/api/glossary/review?" + View))!;

    private static async Task<GlossaryObservedOccurrence> ObservationAsync(HttpClient client)
    {
        var candidates = await client.GetFromJsonAsync<JsonElement>("/api/glossary?" + View);
        var occurrence = candidates.EnumerateArray().SelectMany(candidate => candidate.GetProperty("occurrences").EnumerateArray())
            .First(value => value.GetProperty("repositoryId").GetString() == "api-cs");
        return (await client.GetFromJsonAsync<GlossaryObservedOccurrence>("/api/glossary/observation?" + View + "&occurrence=" + Uri.EscapeDataString(occurrence.GetProperty("id").GetString()!)))!;
    }

    private static async Task<JsonElement> MutateAsync(HttpClient client, HttpMethod method, string path, object body, HttpStatusCode expected = HttpStatusCode.OK, string? scope = null)
    {
        using var request = new HttpRequestMessage(method, "/api/glossary/" + path + (scope is null ? "" : "?" + scope)) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CE-Glossary-Write", "1");
        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == expected, $"{method} {path}: expected {(int)expected}, got {(int)response.StatusCode}: {json}");
        return json;
    }
}

/// <summary>The public marker selects the Explorer entry assembly without exposing its internal Program type.</summary>
internal sealed class GlossaryHttpFactory : WebApplicationFactory<ExplorerIndex>
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ce-glossary-http-host").FullName;
    private readonly string _connection;
    private readonly string _artifacts;
    private readonly string? _recordings;

    public GlossaryHttpFactory(string connection, string? artifacts = null, string? recordings = null)
    {
        _connection = connection;
        _artifacts = artifacts ?? Fixtures.ExpectedDirectory;
        _recordings = recordings;
        File.WriteAllText(Path.Combine(_directory, "capabilities.json"), JsonSerializer.Serialize(new
        {
            capabilities = new[] { new { id = "forecast-only", name = "Forecast only", repositories = new[]
            { new { id = "forecast-cs", location = Fixtures.RepoDirectory("forecast-cs"), kind = "local", @ref = "HEAD" } } } },
        }));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("database", _connection);
        builder.UseSetting("artifacts", _artifacts);
        builder.UseSetting("repos", Path.Combine(Fixtures.RepositoryRoot, "fixtures", ".repos"));
        builder.UseSetting("capabilities", Path.Combine(_directory, "capabilities.json"));
        builder.UseSetting("workspace", Path.Combine(_directory, "workspace"));
        builder.UseSetting("requests", Path.Combine(_directory, "requests"));
        builder.UseSetting("interpretation:provider", _recordings is null ? "none" : "recorded");
        if (_recordings is not null) builder.UseSetting("interpretation:recordings", _recordings);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
