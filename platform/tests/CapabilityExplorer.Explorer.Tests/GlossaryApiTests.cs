using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CapabilityExplorer.Explorer;
using Xunit;

namespace CapabilityExplorer.Tests;

public sealed class GlossaryApiTests(ExplorerFactory factory) : IClassFixture<ExplorerFactory>
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<List<GlossaryCandidate>> Get(string query = "")
    {
        var response = await _client.GetAsync(new Uri("/api/glossary" + query, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<GlossaryCandidate>>(JsonOptions))!;
    }

    [Fact]
    public async Task Worker_and_web_active_week_group_but_calendar_week_calculations_remain_separate()
    {
        var candidates = await Get();
        var active = candidates.Single(c => c.Term == "average per active week");
        var calculations = active.Occurrences.Where(o => o.Role == "calculation").ToList();
        Assert.Equal(["web-ts", "worker-cs"], calculations.Select(o => o.RepositoryId).Distinct().Order());
        Assert.DoesNotContain(calculations, o => o.RepositoryId == "api-cs");
        var calendar = candidates.Single(c => c.Term == "calendar week average");
        Assert.Contains(calendar.Occurrences, o => o.RepositoryId == "api-cs" && o.Role == "calculation");
        Assert.DoesNotContain(calendar.Occurrences, o => o.RepositoryId is "worker-cs" or "web-ts");
    }

    [Fact]
    public async Task Selected_revision_changes_names_and_keeps_every_occurrence_and_evidence_at_selected_sha()
    {
        var selection = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/view?view=api-cs:v1-base");
        var candidates = await Get("?view=api-cs:v1-base");
        var calendar = candidates.Single(c => c.Term == "average per calendar week");
        Assert.Contains(calendar.Occurrences, o => o.RepositoryId == "api-cs" && o.Role == "calculation");
        Assert.DoesNotContain(candidates.SelectMany(c => c.Occurrences), o => o.RepositoryId == "api-cs" && o.Name == "CalendarWeekAverage");
        Assert.All(candidates.SelectMany(c => c.Occurrences), o =>
        {
            Assert.Equal(selection![o.RepositoryId], o.CommitSha);
            Assert.All(o.Evidence, e =>
            {
                Assert.Equal(o.RepositoryId, e.RepositoryId);
                Assert.Equal(o.CommitSha, e.CommitSha);
            });
        });
    }

    [Fact]
    public async Task Evidence_can_be_followed_to_the_existing_exact_revision_source_viewer()
    {
        var active = (await Get("?view=worker-cs:v1-base")).Single(c => c.Term == "average per active week");
        var occurrence = active.Occurrences.Single(o => o.RepositoryId == "worker-cs" && o.Role == "calculation");
        Assert.NotEmpty(occurrence.UnitPath);
        Assert.NotEmpty(occurrence.Key);
        var evidence = Assert.Single(occurrence.Evidence.Take(1));
        var path = $"/api/source?repo={Uri.EscapeDataString(evidence.RepositoryId)}&commit={evidence.CommitSha}" +
            $"&path={Uri.EscapeDataString(evidence.Path)}&start={evidence.StartLine}&end={evidence.EndLine}";
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var source = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(source.GetProperty("lines").EnumerateArray());
        Assert.Equal(evidence.StartLine, source.GetProperty("highlightStart").GetInt32());
    }

    [Fact]
    public async Task Repeated_requests_produce_identical_bytes_without_enabling_optional_services()
    {
        var first = await _client.GetByteArrayAsync(new Uri("/api/glossary", UriKind.Relative));
        Assert.NotEmpty(first);
        Assert.Equal(first, await _client.GetByteArrayAsync(new Uri("/api/glossary", UriKind.Relative)));
        var meta = await _client.GetFromJsonAsync<JsonElement>("/api/meta");
        Assert.False(meta.GetProperty("interpretation").GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("?cap=does-not-exist")]
    [InlineData("?view=not-a-selection")]
    [InlineData("?view=api-cs:missing-revision")]
    public async Task Invalid_selections_follow_existing_calculations_behavior(string query)
    {
        var existing = await _client.GetAsync(new Uri("/api/calculations" + query, UriKind.Relative));
        var glossary = await _client.GetAsync(new Uri("/api/glossary" + query, UriKind.Relative));
        Assert.Equal(existing.StatusCode, glossary.StatusCode);
        if (glossary.IsSuccessStatusCode)
        {
            Assert.Equal(await _client.GetByteArrayAsync(new Uri("/api/glossary", UriKind.Relative)), await glossary.Content.ReadAsByteArrayAsync());
        }
    }
}

public sealed class GlossaryCapabilityApiTests(CapabilityExplorerFactory factory) : IClassFixture<CapabilityExplorerFactory>
{
    [Fact]
    public async Task Capability_filters_repositories_and_prefers_locked_revision_but_allows_in_scope_override()
    {
        var client = factory.CreateClient();
        var candidates = (await client.GetFromJsonAsync<List<GlossaryCandidate>>("/api/glossary?cap=demand-pricing", GlossaryApiTests.JsonOptions))!;
        Assert.NotEmpty(candidates);
        var occurrences = candidates.SelectMany(c => c.Occurrences).ToList();
        Assert.All(occurrences, o => Assert.Contains(o.RepositoryId, new[] { "forecast-cs", "pricing-node" }));
        Assert.Contains(occurrences, o => o.RepositoryId == "forecast-cs");
        Assert.All(occurrences.Where(o => o.RepositoryId == "forecast-cs"), o => Assert.Equal(Fixtures.Lock["forecast-cs"]["v1-base"], o.CommitSha));
        Assert.All(occurrences, o => Assert.Contains("demand-pricing", o.Capabilities));
        var overridden = (await client.GetFromJsonAsync<List<GlossaryCandidate>>("/api/glossary?cap=demand-pricing&view=forecast-cs:v2-contract,api-cs:v1-base", GlossaryApiTests.JsonOptions))!;
        var selected = overridden.SelectMany(c => c.Occurrences).ToList();
        Assert.DoesNotContain(selected, o => o.RepositoryId == "api-cs");
        Assert.All(selected.Where(o => o.RepositoryId == "forecast-cs"), o => Assert.Equal(Fixtures.Lock["forecast-cs"]["v2-contract"], o.CommitSha));
    }
}
