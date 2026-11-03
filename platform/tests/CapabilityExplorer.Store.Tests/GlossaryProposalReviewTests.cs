using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Interpretation;
using CapabilityExplorer.Store;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class GlossaryProposalReviewTests(PostgresFixture db) : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(".artifacts", "glossary-review-" + Guid.NewGuid().ToString("N")));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string View => "view=api-cs:" + Fixtures.Lock["api-cs"]["v1-base"];

    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected")]
    public async Task Explicit_decision_persists_across_restart_and_only_acceptance_creates_audited_assertions(string decision)
    {
        var sourceCounts = await db.CountAllAsync();
        var id = Guid.NewGuid();
        GlossaryProposalReviewRequest body;
        using (var host = new GlossaryHttpFactory(db.ConnectionString, recordings: _directory))
        using (var client = host.CreateClient())
        {
            var (candidate, proposal) = await Propose(host, client);
            var occurrence = proposal.Draft!.Occurrences[0].OccurrenceId;
            body = new(id, candidate.Term, proposal.InputHash, decision, true, "Reviewer A",
                decision == "accepted" ? [new(occurrence, "alias")] : []);
            var saved = await Post(client, "proposals/review?" + View, body);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var result = (await saved.Content.ReadFromJsonAsync<GlossaryMutationResult<GlossaryProposalReviewWrite>>(Json))!;
            Assert.Equal(GlossaryMutationStatus.Created, result.Status);
            var repeat = await Post(client, "proposals/review?" + View, body);
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
            var repeated = (await repeat.Content.ReadFromJsonAsync<GlossaryMutationResult<GlossaryProposalReviewWrite>>(Json))!;
            Assert.Equal(GlossaryMutationStatus.Unchanged, repeated.Status);
            Assert.Equal(HttpStatusCode.Conflict, (await Post(client, "proposals/review?" + View, body with { Reviewer = "Reviewer B" })).StatusCode);
        }
        using (var restarted = new GlossaryHttpFactory(db.ConnectionString))
        using (var client = restarted.CreateClient())
        {
            var snapshot = (await client.GetFromJsonAsync<GlossaryReviewView>("/api/glossary/review?" + View))!;
            var store = new GlossaryStore(db.DataSource);
            var retained = (await store.GetProposalReviewAsync(id))!;
            Assert.Equal((body.InputHash, decision, "Reviewer A"), (retained.InputHash, retained.Decision, retained.Reviewer));
            Assert.Equal("reviewed", retained.Data.GetProperty("reviewStatus").GetString());
            Assert.Equal("unreviewed", retained.Data.GetProperty("proposal").GetProperty("reviewStatus").GetString());
            if (decision == "accepted")
            {
                var term = Assert.Single(snapshot.Snapshot.Terms, t => t.Id == id);
                Assert.Equal("Source-grounded proposed language", term.Name);
                var link = Assert.Single(snapshot.Snapshot.Links, l => l.TermId == id);
                Assert.Equal(body.Links[0].OccurrenceId, link.Observed.OccurrenceId);
                Assert.Equal("alias", link.Role);
                Assert.Contains(body.InputHash, link.Note!, StringComparison.Ordinal);
                Assert.Equal("current", Assert.Single(snapshot.LinkStates, l => l.Id == link.Id).State);
                Assert.Equal("create", Assert.Single((await store.GetAuditAsync(id)).Events).Action);
                Assert.Equal("Reviewer A", Assert.Single((await store.GetAuditAsync(link.Id)).Events).Actor);
            }
            else
            {
                Assert.DoesNotContain(snapshot.Snapshot.Terms, t => t.Id == id);
                Assert.DoesNotContain(snapshot.Snapshot.Links, l => l.TermId == id);
                Assert.Empty((await store.GetAuditAsync(id)).Events);
            }
        }
        Assert.Equal(sourceCounts, await db.CountAllAsync());
    }

    [Fact]
    public async Task Unreviewed_stale_out_of_scope_or_invalid_selections_cannot_create_terms_or_reviews()
    {
        using var host = new GlossaryHttpFactory(db.ConnectionString, recordings: _directory);
        using var client = host.CreateClient();
        var (candidate, proposal) = await Propose(host, client);
        var occurrence = proposal.Draft!.Occurrences[0].OccurrenceId;
        var valid = new GlossaryProposalReviewRequest(Guid.NewGuid(), candidate.Term, proposal.InputHash, "accepted", true,
            "Reviewer", [new(occurrence, "alias")]);
        var cases = new (GlossaryProposalReviewRequest Body, string Scope, HttpStatusCode Status)[]
        {
            (valid with { Reviewed = false }, View, HttpStatusCode.BadRequest),
            (valid with { InputHash = new string('a', 64) }, View, HttpStatusCode.Conflict),
            (valid with { Links = [new("invented", "alias")] }, View, HttpStatusCode.BadRequest),
            (valid with { Links = [new(occurrence, "invented")] }, View, HttpStatusCode.BadRequest),
            (valid with { Reviewer = "" }, View, HttpStatusCode.BadRequest),
            (valid with { Links = [] }, View, HttpStatusCode.BadRequest),
            (valid with { Links = [new(occurrence, "alias"), new(occurrence, "same")] }, View, HttpStatusCode.BadRequest),
            (valid, "cap=forecast-only", HttpStatusCode.BadRequest),
            (valid, "view=api-cs:" + Fixtures.Lock["api-cs"]["v2-rename"], HttpStatusCode.Conflict),
        };
        var store = new GlossaryStore(db.DataSource);
        foreach (var (body, scope, status) in cases)
        {
            Assert.Equal(status, (await Post(client, "proposals/review?" + scope, body)).StatusCode);
            Assert.Null(await store.GetTermAsync(valid.Id));
            Assert.Null(await store.GetProposalReviewAsync(valid.Id));
        }
    }

    [Fact]
    public async Task Same_revision_reanalysis_invalidates_proposal_before_acceptance()
    {
        var artifacts = Path.Combine(_directory, "artifacts");
        Directory.CreateDirectory(artifacts);
        var file = Path.Combine(artifacts, "source.jsonl");
        await File.WriteAllTextAsync(file, Fixtures.Golden("api-cs/v1-base/Sales.Api.jsonl"));
        using var host = new GlossaryHttpFactory(db.ConnectionString, artifacts, _directory);
        using var client = host.CreateClient();
        var (candidate, proposal) = await Propose(host, client);
        var original = await File.ReadAllTextAsync(file);
        // Whitespace changes analysis content identity without changing revision or occurrence IDs.
        await File.WriteAllTextAsync(file, original.Replace("\"kind\":", "\"kind\": ", StringComparison.Ordinal));
        Assert.True(host.Services.GetRequiredService<ExplorerState>().Refresh());
        var id = Guid.NewGuid();
        var body = new GlossaryProposalReviewRequest(id, candidate.Term, proposal.InputHash, "accepted", true, "Reviewer",
            [new(proposal.Draft!.Occurrences[0].OccurrenceId, "same")]);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, "proposals/review?" + View, body)).StatusCode);
        Assert.Null(await new GlossaryStore(db.DataSource).GetProposalReviewAsync(id));
        Assert.Null(await new GlossaryStore(db.DataSource).GetTermAsync(id));
    }

    private async Task<(GlossaryCandidate Candidate, GlossaryProposal Proposal)> Propose(GlossaryHttpFactory host, HttpClient client)
    {
        var candidates = (await client.GetFromJsonAsync<List<GlossaryCandidate>>("/api/glossary?" + View, Json))!;
        var candidate = candidates.First(c => c.Occurrences.Any(o => o.RepositoryId == "api-cs" && o.Evidence.Count > 0));
        var source = candidate.Occurrences.First(o => o.RepositoryId == "api-cs" && o.Evidence.Count > 0);
        var selected = host.Services.GetRequiredService<ExplorerState>().Current.Index.View("api-cs:" + Fixtures.Lock["api-cs"]["v1-base"]);
        var input = GlossaryProposalApi.BuildInput(selected, candidate.Term, null);
        var service = host.Services.GetRequiredService<Intelligence>().Layer.Glossary!;
        var request = service.BuildRequest(input);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, request.InputHash + ".json"),
            JsonSerializer.Serialize(new GlossaryDraft("Source-grounded proposed language", "An explicitly reviewed source-language suggestion.", [], "Sales",
                [new(source.Id, source.Name, source.Evidence.Select(e => e.Id).ToArray())], [], []), Json));
        var result = await Post(client, "propose?" + View, new GlossaryProposeRequest(candidate.Term));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var proposal = (await result.Content.ReadFromJsonAsync<GlossaryProposal>())!;
        Assert.Equal("proposed", proposal.Status);
        Assert.Equal("unreviewed", proposal.ReviewStatus);
        return (candidate, proposal);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/glossary/" + path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CE-Glossary-Write", "1");
        return client.SendAsync(request);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
