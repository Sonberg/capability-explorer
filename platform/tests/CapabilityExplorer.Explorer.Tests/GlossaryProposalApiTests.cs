using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapabilityExplorer.Explorer;
using CapabilityExplorer.Interpretation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryProposalApiTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(".artifacts", "glossary-api-" + Guid.NewGuid().ToString("N")));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task No_model_keeps_candidates_available_without_fabricated_definitions()
    {
        using var root = new ExplorerFactory();
        using var host = root.WithWebHostBuilder(builder => builder.UseSetting("interpretation:provider", "none"));
        using var client = host.CreateClient();
        var candidates = await Candidates(client);
        var response = await Post(client, "/api/glossary/propose", new { candidate = candidates[0].Term });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Deterministic candidates remain available", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotEmpty(await Candidates(client));
    }

    [Fact]
    public async Task Recorded_proposal_uses_actual_selected_source_and_never_implicitly_saves_review()
    {
        using var root = new ExplorerFactory();
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("interpretation:provider", "recorded");
            builder.UseSetting("interpretation:recordings", _directory);
        });
        using var client = host.CreateClient();
        var candidate = (await Candidates(client)).First(c => c.Occurrences.Any(o => o.Evidence.Count > 0));
        var source = candidate.Occurrences.First(o => o.Evidence.Count > 0);
        var input = GlossaryProposalApi.BuildInput(host.Services.GetRequiredService<ExplorerState>().Current.Index.View(null), candidate.Term, null);
        Assert.All(input.Occurrences, o => Assert.Equal(64, o.ArtifactContentSha256.Length));
        var service = host.Services.GetRequiredService<Intelligence>().Layer.Glossary!;
        var request = service.BuildRequest(input);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, request.InputHash + ".json"), JsonSerializer.Serialize(
            new GlossaryDraft("Reviewed language", "A suggested definition requiring source review.", [], null,
                [new(source.Id, source.Name, source.Evidence.Select(e => e.Id).ToArray())], [], []), Json));
        var response = await Post(client, "/api/glossary/propose", new { candidate = candidate.Term });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = (await response.Content.ReadFromJsonAsync<GlossaryProposal>())!;
        Assert.Equal("proposed", proposal.Status);
        Assert.Equal(request.InputHash, proposal.InputHash);
        Assert.Equal("unreviewed", proposal.ReviewStatus);
        var review = await client.GetFromJsonAsync<GlossaryReviewView>("/api/glossary/review");
        Assert.Empty(review!.Snapshot.Terms);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client, "/api/glossary/proposals/review", new
        {
            id = Guid.NewGuid(), candidate = candidate.Term, inputHash = proposal.InputHash,
            decision = "accepted", reviewed = true, reviewer = "Reviewer", links = new[] { new { occurrenceId = source.Id, role = "alias" } },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/glossary/propose", new { candidate = candidate.Term })).StatusCode);
    }

    [Theory]
    [InlineData("/api/glossary/propose?cap=missing", "x")]
    [InlineData("/api/glossary/propose?view=bad", "x")]
    [InlineData("/api/glossary/propose", "not-a-candidate")]
    public async Task Unknown_scope_or_candidate_is_rejected_before_model_access(string route, string candidate)
    {
        using var host = new ExplorerFactory();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, route, new { candidate })).StatusCode);
    }

    private static async Task<List<GlossaryCandidate>> Candidates(HttpClient client) =>
        (await client.GetFromJsonAsync<List<GlossaryCandidate>>("/api/glossary", GlossaryApiTests.JsonOptions))!;

    private static Task<HttpResponseMessage> Post(HttpClient client, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CE-Glossary-Write", "1");
        return client.SendAsync(request);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
